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

// METRICS 4.18: Qoder IDE's chat database `local.db` (read-only), and the roots of Qoder CLI (4.19), whose transcripts
// the Claude parser reads.
namespace agentspeed::qoder {

inline constexpr std::string_view kDatabaseName = "local.db";
inline constexpr std::int64_t kMaxJsonBytes = 1LL << 20;

using Environment = std::function<std::optional<std::string>(const std::string&)>;

// `<data>` of METRICS 4.18 on Linux: `$XDG_CONFIG_HOME` when not empty, else `~/.config`.
std::string dataFolder(const Environment& environment, const std::string& home);
// The automatic entry's databases: the international edition (`Qoder`), then the China edition (`QoderCN`).
std::vector<std::string> automaticDatabases(const Environment& environment, const std::string& home);
// A regular file, never a symbolic link (the folders above it may be links).
bool isDatabaseFile(const std::string& path);
// A user location's database: the first that exists of `local.db`, `cache/db/local.db` (a `SharedClientCache` folder)
// and `SharedClientCache/cache/db/local.db` (the IDE's data folder).
std::optional<std::string> databaseOf(const std::string& folder);
// Where a folder without a database would keep it: under the deepest of those layouts whose folders exist.
std::string expectedDatabaseOf(const std::string& folder);

// One root of the location list (METRICS 2.1): an automatic entry's database, or a user location (a folder) whose
// database is looked up again at every rediscovery.
struct Root {
    std::string path;
    bool isDatabase = false;

    static Root database(std::string path) { return {std::move(path), true}; }
    static Root location(std::string folder) { return {std::move(folder), false}; }
    std::optional<std::string> databaseNow() const;
    // What the root names for the editing rules: the database that exists, else where the folder's layout keeps it.
    std::string locationKey() const;
    // The folder Settings and the source row name: a database's folder, else the location itself.
    std::string displayFolder() const;
    bool operator==(const Root&) const = default;
};

// The databases the roots name now, in list order, each once (resolved).
std::vector<std::string> databases(const std::vector<Root>& roots);
// The first root that names an existing database (a database root shows its folder), else the first root.
std::string displayRoot(const std::vector<Root>& roots);

// METRICS 4.19: `<config>/projects` of both editions: `$QODER_CONFIG_DIR`, else `~/.qoder`; `$QODERCN_CONFIG_DIR`,
// else `~/.qoder-cn`.
std::vector<std::string> cliRoots(const Environment& environment, const std::string& home);
// A Qoder CLI user location (a config folder or its `projects` folder) read as that `projects` folder.
std::string cliProjectsOf(const std::string& location);

// The working folder of `chat_session.project_uri`: a path as it is, or a `file://` URI's percent-decoded path without
// the `/` before a drive letter.
std::optional<std::string> projectPath(const std::optional<std::string>& uri);

enum class Role : std::uint8_t { Other, User, Tool, Assistant };

// `token_info` as token values (METRICS 3.2).
struct Tokens {
    std::int64_t prompt = 0, completion = 0, cached = 0;
    bool operator==(const Tokens&) const = default;
};

// nullopt when the text is not one JSON object.
std::optional<Tokens> tokensOf(std::string_view json);
// `model_info.model_key` trimmed; nullopt when blank, no string or the text is not one JSON object.
std::optional<std::string> modelKeyOf(std::string_view json);
// `chat_record.extra["ai-coding/turn-info"].finishedAt` (Unix ms, finite, truncated).
std::optional<std::int64_t> finishedAtOf(std::string_view json);

// `"qoder:" + chat_message.id`, else `"qoder:" + session id + ":row:" + rowid`.
std::string callKey(const std::string& session, const std::optional<std::string>& messageId, std::int64_t rowId);
// The call of an assistant row whose `token_info` is an object; nullopt when its input and output are both 0.
std::optional<ParsedResponse> response(const Tokens& tokens, const std::string& session, const std::optional<std::string>& messageId,
                                       std::int64_t rowId, std::optional<std::int64_t> startMs, std::int64_t endMs,
                                       const std::optional<std::string>& model, const std::string& agentId);

struct Session {
    std::string id;
    std::optional<std::string> projectPath;
    // `parent_session_id` when it is not blank, as stored: the session is a sub-agent.
    std::optional<std::string> parentId;
    bool operator==(const Session&) const = default;
};

struct Row {
    std::int64_t rowId = 0;
    std::optional<std::string> sessionId;
    // `id` trimmed; nullopt when blank or no text.
    std::optional<std::string> messageId;
    // nullopt when `role` is no string (a malformed row).
    std::optional<Role> role;
    // `gmt_create` in Unix milliseconds; nullopt when it is no finite number.
    std::optional<std::int64_t> timeMs;
    std::optional<Tokens> tokens;
    std::optional<std::string> model, requestId;
};

// The calls of one session's rows in `rowid` order, each timed from the row before it (history).
std::vector<ParsedResponse> responsesOf(const std::vector<Row>& rows, const std::string& session, const std::string& agentId);

struct RowStats {
    std::int64_t count = 0, maxRowId = 0;
};

class UnsupportedSchema : public std::runtime_error {
public:
    UnsupportedSchema() : std::runtime_error("unsupported Qoder IDE database schema") {}
};

// Opened read-only with OpenCode's safeguards (METRICS 4.5): never written, never given a `-wal` or `-shm`.
class Database {
public:
    // Throws UnsupportedSchema, or sqlite::Error when it cannot be read.
    static Database open(const std::string& path);

    const std::string& path() const { return path_; }
    std::string agentId(const std::string& session) const { return path_ + "#" + session; }

    // Runs `body` in one read transaction, so sessions, rows and turn records come from one snapshot.
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
    // The sessions of these ids that `chat_session` lists.
    std::vector<Session> sessions(const std::set<std::string>& ids) const;
    std::int64_t maxRowId() const;
    // Rows above `rowId`, in `rowid` order.
    std::vector<Row> rowsAfter(std::int64_t rowId) const;
    // The startup read (METRICS 4.18 Reading): from the highest `rowid` down while rows are not older than `thresholdMs`
    // (a row without a time does not stop it), in `rowid` order.
    std::vector<Row> recentRows(std::int64_t thresholdMs) const;
    // The time of the session's last row below `rowId`; nullopt when it has none or that row has no time.
    std::optional<std::int64_t> previousRowTime(const std::string& session, std::int64_t rowId) const;
    // Every row of the given sessions in `rowid` order: one query per session, or one scan for many.
    std::vector<Row> rowsOfSessions(const std::set<std::string>& sessions) const;
    // Each session's row count and highest `rowid`; reads no message.
    std::map<std::string, RowStats> rowStats() const;
    // The latest `finishedAt` of each request's `chat_record` rows; empty when the database keeps no turn records.
    std::map<std::string, std::int64_t> finishedAt(const std::set<std::string>& requests) const;

private:
    Database(std::unique_ptr<sqlite::Connection> connection, std::string path, bool requestIds, bool parents, bool records)
        : connection_(std::move(connection)), path_(std::move(path)), requestIds_(requestIds), parents_(parents), records_(records) {}
    std::string rowColumns() const;
    std::string sessionColumns() const;

    std::unique_ptr<sqlite::Connection> connection_;
    std::string path_;
    bool requestIds_ = false, parents_ = false, records_ = false;
};

}  // namespace agentspeed::qoder
