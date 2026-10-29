#include "agentspeed/opencode.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <filesystem>

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/sqlite.hpp"

namespace agentspeed::opencode {

namespace {

constexpr std::int64_t kForkToleranceMs = 2000;
constexpr const char* kAgentPrefix = "opencode:";
constexpr const char* kSessionColumns = "id, parent_id, directory, time_created, time_updated";
constexpr const char* kRowColumns = "id, session_id, type, seq, time_created, time_updated, data";
constexpr std::size_t kIdBatch = 200;

std::optional<std::int64_t> readTime(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    const auto raw = v.raw();
    std::int64_t exact = 0;
    if (const auto [end, ec] = std::from_chars(raw.data(), raw.data() + raw.size(), exact);
        ec == std::errc() && end == raw.data() + raw.size()) {
        return exact;
    }
    const auto real = v.number();
    if (real && std::isfinite(*real) && std::abs(*real) < 9e15) return static_cast<std::int64_t>(*real);
    return std::nullopt;
}

bool forkCopy(const Data& data, const SessionInfo& session) {
    return data.created && *data.created < session.createdMs - kForkToleranceMs;
}

RowKind kindOf(std::string_view type) {
    if (type == "assistant") return RowKind::Assistant;
    if (type == "compaction") return RowKind::Compaction;
    if (type == "idle") return RowKind::Idle;
    return RowKind::Other;
}

std::vector<Row> readRows(sqlite::Statement& statement, const std::function<bool(const std::string&, std::int64_t)>& skip) {
    std::vector<Row> rows;
    try {
        while (statement.step()) {
            auto id = statement.getString(0);
            auto session = statement.getString(1);
            if (!id || !session) continue;
            const auto updated = statement.getInt64(5);
            if (skip && skip(*id, updated)) continue;
            Row row;
            row.id = std::move(*id);
            row.sessionId = std::move(*session);
            row.kind = kindOf(statement.getText(2));
            row.seq = statement.getInt64(3);
            row.timeCreated = statement.getInt64(4);
            row.timeUpdated = updated;
            row.data = readData(statement.getText(6));
            rows.push_back(std::move(row));
        }
    } catch (...) {
        statement.reset();
        throw;
    }
    statement.reset();
    return rows;
}

}  // namespace

bool Row::unfinished() const {
    if (!data) return false;
    if (kind == RowKind::Assistant) return !data->streamed || !data->completed;
    if (kind == RowKind::Compaction) return !data->completed;
    return false;
}

std::optional<Data> readData(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    Data d;
    if (const auto time = root["time"]; time.isObject()) {
        d.created = readTime(time["created"]);
        d.streamed = readTime(time["streamed"]);
        d.completed = readTime(time["completed"]);
    }
    if (const auto model = root["model"]; model.isObject()) {
        d.model = model["id"].nonEmptyString();
        d.provider = model["providerID"].nonEmptyString();
        if (const auto variant = model["variant"]; variant.isString()) d.variant = variant.string();
    }
    if (const auto native = root["native"]; native.isObject()) d.serviceTier = native["serviceTier"].nonEmptyString();
    d.finishedWithToolCalls = root["finish"].equals("tool-calls");
    if (const auto tokens = root["tokens"]; tokens.isObject()) {
        d.hasTokens = true;
        const auto input = tokens["input"].count();
        const auto output = tokens["output"].count();
        const auto reasoning = tokens["reasoning"].count();
        std::int64_t read = 0, write = 0;
        if (const auto cache = tokens["cache"]; cache.isObject()) {
            read = cache["read"].count();
            write = cache["write"].count();
        }
        // OpenCode reports reasoning separately: output = output + reasoning.
        d.tokens.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, read), write);
        d.tokens.cachedInput = read;
        d.tokens.cacheWriteInput = write;
        d.tokens.output = TokenCounts::saturatingAdd(output, reasoning);
        d.tokens.reasoning = reasoning;
    }
    return d;
}

std::string agentId(const std::string& sessionId) { return kAgentPrefix + sessionId; }

ExecutionModes modesOf(const Data& data) {
    return ExecutionModes::of(native::effort(data.variant, ModeEvidence::Recorded, ModeScope::Request))
        .merge(native::openAiTier(data.serviceTier, ModeEvidence::Served, ModeScope::Response, data.provider));
}

std::optional<ParsedResponse> response(const Row& row, const SessionInfo& session) {
    if (!row.data || !row.data->hasTokens) return std::nullopt;
    const Data& data = *row.data;
    std::optional<std::int64_t> startMs;
    std::int64_t endMs = 0;
    if (row.kind == RowKind::Assistant) {
        // The stream end, not the completion: tool execution after the stream is not generation.
        if (forkCopy(data, session) || !data.streamed) return std::nullopt;
        startMs = data.created;
        endMs = *data.streamed;
    } else if (row.kind == RowKind::Compaction) {
        startMs = data.created;
        endMs = data.completed.value_or(row.timeUpdated);
    } else {
        return std::nullopt;
    }
    ParsedResponse r;
    r.key = row.id;
    r.source = SourceKind::OpenCode;
    r.agentId = agentId(session.id);
    r.model = data.model;
    r.startMs = startMs;
    r.endMs = endMs;
    r.tokens = data.tokens;
    r.provider = data.provider;
    r.modes = modesOf(data);
    return r;
}

AgentState replay(const SessionInfo& session, const std::vector<Row>& rows, std::optional<std::string> model,
                  const ExecutionModes& initialModes) {
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlight, lastLine;
    std::int64_t malformed = 0;
    ExecutionModes modes = initialModes;
    for (const auto& row : rows) {
        if (!row.data) {
            ++malformed;
            continue;
        }
        const Data& data = *row.data;
        if (row.kind == RowKind::Assistant) {
            if (forkCopy(data, session)) continue;
            if (data.model) model = data.model;
            modes = modesOf(data);
            if (!data.streamed) {
                phase = Phase::Generating;
                inFlight = data.created;
            } else {
                phase = data.finishedWithToolCalls ? Phase::Tools : Phase::Idle;
                inFlight.reset();
            }
        } else if (row.kind == RowKind::Idle) {
            phase = Phase::Idle;
            inFlight.reset();
        }
        lastLine = lastLine ? std::max(*lastLine, row.timeUpdated) : row.timeUpdated;
    }
    AgentState state;
    state.id = agentId(session.id);
    state.sessionId = session.id;
    state.parentSessionId = session.parentId;
    state.createdMs = session.createdMs;
    const std::string project = session.directory ? path_text::basename(*session.directory) : std::string();
    if (!project.empty()) state.project = project;
    state.model = model;
    state.isSubagent = session.parentId.has_value();
    state.phase = phase;
    state.inFlightStartMs = inFlight;
    state.lastLineMs = lastLine;
    state.malformedRows = malformed;
    state.modes = modes;
    return state;
}

Database::Database(std::unique_ptr<sqlite::Connection> connection, bool immutable)
    : connection_(std::move(connection)), immutable_(immutable) {}

Database::~Database() = default;

std::string Database::databasePath(const std::string& root) {
    if (root.empty()) return {};
    std::error_code ec;
    if (std::filesystem::is_regular_file(root, ec)) return root;
    return (std::filesystem::path(root) / kFileName).string();
}

std::unique_ptr<Database> Database::open(const std::string& path) {
    std::error_code ec;
    if (path.empty() || !std::filesystem::exists(path, ec)) throw OpenError{Status::Missing, "missing"};
    std::unique_ptr<sqlite::Connection> connection;
    bool immutable = false;
    try {
        connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    } catch (const sqlite::Error& e) {
        const bool unsupported = e.primaryCode() == 1 || e.primaryCode() == 26;
        throw OpenError{unsupported ? Status::Unsupported : Status::Unreadable, e.what()};
    }
    try {
        if (!connection->hasTables({"session_v2", "session_message"})) {
            throw OpenError{Status::Unsupported, "required tables are missing"};
        }
    } catch (const sqlite::Error& e) {
        const bool unsupported = e.primaryCode() == 1 || e.primaryCode() == 26;
        throw OpenError{unsupported ? Status::Unsupported : Status::Unreadable, e.what()};
    }
    return std::unique_ptr<Database>(new Database(std::move(connection), immutable));
}

std::vector<SessionInfo> Database::sessions(std::optional<std::int64_t> updatedSince) {
    std::string sql = std::string("SELECT ") + kSessionColumns + " FROM session_v2";
    if (updatedSince) sql += " WHERE time_updated >= ?1";
    auto statement = connection_->prepare(sql);
    if (updatedSince) statement.bind(1, *updatedSince);
    std::vector<SessionInfo> out;
    while (statement.step()) {
        auto id = statement.getString(0);
        if (!id) continue;
        out.push_back({*id, statement.getString(1), statement.getString(2), statement.getInt64(3), statement.getInt64(4)});
    }
    return out;
}

std::vector<Row> Database::sessionRows(const std::string& sessionId, std::optional<std::int64_t> updatedSince,
                                       const std::function<bool(const std::string&, std::int64_t)>& skip) {
    std::string sql = std::string("SELECT ") + kRowColumns + " FROM session_message WHERE session_id = ?1";
    if (updatedSince) sql += " AND time_updated >= ?2";
    sql += " ORDER BY seq";
    auto statement = connection_->prepare(sql);
    statement.bind(1, sessionId);
    if (updatedSince) statement.bind(2, *updatedSince);
    return readRows(statement, skip);
}

std::vector<Row> Database::rowsCreatedSince(std::int64_t sinceMs,
                                            const std::function<bool(const std::string&, std::int64_t)>& skip) {
    auto statement = connection_->prepare(std::string("SELECT ") + kRowColumns +
                                          " FROM session_message WHERE time_created >= ?1 ORDER BY session_id, seq");
    statement.bind(1, sinceMs);
    return readRows(statement, skip);
}

std::vector<Row> Database::rowsByIds(const std::vector<std::string>& ids,
                                     const std::function<bool(const std::string&, std::int64_t)>& skip) {
    std::vector<Row> rows;
    for (std::size_t start = 0; start < ids.size(); start += kIdBatch) {
        const std::size_t count = std::min(kIdBatch, ids.size() - start);
        std::string placeholders;
        for (std::size_t i = 0; i < count; ++i) placeholders += i == 0 ? "?" : ",?";
        auto statement = connection_->prepare(std::string("SELECT ") + kRowColumns + " FROM session_message WHERE id IN (" +
                                              placeholders + ") ORDER BY session_id, seq");
        for (std::size_t i = 0; i < count; ++i) statement.bind(static_cast<int>(i + 1), ids[start + i]);
        auto chunk = readRows(statement, skip);
        rows.insert(rows.end(), std::make_move_iterator(chunk.begin()), std::make_move_iterator(chunk.end()));
    }
    return rows;
}

std::optional<std::int64_t> Database::maxCreatedMs() {
    auto statement = connection_->prepare("SELECT max(time_created) FROM session_message");
    return statement.step() ? statement.getNullableInt64(0) : std::nullopt;
}

Source::Source(std::string root, Deduper& deduper)
    : root_(std::move(root)), databasePath_(Database::databasePath(root_)),
      walPath_(databasePath_.empty() ? std::string() : databasePath_ + "-wal"), deduper_(deduper) {}

Source::~Source() = default;

std::optional<Source::Stamp> Source::stampOf(const std::string& path) {
    struct stat st {};
    if (path.empty() || stat(path.c_str(), &st) != 0) return std::nullopt;
    return Stamp{static_cast<std::int64_t>(st.st_size),
                 static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec};
}

std::vector<AgentState> Source::agents() const {
    std::vector<AgentState> out;
    for (const auto& [_, t] : sessions_) {
        if (t.state && t.state->lastLineMs) out.push_back(*t.state);
    }
    return out;
}

void Source::close(Status status) {
    database_.reset();
    status_ = status;
}

bool Source::refreshAvailability() {
    const Status previous = status_;
    std::error_code ec;
    databaseExists_ = !databasePath_.empty() && std::filesystem::exists(databasePath_, ec);
    if (!databaseExists_) {
        close(Status::Missing);
        return previous != Status::Missing;
    }
    struct stat st {};
    std::optional<Stamp> identity;
    if (stat(databasePath_.c_str(), &st) == 0) {
        identity = Stamp{static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino)};
    }
    if (database_ && (!identity || identity == identity_)) return false;
    close(Status::Missing);
    try {
        database_ = Database::open(databasePath_);
        status_ = Status::Ready;
    } catch (const OpenError& e) {
        status_ = e.status;
    }
    identity_ = identity;
    databaseStamp_ = stampOf(databasePath_);
    walStamp_ = stampOf(walPath_);
    needsSeed_ = status_ == Status::Ready;
    return status_ != previous || status_ == Status::Ready;
}

Update Source::seed(std::int64_t todayStartMs) {
    Update update;
    update.reset = true;
    std::vector<std::string> previous;
    for (const auto& [id, t] : sessions_) {
        if (t.state && t.state->lastLineMs) previous.push_back(id);
    }
    sessions_.clear();
    rowSessions_.clear();
    watchedRows_.clear();
    createdCursor_.reset();
    sessionCursor_.reset();
    newestRowUpdate_ = 0;
    needsSeed_ = false;
    if (database_) {
        const std::int64_t threshold = todayStartMs - kStartupLookbackMs;
        try {
            createdCursor_ = database_->maxCreatedMs().value_or(0);
            auto infos = database_->sessions();
            std::sort(infos.begin(), infos.end(), [](const SessionInfo& a, const SessionInfo& b) { return a.id < b.id; });
            for (const auto& info : infos) {
                sessionCursor_ = std::max(sessionCursor_.value_or(info.updatedMs), info.updatedMs);
                Tracked tracked;
                tracked.info = info;
                if (info.updatedMs < threshold) {
                    tracked.rowWatermark = info.updatedMs;
                    sessions_[info.id] = std::move(tracked);
                    continue;
                }
                sessions_[info.id] = std::move(tracked);
                apply(database_->sessionRows(info.id), info.id, update);
            }
        } catch (const sqlite::Error&) {
            // Busy or vanished: read again on the next rediscovery.
            needsSeed_ = true;
        }
    }
    for (const auto& id : previous) {
        const auto agent = agentId(id);
        if (!update.agents.contains(agent)) update.removed.push_back(agent);
    }
    return update;
}

Update Source::refresh() {
    Update update;
    dropStaleImmutableConnection();
    if (status_ != Status::Ready || !database_) return update;
    if (auto stamp = stampOf(databasePath_)) databaseStamp_ = stamp;
    walStamp_ = stampOf(walPath_);
    auto known = [this](const std::string& id, std::int64_t updated) { return isKnown(id, updated); };
    try {
        std::vector<std::string> advanced;
        for (const auto& info : database_->sessions(sessionCursor_.value_or(0) - kCursorOverlapMs)) {
            sessionCursor_ = std::max(sessionCursor_.value_or(info.updatedMs), info.updatedMs);
            if (auto it = sessions_.find(info.id); it != sessions_.end()) {
                Tracked& tracked = it->second;
                if (info == tracked.info) continue;
                const bool moved = info.updatedMs > tracked.info.updatedMs;
                tracked.info = info;
                if (!tracked.rows.empty()) update.agents[agentId(info.id)] = replayTracked(tracked);
                if (moved) advanced.push_back(info.id);
            } else {
                Tracked tracked;
                tracked.info = info;
                sessions_[info.id] = std::move(tracked);
                advanced.push_back(info.id);
            }
        }
        std::map<std::string, std::vector<Row>> changed;
        auto collect = [&](std::vector<Row> rows) {
            for (auto& row : rows) changed[row.sessionId].push_back(std::move(row));
        };
        collect(database_->rowsCreatedSince(createdCursor_.value_or(0) - kCursorOverlapMs, known));
        if (!watchedRows_.empty()) {
            std::vector<std::string> ids;
            for (const auto& [id, _] : watchedRows_) ids.push_back(id);
            collect(database_->rowsByIds(ids, known));
        }
        for (const auto& id : advanced) {
            const auto& tracked = sessions_[id];
            std::optional<std::int64_t> since;
            if (tracked.rowWatermark) since = *tracked.rowWatermark - kCursorOverlapMs;
            collect(database_->sessionRows(id, since, known));
        }
        for (auto& [session, rows] : changed) {
            if (sessions_.contains(session)) apply(std::move(rows), session, update);
        }
    } catch (const sqlite::Error&) {
        // A busy or vanished database is read again on the next change, poll or rediscovery.
    }
    return update;
}

std::optional<Update> Source::refreshIfChanged() {
    if (status_ != Status::Ready) return std::nullopt;
    const auto database = stampOf(databasePath_);
    const auto wal = stampOf(walPath_);
    if (database == databaseStamp_ && wal == walStamp_) return std::nullopt;
    if (!database) return std::nullopt;
    return refresh();
}

Update Source::rediscover(std::int64_t nowMs, std::int64_t todayStartMs) {
    if (refreshAvailability() || needsSeed_) return status_ == Status::Ready ? seed(todayStartMs) : drop();
    Update update = refresh();
    if (status_ != Status::Ready || !database_) return update;
    std::set<std::string> present;
    try {
        for (const auto& s : database_->sessions()) present.insert(s.id);
    } catch (const sqlite::Error&) {
        return update;
    }
    for (auto it = sessions_.begin(); it != sessions_.end();) {
        if (present.contains(it->first)) {
            ++it;
            continue;
        }
        release(it->second);
        const auto agent = agentId(it->first);
        update.agents.erase(agent);
        update.removed.push_back(agent);
        it = sessions_.erase(it);
    }
    for (auto& [_, tracked] : sessions_) {
        if (!tracked.rows.empty() && tracked.state && tracked.state->phase != Phase::Generating && tracked.state->lastLineMs &&
            nowMs - *tracked.state->lastLineMs > kCompactAfterMs) {
            tracked.model = tracked.state->model;
            tracked.modes = tracked.state->modes;
            release(tracked);
        }
    }
    return update;
}

void Source::dropStaleImmutableConnection() {
    std::error_code ec;
    if (!database_ || !database_->immutable() ||
        (!std::filesystem::exists(walPath_, ec) && stampOf(databasePath_) == databaseStamp_)) {
        return;
    }
    database_.reset();
    try {
        database_ = Database::open(databasePath_);
        status_ = Status::Ready;
    } catch (const OpenError& e) {
        status_ = e.status;
    }
}

Update Source::drop() {
    Update update;
    update.reset = true;
    for (const auto& [id, t] : sessions_) {
        if (t.state && t.state->lastLineMs) update.removed.push_back(agentId(id));
    }
    sessions_.clear();
    rowSessions_.clear();
    watchedRows_.clear();
    return update;
}

void Source::apply(std::vector<Row> rows, const std::string& sessionId, Update& update) {
    auto it = sessions_.find(sessionId);
    if (it == sessions_.end()) return;
    Tracked& tracked = it->second;
    const std::string agent = agentId(sessionId);
    std::stable_sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) { return a.seq < b.seq; });
    for (auto& row : rows) {
        if (auto known = tracked.rows.find(row.id); known != tracked.rows.end() && known->second == row) continue;
        tracked.rows[row.id] = row;
        rowSessions_[row.id] = sessionId;
        tracked.rowWatermark = std::max(tracked.rowWatermark.value_or(row.timeUpdated), row.timeUpdated);
        newestRowUpdate_ = std::max(newestRowUpdate_, row.timeUpdated);
        createdCursor_ = std::max(createdCursor_.value_or(row.timeCreated), row.timeCreated);
        watch(row);
        auto r = response(row, tracked.info);
        if (!r) continue;
        bool completion = false;
        if (auto previous = tracked.emitted.find(row.id); previous != tracked.emitted.end()) {
            if (previous->second.key == r->key && previous->second.startMs == r->startMs &&
                previous->second.endMs == r->endMs && previous->second.tokens == r->tokens &&
                previous->second.model == r->model && previous->second.provider == r->provider &&
                previous->second.modes == r->modes) {
                continue;
            }
        } else {
            std::string owner;
            if (deduper_.tryClaim(row.id, agent, &owner)) {
                completion = true;
            } else if (owner != agent) {
                continue;
            }
        }
        tracked.emitted[row.id] = *r;
        update.responses.emplace_back(*r, completion);
    }
    update.agents[agent] = replayTracked(tracked);
    pruneWatchedRows();
}

AgentState Source::replayTracked(Tracked& tracked) {
    std::vector<Row> rows;
    for (const auto& [_, row] : tracked.rows) rows.push_back(row);
    std::stable_sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) { return a.seq < b.seq; });
    tracked.state = replay(tracked.info, rows, tracked.model, tracked.modes);
    return *tracked.state;
}

bool Source::isKnown(const std::string& id, std::int64_t updated) const {
    const auto session = rowSessions_.find(id);
    if (session == rowSessions_.end()) return false;
    const auto tracked = sessions_.find(session->second);
    if (tracked == sessions_.end()) return false;
    const auto row = tracked->second.rows.find(id);
    return row != tracked->second.rows.end() && row->second.timeUpdated == updated;
}

void Source::watch(const Row& row) {
    if (row.unfinished() || row.timeUpdated >= newestRowUpdate_ - kRecentRowMs) {
        watchedRows_[row.id] = row.timeUpdated;
    } else {
        watchedRows_.erase(row.id);
    }
}

void Source::pruneWatchedRows() {
    const std::int64_t staleBefore = newestRowUpdate_ - kStaleInFlightMs;
    for (auto it = watchedRows_.begin(); it != watchedRows_.end();) {
        it = it->second < staleBefore ? watchedRows_.erase(it) : std::next(it);
    }
    if (watchedRows_.size() <= kMaxWatchedRows) return;
    std::vector<std::pair<std::string, std::int64_t>> byAge(watchedRows_.begin(), watchedRows_.end());
    std::stable_sort(byAge.begin(), byAge.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
    for (std::size_t i = kMaxWatchedRows; i < byAge.size(); ++i) watchedRows_.erase(byAge[i].first);
}

void Source::release(Tracked& tracked) {
    for (const auto& [id, _] : tracked.rows) {
        rowSessions_.erase(id);
        watchedRows_.erase(id);
    }
    tracked.rows.clear();
    tracked.emitted.clear();
}

}  // namespace agentspeed::opencode
