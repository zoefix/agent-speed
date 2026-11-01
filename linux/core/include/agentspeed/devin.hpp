#pragma once

#include <map>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/sqlite.hpp"

namespace agentspeed::devin {

struct Location {
    std::string displayPath;
    std::optional<std::string> databasePath;
    std::vector<std::string> desktopFolders;
    bool exists() const;
};

Location automaticLocation(const Environment& environment, const std::string& home);
Location resolveLocation(const std::optional<std::string>& setting, const Environment& environment, const std::string& home);
std::vector<std::string> desktopFiles(const Location& location);

struct Session {
    std::string id;
    std::optional<std::string> workingDirectory, model, title;
    std::int64_t lastActivityMs = 0;
};

struct MessageRow {
    std::int64_t rowId = 0, createdMs = 0;
    std::string sessionId;
    std::optional<std::string> role;
    std::string metadata;
};

class UnsupportedSchema : public std::runtime_error {
public:
    UnsupportedSchema() : std::runtime_error("unsupported Devin database schema") {}
};

class Database {
public:
    static Database open(const std::string& path);
    std::vector<Session> sessions() const;
    std::vector<MessageRow> messages(std::int64_t afterRowId, std::int64_t sinceMs) const;

private:
    explicit Database(std::unique_ptr<sqlite::Connection> connection, bool hasTitle)
        : connection_(std::move(connection)), hasTitle_(hasTitle) {}
    std::unique_ptr<sqlite::Connection> connection_;
    bool hasTitle_ = false;
};

struct Agent {
    std::string id;
    std::optional<std::string> project, model, title;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> startMs, lastMs;
    int malformedLines = 0;
    bool operator==(const Agent&) const = default;
};

struct Update {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    std::vector<Agent> agents;
    std::vector<std::string> removed, removedResponses;
    bool changed() const { return !responses.empty() || !agents.empty() || !removed.empty() || !removedResponses.empty(); }
};

class Source {
public:
    static constexpr std::int64_t kBacklogWindowMs = 15 * 60'000;
    Source(Location location, Deduper& deduper) : location_(std::move(location)), deduper_(deduper) {}

    bool available() const { return location_.exists(); }
    bool unsupported() const { return unsupported_; }
    bool unreadable() const { return unreadable_; }
    int malformedLines() const;
    int activeSessionCount(std::int64_t todayStartMs) const;
    bool isWatchedPath(const std::string& path) const;
    std::vector<std::string> watchDirectories() const;

    Update seed(std::int64_t todayStartMs);
    Update refreshIfChanged() { return currentStamp() == stamp_ && !needsDatabase_ ? Update{} : refresh(); }
    Update refresh(bool force = false);

private:
    void readDatabase(bool force, Update& update);
    void readDesktop(Update& update);
    Agent readDesktopFile(const std::string& path, Update& update);
    void emit(const ParsedResponse& response, const std::optional<std::string>& cliSession, Update& update);
    bool duplicate(const Agent& agent) const;
    void removeResponses(const std::string& agentId, Update& update);
    std::string currentStamp() const;

    Location location_;
    Deduper& deduper_;
    std::map<std::string, Session> sessions_;
    std::map<std::string, Agent> agents_;
    std::map<std::string, ParsedResponse> emitted_;
    std::set<std::string> usedCliSessions_, duplicates_;
    std::int64_t lastRowId_ = 0, sinceMs_ = 0;
    std::optional<std::string> stamp_, databaseStamp_;
    std::optional<std::pair<std::int64_t, std::int64_t>> databaseIdentity_;
    bool needsDatabase_ = true, unsupported_ = false, unreadable_ = false;
};

TokenCounts cliTokens(const json::Value& usage);
std::optional<std::int64_t> duration(const json::Value& usage);

}  // namespace agentspeed::devin
