#pragma once

#include <cstdint>

#include "agentspeed/snapshot.hpp"

namespace agentspeed {

// A deterministic, moving snapshot for showing the UI without any agent logs.
Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs);

}  // namespace agentspeed
