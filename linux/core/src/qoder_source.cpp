#include "agentspeed/qoder_source.hpp"

#include <algorithm>
#include <limits>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed::qoder {

namespace {

// Calls are rarely written again, so older emitted calls are forgotten once there are many.
constexpr std::size_t kEmittedLimit = 4096;
constexpr std::int64_t kEmittedWindowMs = 2 * 3'600'000;
constexpr std::int64_t kMinMs = std::numeric_limits<std::int64_t>::min();

std::int64_t saturatingMinus(std::int64_t value, std::int64_t delta) { return value < kMinMs + delta ? kMinMs : value - delta; }

}  // namespace

// MARK: - Reader

void Reader::State::note(std::optional<std::int64_t> ms) {
    if (ms && (!lastLineMs || *ms > *lastLineMs)) lastLineMs = ms;
}

Agent Reader::State::agent(const std::string& id, const std::string& session) const {
    return {id, session, project, cwdPath, model, parentAgentId, isSubagent, phase, inFlightStartMs, lastLineMs, malformed};
}

int Reader::malformedRows() const {
    int total = 0;
    for (const auto& [id, state] : states_) total += state.malformed;
    return total;
}

std::vector<std::string> Reader::agentIds() const {
    std::vector<std::string> out;
    for (const auto& [session, state] : states_) out.push_back(agentId(session));
    return out;
}

std::vector<Agent> Reader::agents() const {
    std::vector<Agent> out;
    for (const auto& [session, state] : states_) out.push_back(state.agent(agentId(session), session));
    return out;
}

std::optional<std::string> Reader::cwdOf(const std::string& id) const {
    const auto prefix = path_ + "#";
    if (id.compare(0, prefix.size(), prefix) != 0) return std::nullopt;
    const auto state = states_.find(id.substr(prefix.size()));
    return state == states_.end() ? std::nullopt : state->second.cwdPath;
}

int Reader::activeSessionCount(std::int64_t todayStartMs) const {
    return static_cast<int>(std::count_if(states_.begin(), states_.end(), [&](const auto& entry) {
        return entry.second.lastLineMs && *entry.second.lastLineMs >= todayStartMs;
    }));
}

void Reader::reset() {
    release();
    sessions_.clear();
    foreign_.clear();
    states_.clear();
    emitted_.clear();
    cursor_.reset();
    stamp_.reset();
    identity_.reset();
    needsSeed_ = true;
}

std::optional<Update> Reader::refresh(std::int64_t todayStartMs, Deduper& deduper) {
    if (!isDatabaseFile(path_)) {
        if (!exists()) return std::nullopt;
        Update gone;
        gone.removed = agentIds();
        reset();
        status_ = Status::Missing;
        return gone;
    }
    const auto stamp = sqlite::stampOf(path_);
    std::optional<Identity> identity;
    if (const auto s = fsx::statOf(path_)) identity = Identity{s->device, s->inode};
    // File times have the kernel clock's resolution: a replaced database can keep the stamp, so its identity counts too.
    if (stamp && stamp == stamp_ && identity == identity_ && (status_ == Status::Unsupported || (status_ == Status::Ready && !needsSeed_))) {
        return std::nullopt;
    }

    // Another file at the path or a database that shrank is read again like at startup.
    const bool replaced = stamp_ && stamp &&
                          ((identity_ && identity && *identity_ != *identity) || stamp->databaseLength < stamp_->databaseLength);
    Update update;
    if (replaced) {
        update.removed = agentIds();
        reset();
    }
    try {
        const auto database = Database::open(path_);
        const bool seeding = needsSeed_;
        const auto result = database.snapshot([&] { return readSnapshot(database, seeding, todayStartMs); });
        status_ = Status::Ready;
        needsSeed_ = false;
        update.seeded = seeding;
        for (const auto& row : result.rows) consume(row, result, deduper, update);
        finishAwaited(result.finished, update);
        cursor_ = std::max({cursor_.value_or(0), result.top, result.rows.empty() ? std::int64_t{0} : result.rows.back().rowId});
        if (emitted_.size() > kEmittedLimit && !result.rows.empty() && result.rows.back().timeMs) {
            const auto newest = *result.rows.back().timeMs;
            std::erase_if(emitted_, [&](const auto& entry) { return entry.second.endMs < newest - kEmittedWindowMs; });
        }
        stamp_ = stamp;
        identity_ = identity;
    } catch (const UnsupportedSchema&) {
        status_ = Status::Unsupported;
        stamp_ = stamp;
        identity_ = identity;
    } catch (const sqlite::Error&) {
        status_ = Status::Unreadable;
    }
    // Removed agents read again in the same pass stay.
    std::erase_if(update.removed, [&](const std::string& id) { return update.agents.contains(id); });
    return update;
}

// One snapshot's reads: every session at startup (each claimed unless an earlier database has it), else the sessions of
// new rows not known yet; the rows; the row before the first one of each session read for the first time; the turn
// records of the assistant rows read and of the waiting agents.
Reader::Read Reader::readSnapshot(const Database& database, bool seeding, std::int64_t todayStartMs) {
    Read result;
    result.rows = seeding ? database.recentRows(saturatingMinus(todayStartMs, kStartupWindowMs)) : database.rowsAfter(cursor_.value_or(0));
    result.top = database.maxRowId();
    if (seeding) {
        claimSessions(database.sessions());
    } else {
        std::set<std::string> unknown;
        for (const auto& row : result.rows) {
            if (row.sessionId && !sessions_.contains(*row.sessionId) && !foreign_.contains(*row.sessionId)) unknown.insert(*row.sessionId);
        }
        if (!unknown.empty()) claimSessions(database.sessions(unknown));
    }
    std::map<std::string, std::int64_t> firstRows;
    std::set<std::string> requests;
    std::optional<std::int64_t> newest;
    for (const auto& row : result.rows) {
        if (row.timeMs && (!newest || *row.timeMs > *newest)) newest = row.timeMs;
        if (!row.sessionId || !sessions_.contains(*row.sessionId)) continue;
        if (!states_.contains(*row.sessionId)) firstRows.emplace(*row.sessionId, row.rowId);
        if (row.role == Role::Assistant && row.requestId) requests.insert(*row.requestId);
    }
    for (const auto& [session, rowId] : firstRows) result.previous[session] = database.previousRowTime(session, rowId);
    const auto since = newest ? saturatingMinus(*newest, kAwaitingWindowMs) : kMinMs;
    for (const auto& [session, state] : states_) {
        if (state.awaiting && state.awaiting->rowMs >= since) requests.insert(state.awaiting->request);
    }
    result.finished = database.finishedAt(requests);
    return result;
}

void Reader::claimSessions(const std::vector<Session>& listed) {
    for (const auto& session : listed) {
        if (sessions_.contains(session.id) || foreign_.contains(session.id)) continue;
        if (claims_.claim(session.id, position_)) {
            sessions_.emplace(session.id, session);
        } else {
            foreign_.insert(session.id);
        }
    }
}

Reader::State Reader::freshState(const Session& session) const {
    State state;
    // A session without a working folder is named by its id.
    const auto name = session.projectPath ? path_text::basename(*session.projectPath) : std::string();
    state.project = name.empty() ? session.id : name;
    state.cwdPath = session.projectPath;
    state.isSubagent = session.parentId.has_value();
    if (session.parentId) state.parentAgentId = agentId(*session.parentId);
    return state;
}

void Reader::consume(const Row& row, const Read& batch, Deduper& deduper, Update& update) {
    if (!row.sessionId) return;
    const auto session = sessions_.find(*row.sessionId);
    if (session == sessions_.end()) return;
    const auto& id = *row.sessionId;
    auto found = states_.find(id);
    if (found == states_.end()) {
        auto fresh = freshState(session->second);
        if (const auto previous = batch.previous.find(id); previous != batch.previous.end()) fresh.previousRowMs = previous->second;
        found = states_.emplace(id, std::move(fresh)).first;
    }
    auto& state = found->second;
    const auto agent = agentId(id);
    // The call's request was sent when the session's previous row was written.
    const auto start = state.previousRowMs;
    state.previousRowMs = row.timeMs;
    state.awaiting.reset();
    state.note(row.timeMs);
    if (!row.timeMs || !row.role) {
        ++state.malformed;
        update.agents[agent] = state.agent(agent, id);
        return;
    }
    const auto time = *row.timeMs;
    const bool current = time >= state.phaseRowMs.value_or(kMinMs);
    switch (*row.role) {
        case Role::Assistant: {
            if (row.tokens) {
                if (auto r = response(*row.tokens, id, row.messageId, row.rowId, start, time, row.model, agent)) {
                    // Each database owns its calls' keys: a stored copy updates the call, another database's key is ignored.
                    std::string owner;
                    const bool claimed = deduper.tryClaim(r->key, path_, &owner);
                    if (claimed || owner == path_) {
                        if (r->model) state.model = r->model;
                        if (const auto previous = emitted_.find(r->key); previous == emitted_.end() || previous->second != *r) {
                            emitted_[r->key] = *r;
                            update.responses.emplace_back(std::move(*r), claimed);
                        }
                    }
                }
            }
            // A stored copy never moves the phase back: a row older than the one that set it changes nothing.
            if (!current) break;
            state.phaseRowMs = time;
            state.inFlightStartMs.reset();
            const auto done = row.requestId ? batch.finished.find(*row.requestId) : batch.finished.end();
            if (done != batch.finished.end() && done->second >= time) {
                state.phase = Phase::Idle;
            } else {
                state.phase = Phase::Tools;
                if (row.requestId) state.awaiting = Awaiting{*row.requestId, time};
            }
            break;
        }
        case Role::User:
        case Role::Tool:
            // The prompt or a tool result: the next request follows.
            if (!current) break;
            state.phaseRowMs = time;
            state.phase = Phase::Generating;
            state.inFlightStartMs = time;
            break;
        case Role::Other: break;
    }
    update.agents[agent] = state.agent(agent, id);
}

void Reader::finishAwaited(const std::map<std::string, std::int64_t>& finished, Update& update) {
    for (auto& [session, state] : states_) {
        if (!state.awaiting) continue;
        const auto done = finished.find(state.awaiting->request);
        if (done == finished.end() || done->second < state.awaiting->rowMs) continue;
        state.awaiting.reset();
        state.phase = Phase::Idle;
        state.inFlightStartMs.reset();
        update.agents[agentId(session)] = state.agent(agentId(session), session);
    }
}

// MARK: - Source

std::string Source::displayRoot() const { return qoder::displayRoot(roots_); }

bool Source::available() const {
    return std::any_of(readers_.begin(), readers_.end(), [](const auto& r) { return r->exists(); }) || !databases(roots_).empty();
}

bool Source::unsupported() const {
    return std::any_of(readers_.begin(), readers_.end(), [](const auto& r) { return r->status() == Status::Unsupported; });
}

int Source::unreadableCount() const {
    return static_cast<int>(std::count_if(readers_.begin(), readers_.end(), [](const auto& r) { return r->status() == Status::Unreadable; }));
}

std::int64_t Source::malformedRows() const {
    std::int64_t total = removedMalformedRows_;
    for (const auto& reader : readers_) total += reader->malformedRows();
    return total;
}

std::vector<Agent> Source::agents() const {
    std::vector<Agent> out;
    for (const auto& reader : readers_) {
        for (auto& agent : reader->agents()) out.push_back(std::move(agent));
    }
    return out;
}

std::optional<std::string> Source::cwdOf(const std::string& agentId) const {
    for (const auto& reader : readers_) {
        if (auto cwd = reader->cwdOf(agentId)) return cwd;
    }
    return std::nullopt;
}

int Source::activeSessionCount(std::int64_t todayStartMs) const {
    int total = 0;
    for (const auto& reader : readers_) total += reader->activeSessionCount(todayStartMs);
    return total;
}

std::vector<Update> Source::seed(std::int64_t todayStartMs) {
    todayStartMs_ = todayStartMs;
    std::vector<Update> updates;
    if (auto removed = sync(databases(roots_), true)) updates.push_back(std::move(*removed));
    for (auto& reader : readers_) {
        if (auto update = reader->refresh(todayStartMs, deduper_)) updates.push_back(std::move(*update));
    }
    return updates;
}

std::vector<Update> Source::refresh() {
    std::vector<Update> updates;
    for (auto& reader : readers_) {
        if (auto update = reader->refresh(todayStartMs_, deduper_)) updates.push_back(std::move(*update));
    }
    return updates;
}

std::vector<Update> Source::rediscover(std::int64_t todayStartMs) {
    todayStartMs_ = todayStartMs;
    std::vector<Update> updates;
    if (auto removed = sync(databases(roots_), false)) updates.push_back(std::move(*removed));
    for (auto& update : refresh()) updates.push_back(std::move(update));
    return updates;
}

std::optional<Update> Source::sync(const std::vector<std::string>& databaseList, bool force) {
    const bool same = databaseList.size() == readers_.size() &&
                      std::equal(databaseList.begin(), databaseList.end(), readers_.begin(),
                                 [](const std::string& path, const auto& reader) { return reader->path() == path; });
    if (!force && same) return std::nullopt;
    Update removed;
    for (const auto& reader : readers_) {
        for (auto& id : reader->agentIds()) removed.removed.push_back(std::move(id));
        removedMalformedRows_ += reader->malformedRows();
        reader->release();
    }
    readers_.clear();
    for (std::size_t i = 0; i < databaseList.size(); ++i) {
        readers_.push_back(std::make_unique<Reader>(databaseList[i], claims_, static_cast<int>(i)));
    }
    if (!removed.changed()) return std::nullopt;
    removed.seeded = true;
    return removed;
}

}  // namespace agentspeed::qoder
