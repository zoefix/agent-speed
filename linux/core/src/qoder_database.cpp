#include <sqlite3.h>

#include <algorithm>
#include <cmath>
#include <limits>

#include "agentspeed/qoder.hpp"
#include "agentspeed/text.hpp"

// METRICS 4.18 Opening and rows: the tables of the IDE's agent core, read in one short transaction at a time.
namespace agentspeed::qoder {

namespace {

std::set<std::string, std::less<>> columnsOf(const sqlite::Connection& connection, const std::string& table) {
    auto query = connection.prepare("PRAGMA table_info(" + table + ")");
    std::set<std::string, std::less<>> columns;
    while (query.step()) {
        if (auto name = query.getString(1)) columns.insert(std::move(*name));
    }
    return columns;
}

bool hasAll(const std::set<std::string, std::less<>>& columns, std::initializer_list<std::string_view> required) {
    return std::all_of(required.begin(), required.end(), [&](std::string_view c) { return columns.contains(c); });
}

// The column when it is text of at most 1 MiB, else NULL.
std::string bounded(const std::string& column) {
    return "CASE WHEN typeof(" + column + ") = 'text' AND length(CAST(" + column + " AS BLOB)) <= " + std::to_string(kMaxJsonBytes) +
           " THEN " + column + " END";
}

std::optional<std::string> textOf(const sqlite::Statement& query, int column) {
    return query.columnType(column) == SQLITE_TEXT ? query.getString(column) : std::nullopt;
}

std::optional<std::string> trimmedTextOf(const sqlite::Statement& query, int column) {
    auto value = textOf(query, column);
    if (!value) return std::nullopt;
    auto trimmed = text::strip(*value);
    if (trimmed.empty()) return std::nullopt;
    return trimmed;
}

// `gmt_create` (Unix ms, an integer or a finite real), truncated and saturated (METRICS 3.6).
std::optional<std::int64_t> timeMsOf(const sqlite::Statement& query, int column) {
    switch (query.columnType(column)) {
        case SQLITE_INTEGER: return query.getInt64(column);
        case SQLITE_FLOAT: {
            const double ms = std::trunc(query.getDouble(column));
            if (!std::isfinite(ms)) return std::nullopt;
            if (ms >= 9.223372036854775807e18) return std::numeric_limits<std::int64_t>::max();
            if (ms <= -9.223372036854775808e18) return std::numeric_limits<std::int64_t>::min();
            return static_cast<std::int64_t>(ms);
        }
        default: return std::nullopt;
    }
}

std::optional<Role> roleOf(const sqlite::Statement& query, int column) {
    if (query.columnType(column) != SQLITE_TEXT) return std::nullopt;
    const auto text = query.getText(column);
    if (text == "assistant") return Role::Assistant;
    if (text == "user") return Role::User;
    if (text == "tool") return Role::Tool;
    return Role::Other;
}

Row rowOf(const sqlite::Statement& query) {
    Row row;
    row.rowId = query.getInt64(0);
    row.messageId = trimmedTextOf(query, 1);
    row.sessionId = textOf(query, 2);
    row.role = roleOf(query, 3);
    row.timeMs = timeMsOf(query, 4);
    if (query.columnType(5) == SQLITE_TEXT) row.tokens = tokensOf(query.getText(5));
    if (query.columnType(6) == SQLITE_TEXT) row.model = modelKeyOf(query.getText(6));
    row.requestId = textOf(query, 7);
    return row;
}

Session sessionOf(const sqlite::Statement& query) {
    Session session;
    session.id = *query.getString(0);
    session.projectPath = projectPath(textOf(query, 1));
    if (auto parent = textOf(query, 2); parent && !text::strip(*parent).empty()) session.parentId = std::move(parent);
    return session;
}

}  // namespace

Database Database::open(const std::string& path) {
    if (!isDatabaseFile(path)) throw sqlite::Error(SQLITE_CANTOPEN, "no Qoder IDE database");
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    const auto message = columnsOf(*connection, "chat_message");
    const auto session = columnsOf(*connection, "chat_session");
    if (!hasAll(message, {"id", "session_id", "role", "token_info", "model_info", "gmt_create"}) ||
        !hasAll(session, {"session_id", "project_uri"})) {
        throw UnsupportedSchema();
    }
    const auto record = columnsOf(*connection, "chat_record");
    Database database(std::move(connection), path, message.contains("request_id"), session.contains("parent_session_id"),
                      hasAll(record, {"request_id", "extra"}));
    try {
        // A table without `rowid` (WITHOUT ROWID) cannot be read in row order.
        database.connection_->prepare(database.rowColumns() + " LIMIT 0");
    } catch (const sqlite::Error& error) {
        if (error.primaryCode() == SQLITE_ERROR) throw UnsupportedSchema();
        throw;
    }
    return database;
}

std::string Database::rowColumns() const {
    return "SELECT rowid, id, session_id, role, gmt_create, " + bounded("token_info") + ", " + bounded("model_info") + ", " +
           (requestIds_ ? "request_id" : "NULL") + " FROM chat_message";
}

std::string Database::sessionColumns() const {
    return std::string("SELECT session_id, project_uri, ") + (parents_ ? "parent_session_id" : "NULL") + " FROM chat_session";
}

std::vector<Session> Database::sessions() const {
    std::vector<Session> out;
    auto query = connection_->prepare(sessionColumns());
    while (query.step()) {
        if (query.columnType(0) == SQLITE_TEXT) out.push_back(sessionOf(query));
    }
    return out;
}

std::vector<Session> Database::sessions(const std::set<std::string>& ids) const {
    std::vector<Session> out;
    auto query = connection_->prepare(sessionColumns() + " WHERE session_id = ?");
    for (const auto& id : ids) {
        query.bind(1, std::string_view(id));
        while (query.step()) {
            if (query.columnType(0) == SQLITE_TEXT && query.getText(0) == id) out.push_back(sessionOf(query));
        }
        query.reset();
    }
    return out;
}

std::int64_t Database::maxRowId() const { return connection_->scalarInt64("SELECT max(rowid) FROM chat_message"); }

std::vector<Row> Database::rowsAfter(std::int64_t rowId) const {
    auto query = connection_->prepare(rowColumns() + " WHERE rowid > ? ORDER BY rowid");
    query.bind(1, rowId);
    std::vector<Row> rows;
    while (query.step()) rows.push_back(rowOf(query));
    return rows;
}

std::vector<Row> Database::recentRows(std::int64_t thresholdMs) const {
    auto query = connection_->prepare(rowColumns() + " ORDER BY rowid DESC");
    std::vector<Row> rows;
    while (query.step()) {
        // Rows are written in time order: the first older row ends the read, a row without a time does not.
        if (const auto time = timeMsOf(query, 4); time && *time < thresholdMs) break;
        rows.push_back(rowOf(query));
    }
    std::reverse(rows.begin(), rows.end());
    return rows;
}

std::optional<std::int64_t> Database::previousRowTime(const std::string& session, std::int64_t rowId) const {
    auto query = connection_->prepare("SELECT gmt_create FROM chat_message WHERE session_id = ? AND rowid < ? ORDER BY rowid DESC LIMIT 1");
    query.bind(1, std::string_view(session)).bind(2, rowId);
    return query.step() ? timeMsOf(query, 0) : std::nullopt;
}

std::vector<Row> Database::rowsOfSessions(const std::set<std::string>& sessions) const {
    std::vector<Row> rows;
    if (sessions.size() > 8) {
        auto scan = connection_->prepare(rowColumns() + " ORDER BY rowid");
        while (scan.step()) {
            if (const auto id = textOf(scan, 2); id && sessions.contains(*id)) rows.push_back(rowOf(scan));
        }
        return rows;
    }
    auto query = connection_->prepare(rowColumns() + " WHERE session_id = ? ORDER BY rowid");
    for (const auto& session : sessions) {
        query.bind(1, std::string_view(session));
        while (query.step()) {
            // `=` also matches a number stored as text; only the session's own text id is its row.
            if (const auto id = textOf(query, 2); id && *id == session) rows.push_back(rowOf(query));
        }
        query.reset();
    }
    std::sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) { return a.rowId < b.rowId; });
    return rows;
}

std::map<std::string, RowStats> Database::rowStats() const {
    std::map<std::string, RowStats> out;
    auto query = connection_->prepare("SELECT session_id, count(*), max(rowid) FROM chat_message GROUP BY session_id");
    while (query.step()) {
        if (auto id = textOf(query, 0)) {
            auto& stats = out[std::move(*id)];
            stats.count += query.getInt64(1);
            stats.maxRowId = std::max(stats.maxRowId, query.getInt64(2));
        }
    }
    return out;
}

std::map<std::string, std::int64_t> Database::finishedAt(const std::set<std::string>& requests) const {
    std::map<std::string, std::int64_t> out;
    if (!records_ || requests.empty()) return out;
    auto query = connection_->prepare("SELECT request_id, " + bounded("extra") + " FROM chat_record WHERE request_id = ?");
    for (const auto& request : requests) {
        query.bind(1, std::string_view(request));
        while (query.step()) {
            if (query.columnType(0) != SQLITE_TEXT || query.getText(0) != request || query.columnType(1) != SQLITE_TEXT) continue;
            const auto at = finishedAtOf(query.getText(1));
            if (!at) continue;
            const auto [it, added] = out.emplace(request, *at);
            if (!added) it->second = std::max(it->second, *at);
        }
        query.reset();
    }
    return out;
}

}  // namespace agentspeed::qoder
