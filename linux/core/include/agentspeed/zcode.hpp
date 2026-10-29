#pragma once

#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"
#include "agentspeed/sqlite.hpp"

// METRICS 4.10: ZCode's SQLite database (`model_usage` rows per request, `session` and `message` for agents).
namespace agentspeed::zcode {

ExecutionModes modesOf(std::string_view data, const std::optional<std::string>& provider);

struct Row {
    std::string id, sessionId;
    std::optional<std::string> parentId, directory;
    std::int64_t createdMs = 0, updatedMs = 0;
    std::optional<std::string> model, provider;
    std::string status;
    std::optional<std::int64_t> startedMs, completedMs, durationMs;
    TokenCounts tokens;
    ExecutionModes modes;

    std::string agentId(const std::string& databasePath) const { return databasePath + "#" + sessionId; }
    std::optional<ParsedResponse> response(const std::string& databasePath) const;
};

struct Agent {
    std::string id, sessionId;
    std::optional<std::string> parentSessionId;
    std::int64_t createdMs = 0;
    std::optional<std::string> project, model;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> startMs;
    std::int64_t lastMs = 0;
    ExecutionModes modes;
    bool modeContext = false;
    bool operator==(const Agent&) const = default;
};

class UnsupportedSchema : public std::runtime_error {
public:
    UnsupportedSchema() : std::runtime_error("unsupported ZCode database schema") {}
};

class Database {
public:
    // Throws UnsupportedSchema, or sqlite::Error when it cannot be read.
    static Database open(const std::string& path);
    std::vector<Row> read(std::int64_t sinceMs = 0) const;
    std::vector<Agent> readAgents(std::int64_t sinceMs) const;

private:
    explicit Database(std::unique_ptr<sqlite::Connection> connection) : connection_(std::move(connection)) {}
    bool hasRunningTool(const std::string& message) const;

    std::unique_ptr<sqlite::Connection> connection_;
    mutable std::optional<bool> hasParts_;
};

struct Update {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    std::vector<Agent> agents;
    std::vector<std::string> removed;
    bool changed() const { return !responses.empty() || !agents.empty() || !removed.empty(); }
};

class Source {
public:
    static constexpr std::int64_t kBacklogWindowMs = 15 * 60'000;

    Source(std::string root, Deduper& deduper) : root_(std::move(root)), deduper_(deduper) {}

    std::string databasePath() const;
    bool available() const;
    bool unsupported() const { return unsupported_; }
    bool unreadable() const { return unreadable_; }
    std::vector<Agent> agents() const;
    int activeSessionCount(std::int64_t todayStartMs) const;
    bool isWatchedPath(const std::string& path) const;

    Update seed(std::int64_t todayStartMs);
    Update refresh(bool force = false);

    // Monotonic milliseconds; replaceable in tests.
    std::function<std::int64_t()> clockMs;

private:
    struct Identity {
        std::int64_t device = 0, inode = 0;
        bool operator==(const Identity&) const = default;
    };
    static std::optional<Identity> identityOf(const std::string& path);

    std::string root_;
    Deduper& deduper_;
    std::map<std::string, ParsedResponse> emitted_;
    std::map<std::string, Agent> agents_;
    std::optional<sqlite::DatabaseStamp> stamp_;
    std::optional<Identity> identity_;
    std::optional<std::string> lastPath_;
    std::int64_t sinceMs_ = 0;
    std::optional<std::int64_t> lastReadMs_;
    bool unsupported_ = false, unreadable_ = false;
};

}  // namespace agentspeed::zcode
