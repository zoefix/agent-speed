#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/report_values.hpp"
#include "agentspeed/perf.hpp"
#include "agentspeed/traffic_service.hpp"
#include "agentspeed/unpriced.hpp"
#include "agentspeed/work_signals.hpp"

namespace agentspeed {

// METRICS 6.9, 6.10: an agent's performance score with the facts of the score card, and its work signals.
struct AgentPerf {
    perf::Evaluation score;
    work::Signals workSignals;  // not `signals`: a Qt keyword in the app
};

struct AgentRow {
    std::string id;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> project;
    std::optional<std::string> model;
    Phase phase = Phase::Idle;
    double liveOutput = 0;
    double liveTotal = 0;
    int depth = 0;
    bool subagent = false;
    std::optional<std::string> parentId;
    std::optional<std::int64_t> lastLineMs;
    // EXECUTION-MODES: the effort and speed the row shows (mode tokens), nullopt when unknown.
    std::optional<std::string> effort;
    std::optional<std::string> speed;
    // A value only a config file holds, shown in the configured form.
    bool effortConfigured = false, speedConfigured = false;
    // METRICS 6.9: every listed agent.
    std::optional<AgentPerf> perf;
    // METRICS 6.9 Live thinking: this agent's reasoning tokens per second and share.
    double liveThinking = 0;
    double thinkingShare = 0;
};

// 性能 and 思考 of one set of sources (METRICS 6.9 Sets and groups, Live thinking): an orb, the tray, a board.
struct ScoreSet {
    std::optional<std::vector<SourceKind>> sources;  // nullopt: every enabled source
    perf::Highest value;
    std::int64_t scored = 0;   // N: the set's agents with a score in the value's form
    bool provisional = false;  // the supplying agent's score is provisional (3 ≤ n < 10)
    double liveThinking = 0;
    double thinkingShare = 0;
    bool generating = false;                // an agent of the set is generating
};

// A board group's value (METRICS 6.9 Sets and groups): a root and its descendants in the agents list.
struct ScoreGroup {
    std::string root;
    std::vector<std::string> members;
    perf::Highest value;
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
    double liveTotal = 0;
    double liveEstimatedShare = 0;
    bool enabled = true;
    // METRICS 2: detected (its root exists) and the root shown in settings.
    bool available = false;
    std::string path;
    int sessions = 0;
    int activeAgents = 0;
    std::int64_t todayInput = 0;
    std::int64_t todayRequests = 0;
    // METRICS 9.5, 9.6: micro-USD per second, today's known costs and the responses without a price.
    double liveCost = 0;
    double liveCostKnownShare = 1;
    std::int64_t todayCostMicroUsd = 0;
    std::int64_t unpricedRequests = 0;
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
    double liveCost = 0;
    double liveCostKnownShare = 1;
    std::int64_t todayCostMicroUsd = 0;
    std::int64_t todayUnpricedRequests = 0;
    int priceVersion = 0;
    int generating = 0;
    int tools = 0;
    std::vector<AgentRow> agents;
    std::vector<SourceRow> sources;
    // METRICS 6.8 peakToday_A of each requested set (Monitor::setPeakSets), with its live value.
    std::vector<double> peakSets;
    // One per requested set (Monitor::setScoreSets), then the board groups of the agents list.
    std::vector<ScoreSet> scoreSets;
    std::vector<ScoreGroup> scoreGroups;
    // METRICS 10: network traffic (this computer, sources, agents).
    traffic::View traffic;
    // Output tok/s of the last 120 one-second buckets, oldest first; the last one is the live value (METRICS 6.5).
    std::vector<double> history;
    // METRICS 9.10 Live: today's unpriced facts, one part per source with today's responses.
    std::vector<unpriced::Part> unpricedToday;
    // METRICS 11.2, 11.4: what live reporting takes from this tick.
    report::Facts report;
    bool demo = false;
    std::int64_t todayStartMs = 0;
    Warnings warnings;
};

}  // namespace agentspeed
