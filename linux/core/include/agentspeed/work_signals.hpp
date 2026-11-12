#pragma once

#include <cstdint>
#include <deque>
#include <optional>

#include "agentspeed/actions.hpp"

// Work signals (METRICS 6.10, spec/tools/work_signals.py): the looping, repeating, novelty and recovery read from an
// agent's actions.
namespace agentspeed::work {

struct Signals {
    std::optional<std::int64_t> looping, repeating;
    std::optional<double> novelty;
    std::int64_t failed = 0, recovered = 0;
};
// Over the actions that are not seeded.
Signals signalsOf(const std::deque<Action>& actions, std::int64_t now);
// The result time of the newest tool result when it was read live and failed right after the agent's previous result
// failed too (CHARACTERS 5, Behavior, `trouble`): results in the order they were read, a later call winning a tie.
std::optional<std::int64_t> repeatFailure(const std::deque<Action>& actions);

}  // namespace agentspeed::work
