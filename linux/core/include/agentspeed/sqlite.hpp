#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>

struct sqlite3;
struct sqlite3_stmt;

// A thin read-only SQLite layer for the database sources (METRICS 4.5, 5.2, 5.4).
namespace agentspeed::sqlite {

enum class OpenMode {
    // Never writes; without `immutable`, so concurrent writers stay visible.
    ReadOnly,
    // `immutable=1`: reads the database file alone (no WAL, no locks) and creates nothing.
    Immutable,
};

class Error : public std::runtime_error {
public:
    Error(int code, const std::string& message) : std::runtime_error(message), code_(code) {}
    int code() const { return code_; }
    int primaryCode() const { return code_ & 0xFF; }

private:
    int code_;
};

class Statement {
public:
    Statement(sqlite3* db, sqlite3_stmt* stmt) : db_(db), stmt_(stmt) {}
    ~Statement();
    Statement(const Statement&) = delete;
    Statement& operator=(const Statement&) = delete;
    Statement(Statement&& other) noexcept;

    Statement& bind(int index, std::int64_t value);
    Statement& bind(int index, std::string_view value);
    bool step();
    void reset();
    std::optional<std::string> getString(int column) const;
    std::string_view getText(int column) const;
    std::string_view getBlob(int column) const;
    std::int64_t getInt64(int column) const;
    std::optional<std::int64_t> getNullableInt64(int column) const;
    bool isNull(int column) const;

private:
    sqlite3* db_;
    sqlite3_stmt* stmt_;
};

class Connection {
public:
    static Connection open(const std::string& path, OpenMode mode, int busyTimeoutMs = 2000);
    ~Connection();
    Connection(const Connection&) = delete;
    Connection& operator=(const Connection&) = delete;
    Connection(Connection&& other) noexcept;

    Statement prepare(std::string_view sql) const;
    std::int64_t scalarInt64(std::string_view sql) const;
    void execute(const std::string& sql) const;
    bool hasTables(std::initializer_list<std::string_view> names) const;
    const std::string& path() const { return path_; }

private:
    Connection(sqlite3* db, std::string path) : db_(db), path_(std::move(path)) {}
    sqlite3* db_;
    std::string path_;
};

// The header says WAL (bytes 18 and 19 are both 2) and no `-wal` file exists: the writer closed it cleanly, and a
// `mode=ro` open could create `-wal` and `-shm` files next to it.
bool isClosedWalDatabase(const std::string& path);
// Opens without ever creating a file: immutable for a closed WAL database, else read-only with an immutable fallback.
Connection openReadOnly(const std::string& path, bool& immutable);
std::string immutableUri(const std::string& path);

}  // namespace agentspeed::sqlite
