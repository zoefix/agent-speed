#include "agentspeed/openclaw.hpp"

#include <sys/stat.h>
#include <zstd.h>

#include <algorithm>

#include "agentspeed/json.hpp"

namespace agentspeed::openclaw {

namespace {

constexpr std::int64_t kCopiedHistoryMs = 2000;
constexpr std::int64_t kBacklogWindowMs = 15 * 60'000;
constexpr int kInteger = 1, kFloat = 2, kText = 3, kBlob = 4;

std::optional<std::int64_t> integer(const sqlite::Statement& s, int column) {
    return s.columnType(column) == kInteger ? std::optional<std::int64_t>(s.getInt64(column)) : std::nullopt;
}

// A row's entry is one JSON object: blank text, other JSON values and invalid UTF-8 are malformed (unlike a blank line).
Entry parse(std::string_view raw) {
    Entry entry;
    if (raw.size() > Database::kMaxEntryBytes) return entry;
    if (json::isBlank(raw)) {
        entry.status = Entry::Status::Malformed;
        return entry;
    }
    bool malformed = false;
    auto line = pi::readLine(raw, malformed);
    if (malformed || !line) {
        entry.status = Entry::Status::Malformed;
        return entry;
    }
    entry.status = Entry::Status::Valid;
    entry.line = std::move(*line);
    entry.text = std::string(raw);
    return entry;
}

std::set<std::string> columnsOf(const sqlite::Connection& c, const std::string& table) {
    std::set<std::string> columns;
    auto s = c.prepare("PRAGMA table_info(" + table + ")");
    while (s.step()) {
        if (auto name = s.getString(1)) columns.insert(*name);
    }
    return columns;
}

std::optional<std::pair<std::int64_t, std::int64_t>> identityOf(const std::string& path) {
    struct stat st {};
    if (stat(path.c_str(), &st) != 0) return std::nullopt;
    return std::make_pair(static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino));
}

}  // namespace

bool Window::delegated() const {
    return acpOwned || !(runtime.empty() || runtime == "openclaw" || runtime == "pi" || runtime == "auto" || runtime == "default");
}

bool Window::mirrorsOnly() const {
    return acpOwned || runtime == "codex" || runtime == "claude-cli" || runtime.rfind("acp", 0) == 0;
}

Parser::Parser(const std::string& databasePath, Window window, Deduper& deduper)
    : TranscriptParser(agentIdOf(databasePath, window.sessionId), deduper, SourceKind::OpenClaw),
      databasePath_(databasePath), window_(std::move(window)) {
    setSource(SourceKind::OpenClaw);
    setSubagent(window_.isSubagent());
}

AgentLink Parser::link() const { return {agentId(), LinkKind::OpenClawWindow, agentId(), parentId_, headerMs_, 0}; }

void Parser::updateWindow(const Window& window) {
    window_ = window;
    setSubagent(window.isSubagent());
}

bool Parser::runtimeChanged(const Window& window) const {
    return window.delegated() != window_.delegated() || window.mirrorsOnly() != window_.mirrorsOnly();
}

void Parser::setParent(const std::optional<std::string>& parentId) {
    if (parentId != parentId_) {
        parentId_ = parentId;
        markChanged();
    }
}

void Parser::reset() {
    TranscriptParser::reset();
    pendingToolCalls_.clear();
    responses_.clear();
    headerSeen_ = false;
    headerMs_.reset();
    setSource(SourceKind::OpenClaw);
    setSubagent(window_.isSubagent());
}

void Parser::feed(std::string_view text, ResponseSink& sink) {
    const Entry entry = parse(text);
    if (entry.status == Entry::Status::Malformed) {
        countMalformed();
        return;
    }
    if (entry.status == Entry::Status::Valid) applyEntry(entry, sink);
}

void Parser::applyEntry(const Entry& entry, ResponseSink& sink) {
    apply(entry.line, sink);
    observeText(entry.text);
}

bool Parser::isMirror(const pi::Line& line) const {
    const std::string provider = line.call.provider.value_or("");
    return line.mirrored || (line.call.apiIsCli && provider == "claude-cli") || provider.rfind("acp-", 0) == 0 ||
           window_.mirrorsOnly();
}

void Parser::apply(const pi::Line& line, ResponseSink& sink) {
    if (line.kind == pi::Kind::Session) {
        if (headerSeen_) return;
        headerSeen_ = true;
        headerMs_ = line.timestampMs;
        setProjectFromCwd(line.cwd);
        markChanged();
        if (line.timestampMs) touch(*line.timestampMs);
        return;
    }
    if (!headerSeen_ || !line.id || !line.timestampMs || (headerMs_ && *line.timestampMs < *headerMs_ - kCopiedHistoryMs)) return;
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    if (line.kind != pi::Kind::Message) return;
    switch (line.role) {
        case pi::Role::Assistant:
            applyAssistant(line, ts, sink);
            break;
        case pi::Role::User:
        case pi::Role::Developer:
            if (window_.delegated()) break;
            pendingToolCalls_.clear();
            setPhase(Phase::Generating, ts, ts);
            break;
        case pi::Role::ToolResult:
            if (window_.delegated()) break;
            if (line.toolCallId) pendingToolCalls_.erase(*line.toolCallId);
            if (phase() == Phase::Tools && pendingToolCalls_.empty()) setPhase(Phase::Generating, ts, ts);
            break;
        case pi::Role::Other:
            break;
    }
}

void Parser::applyAssistant(const pi::Line& line, std::int64_t ts, ResponseSink& sink) {
    const std::string key = "openclaw:" + *line.id + ":" + std::to_string(line.startedMs.value_or(ts));
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    const auto& call = line.call;
    if (!call.guardedMessage()) return;
    const bool failed = !(line.stop == pi::Stop::Stop || line.stop == pi::Stop::Length || line.stop == pi::Stop::ToolUse);
    if (!isMirror(line) && !call.usageUnavailable && call.hasUsage) {
        const bool timed = !window_.delegated() && !call.apiIsCli && !failed;
        const bool completion = deduper().tryClaim(key, agentId());
        ParsedResponse r;
        r.key = key;
        r.source = SourceKind::OpenClaw;
        r.agentId = agentId();
        r.model = call.model;
        r.startMs = timed ? line.startedMs : std::nullopt;
        r.endMs = ts;
        r.tokens = call.tokens;
        r.tokens.reasoning = call.reasoningTokens;
        r.provider = call.provider;
        bool same = false;
        if (auto previous = responses_.find(key); previous != responses_.end()) {
            const auto& p = previous->second;
            same = p.startMs == r.startMs && p.endMs == r.endMs && p.tokens == r.tokens && p.model == r.model &&
                   p.provider == r.provider;
        }
        if (!same) {
            responses_[key] = r;
            sink.onResponse(r, completion);
        }
        setModel(call.model);
    }
    if (window_.delegated()) return;
    if (failed) {
        pendingToolCalls_.clear();
        setPhase(Phase::Idle, ts, std::nullopt);
    } else if (!line.toolCallIds.empty()) {
        pendingToolCalls_.clear();
        pendingToolCalls_.insert(line.toolCallIds.begin(), line.toolCallIds.end());
        setPhase(Phase::Tools, ts, std::nullopt);
    } else {
        setPhase(Phase::Idle, ts, std::nullopt);
    }
}

Database::Database(std::unique_ptr<sqlite::Connection> connection, bool immutable, const std::set<std::string>& tables)
    : connection_(std::move(connection)), immutable_(immutable),
      supported_(tables.contains("session_windows") && tables.contains("transcript_events")),
      hasNodes_(tables.contains("session_nodes")), hasWatermarks_(tables.contains("transcript_rewrite_watermarks")) {
    if (!supported_) return;
    const auto windows = columnsOf(*connection_, "session_windows");
    auto column = [&](const char* name, const char* fallback) { return std::string(windows.contains(name) ? name : fallback); };
    windowsSql_ = "SELECT session_id, created_at, " + column("transcript_updated_at", "NULL") + ", " +
                  column("spawned_by", "NULL") + ", " + column("agent_harness_id", "NULL") + ", " +
                  column("acp_owned", "0") + " FROM session_windows ORDER BY created_at, session_id";
    const auto events = columnsOf(*connection_, "transcript_events");
    const std::string zstd =
        events.contains("event_zstd") && events.contains("event_utf8_bytes") ? "event_zstd, event_utf8_bytes" : "NULL, NULL";
    rowsSql_ = "SELECT seq, event_json, " + zstd + " FROM transcript_events WHERE session_id = ?1 AND seq > ?2 ORDER BY seq";
}

Database::~Database() = default;

std::unique_ptr<Database> Database::open(const std::string& path) {
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    std::set<std::string> tables;
    {
        auto s = connection->prepare("SELECT name FROM sqlite_master WHERE type = 'table'");
        while (s.step()) {
            if (auto name = s.getString(0)) tables.insert(*name);
        }
    }
    return std::unique_ptr<Database>(new Database(std::move(connection), immutable, tables));
}

void Database::begin() { connection_->execute("BEGIN"); }

void Database::commit() {
    try {
        connection_->execute("COMMIT");
    } catch (const sqlite::Error&) {
    }
}

std::vector<Window> Database::windows() {
    std::vector<Window> out;
    auto s = connection_->prepare(windowsSql_);
    while (s.step()) {
        if (s.isNull(0)) continue;
        Window w;
        w.sessionId = *s.getString(0);
        w.createdMs = integer(s, 1);
        w.updatedMs = integer(s, 2) ? integer(s, 2) : w.createdMs;
        if (s.columnType(3) == kText) w.spawnedBy = s.getString(3);
        if (s.columnType(4) == kText) {
            std::string runtime = *s.getString(4);
            const auto begin = runtime.find_first_not_of(" \t\r\n\v\f");
            const auto end = runtime.find_last_not_of(" \t\r\n\v\f");
            runtime = begin == std::string::npos ? std::string() : runtime.substr(begin, end - begin + 1);
            for (char& c : runtime) {
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            }
            w.runtime = runtime;
        }
        const int acp = s.columnType(5);
        w.acpOwned = acp == kInteger ? s.getInt64(5) == 1 : acp == kFloat ? s.getDouble(5) == 1.0 : false;
        out.push_back(std::move(w));
    }
    return out;
}

std::map<std::string, std::string> Database::currentSessions() {
    std::map<std::string, std::string> current;
    if (!hasNodes_) return current;
    try {
        auto s = connection_->prepare("SELECT session_key, current_session_id FROM session_nodes");
        while (s.step()) {
            auto key = s.getString(0);
            auto session = s.getString(1);
            if (key && session) current[*key] = *session;
        }
    } catch (const sqlite::Error&) {
    }
    return current;
}

std::map<std::string, std::string> Database::watermarks() {
    std::map<std::string, std::string> generations;
    if (!hasWatermarks_) return generations;
    try {
        auto s = connection_->prepare("SELECT session_id, generation FROM transcript_rewrite_watermarks");
        while (s.step()) {
            if (auto session = s.getString(0)) generations[*session] = s.getString(1).value_or("");
        }
    } catch (const sqlite::Error&) {
    }
    return generations;
}

std::optional<std::int64_t> Database::maxSeq(const std::string& sessionId) {
    auto s = connection_->prepare("SELECT max(seq) FROM transcript_events WHERE session_id = ?1");
    s.bind(1, sessionId);
    return s.step() ? integer(s, 0) : std::nullopt;
}

std::int64_t Database::rows(const std::string& sessionId, std::int64_t afterSeq,
                            const std::function<bool(std::int64_t, const Entry&)>& onRow) {
    auto s = connection_->prepare(rowsSql_);
    s.bind(1, sessionId);
    s.bind(2, afterSeq);
    std::int64_t last = afterSeq;
    while (s.step()) {
        const std::int64_t seq = integer(s, 0).value_or(last);
        last = std::max(last, seq);
        Entry entry;
        if (s.columnType(1) == kText) {
            entry = parse(s.getText(1));
        } else if (s.columnType(2) != kBlob || s.columnType(3) != kInteger || s.getInt64(3) <= 0 ||
                   s.getInt64(3) > kMaxEventBytes) {
            entry.status = Entry::Status::Malformed;
        } else {
            std::string decoded(static_cast<std::size_t>(s.getInt64(3)), '\0');
            const auto blob = s.getBlob(2);
            const std::size_t written = ZSTD_decompress(decoded.data(), decoded.size(), blob.data(), blob.size());
            if (ZSTD_isError(written)) {
                entry.status = Entry::Status::Malformed;
            } else {
                decoded.resize(written);
                entry = parse(decoded);
            }
        }
        if (!onRow(seq, entry)) break;
    }
    return last;
}

std::optional<Entry> Database::header(const std::string& sessionId) {
    std::optional<Entry> header;
    rows(sessionId, INT64_MIN, [&](std::int64_t, const Entry& entry) {
        if (entry.status == Entry::Status::Valid && entry.line.kind == pi::Kind::Session) {
            header = entry;
            return false;
        }
        return true;
    });
    return header;
}

void linkParents(const std::string& path, const std::vector<Parser*>& parsers, const std::map<std::string, std::string>& current) {
    std::set<std::string> tracked;
    for (const auto* p : parsers) tracked.insert(p->sessionId());
    for (auto* parser : parsers) {
        std::optional<std::string> parent;
        if (const auto& key = parser->window().spawnedBy; key && !key->empty()) {
            if (auto it = current.find(*key); it != current.end() && it->second != parser->sessionId() && tracked.contains(it->second)) {
                parent = Parser::agentIdOf(path, it->second);
            }
        }
        parser->setParent(parent);
    }
}

std::optional<std::vector<std::unique_ptr<Parser>>> readAll(const std::string& path, Deduper& deduper,
                                                            const std::function<ResponseSink&(Parser&)>& sinkFor,
                                                            const std::function<void(Parser&, std::int64_t)>& afterRow) {
    auto database = Database::open(path);
    if (!database->supported()) return std::nullopt;
    std::vector<std::unique_ptr<Parser>> parsers;
    database->begin();
    for (const auto& window : database->windows()) {
        if (std::any_of(parsers.begin(), parsers.end(), [&](const auto& p) { return p->sessionId() == window.sessionId; })) continue;
        auto parser = std::make_unique<Parser>(path, window, deduper);
        ResponseSink& sink = sinkFor(*parser);
        database->rows(window.sessionId, INT64_MIN, [&](std::int64_t seq, const Entry& entry) {
            if (entry.status == Entry::Status::Malformed) {
                parser->addMalformed(1);
            } else if (entry.status == Entry::Status::Valid) {
                parser->applyEntry(entry, sink);
            }
            if (afterRow) afterRow(*parser, seq);
            return true;
        });
        parsers.push_back(std::move(parser));
    }
    std::vector<Parser*> raw;
    for (auto& p : parsers) raw.push_back(p.get());
    linkParents(path, raw, database->currentSessions());
    database->commit();
    return parsers;
}

Source::Source(std::function<std::vector<std::string>()> discover, Deduper& deduper)
    : discover_(std::move(discover)), deduper_(deduper) {}

Source::~Source() = default;

std::vector<Parser*> Source::parsers() const {
    std::vector<Parser*> out;
    for (const auto& [_, d] : databases_) {
        for (const auto& [__, w] : d.windows) out.push_back(w.parser.get());
    }
    return out;
}

bool Source::unsupported() const {
    return std::any_of(databases_.begin(), databases_.end(), [](const auto& d) { return d.second.unsupported; });
}

std::int64_t Source::malformedLines() const {
    std::int64_t total = removedMalformedLines_;
    for (const auto* p : parsers()) total += p->malformedLines();
    return total;
}

std::vector<std::pair<std::string, sqlite::DatabaseStamp>> Source::discovered() {
    std::vector<std::pair<std::string, sqlite::DatabaseStamp>> found;
    for (const auto& path : discover_()) {
        if (auto stamp = sqlite::stampOf(path)) found.emplace_back(path, *stamp);
    }
    std::stable_sort(found.begin(), found.end(), [](const auto& a, const auto& b) {
        if (a.second.lastWriteNs() != b.second.lastWriteNs()) return a.second.lastWriteNs() < b.second.lastWriteNs();
        return a.first < b.first;
    });
    return found;
}

Update Source::seed(std::int64_t todayStartMs) {
    thresholdMs_ = todayStartMs - kBacklogWindowMs;
    Update update;
    for (const auto& [path, _] : discovered()) {
        Tracked& tracked = databases_[path];
        tracked.path = path;
        read(tracked, update);
    }
    return update;
}

Update Source::refresh(bool discover) {
    Update update;
    if (discover) {
        const auto found = discovered();
        std::set<std::string> present;
        for (const auto& [path, _] : found) present.insert(path);
        for (auto it = databases_.begin(); it != databases_.end();) {
            if (present.contains(it->first)) {
                ++it;
                continue;
            }
            drop(it->second, update);
            it = databases_.erase(it);
            update.changed = true;
        }
        for (const auto& [path, _] : found) {
            if (databases_.contains(path)) continue;
            Tracked& tracked = databases_[path];
            tracked.path = path;
            read(tracked, update);
        }
    }
    for (auto it = databases_.begin(); it != databases_.end();) {
        Tracked& tracked = it->second;
        const auto stamp = sqlite::stampOf(tracked.path);
        if (!stamp) {
            if (discover) {
                ++it;
                continue;
            }
            drop(tracked, update);
            it = databases_.erase(it);
            update.changed = true;
            continue;
        }
        if (stamp != tracked.stamp || (discover && tracked.unreadable)) read(tracked, update);
        ++it;
    }
    return update;
}

void Source::read(Tracked& tracked, Update& update) {
    const auto stamp = sqlite::stampOf(tracked.path);
    if (!stamp) return;
    const auto identity = identityOf(tracked.path);
    if (tracked.identity && identity && *tracked.identity != *identity) {
        // Replaced: every window is read again from its first row (parser state reset, never the dedupe set).
        drop(tracked, update);
        tracked.opened = false;
    }
    if (identity) tracked.identity = identity;
    const bool wasUnsupported = tracked.unsupported, wasUnreadable = tracked.unreadable;
    try {
        auto database = Database::open(tracked.path);
        tracked.unreadable = false;
        tracked.unsupported = !database->supported();
        if (database->supported()) {
            database->begin();
            try {
                readWindows(tracked, *database, update);
            } catch (...) {
                database->commit();
                throw;
            }
            database->commit();
        } else {
            drop(tracked, update);
        }
        tracked.opened = true;
        tracked.stamp = stamp;
        ++update.databasesRead;
        update.bytesRead += stamp->length();
    } catch (const sqlite::Error&) {
        // Busy, locked or unreadable: read again on the next change or rediscovery.
        tracked.unreadable = true;
        tracked.stamp = stamp;
    }
    update.changed |= wasUnsupported != tracked.unsupported || wasUnreadable != tracked.unreadable;
}

void Source::readWindows(Tracked& tracked, Database& database, Update& update) {
    const auto windows = database.windows();
    const auto generations = database.watermarks();
    const bool first = !tracked.opened;
    std::set<std::string> present;
    for (const auto& window : windows) {
        if (!present.insert(window.sessionId).second) continue;
        std::optional<std::string> generation;
        if (auto g = generations.find(window.sessionId); g != generations.end()) generation = g->second;
        auto it = tracked.windows.find(window.sessionId);
        if (it == tracked.windows.end()) {
            WindowState state;
            state.parser = std::make_unique<Parser>(tracked.path, window, deduper_);
            if (observerFactory_) state.parser->setLineObserver(observerFactory_(*state.parser));
            state.generation = generation;
            state.updatedMs = window.updatedMs;
            auto& stored = tracked.windows[window.sessionId] = std::move(state);
            if (first && window.updatedMs.value_or(INT64_MIN) < thresholdMs_) {
                stored.lastSeq = database.maxSeq(window.sessionId).value_or(INT64_MIN);
                stored.needsHeader = true;
                continue;
            }
            readRows(stored, database, update);
            continue;
        }
        WindowState& state = it->second;
        bool rewritten = generation != state.generation;
        const bool runtime = state.parser->runtimeChanged(window);
        state.parser->updateWindow(window);
        if (!rewritten && !runtime && window.updatedMs == state.updatedMs) continue;
        if (!rewritten && !runtime && database.maxSeq(window.sessionId).value_or(INT64_MIN) < state.lastSeq) rewritten = true;
        if (rewritten || runtime) {
            state.parser->reset();
            state.lastSeq = INT64_MIN;
            state.needsHeader = false;
            update.changed = true;
        }
        state.generation = generation;
        state.updatedMs = window.updatedMs;
        readRows(state, database, update);
    }
    std::vector<std::string> gone;
    for (const auto& [id, _] : tracked.windows) {
        if (!present.contains(id)) gone.push_back(id);
    }
    for (const auto& id : gone) removeWindow(tracked, id, update);
    std::vector<Parser*> raw;
    for (auto& [_, w] : tracked.windows) raw.push_back(w.parser.get());
    linkParents(tracked.path, raw, database.currentSessions());
}

void Source::readRows(WindowState& state, Database& database, Update& update) {
    if (state.needsHeader) {
        // An older window read from its end: its header first, as omp does for a file read from an offset (4.7).
        state.needsHeader = false;
        if (auto header = database.header(state.parser->sessionId())) state.parser->applyEntry(*header, update);
    }
    const auto before = state.parser->version();
    state.lastSeq = database.rows(state.parser->sessionId(), state.lastSeq, [&](std::int64_t, const Entry& entry) {
        if (entry.status == Entry::Status::Malformed) {
            state.parser->addMalformed(1);
        } else if (entry.status == Entry::Status::Valid) {
            state.parser->applyEntry(entry, update);
        }
        return true;
    });
    update.changed |= state.parser->version() != before;
}

void Source::removeWindow(Tracked& tracked, const std::string& sessionId, Update& update) {
    auto it = tracked.windows.find(sessionId);
    removedMalformedLines_ += it->second.parser->malformedLines();
    update.removed.push_back(it->second.parser->agentId());
    tracked.windows.erase(it);
    update.changed = true;
}

void Source::drop(Tracked& tracked, Update& update) {
    std::vector<std::string> ids;
    for (const auto& [id, _] : tracked.windows) ids.push_back(id);
    for (const auto& id : ids) removeWindow(tracked, id, update);
}

}  // namespace agentspeed::openclaw
