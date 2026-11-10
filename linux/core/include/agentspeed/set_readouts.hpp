#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/engine.hpp"
#include "agentspeed/model.hpp"

// METRICS 6.9 *Readouts of a set* (spec/tools/perf_score.py set_first_latency, recent_speed, running_models): a
// `character` orb's 首字 row and its models card. They never drive heat, titles, peaks or the character choice.
namespace agentspeed::perf {

// The set's first-content latency: the median in ms (nullopt with fewer than 3 values) and how many values it had.
struct SetLatency {
    std::optional<double> ms;
    std::int64_t n = 0;
    bool operator==(const SetLatency&) const = default;
};

// One model of the set's running agents: its exact id, the highest recentSpeed of its agents (nullopt when none has
// one), how many agents run it, and the source of the agent that supplied the speed (else its first agent), for the
// vendor.
struct RunningModel {
    std::string model;
    std::optional<double> speed;
    std::int64_t agents = 0;
    SourceKind source = SourceKind::Codex;
    // The speed as shown: rounded half away from zero.
    std::optional<std::int64_t> shown() const;
    bool operator==(const RunningModel&) const = default;
};

// An agent of the set as its readouts see it: its sample's first-content latencies (Evaluation::latencies), its model,
// its phase after the staleness rule (6.7 step 1) and its own recentSpeed (6.2, no fallback).
struct ReadoutAgent {
    std::vector<std::int64_t> latencies;
    std::optional<std::string> model;
    Phase phase = Phase::Idle;
    std::optional<double> recentSpeed;
    SourceKind source = SourceKind::Codex;
};

struct SetReadouts {
    SetLatency firstLatency;
    std::vector<RunningModel> models;
    bool operator==(const SetReadouts&) const = default;
};

// 6.2 recentSpeed: Σ output / Σ duration over the up to 3 newest measured responses (newest endMs first, ties by key,
// ordinal) that end within the last 10 minutes; nullopt without one.
std::optional<double> recentSpeed(const std::vector<const engine::Response*>& own, std::int64_t now);

// The median over the union of the agents' latencies (an even count: the mean of the middle two).
SetLatency setFirstLatency(const std::vector<ReadoutAgent>& agents);

// Agents that are not idle and report a non-empty model, grouped by that exact string; each group's highest speed,
// fastest first, groups without a speed last, ties by model (ordinal, UTF-16 code units).
std::vector<RunningModel> runningModels(const std::vector<ReadoutAgent>& agents);

SetReadouts readoutsOf(const std::vector<ReadoutAgent>& agents);

}  // namespace agentspeed::perf
