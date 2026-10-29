#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/task_events.hpp"
#include "agentspeed/traffic_service.hpp"
#include "agentspeed/work_signals.hpp"

namespace agentspeed {

// METRICS 6.9–6.11: an agent's current task, its score, predictions and work signals.
struct AgentTask {
    tasks::AgentValue value;
    std::optional<tasks::TaskResult> task;
    std::optional<std::int64_t> taskStartMs;
    std::optional<tasks::Predictions> predictions;
    tasks::Models models;
    work::Signals workSignals;  // not `signals`: a Qt keyword in the app
};

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
    // Scored sources only (METRICS 6.9).
    std::optional<AgentTask> task;
    // METRICS 6.9 Live thinking: this agent's reasoning tokens per second and share.
    double liveThinking = 0;
    double thinkingShare = 0;
};

// 智力 and 思考 of one set of sources (METRICS 6.9 Sets and groups, Live thinking): an orb, the tray, a board.
struct ScoreSet {
    std::optional<std::vector<SourceKind>> sources;  // nullopt: every enabled source
    tasks::Highest value;
    std::int64_t scored = 0;                // N: the set's agents with a score in the value's form
    std::optional<std::string> state;       // the supplying agent's state (proof mark: unverified, pending)
    double liveThinking = 0;
    double thinkingShare = 0;
    bool generating = false;                // an agent of the set is generating
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
    // One per requested set (Monitor::setScoreSets), then the board groups of the agents list.
    std::vector<ScoreSet> scoreSets;
    std::vector<tasks::GroupValue> scoreGroups;
    // METRICS 10: network traffic (this computer, sources, agents).
    traffic::View traffic;
    // Output tok/s of the last 120 one-second buckets, oldest first; the last one is the live value (METRICS 6.5).
    std::vector<double> history;
    bool demo = false;
    std::int64_t todayStartMs = 0;
    Warnings warnings;
};

}  // namespace agentspeed
