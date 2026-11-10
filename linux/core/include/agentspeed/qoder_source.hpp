#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/qoder.hpp"
#include "agentspeed/session_claims.hpp"

// METRICS 4.18 for the live monitor: every database of Qoder IDE's location list, one reader each in list order, every
// session read by one database only.
namespace agentspeed::qoder {

// One chat session of a database as the engine sees it (METRICS 4.18 Agents and phase).
struct Agent {
    std::string id, sessionId, project;
    // The session's working folder (METRICS 10.6).
    std::optional<std::string> cwdPath, model;
    // `<database path>#<parent_session_id>` of a sub-agent session; the monitor links it when that agent is tracked.
    std::optional<std::string> parentAgentId;
    bool isSubagent = false;
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

// The live state of one database: its chat sessions as agents, read incrementally by `rowid`.
class Reader {
public:
    static constexpr std::int64_t kStartupWindowMs = 15 * 60'000;
    // A turn record is looked for while its assistant row is this recent (later, the agent shows idle anyway).
    static constexpr std::int64_t kAwaitingWindowMs = 15 * 60'000;

    // `claims` is shared with the source's other databases; `position` is this database's place in the list.
    Reader(std::string path, SessionClaims& claims, int position) : path_(std::move(path)), claims_(claims), position_(position) {}

    const std::string& path() const { return path_; }
    Status status() const { return status_; }
    bool exists() const { return status_ != Status::Missing; }
    int malformedRows() const;
    std::string agentId(const std::string& session) const { return path_ + "#" + session; }
    std::vector<std::string> agentIds() const;
    std::vector<Agent> agents() const;
    std::optional<std::string> cwdOf(const std::string& agentId) const;
    // Sessions with a row today, sub-agent sessions included (DESIGN 5).
    int activeSessionCount(std::int64_t todayStartMs) const;
    void release() { claims_.releaseAll(position_); }

    // The startup window when the database is new or was replaced (`seeded`), else the rows above the highest `rowid`
    // seen, the sessions of new rows and the turn records of waiting agents. nullopt when nothing changed.
    std::optional<Update> refresh(std::int64_t todayStartMs, Deduper& deduper);

private:
    // An assistant row that set `tools` and is still its session's latest row: its turn record may finish it.
    struct Awaiting {
        std::string request;
        std::int64_t rowMs = 0;
    };
    struct State {
        std::string project;
        std::optional<std::string> cwdPath, model, parentAgentId;
        bool isSubagent = false;
        Phase phase = Phase::Idle;
        // The time of the row that last set the phase, and of the latest row read (a call's start).
        std::optional<std::int64_t> phaseRowMs, previousRowMs, inFlightStartMs, lastLineMs;
        std::optional<Awaiting> awaiting;
        int malformed = 0;

        void note(std::optional<std::int64_t> ms);
        Agent agent(const std::string& id, const std::string& session) const;
    };
    struct Read {
        std::vector<Row> rows;
        std::int64_t top = 0;
        // The time of the row just below the first row read of each session read for the first time.
        std::map<std::string, std::optional<std::int64_t>> previous;
        std::map<std::string, std::int64_t> finished;
    };
    struct Identity {
        std::int64_t device = 0, inode = 0;
        bool operator==(const Identity&) const = default;
    };

    void reset();
    Read readSnapshot(const Database& database, bool seeding, std::int64_t todayStartMs);
    void claimSessions(const std::vector<Session>& listed);
    State freshState(const Session& session) const;
    void consume(const Row& row, const Read& batch, Deduper& deduper, Update& update);
    void finishAwaited(const std::map<std::string, std::int64_t>& finished, Update& update);

    std::string path_;
    SessionClaims& claims_;
    int position_;
    Status status_ = Status::Missing;
    std::map<std::string, Session> sessions_;
    // Sessions another database of the list reads.
    std::set<std::string> foreign_;
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

    // Startup read (METRICS 4.18, 5): every database in list order, the rows of the startup window.
    std::vector<Update> seed(std::int64_t todayStartMs);
    // About once a second: every database whose file or `-wal` changed.
    std::vector<Update> refresh();
    // Every 10 seconds: the roots resolved again. A list of databases that changed starts every reader over like at
    // startup, so the earliest database keeps its sessions.
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

}  // namespace agentspeed::qoder
