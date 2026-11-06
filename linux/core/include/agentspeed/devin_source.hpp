#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/devin.hpp"
#include "agentspeed/session_claims.hpp"

// METRICS 4.17 for the live monitor: every database of Devin's location list, one reader each in list order, every
// session read by one database only.
namespace agentspeed::devin {

// One session of a CLI database as the engine sees it (METRICS 4.17 Agents and phase).
struct Agent {
    std::string id, sessionId, project;
    // The session's working directory (METRICS 10.6).
    std::optional<std::string> cwdPath, model;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs, lastLineMs;
    int malformedRows = 0;
    bool operator==(const Agent&) const = default;
};

// What one read changed.
struct Update {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    // Agent id → state, for agents whose state may have changed.
    std::map<std::string, Agent> agents;
    std::vector<std::string> removed;
    // A startup read (the database is new, replaced or read again): it counts but raises no live event.
    bool seeded = false;
    bool changed() const { return !responses.empty() || !agents.empty() || !removed.empty(); }
};

enum class Status { Missing, Ready, Unsupported, Unreadable };

// The live state of one database: its sessions as agents, read incrementally by `row_id`.
class Reader {
public:
    static constexpr std::int64_t kStartupWindowMs = 15 * 60'000;

    // `claims` is shared with the source's other databases; `position` is this database's place in the list.
    Reader(std::string path, SessionClaims& claims, int position) : path_(std::move(path)), claims_(claims), position_(position) {}

    const std::string& path() const { return path_; }
    Status status() const { return status_; }
    bool exists() const { return status_ != Status::Missing; }
    std::int64_t databaseBytes() const { return stamp_ ? stamp_->length() : 0; }
    int malformedRows() const;
    std::string agentId(const std::string& session) const { return path_ + "#" + session; }
    std::vector<std::string> agentIds() const;
    std::vector<Agent> agents() const;
    std::optional<std::string> cwdOf(const std::string& agentId) const;
    // Sessions with a row today (DESIGN 5).
    int activeSessionCount(std::int64_t todayStartMs) const;
    void release() { claims_.releaseAll(position_); }

    // The startup window when the database is new or was replaced (`seeded`), else the rows above the highest `row_id`
    // seen and the `sessions` table. nullopt when nothing changed.
    std::optional<Update> refresh(std::int64_t todayStartMs, Deduper& deduper);

private:
    struct State {
        std::string project;
        std::optional<std::string> cwdPath, model;
        Phase phase = Phase::Idle;
        std::optional<std::int64_t> phaseRowMs, inFlightStartMs, lastLineMs;
        int malformed = 0;
        bool called = false;
        std::optional<std::string> callModel;

        void apply(const Session& session);
        void call(const std::optional<std::string>& model, const Session& session);
        void note(std::optional<std::int64_t> ms);
        Agent agent(const std::string& id, const std::string& session) const;
    };
    struct Identity {
        std::int64_t device = 0, inode = 0;
        bool operator==(const Identity&) const = default;
    };

    void reset();
    void readSessions(const std::vector<Session>& rows, Update& update);
    void consume(const Row& row, Deduper& deduper, Update& update);

    std::string path_;
    SessionClaims& claims_;
    int position_;
    Status status_ = Status::Missing;
    std::map<std::string, Session> sessions_;
    std::map<std::string, State> states_;
    std::map<std::string, ParsedResponse> emitted_;
    std::optional<sqlite::DatabaseStamp> stamp_;
    std::optional<Identity> identity_;
    std::optional<std::int64_t> cursor_;
    bool needsSeed_ = true;
};

class Source {
public:
    Source(std::vector<Root> roots, Deduper& deduper) : roots_(std::move(roots)), deduper_(deduper) {}

    const std::vector<Root>& roots() const { return roots_; }
    const std::vector<std::unique_ptr<Reader>>& readers() const { return readers_; }
    std::string displayRoot() const;
    // Detected: a database exists.
    bool available() const;
    bool unsupported() const;
    int unreadableCount() const;
    std::int64_t malformedRows() const;
    std::vector<Agent> agents() const;
    std::optional<std::string> cwdOf(const std::string& agentId) const;
    int activeSessionCount(std::int64_t todayStartMs) const;

    // Startup read (METRICS 4.17, 5): every database in list order, the rows of the startup window.
    std::vector<Update> seed(std::int64_t todayStartMs);
    // About once a second: every database whose file or `-wal` changed.
    std::vector<Update> refresh();
    // Every 10 seconds: the roots resolved again. A list of databases that changed (one appeared, vanished or moved)
    // starts every reader over like at startup, so the earliest database keeps its sessions.
    std::vector<Update> rediscover(std::int64_t todayStartMs);

private:
    std::optional<Update> sync(const std::vector<std::string>& databases, bool force);

    std::vector<Root> roots_;
    Deduper& deduper_;
    SessionClaims claims_;
    std::vector<std::unique_ptr<Reader>> readers_;
    std::int64_t todayStartMs_ = 0;
    std::int64_t removedMalformedRows_ = 0;
};

}  // namespace agentspeed::devin
