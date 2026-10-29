#pragma once

#include <cstdint>
#include <optional>
#include <vector>

#include "agentspeed/snapshot.hpp"

namespace agentspeed {

// A deterministic, moving snapshot for showing the UI without any agent logs, with the 性能 and 思考 of each requested
// source set (nullopt: every source), as Monitor::setScoreSets gives them.
Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs,
                      const std::vector<std::optional<std::vector<SourceKind>>>& sets = {std::nullopt});
// The demo agents' responseCompleted events in (fromMs, toMs].
std::vector<Completion> demoCompletions(std::int64_t fromMs, std::int64_t toMs);

}  // namespace agentspeed
