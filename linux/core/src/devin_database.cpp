#include <sqlite3.h>

#include <algorithm>
#include <cmath>
#include <limits>

#include "agentspeed/devin.hpp"

// METRICS 4.17 Opening and rows: the tables the CLI keeps, read in one short transaction at a time.
namespace agentspeed::devin {

namespace {

constexpr const char* kRowColumns =
    "SELECT row_id, session_id, created_at, CASE WHEN typeof(chat_message) = 'text' AND length(CAST(chat_message AS BLOB)) <= 33554432 "
    "THEN chat_message END FROM message_nodes";

bool hasColumns(const sqlite::Connection& connection, const std::string& table, std::initializer_list<std::string_view> required) {
    auto query = connection.prepare("PRAGMA table_info(" + table + ")");
    std::set<std::string, std::less<>> columns;
    while (query.step()) {
        if (auto name = query.getString(1)) columns.insert(std::move(*name));
    }
    return std::all_of(required.begin(), required.end(), [&](std::string_view c) { return columns.contains(c); });
}

std::optional<std::string> textOf(const sqlite::Statement& query, int column) {
    return query.columnType(column) == SQLITE_TEXT ? query.getString(column) : std::nullopt;
}

// `created_at` (Unix seconds, an integer or a finite real) in milliseconds, truncated and saturated (METRICS 3.6).
std::optional<std::int64_t> timeMsOf(const sqlite::Statement& query, int column) {
    constexpr auto kMax = std::numeric_limits<std::int64_t>::max(), kMin = std::numeric_limits<std::int64_t>::min();
    switch (query.columnType(column)) {
        case SQLITE_INTEGER: {
            const auto seconds = query.getInt64(column);
            if (seconds > kMax / 1000) return kMax;
            if (seconds < kMin / 1000) return kMin;
            return seconds * 1000;
        }
        case SQLITE_FLOAT: {
            const double ms = std::trunc(query.getDouble(column) * 1000);
            if (!std::isfinite(ms)) return std::nullopt;
            if (ms >= 9.223372036854775807e18) return kMax;
            if (ms <= -9.223372036854775808e18) return kMin;
            return static_cast<std::int64_t>(ms);
        }
        default: return std::nullopt;
    }
}

Row rowOf(const sqlite::Statement& query) {
    Row row;
    row.rowId = query.getInt64(0);
    row.sessionId = textOf(query, 1);
    row.timeMs = timeMsOf(query, 2);
    if (query.columnType(3) == SQLITE_TEXT) row.message = Message::parse(query.getText(3));
    return row;
}

}  // namespace

Database Database::open(const std::string& path) {
    if (!isDatabaseFile(path)) throw sqlite::Error(SQLITE_CANTOPEN, "no Devin CLI database");
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    if (!hasColumns(*connection, "sessions", {"id", "working_directory", "model"}) ||
        !hasColumns(*connection, "message_nodes", {"row_id", "session_id", "chat_message", "created_at"})) {
        throw UnsupportedSchema();
    }
    return Database(std::move(connection), path);
}

std::vector<Session> Database::sessions() const {
    std::vector<Session> out;
    auto query = connection_->prepare("SELECT id, working_directory, model FROM sessions");
    while (query.step()) {
        auto id = textOf(query, 0);
        if (!id) continue;
        Session session;
        session.id = std::move(*id);
        if (auto directory = textOf(query, 1); directory && !directory->empty()) session.workingDirectory = std::move(directory);
        session.model = sessionModel(textOf(query, 2));
        out.push_back(std::move(session));
    }
    return out;
}

std::int64_t Database::maxRowId() const { return connection_->scalarInt64("SELECT max(row_id) FROM message_nodes"); }

std::vector<Row> Database::rowsAfter(std::int64_t rowId) const {
    auto query = connection_->prepare(std::string(kRowColumns) + " WHERE row_id > ? ORDER BY row_id");
    query.bind(1, rowId);
    std::vector<Row> rows;
    while (query.step()) rows.push_back(rowOf(query));
    return rows;
}

std::vector<Row> Database::recentRows(std::int64_t thresholdMs) const {
    auto query = connection_->prepare(std::string(kRowColumns) + " ORDER BY row_id DESC");
    std::vector<Row> rows;
    while (query.step()) {
        // Rows are written in time order: the first older row ends the read, a row without a time does not.
        if (const auto time = timeMsOf(query, 2); time && *time < thresholdMs) break;
        rows.push_back(rowOf(query));
    }
    std::reverse(rows.begin(), rows.end());
    return rows;
}

std::vector<Row> Database::rowsOfSessions(const std::set<std::string>& sessions) const {
    std::vector<Row> rows;
    if (sessions.size() > 8) {
        auto scan = connection_->prepare(std::string(kRowColumns) + " ORDER BY row_id");
        while (scan.step()) {
            if (const auto id = textOf(scan, 1); id && sessions.contains(*id)) rows.push_back(rowOf(scan));
        }
        return rows;
    }
    auto query = connection_->prepare(std::string(kRowColumns) + " WHERE session_id = ? ORDER BY row_id");
    for (const auto& session : sessions) {
        query.bind(1, std::string_view(session));
        while (query.step()) rows.push_back(rowOf(query));
        query.reset();
    }
    std::sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) { return a.rowId < b.rowId; });
    return rows;
}

std::map<std::string, RowStats> Database::rowStats() const {
    std::map<std::string, RowStats> out;
    auto query = connection_->prepare("SELECT session_id, count(*), max(row_id) FROM message_nodes GROUP BY session_id");
    while (query.step()) {
        if (auto id = textOf(query, 0)) out[std::move(*id)] = {query.getInt64(1), query.getInt64(2)};
    }
    return out;
}

}  // namespace agentspeed::devin
