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
};

struct SourceRow {
    SourceKind source = SourceKind::Codex;
    std::int64_t todayOutput = 0;
    double liveOutput = 0;
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
    bool demo = false;
};

}  // namespace agentspeed
