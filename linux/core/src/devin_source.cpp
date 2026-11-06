#include "agentspeed/devin_source.hpp"

#include <algorithm>
#include <limits>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed::devin {

namespace {

// Copies of a call are written together, so older emitted calls are forgotten once there are many.
constexpr std::size_t kEmittedLimit = 4096;
constexpr std::int64_t kEmittedWindowMs = 2 * 3'600'000;

}  // namespace

// MARK: - Reader

void Reader::State::apply(const Session& session) {
    // A session without a working directory is named by its id, never by the database's folder.
    const auto name = session.workingDirectory ? path_text::basename(*session.workingDirectory) : std::string();
    project = name.empty() ? session.id : name;
    cwdPath = session.workingDirectory;
    model = called && callModel ? callModel : session.model;
}

void Reader::State::call(const std::optional<std::string>& callModelNow, const Session& session) {
    called = true;
    callModel = callModelNow;
    model = callModelNow ? callModelNow : session.model;
}

void Reader::State::note(std::optional<std::int64_t> ms) {
    if (ms && (!lastLineMs || *ms > *lastLineMs)) lastLineMs = ms;
}

Agent Reader::State::agent(const std::string& id, const std::string& session) const {
    return {id, session, project, cwdPath, model, phase, inFlightStartMs, lastLineMs, malformed};
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
        std::vector<Session> sessions;
        std::vector<Row> rows;
        std::int64_t top = 0;
        database.snapshot([&] {
            sessions = database.sessions();
            rows = seeding ? database.recentRows(todayStartMs - kStartupWindowMs) : database.rowsAfter(cursor_.value_or(0));
            top = database.maxRowId();
        });
        status_ = Status::Ready;
        needsSeed_ = false;
        update.seeded = seeding;
        readSessions(sessions, update);
        for (const auto& row : rows) consume(row, deduper, update);
        cursor_ = std::max({cursor_.value_or(0), top, rows.empty() ? std::int64_t{0} : rows.back().rowId});
        if (emitted_.size() > kEmittedLimit && !rows.empty() && rows.back().timeMs) {
            const auto newest = *rows.back().timeMs;
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

void Reader::readSessions(const std::vector<Session>& rows, Update& update) {
    std::map<std::string, Session> next;
    for (const auto& session : rows) {
        if (claims_.claim(session.id, position_)) next[session.id] = session;
    }
    for (const auto& [id, session] : sessions_) {
        if (!next.contains(id)) claims_.release(id, position_);
    }
    for (const auto& [id, session] : next) {
        const auto state = states_.find(id);
        if (state == states_.end()) continue;
        if (const auto known = sessions_.find(id); known != sessions_.end() && known->second == session) continue;
        state->second.apply(session);
        update.agents[agentId(id)] = state->second.agent(agentId(id), id);
    }
    sessions_ = std::move(next);
}

void Reader::consume(const Row& row, Deduper& deduper, Update& update) {
    if (!row.sessionId) return;
    const auto session = sessions_.find(*row.sessionId);
    if (session == sessions_.end()) return;
    const auto& id = *row.sessionId;
    auto [found, created] = states_.try_emplace(id);
    auto& state = found->second;
    if (created) state.apply(session->second);
    const auto agent = agentId(id);
    if (!row.timeMs || !row.message) {
        ++state.malformed;
        state.note(row.timeMs);
    } else {
        const auto time = *row.timeMs;
        state.note(time);
        if (auto r = response(*row.message, id, row.rowId, time, session->second.model, agent)) {
            // Each database owns its calls' keys: a stored copy updates the call, another database's key is ignored.
            std::string owner;
            const bool claimed = deduper.tryClaim(r->key, path_, &owner);
            if (claimed || owner == path_) {
                state.call(r->model, session->second);
                if (const auto previous = emitted_.find(r->key); previous == emitted_.end() || previous->second != *r) {
                    emitted_[r->key] = *r;
                    update.responses.emplace_back(std::move(*r), claimed);
                }
            }
        }
        // A stored copy never moves the phase back: a row older than the one that set it changes nothing.
        if (const auto phase = row.message->phase(); phase && time >= state.phaseRowMs.value_or(std::numeric_limits<std::int64_t>::min())) {
            state.phaseRowMs = time;
            state.phase = *phase;
            state.inFlightStartMs = *phase == Phase::Generating ? std::optional<std::int64_t>(time) : std::nullopt;
        }
    }
    update.agents[agent] = state.agent(agent, id);
}

// MARK: - Source

std::string Source::displayRoot() const { return devin::displayRoot(roots_); }

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

}  // namespace agentspeed::devin
