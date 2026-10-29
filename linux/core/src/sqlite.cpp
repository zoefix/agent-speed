#include "agentspeed/sqlite.hpp"

#include <sqlite3.h>

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

namespace agentspeed::sqlite {

namespace {

bool fileExists(const std::string& path) {
    std::error_code ec;
    return std::filesystem::exists(path, ec);
}

std::string message(sqlite3* db) {
    const char* text = db ? sqlite3_errmsg(db) : nullptr;
    return text ? text : "SQLite error";
}

}  // namespace

Statement::~Statement() {
    if (stmt_) sqlite3_finalize(stmt_);
}

Statement::Statement(Statement&& other) noexcept : db_(other.db_), stmt_(other.stmt_) { other.stmt_ = nullptr; }

Statement& Statement::bind(int index, std::int64_t value) {
    sqlite3_bind_int64(stmt_, index, value);
    return *this;
}

Statement& Statement::bind(int index, std::string_view value) {
    sqlite3_bind_text(stmt_, index, value.data(), static_cast<int>(value.size()), SQLITE_TRANSIENT);
    return *this;
}

bool Statement::step() {
    const int code = sqlite3_step(stmt_);
    if (code == SQLITE_ROW) return true;
    if (code == SQLITE_DONE) return false;
    const std::string text = message(db_);
    sqlite3_reset(stmt_);
    throw Error(code, text);
}

void Statement::reset() {
    sqlite3_reset(stmt_);
    sqlite3_clear_bindings(stmt_);
}

bool Statement::isNull(int column) const { return sqlite3_column_type(stmt_, column) == SQLITE_NULL; }

std::optional<std::string> Statement::getString(int column) const {
    if (isNull(column)) return std::nullopt;
    const auto* text = reinterpret_cast<const char*>(sqlite3_column_text(stmt_, column));
    const int size = sqlite3_column_bytes(stmt_, column);
    return text ? std::string(text, static_cast<std::size_t>(size)) : std::string();
}

std::string_view Statement::getText(int column) const {
    if (isNull(column)) return {};
    const auto* text = reinterpret_cast<const char*>(sqlite3_column_text(stmt_, column));
    const int size = sqlite3_column_bytes(stmt_, column);
    return text ? std::string_view(text, static_cast<std::size_t>(size)) : std::string_view();
}

std::int64_t Statement::getInt64(int column) const { return sqlite3_column_int64(stmt_, column); }

std::optional<std::int64_t> Statement::getNullableInt64(int column) const {
    if (isNull(column)) return std::nullopt;
    return sqlite3_column_int64(stmt_, column);
}

Connection Connection::open(const std::string& path, OpenMode mode, int busyTimeoutMs) {
    int flags = SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_READONLY;
    if (mode == OpenMode::Immutable) flags |= SQLITE_OPEN_URI;
    const std::string name = mode == OpenMode::Immutable ? immutableUri(path) : path;
    sqlite3* db = nullptr;
    const int code = sqlite3_open_v2(name.c_str(), &db, flags, nullptr);
    if (code != SQLITE_OK) {
        const std::string text = db ? message(db) : "unable to open database";
        if (db) sqlite3_close_v2(db);
        throw Error(code, text);
    }
    sqlite3_extended_result_codes(db, 1);
    sqlite3_busy_timeout(db, busyTimeoutMs);
    return Connection(db, path);
}

Connection::~Connection() {
    if (db_) sqlite3_close_v2(db_);
}

Connection::Connection(Connection&& other) noexcept : db_(other.db_), path_(std::move(other.path_)) { other.db_ = nullptr; }

Statement Connection::prepare(std::string_view sql) const {
    sqlite3_stmt* stmt = nullptr;
    const int code = sqlite3_prepare_v2(db_, sql.data(), static_cast<int>(sql.size()), &stmt, nullptr);
    if (code != SQLITE_OK) throw Error(code, message(db_));
    return Statement(db_, stmt);
}

std::int64_t Connection::scalarInt64(std::string_view sql) const {
    Statement statement = prepare(sql);
    return statement.step() ? statement.getInt64(0) : 0;
}

bool Connection::hasTables(std::initializer_list<std::string_view> names) const {
    std::string sql = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN (";
    bool first = true;
    for (auto name : names) {
        sql += first ? "'" : ",'";
        sql += name;
        sql += "'";
        first = false;
    }
    sql += ")";
    return scalarInt64(sql) == static_cast<std::int64_t>(names.size());
}

bool isClosedWalDatabase(const std::string& path) {
    if (fileExists(path + "-wal")) return false;
    std::ifstream in(path, std::ios::binary);
    char header[20];
    if (!in.read(header, sizeof header)) return false;
    return std::memcmp(header, "SQLite format 3\0", 16) == 0 && header[18] == 2 && header[19] == 2;
}

std::string immutableUri(const std::string& path) {
    std::error_code ec;
    std::string full = std::filesystem::absolute(path, ec).string();
    if (ec) full = path;
    std::string uri = "file:";
    if (full.empty() || full[0] != '/') {
        uri += "///";
    } else if (full.rfind("//", 0) == 0) {
        uri += "//";
    }
    for (unsigned char b : full) {
        if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '/' || b == ':' ||
            b == '-' || b == '_' || b == '.' || b == '~') {
            uri += static_cast<char>(b);
        } else {
            char hex[4];
            std::snprintf(hex, sizeof hex, "%%%02X", b);
            uri += hex;
        }
    }
    return uri + "?mode=ro&immutable=1";
}

namespace {

// Opening is lazy: the first query is what actually reads (or fails to read) the database.
Connection verified(Connection connection) {
    connection.scalarInt64("SELECT count(*) FROM sqlite_master");
    return connection;
}

}  // namespace

Connection openReadOnly(const std::string& path, bool& immutable) {
    immutable = isClosedWalDatabase(path);
    if (immutable) return verified(Connection::open(path, OpenMode::Immutable));
    try {
        return verified(Connection::open(path, OpenMode::ReadOnly));
    } catch (const Error&) {
        if (fileExists(path + "-wal")) throw;
        immutable = true;
        return verified(Connection::open(path, OpenMode::Immutable));
    }
}

}  // namespace agentspeed::sqlite
