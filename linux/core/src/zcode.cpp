#include "agentspeed/zcode.hpp"

#include <sqlite3.h>
#include <sys/stat.h>

#include <algorithm>
#include <chrono>
#include <set>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed::zcode {

namespace {

const std::vector<std::string_view> kUsageColumns = {"id", "session_id", "provider_id", "model_id", "status", "started_at",
                                                     "completed_at", "duration_ms", "input_tokens", "output_tokens",
                                                     "reasoning_tokens", "cache_creation_input_tokens",
                                                     "cache_read_input_tokens"};
const std::vector<std::string_view> kSessionColumns = {"id", "parent_id", "directory", "time_created", "time_updated"};

bool hasColumns(const sqlite::Connection& connection, const std::string& table, const std::vector<std::string_view>& required) {
    auto query = connection.prepare("PRAGMA table_info(" + table + ")");
    std::set<std::string, std::less<>> columns;
    while (query.step()) {
        if (auto name = query.getString(1)) columns.insert(*name);
    }
    return std::all_of(required.begin(), required.end(), [&](std::string_view c) { return columns.contains(c); });
}

std::int64_t clampTokens(std::int64_t value) { return value < 0 ? 0 : std::min(value, TokenCounts::kMax); }

std::optional<std::int64_t> valid(std::int64_t value) { return harness::validTimestamp(value); }

}  // namespace

// The speed facts of a row: its exact model, its card provider and the card of the user message it answers.
ExecutionModes speedModes(std::string_view data, const std::optional<std::string>& provider, const std::optional<std::string>& model,
                          std::int64_t startedMs) {
    auto modes = native::zcodeModel(model, ModeEvidence::Selected, ModeScope::Request);
    bool mock = false;
    std::optional<std::int64_t> fallback;
    json::Document document;
    if (!data.empty() && document.parse(data, 64) && document.root()["role"].equals("user")) {
        const auto highspeed = document.root()["metadata"]["highspeed"];
        const auto schema = highspeed["schemaVersion"];
        const auto card = highspeed["cardId"].nonEmptyString();
        if (schema.isNumber() && schema.isIntegerLiteral() && schema.raw() == "1" && card && highspeed["model"].nonEmptyString()) {
            modes = modes.with(ModeFact{ModeKind::Accelerator, "highspeed", "highspeed", ModeEvidence::Selected, std::string("zcode"), ModeScope::Turn});
            mock = card->starts_with("hsc_mock_");
            if (const auto at = highspeed["fallbackAt"]; at.isNumber() && at.isIntegerLiteral()) fallback = at.count();
        }
    }
    if (mock) return modes;
    if (const auto routed = native::zcodeProvider(provider, ModeEvidence::Recorded, ModeScope::Request); !routed.empty()) {
        return modes.merge(routed);
    }
    if (fallback && startedMs >= *fallback) {
        modes = modes.with(ModeFact{ModeKind::Accelerator, "standard", "fallback", ModeEvidence::Recorded, std::string("zcode"), ModeScope::Request});
    }
    return modes;
}

std::optional<ParsedResponse> Row::response(const std::string& databasePath) const {
    if (status == "running" || !completedMs || *completedMs <= 0) return std::nullopt;
    const auto end = *completedMs;
    auto start = startedMs;
    if (!start && durationMs && *durationMs > 0 && *durationMs <= end) start = end - *durationMs;
    ParsedResponse r;
    r.key = "zcode:" + id;
    r.source = SourceKind::ZCode;
    r.agentId = agentId(databasePath);
    r.model = model;
    r.startMs = start;
    r.endMs = end;
    r.tokens = tokens;
    r.provider = provider;
    r.modes = modes;
    return r;
}

Database Database::open(const std::string& path) {
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    if (!hasColumns(*connection, "model_usage", kUsageColumns) || !hasColumns(*connection, "session", kSessionColumns)) {
        throw UnsupportedSchema();
    }
    return Database(std::move(connection));
}

std::vector<Row> Database::read(std::int64_t sinceMs) const {
    const bool hasModes = hasColumns(*connection_, "model_usage", {"parent_user_message_id"}) &&
                          hasColumns(*connection_, "message", {"id", "session_id", "data"});
    const std::string modeColumn = hasModes ? ", CASE WHEN length(CAST(m.data AS BLOB))<=1048576 THEN m.data END" : ", NULL";
    const auto usageColumn = [&](const std::string& name) {
        return hasColumns(*connection_, "model_usage", {name}) ? ",u." + name : std::string(",NULL");
    };
    const std::string modeJoin =
        hasModes ? " LEFT JOIN message m ON m.id=u.parent_user_message_id AND m.session_id=u.session_id " : " ";
    auto rows = connection_->prepare(
        "SELECT u.id,u.session_id,s.parent_id,s.directory,s.time_created,u.model_id,u.provider_id,u.status,"
        "u.started_at,u.completed_at,u.duration_ms,u.input_tokens,u.output_tokens,u.reasoning_tokens,"
        "u.cache_creation_input_tokens,u.cache_read_input_tokens,s.time_updated" +
        modeColumn + usageColumn("variant") + usageColumn("query_source") +
        " FROM model_usage u LEFT JOIN session s ON s.id=u.session_id " + modeJoin +
        " WHERE coalesce(u.completed_at,u.started_at)>=? ORDER BY u.started_at,u.id");
    rows.bind(1, sinceMs);
    std::vector<Row> result;
    while (rows.step()) {
        auto id = rows.getString(0), session = rows.getString(1);
        if (!id || id->empty() || !session || session->empty()) continue;
        Row row;
        row.id = std::move(*id);
        row.sessionId = std::move(*session);
        row.parentId = rows.getString(2);
        row.directory = rows.getString(3);
        row.createdMs = valid(rows.getInt64(4)).value_or(0);
        row.updatedMs = valid(rows.getInt64(16)).value_or(0);
        row.model = rows.getString(5);
        row.provider = rows.getString(6);
        row.status = rows.getString(7).value_or("");
        row.startedMs = valid(rows.getInt64(8));
        row.completedMs = harness::validTimestamp(rows.getNullableInt64(9));
        row.durationMs = rows.getNullableInt64(10);
        const auto input = clampTokens(rows.getInt64(11)), output = clampTokens(rows.getInt64(12));
        row.tokens.input = input;
        row.tokens.cachedInput = std::min(input, clampTokens(rows.getInt64(15)));
        row.tokens.cacheWriteInput = std::min(input, clampTokens(rows.getInt64(14)));
        row.tokens.output = output;
        row.tokens.reasoning = std::min(output, clampTokens(rows.getInt64(13)));
        row.modes = speedModes(rows.getText(17), row.provider, row.model, row.startedMs.value_or(0));
        if (rows.columnType(18) == SQLITE_TEXT) {
            row.modes = row.modes.with(native::effort(rows.getString(18), ModeEvidence::Recorded, ModeScope::Request));
        }
        const auto querySource = rows.getString(19);
        row.auxiliary = querySource == "compact" || querySource == "session_title" || querySource == "goal_completion_verification";
        result.push_back(std::move(row));
    }
    return result;
}

std::vector<Agent> Database::readAgents(std::int64_t sinceMs) const {
    std::vector<Agent> result;
    if (!hasColumns(*connection_, "message", {"id", "session_id", "time_created", "time_updated", "data"})) return result;
    auto rows = connection_->prepare(
        "SELECT m.id,m.session_id,m.time_created,m.time_updated,"
        "CASE WHEN length(CAST(m.data AS BLOB))<=1048576 THEN m.data END,s.parent_id,s.directory,s.time_created,s.time_updated "
        "FROM session s JOIN message m ON m.id=("
        "SELECT id FROM message WHERE session_id=s.id ORDER BY time_created DESC,id DESC LIMIT 1) "
        "WHERE s.time_updated>=?");
    rows.bind(1, sinceMs);
    while (rows.step()) {
        const auto message = rows.getString(0), session = rows.getString(1);
        const auto data = rows.getText(4);
        if (!message || !session || data.empty()) continue;
        json::Document document;
        if (!document.parse(data)) continue;
        const auto root = document.root();
        const auto role = root["role"].nonEmptyString();
        if (role != "assistant" && role != "user") continue;
        const auto timing = root["time"];
        const auto created = harness::timestamp(timing["created"]).value_or(valid(rows.getInt64(2)).value_or(0));
        const auto completed = harness::timestamp(timing["completed"]);
        const auto error = root["error"];
        auto phase = role == "user" || (!completed && (!error.valid() || error.isNull())) ? Phase::Generating : Phase::Idle;
        if (role == "assistant" && hasRunningTool(*message)) phase = Phase::Tools;
        auto project = rows.getString(6);
        if (!project) project = root["path"]["cwd"].nonEmptyString();
        Agent agent;
        agent.id = connection_->path() + "#" + *session;
        agent.sessionId = *session;
        agent.parentSessionId = rows.getString(5);
        agent.createdMs = valid(rows.getInt64(7)).value_or(0);
        if (project) agent.project = path_text::basename(*project);
        agent.model = root["modelId"].nonEmptyString();
        if (!agent.model) agent.model = root["modelID"].nonEmptyString();
        agent.phase = phase;
        if (phase == Phase::Generating) agent.startMs = created;
        agent.lastMs = std::max(valid(rows.getInt64(8)).value_or(0),
                                std::max(valid(rows.getInt64(3)).value_or(0), completed.value_or(created)));
        result.push_back(std::move(agent));
    }
    return result;
}

bool Database::hasRunningTool(const std::string& message) const {
    if (!hasParts_) hasParts_ = hasColumns(*connection_, "part", {"message_id", "data"});
    if (!*hasParts_) return false;
    auto rows = connection_->prepare("SELECT CASE WHEN length(CAST(data AS BLOB))<=1048576 THEN data END FROM part WHERE message_id=?");
    rows.bind(1, std::string_view(message));
    while (rows.step()) {
        const auto data = rows.getText(0);
        if (data.empty()) continue;
        json::Document document;
        if (!document.parse(data)) continue;
        const auto root = document.root();
        const auto status = root["state"]["status"].nonEmptyString();
        if (root["type"].equals("tool") && (status == "pending" || status == "running")) return true;
    }
    return false;
}

std::string Source::databasePath() const { return harness::zcodeDatabase(root_); }

bool Source::available() const {
    struct stat st {};
    const auto path = databasePath();
    return !path.empty() && ::stat(path.c_str(), &st) == 0 && S_ISREG(st.st_mode);
}

std::vector<Agent> Source::agents() const {
    std::vector<Agent> result;
    for (const auto& [id, agent] : agents_) result.push_back(agent);
    return result;
}

int Source::activeSessionCount(std::int64_t todayStartMs) const {
    return static_cast<int>(std::count_if(agents_.begin(), agents_.end(), [&](const auto& entry) {
        return entry.second.lastMs >= todayStartMs && !entry.second.parentSessionId;
    }));
}

bool Source::isWatchedPath(const std::string& path) const {
    const auto database = databasePath();
    return path == database || path == database + "-wal";
}

std::optional<Source::Identity> Source::identityOf(const std::string& path) {
    struct stat st {};
    if (::stat(path.c_str(), &st) != 0) return std::nullopt;
    return Identity{static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino)};
}

Update Source::seed(std::int64_t todayStartMs) {
    sinceMs_ = std::max<std::int64_t>(0, todayStartMs - kBacklogWindowMs);
    auto update = refresh(true);
    lastReadMs_.reset();
    return update;
}

Update Source::refresh(bool force) {
    Update update;
    const auto path = databasePath();
    const auto stamp = sqlite::stampOf(path);
    const auto identity = identityOf(path);
    const bool replaced = path != lastPath_ || (identity_ && identity && *identity_ != *identity);
    if (!stamp || replaced) {
        for (const auto& [id, agent] : agents_) update.removed.push_back(id);
        agents_.clear();
        currentRows_.clear();
        stamp_.reset();
        lastReadMs_.reset();
    }
    lastPath_ = path;
    identity_ = identity;
    if (!stamp || (!force && stamp == stamp_)) return update;

    const auto now = clockMs ? clockMs()
                             : std::chrono::duration_cast<std::chrono::milliseconds>(
                                   std::chrono::steady_clock::now().time_since_epoch())
                                   .count();
    if (!force && lastReadMs_ && now - *lastReadMs_ < 500) return update;
    lastReadMs_ = now;
    try {
        const auto database = Database::open(path);
        const auto rows = database.read(sinceMs_);
        std::vector<std::string> order;
        std::map<std::string, std::vector<const Row*>> groups;
        for (const auto& row : rows) {
            auto& group = groups[row.sessionId];
            if (group.empty()) order.push_back(row.sessionId);
            group.push_back(&row);
        }
        for (const auto& sessionId : order) {
            const auto& group = groups[sessionId];
            const Row* latest = group.front();
            std::int64_t last = 0;
            for (const auto* row : group) {
                if (row->startedMs > latest->startedMs) latest = row;
                last = std::max(last, std::max(row->updatedMs, row->completedMs ? *row->completedMs : row->startedMs.value_or(0)));
            }
            Agent agent;
            agent.id = latest->agentId(path);
            agent.sessionId = latest->sessionId;
            agent.parentSessionId = latest->parentId;
            agent.createdMs = latest->createdMs;
            if (latest->directory) agent.project = path_text::basename(*latest->directory);
            agent.model = latest->model;
            const bool running = latest->status == "running";
            agent.phase = running ? Phase::Generating : Phase::Idle;
            if (running) agent.startMs = latest->startedMs;
            agent.lastMs = last;
            // The latest row that is not a title, compaction or verification call.
            if (const auto known = agents_.find(agent.id); known != agents_.end()) agent.modes = known->second.modes;
            for (const auto* row : group) {
                if (row->auxiliary) continue;
                const std::pair<std::int64_t, std::string> order{row->startedMs.value_or(0), row->id};
                auto& current = currentRows_[agent.id];
                if (!current || *current <= order) {
                    current = order;
                    agent.modes = row->modes;
                }
            }
            if (const auto known = agents_.find(agent.id); known == agents_.end() || known->second != agent) {
                agents_[agent.id] = agent;
                update.agents.push_back(agent);
            }
            for (const auto* row : group) {
                auto response = row->response(path);
                if (!response) continue;
                if (const auto previous = emitted_.find(response->key); previous != emitted_.end() && previous->second == *response) continue;
                std::string owner;
                const bool completion = deduper_.tryClaim(response->key, response->agentId, &owner);
                if (!completion && owner != response->agentId) continue;
                emitted_[response->key] = *response;
                update.responses.emplace_back(*response, completion);
            }
        }
        for (auto agent : database.readAgents(sinceMs_)) {
            if (const auto known = agents_.find(agent.id); known != agents_.end()) {
                if (agent.lastMs < known->second.lastMs) continue;
                if (!agent.model) agent.model = known->second.model;
                agent.modes = known->second.modes;
                if (agent == known->second) continue;
            }
            agents_[agent.id] = agent;
            const auto previous = std::find_if(update.agents.begin(), update.agents.end(), [&](const Agent& a) { return a.id == agent.id; });
            if (previous != update.agents.end()) {
                *previous = agent;
            } else {
                update.agents.push_back(agent);
            }
        }
        unsupported_ = false;
        unreadable_ = false;
        stamp_ = stamp;
    } catch (const UnsupportedSchema&) {
        unsupported_ = true;
        stamp_ = stamp;
    } catch (const sqlite::Error&) {
        unreadable_ = true;
    }
    return update;
}

}  // namespace agentspeed::zcode
