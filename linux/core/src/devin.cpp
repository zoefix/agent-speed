#include "agentspeed/devin.hpp"

#include <algorithm>
#include <fstream>
#include <set>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::devin {

namespace {

std::int64_t milliseconds(std::int64_t seconds) {
    if (seconds > INT64_MAX / 1000) return INT64_MAX;
    if (seconds < INT64_MIN / 1000) return INT64_MIN;
    return seconds * 1000;
}

bool hasColumns(const sqlite::Connection& connection, const std::string& table, const std::vector<std::string_view>& required) {
    auto query = connection.prepare("PRAGMA table_info(" + table + ")");
    std::set<std::string, std::less<>> columns;
    while (query.step()) {
        if (auto name = query.getString(1)) columns.insert(*name);
    }
    return std::all_of(required.begin(), required.end(), [&](std::string_view column) { return columns.contains(column); });
}

std::optional<std::string> trimmed(const std::optional<std::string>& value) {
    if (!value) return std::nullopt;
    auto result = text::strip(*value);
    return result.empty() ? std::nullopt : std::optional<std::string>(result);
}

std::optional<std::string> sessionModel(const std::optional<std::string>& value) {
    auto model = trimmed(value);
    if (!model || fsx::equalsNoCase(*model, "adaptive")) return std::nullopt;
    return model;
}

std::optional<std::string> responseModel(const json::Value& metadata, const Session& session) {
    if (auto model = trimmed(metadata["generation_model"].string())) return model;
    return sessionModel(session.model);
}

json::Value at(const json::Value& root, const std::vector<std::string_view>& path) {
    auto value = root;
    for (const auto part : path) value = value[part];
    return value;
}

json::Value legacyUsage(const json::Value& notification) {
    for (const auto& path : {std::vector<std::string_view>{"content", "metadata", "metrics"},
                             {"metadata", "metrics"}, {"metrics"}, {"content", "metadata"}, {"metadata"}}) {
        auto value = notification;
        bool found = true;
        for (const auto part : path) {
            value = value[part];
            if (!value.valid()) { found = false; break; }
        }
        if (found) return value;
    }
    return {};
}

std::optional<std::int64_t> timestamp(const json::Value& notification) {
    for (const auto& path : {std::vector<std::string_view>{"content", "metadata", "created_at"},
                             {"metadata", "created_at"}, {"created_at"}, {"timestamp"}}) {
        const auto value = at(notification, path);
        if (value.isString()) {
            if (auto stamp = parseTimestamp(*value.string())) return stamp;
        }
    }
    return std::nullopt;
}

std::optional<std::string> model(const json::Value& notification) {
    for (const auto& path : {std::vector<std::string_view>{"content", "metadata", "generation_model"},
                             {"metadata", "generation_model"}, {"_meta", "cognition.ai/model"}}) {
        if (auto value = trimmed(at(notification, path).string()); value && !fsx::equalsNoCase(*value, "adaptive")) return value;
    }
    return std::nullopt;
}

bool canonicalUsage(const json::Value& meta) {
    return meta.isObject() && (at(meta, {"cognition.ai/inputTokens"}).valid() || at(meta, {"cognition.ai/cachedReadTokens"}).valid() ||
                               at(meta, {"cognition.ai/cachedWriteTokens"}).valid() || at(meta, {"cognition.ai/outputTokens"}).valid());
}

std::string stampOf(const std::optional<std::string>& path) {
    if (!path) return "-";
    const auto stamp = sqlite::stampOf(*path);
    if (!stamp) return "-";
    return std::to_string(stamp->databaseLength) + ":" + std::to_string(stamp->databaseWriteNs) + ":" +
           std::to_string(stamp->walLength) + ":" + std::to_string(stamp->walWriteNs);
}

}  // namespace

bool Location::exists() const {
    if (databasePath && fsx::isRegularFile(*databasePath)) return true;
    return std::any_of(desktopFolders.begin(), desktopFolders.end(), [](const std::string& folder) { return fsx::isDirectory(folder); });
}

Location automaticLocation(const Environment& environment, const std::string& home) {
    const auto xdg = environment("XDG_DATA_HOME");
    const auto base = xdg && !xdg->empty() ? *xdg : home + "/.local/share";
    return {base + "/devin", base + "/devin/cli/sessions.db",
            {home + "/.config/Devin/User/acp-events", home + "/.config/devin/User/acp-events"}};
}

Location resolveLocation(const std::optional<std::string>& setting, const Environment& environment, const std::string& home) {
    if (!setting) return automaticLocation(environment, home);
    const auto path = paths::normalizeRoot(setting);
    if (path.ends_with(".db")) return {path, path, {}};
    if (fsx::equalsNoCase(path_text::basename(path), "acp-events")) return {path, std::nullopt, {path}};
    const auto direct = path + "/sessions.db";
    return {path, fsx::isRegularFile(direct) ? direct : path + "/cli/sessions.db",
            {path + (fsx::equalsNoCase(path_text::basename(path), "User") ? "/acp-events" : "/User/acp-events")}};
}

std::vector<std::string> desktopFiles(const Location& location) {
    std::vector<std::string> files;
    for (const auto& folder : location.desktopFolders) {
        for (const auto& entry : fsx::list(folder)) {
            if (entry.name.ends_with(".ndjson") && !entry.name.starts_with('.') && entry.stat.regular && !entry.stat.link) {
                files.push_back(entry.path);
            }
        }
    }
    std::sort(files.begin(), files.end());
    return files;
}

Database Database::open(const std::string& path) {
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    if (!hasColumns(*connection, "sessions", {"id", "working_directory", "model", "last_activity_at"}) ||
        !hasColumns(*connection, "message_nodes", {"row_id", "session_id", "chat_message", "created_at"})) {
        throw UnsupportedSchema();
    }
    return Database(std::move(connection), hasColumns(*connection, "sessions", {"title"}));
}

std::vector<Session> Database::sessions() const {
    auto rows = connection_->prepare(std::string("SELECT id,working_directory,model,") + (hasTitle_ ? "title" : "NULL") +
                                     ",last_activity_at FROM sessions");
    std::vector<Session> result;
    while (rows.step()) {
        if (auto id = rows.getString(0)) {
            result.push_back({*id, rows.getString(1), rows.getString(2), rows.getString(3), milliseconds(rows.getInt64(4))});
        }
    }
    return result;
}

std::vector<MessageRow> Database::messages(std::int64_t afterRowId, std::int64_t sinceMs) const {
    auto rows = connection_->prepare(
        "SELECT row_id,session_id,created_at,CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.role') ELSE 'invalid' END,"
        "CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.metadata') END "
        "FROM message_nodes WHERE row_id>?1 AND created_at>=?2 ORDER BY row_id");
    rows.bind(1, afterRowId).bind(2, sinceMs <= 0 ? 0 : sinceMs / 1000);
    std::vector<MessageRow> result;
    while (rows.step()) {
        if (auto session = rows.getString(1)) {
            result.push_back({rows.getInt64(0), milliseconds(rows.getInt64(2)), *session, rows.getString(3), std::string(rows.getText(4))});
        }
    }
    return result;
}

TokenCounts cliTokens(const json::Value& usage) {
    const auto cached = usage["cache_read_tokens"].count(), write = usage["cache_creation_tokens"].count();
    return {TokenCounts::saturatingAdd(usage["input_tokens"].count(), TokenCounts::saturatingAdd(cached, write)), cached, write,
            usage["output_tokens"].count(), 0, 0};
}

std::optional<std::int64_t> duration(const json::Value& usage) {
    const auto value = usage["total_time_ms"].finiteNumber();
    if (!value || *value <= 0) return std::nullopt;
    if (*value >= static_cast<double>(INT64_MAX)) return INT64_MAX;
    return static_cast<std::int64_t>(*value);
}

int Source::malformedLines() const {
    int result = 0;
    for (const auto& [_, agent] : agents_) result += agent.malformedLines;
    return result;
}

int Source::activeSessionCount(std::int64_t todayStartMs) const {
    auto count = static_cast<int>(std::count_if(sessions_.begin(), sessions_.end(), [&](const auto& entry) {
        return entry.second.lastActivityMs >= todayStartMs;
    }));
    count += static_cast<int>(std::count_if(agents_.begin(), agents_.end(), [&](const auto& entry) {
        return entry.first.ends_with(".ndjson") && entry.second.lastMs && *entry.second.lastMs >= todayStartMs;
    }));
    const auto duplicates = static_cast<int>(std::count_if(agents_.begin(), agents_.end(), [&](const auto& entry) {
        const auto& agent = entry.second;
        if (!agent.title || !agent.lastMs || *agent.lastMs < todayStartMs) return false;
        std::vector<const Session*> matches;
        for (const auto& [_, session] : sessions_) {
            if (trimmed(session.title) == agent.title) matches.push_back(&session);
        }
        return matches.size() == 1 && matches.front()->lastActivityMs >= todayStartMs;
    }));
    return std::max(0, count - duplicates);
}

bool Source::isWatchedPath(const std::string& path) const {
    if (location_.databasePath && (path == *location_.databasePath || path == *location_.databasePath + "-wal")) return true;
    return std::any_of(location_.desktopFolders.begin(), location_.desktopFolders.end(), [&](const std::string& folder) {
        return path.starts_with(folder + "/");
    });
}

std::vector<std::string> Source::watchDirectories() const {
    std::vector<std::string> result;
    for (const auto& folder : location_.desktopFolders) {
        if (fsx::isDirectory(folder)) result.push_back(folder);
    }
    if (location_.databasePath && fsx::isRegularFile(*location_.databasePath)) result.push_back(fsx::parentOf(*location_.databasePath));
    return result;
}

Update Source::seed(std::int64_t todayStartMs) {
    sinceMs_ = std::max<std::int64_t>(0, todayStartMs - kBacklogWindowMs);
    lastRowId_ = 0;
    needsDatabase_ = true;
    return refresh(true);
}

Update Source::refresh(bool force) {
    Update update;
    readDatabase(force, update);
    readDesktop(update);
    for (auto& [id, agent] : agents_) {
        if (!agent.title) continue;
        std::vector<const Session*> matches;
        for (const auto& [_, session] : sessions_) {
            if (trimmed(session.title) == agent.title) matches.push_back(&session);
        }
        if (matches.size() != 1) continue;
        if (!agent.project && matches.front()->workingDirectory) agent.project = path_text::basename(*matches.front()->workingDirectory);
        const auto model = sessionModel(matches.front()->model);
        if (!agent.model) agent.model = model;
        if (!model) continue;
        for (auto& [_, response] : emitted_) {
            if (response.agentId == id && !response.model) response.model = model;
        }
        for (auto& [response, _] : update.responses) {
            if (response.agentId == id && !response.model) response.model = model;
        }
    }
    for (const auto& [id, agent] : agents_) {
        if (!duplicate(agent)) continue;
        duplicates_.insert(id);
        update.removed.push_back(id);
        removeResponses(id, update);
    }
    std::map<std::string, std::size_t> indexes;
    std::vector<std::pair<ParsedResponse, bool>> latest;
    for (const auto& item : update.responses) {
        const auto [it, inserted] = indexes.emplace(item.first.key, latest.size());
        if (!inserted)
            latest[it->second] = {item.first, latest[it->second].second || item.second};
        else
            latest.push_back(item);
    }
    update.responses = std::move(latest);
    for (const auto& [id, agent] : agents_) {
        if (!duplicates_.contains(id) && agent.lastMs) update.agents.push_back(agent);
    }
    stamp_ = currentStamp();
    return update;
}

void Source::readDatabase(bool force, Update& update) {
    if (!location_.databasePath) return;
    const auto& path = *location_.databasePath;
    if (!fsx::isRegularFile(path)) {
        for (auto it = agents_.begin(); it != agents_.end();) {
            if (!it->first.starts_with(path + "#")) { ++it; continue; }
            update.removed.push_back(it->first);
            removeResponses(it->first, update);
            it = agents_.erase(it);
        }
        sessions_.clear();
        usedCliSessions_.clear();
        duplicates_.clear();
        needsDatabase_ = true;
        return;
    }
    if (const auto current = fsx::statOf(path); current && databaseIdentity_ && *databaseIdentity_ != std::pair{current->device, current->inode}) {
        for (auto it = agents_.begin(); it != agents_.end();) {
            if (!it->first.starts_with(path + "#")) { ++it; continue; }
            update.removed.push_back(it->first);
            removeResponses(it->first, update);
            it = agents_.erase(it);
        }
        sessions_.clear();
        usedCliSessions_.clear();
        duplicates_.clear();
        lastRowId_ = 0;
        needsDatabase_ = true;
    }
    if (const auto current = fsx::statOf(path); current) databaseIdentity_ = {current->device, current->inode};
    const auto stamp = stampOf(path);
    if (!force && !needsDatabase_ && stamp == databaseStamp_) return;
    try {
        auto database = Database::open(path);
        for (const auto& session : database.sessions()) sessions_[session.id] = session;
        for (const auto& row : database.messages(lastRowId_, sinceMs_)) {
            lastRowId_ = std::max(lastRowId_, row.rowId);
            const auto found = sessions_.find(row.sessionId);
            if (found == sessions_.end()) continue;
            const auto& session = found->second;
            const auto id = path + "#" + row.sessionId;
            auto agent = agents_[id];
            agent.id = id;
            if (!agent.project && session.workingDirectory) agent.project = path_text::basename(*session.workingDirectory);
            if (!agent.model) agent.model = sessionModel(session.model);
            agent.lastMs = std::max(agent.lastMs.value_or(0), row.createdMs);
            agent.phase = Phase::Idle;
            agent.startMs.reset();
            if (row.role && *row.role == "invalid") {
                ++agent.malformedLines;
                agents_[id] = agent;
                continue;
            }
            json::Document document;
            if (!document.parse(row.metadata) || !document.root().isObject()) {
                if (row.role && (*row.role == "assistant" || *row.role == "user")) ++agent.malformedLines;
                agents_[id] = agent;
                continue;
            }
            const auto metadata = document.root();
            if (row.role && *row.role == "user") {
                agent.phase = Phase::Generating;
                agent.startMs = row.createdMs;
                agents_[id] = agent;
                continue;
            }
            if (!row.role || *row.role != "assistant") {
                agents_[id] = agent;
                continue;
            }
            agent.phase = metadata["finish_reason"].equals("tool_calls") ? Phase::Tools : Phase::Idle;
            if (const auto usage = metadata["metrics"]; usage.isObject()) {
                const auto tokens = cliTokens(usage);
                if (tokens.input > 0 || tokens.output > 0) {
                    agent.model = responseModel(metadata, session);
                    const auto request = trimmed(metadata["request_id"].string());
                    const auto identity = request ? "request:" + *request : "row:" + std::to_string(row.rowId);
                    ParsedResponse response;
                    response.key = "devin:" + row.sessionId + ":" + identity;
                    response.source = SourceKind::Devin;
                    response.agentId = id;
                    response.model = agent.model;
                    if (const auto length = duration(usage)) response.startMs = row.createdMs - *length;
                    response.endMs = row.createdMs;
                    response.tokens = tokens;
                    emit(response, row.sessionId, update);
                }
            }
            agents_[id] = agent;
        }
        unsupported_ = unreadable_ = false;
        needsDatabase_ = false;
        databaseStamp_ = stamp;
    } catch (const UnsupportedSchema&) {
        unsupported_ = true;
    } catch (const sqlite::Error&) {
        unreadable_ = true;
    }
}

void Source::readDesktop(Update& update) {
    const auto files = desktopFiles(location_);
    for (auto it = agents_.begin(); it != agents_.end();) {
        if (!it->first.ends_with(".ndjson") || std::find(files.begin(), files.end(), it->first) != files.end()) { ++it; continue; }
        update.removed.push_back(it->first);
        removeResponses(it->first, update);
        duplicates_.erase(it->first);
        it = agents_.erase(it);
    }
    for (const auto& path : files) {
        try {
            auto agent = readDesktopFile(path, update);
            agents_[path] = std::move(agent);
        } catch (const std::ios_base::failure&) {
        }
    }
}

Agent Source::readDesktopFile(const std::string& path, Update& update) {
    Agent agent;
    agent.id = path;
    const auto stem = path_text::basename(path).substr(0, path_text::basename(path).size() - 7);
    std::int64_t input = 0, cached = 0, write = 0, output = 0;
    std::optional<std::string> aggregateModel;
    std::ifstream in(path, std::ios::binary);
    in.exceptions(std::ios::badbit);
    if (!in) throw std::ios_base::failure("cannot read Devin ACP file");
    std::string line;
    int lineNumber = 0;
    while (std::getline(in, line)) {
        ++lineNumber;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        json::Document document;
        if (!document.parse(line)) { ++agent.malformedLines; continue; }
        const auto notification = document.root()["notification"];
        if (!notification.isObject()) continue;
        if (notification["sessionUpdate"].equals("session_info_update")) {
            if (auto candidate = trimmed(notification["title"].string())) agent.title = candidate;
            continue;
        }
        if (const auto stamp = timestamp(notification)) agent.lastMs = std::max(agent.lastMs.value_or(0), *stamp);
        const auto eventModel = model(notification);
        const auto meta = notification["_meta"];
        if (notification["sessionUpdate"].equals("usage_update") && canonicalUsage(meta)) {
            if (at(meta, {"cognition.ai/inputTokens"}).valid()) input = at(meta, {"cognition.ai/inputTokens"}).count();
            if (at(meta, {"cognition.ai/cachedReadTokens"}).valid()) cached = at(meta, {"cognition.ai/cachedReadTokens"}).count();
            if (at(meta, {"cognition.ai/cachedWriteTokens"}).valid()) write = at(meta, {"cognition.ai/cachedWriteTokens"}).count();
            output = TokenCounts::saturatingAdd(output, at(meta, {"cognition.ai/outputTokens"}).count());
            if (!aggregateModel) aggregateModel = eventModel;
            continue;
        }
        const auto usage = legacyUsage(notification);
        if (!usage.isObject()) continue;
        const auto tokens = cliTokens(usage);
        if ((tokens.input == 0 && tokens.output == 0) || !timestamp(notification)) continue;
        agent.model = eventModel ? eventModel : agent.model;
        ParsedResponse response;
        response.key = "devin-desktop:" + stem + ":line:" + std::to_string(lineNumber);
        response.source = SourceKind::Devin;
        response.agentId = path;
        response.model = eventModel;
        if (const auto length = duration(usage)) response.startMs = *timestamp(notification) - *length;
        response.endMs = *timestamp(notification);
        response.tokens = tokens;
        emit(response, std::nullopt, update);
    }
    if (input + write > 0 || output > 0) {
        agent.model = aggregateModel;
        ParsedResponse response;
        response.key = "devin-desktop:" + stem + ":usage";
        response.source = SourceKind::Devin;
        response.agentId = path;
        response.model = aggregateModel;
        response.endMs = agent.lastMs.value_or(0);
        response.tokens = {TokenCounts::saturatingAdd(input, write), cached, write, output, 0, 0};
        emit(response, std::nullopt, update);
    }
    return agent;
}

void Source::emit(const ParsedResponse& response, const std::optional<std::string>& cliSession, Update& update) {
    const auto current = agents_.find(response.agentId);
    if (current != agents_.end() && duplicate(current->second)) return;
    if (const auto previous = emitted_.find(response.key); previous != emitted_.end() && previous->second == response) return;
    std::string owner;
    const bool claimed = deduper_.tryClaim(response.key, response.agentId, &owner);
    if (!claimed && owner != response.agentId) return;
    const bool completion = !emitted_.contains(response.key);
    emitted_[response.key] = response;
    if (cliSession) usedCliSessions_.insert(*cliSession);
    update.responses.emplace_back(response, claimed && completion);
}

bool Source::duplicate(const Agent& agent) const {
    if (!agent.title) return false;
    std::vector<const Session*> matches;
    for (const auto& [_, session] : sessions_) {
        if (trimmed(session.title) == agent.title) matches.push_back(&session);
    }
    return matches.size() == 1 && usedCliSessions_.contains(matches.front()->id);
}

void Source::removeResponses(const std::string& agentId, Update& update) {
    for (auto it = emitted_.begin(); it != emitted_.end();) {
        if (it->second.agentId != agentId) { ++it; continue; }
        update.removedResponses.push_back(it->first);
        it = emitted_.erase(it);
    }
}

std::string Source::currentStamp() const {
    auto result = stampOf(location_.databasePath);
    for (const auto& path : desktopFiles(location_)) result += "|" + stampOf(path);
    return result;
}

}  // namespace agentspeed::devin
