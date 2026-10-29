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

}  // namespace agentspeed::work
