#pragma once

#include <cstdint>
#include <vector>

#include "agentspeed/snapshot.hpp"

namespace agentspeed {

// A deterministic, moving snapshot for showing the UI without any agent logs.
Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs);
// The demo agents' responseCompleted events in (fromMs, toMs].
std::vector<Completion> demoCompletions(std::int64_t fromMs, std::int64_t toMs);

}  // namespace agentspeed
