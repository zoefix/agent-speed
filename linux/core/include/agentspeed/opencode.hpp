#pragma once

#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/model.hpp"

namespace agentspeed::sqlite {
class Connection;
}

// METRICS 4.5 and 5.2: OpenCode keeps everything in one SQLite database; one agent per session.
namespace agentspeed::opencode {

enum class Status { Missing, Unreadable, Unsupported, Ready };
enum class RowKind { Other, Assistant, Compaction, Idle };

struct SessionInfo {
    std::string id;
    std::optional<std::string> parentId;
    std::optional<std::string> directory;
    std::int64_t createdMs = 0;
    std::int64_t updatedMs = 0;
    bool operator==(const SessionInfo&) const = default;
};

struct Data {
    std::optional<std::int64_t> created, streamed, completed;
    std::optional<std::string> model;
    bool finishedWithToolCalls = false;
    bool hasTokens = false;
    TokenCounts tokens;
    std::optional<std::string> provider, serviceTier, variant;
    bool operator==(const Data&) const = default;
};

struct Row {
    std::string id;
    std::string sessionId;
    RowKind kind = RowKind::Other;
    std::int64_t seq = 0;
    std::int64_t timeCreated = 0;
    std::int64_t timeUpdated = 0;
    std::optional<Data> data;  // nullopt: malformed JSON

    bool unfinished() const;
    bool operator==(const Row&) const = default;
};

struct AgentState {
    std::string id;
    std::string sessionId;
    std::optional<std::string> parentSessionId;
    std::int64_t createdMs = 0;
    std::optional<std::string> project;
    std::optional<std::string> model;
    bool isSubagent = false;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs;
    std::optional<std::int64_t> lastLineMs;
    std::int64_t malformedRows = 0;
    ExecutionModes modes;
};

std::optional<Data> readData(std::string_view json);
std::string agentId(const std::string& sessionId);
std::optional<ParsedResponse> response(const Row& row, const SessionInfo& session);
ExecutionModes modesOf(const Data& data);
AgentState replay(const SessionInfo& session, const std::vector<Row>& rows, std::optional<std::string> model = std::nullopt,
                  const ExecutionModes& modes = {});

class Database {
public:
    static constexpr const char* kFileName = "opencode.db";
    // Throws OpenError.
    static std::unique_ptr<Database> open(const std::string& path);
    static std::string databasePath(const std::string& root);
    ~Database();

    bool immutable() const { return immutable_; }
    std::vector<SessionInfo> sessions(std::optional<std::int64_t> updatedSince = std::nullopt);
    std::vector<Row> sessionRows(const std::string& sessionId, std::optional<std::int64_t> updatedSince = std::nullopt,
                                 const std::function<bool(const std::string&, std::int64_t)>& skip = {});
    std::vector<Row> rowsCreatedSince(std::int64_t sinceMs, const std::function<bool(const std::string&, std::int64_t)>& skip = {});
    std::vector<Row> rowsByIds(const std::vector<std::string>& ids,
                               const std::function<bool(const std::string&, std::int64_t)>& skip = {});
    std::optional<std::int64_t> maxCreatedMs();

private:
    Database(std::unique_ptr<sqlite::Connection> connection, bool immutable);
    std::unique_ptr<sqlite::Connection> connection_;
    bool immutable_;
};

struct OpenError {
    Status status;
    std::string message;
};

struct Update {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    std::map<std::string, AgentState> agents;
    std::vector<std::string> removed;
    bool reset = false;
    bool empty() const { return responses.empty() && agents.empty() && removed.empty() && !reset; }
};

// The live side (METRICS 5.2): seeds today's sessions, then reads new and recently updated rows on each change.
class Source {
public:
    static constexpr std::int64_t kCursorOverlapMs = 2000;
    static constexpr std::int64_t kRecentRowMs = 60'000;
    static constexpr std::size_t kMaxWatchedRows = 256;
    static constexpr std::int64_t kStaleInFlightMs = 30 * 60'000;
    static constexpr std::int64_t kCompactAfterMs = 2 * 3'600'000;
    static constexpr std::int64_t kStartupLookbackMs = 15 * 60'000;

    Source(std::string root, Deduper& deduper);
    ~Source();

    const std::string& databasePath() const { return databasePath_; }
    Status status() const { return status_; }
    bool databaseExists() const { return databaseExists_; }
    std::vector<AgentState> agents() const;

    bool refreshAvailability();
    Update seed(std::int64_t todayStartMs);
    Update refresh();
    std::optional<Update> refreshIfChanged();
    Update rediscover(std::int64_t nowMs, std::int64_t todayStartMs);

private:
    struct Tracked {
        SessionInfo info;
        std::map<std::string, Row> rows;
        std::map<std::string, ParsedResponse> emitted;
        std::optional<AgentState> state;
        std::optional<std::int64_t> rowWatermark;
        std::optional<std::string> model;
        ExecutionModes modes;
    };
    struct Stamp {
        std::int64_t size = 0, mtimeNs = 0;
        bool operator==(const Stamp&) const = default;
    };

    static std::optional<Stamp> stampOf(const std::string& path);
    void apply(std::vector<Row> rows, const std::string& sessionId, Update& update);
    AgentState replayTracked(Tracked& tracked);
    bool isKnown(const std::string& id, std::int64_t updated) const;
    void watch(const Row& row);
    void pruneWatchedRows();
    void release(Tracked& tracked);
    void close(Status status);
    Update drop();
    void dropStaleImmutableConnection();

    std::string root_;
    std::string databasePath_;
    std::string walPath_;
    Deduper& deduper_;
    std::map<std::string, Tracked> sessions_;
    std::map<std::string, std::string> rowSessions_;
    std::map<std::string, std::int64_t> watchedRows_;
    std::unique_ptr<Database> database_;
    std::optional<Stamp> identity_, databaseStamp_, walStamp_;
    std::optional<std::int64_t> createdCursor_, sessionCursor_;
    std::int64_t newestRowUpdate_ = 0;
    bool needsSeed_ = false;
    Status status_ = Status::Missing;
    bool databaseExists_ = false;
};

}  // namespace agentspeed::opencode
