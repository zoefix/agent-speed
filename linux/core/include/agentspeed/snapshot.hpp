#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"

namespace agentspeed {

struct AgentRow {
    std::string id;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> project;
    std::optional<std::string> model;
    Phase phase = Phase::Idle;
    double liveOutput = 0;
    int depth = 0;
    bool subagent = false;
    std::optional<std::string> parentId;
    std::optional<std::int64_t> lastLineMs;
    // EXECUTION-MODES: the effort and speed the row shows (mode tokens), nullopt when unknown.
    std::optional<std::string> effort;
    std::optional<std::string> speed;
};

// A responseCompleted event (METRICS 6.4): the first emission of a response key while running.
struct Completion {
    SourceKind source = SourceKind::Codex;
    std::int64_t output = 0;
    std::optional<double> speed;
};

struct SourceRow {
    SourceKind source = SourceKind::Codex;
    std::int64_t todayOutput = 0;
    double liveOutput = 0;
    bool enabled = true;
    // METRICS 2: detected (its root exists) and the root shown in settings.
    bool available = false;
    std::string path;
    int sessions = 0;
    int activeAgents = 0;
    std::int64_t todayInput = 0;
    std::int64_t todayRequests = 0;
};

struct Warnings {
    bool partialHistory = false;
    int unreadableFiles = 0;
    std::int64_t malformedLines = 0;
    std::int64_t unfinalized = 0;
    std::vector<SourceKind> unsupported;
};

// Everything the UI shows, computed in one engine tick (METRICS 6).
struct Snapshot {
    std::int64_t nowMs = 0;
    double liveOutput = 0;
    double liveTotal = 0;
    double estimatedShare = 0;
    std::int64_t todayOutput = 0;
    std::int64_t todayInput = 0;
    std::int64_t todayRequests = 0;
    std::optional<double> todayAverage;
    double peakToday = 0;
    int generating = 0;
    int tools = 0;
    std::vector<AgentRow> agents;
    std::vector<SourceRow> sources;
    // Output tok/s of the last 120 one-second buckets, oldest first; the last one is the live value (METRICS 6.5).
    std::vector<double> history;
    bool demo = false;
    std::int64_t todayStartMs = 0;
    Warnings warnings;
};

}  // namespace agentspeed
