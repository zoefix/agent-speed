#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/sqlite.hpp"

// METRICS 4.17: the Devin CLI's SQLite database `sessions.db`. Devin Desktop's own files are never read.
namespace agentspeed::devin {

inline constexpr std::string_view kDatabaseName = "sessions.db";
inline constexpr std::int64_t kMaxMessageBytes = 32LL << 20;

// `$CHISEL_SESSION_DB` when not empty, else `$XDG_DATA_HOME/devin/cli/sessions.db`, else
// `~/.local/share/devin/cli/sessions.db` (the CLI keeps the XDG layout on macOS and Linux).
std::string automaticDatabase(const std::function<std::optional<std::string>(const std::string&)>& environment,
                              const std::string& home);
// A regular file, never a symbolic link (the folders above it may be links).
bool isDatabaseFile(const std::string& path);
// A user location's database: `sessions.db` in it, else in its `cli` folder; nullopt when neither exists.
std::optional<std::string> databaseOf(const std::string& folder);

// One root of the location list (METRICS 2.1): the automatic entry's database, or a user location (a folder) whose
// database is looked up again at every rediscovery.
struct Root {
    std::string path;
    bool isDatabase = false;

    static Root database(std::string path) { return {std::move(path), true}; }
    static Root location(std::string folder) { return {std::move(folder), false}; }
    // The database the root names now, if it exists.
    std::optional<std::string> databaseNow() const;
    // What the root names for the editing rules: the database that exists, else where a CLI folder keeps it, resolved.
    std::string locationKey() const;
    // The folder Settings and the source row name: a database's folder, else the location itself.
    std::string displayFolder() const;
    bool operator==(const Root&) const = default;
};

// The databases the roots name now, in list order, each once (resolved).
std::vector<std::string> databases(const std::vector<Root>& roots);
// The path the source reports: the first root that names an existing database (a database root shows its folder),
// else the first root.
std::string displayRoot(const std::vector<Root>& roots);

enum class Role : std::uint8_t { Other, User, Tool, Assistant };

// `metadata.metrics`: token values (METRICS 3.2) and finite, truncated times (3.6).
struct Metrics {
    std::int64_t input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
    std::optional<std::int64_t> totalMs, ttftMs;
    bool operator==(const Metrics&) const = default;
};

// The parts of one `message_nodes.chat_message` that METRICS 4.17 reads; never its content.
struct Message {
    Role role = Role::Other;
    bool hasToolCalls = false, finishedWithToolCalls = false;
    std::optional<std::string> requestId, model;
    std::optional<Metrics> metrics;

    // The phase the row sets; nullopt for other roles.
    std::optional<Phase> phase() const;
    // nullopt when the text is not exactly one JSON object (a malformed row; NaN included).
    static std::optional<Message> parse(std::string_view json);
};

std::string callKey(const std::string& session, const std::optional<std::string>& requestId, std::int64_t rowId);
// `sessions.model` trimmed, unless blank or `adaptive` (a routing mode, not a model).
std::optional<std::string> sessionModel(const std::optional<std::string>& raw);
// The call of an assistant row with metrics; nullopt when it has none or no tokens.
std::optional<ParsedResponse> response(const Message& message, const std::string& session, std::int64_t rowId, std::int64_t endMs,
                                       const std::optional<std::string>& sessionModel, const std::string& agentId);

struct Session {
    std::string id;
    std::optional<std::string> workingDirectory, model;
    bool operator==(const Session&) const = default;
};

struct Row {
    std::int64_t rowId = 0;
    std::optional<std::string> sessionId;
    // `created_at` in Unix milliseconds; nullopt when it is no finite number.
    std::optional<std::int64_t> timeMs;
    // nullopt for a malformed `chat_message`.
    std::optional<Message> message;
};

struct RowStats {
    std::int64_t count = 0, maxRowId = 0;
};

class UnsupportedSchema : public std::runtime_error {
public:
    UnsupportedSchema() : std::runtime_error("unsupported Devin CLI database schema") {}
};

// Opened read-only with OpenCode's safeguards (METRICS 4.5): never written, never given a `-wal` or `-shm`.
class Database {
public:
    // Throws UnsupportedSchema, or sqlite::Error when it cannot be read.
    static Database open(const std::string& path);

    const std::string& path() const { return path_; }
    std::string agentId(const std::string& session) const { return path_ + "#" + session; }

    // Runs `body` in one read transaction, so the sessions and the rows come from one snapshot.
    template <typename Body>
    auto snapshot(Body body) const -> decltype(body()) {
        connection_->execute("BEGIN");
        struct End {
            const sqlite::Connection& connection;
            ~End() {
                try {
                    connection.execute("COMMIT");
                } catch (const sqlite::Error&) {
                }
            }
        } end{*connection_};
        return body();
    }

    std::vector<Session> sessions() const;
    std::int64_t maxRowId() const;
    // Rows above `rowId`, in `row_id` order.
    std::vector<Row> rowsAfter(std::int64_t rowId) const;
    // The startup read (METRICS 4.17 Reading): from the highest `row_id` down while rows are not older than
    // `thresholdMs` (a row without a time does not stop it), in `row_id` order.
    std::vector<Row> recentRows(std::int64_t thresholdMs) const;
    // Every row of the given sessions in `row_id` order: one query per session, or one scan for many.
    std::vector<Row> rowsOfSessions(const std::set<std::string>& sessions) const;
    // Each session's row count and highest `row_id`; reads no message.
    std::map<std::string, RowStats> rowStats() const;

private:
    Database(std::unique_ptr<sqlite::Connection> connection, std::string path)
        : connection_(std::move(connection)), path_(std::move(path)) {}

    std::unique_ptr<sqlite::Connection> connection_;
    std::string path_;
};

}  // namespace agentspeed::devin
