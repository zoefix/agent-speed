#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <tuple>
#include <vector>

#include "agentspeed/quota/estimate/engine.hpp"

// QUOTAS 5.6 Estimate: a snapshot that joins two observations (Claude Code's bridge and usage cache, of this computer
// or of a host) reaches the estimate window by window, each window an observation at its own time (QUOTAS 2).
namespace agentspeed::quota::estimate {

// The sources whose snapshots go through the feed.
bool feedsByWindow(std::string_view source);

class WindowFeed {
public:
    // The snapshots the estimate gets from `snapshot`, oldest first: each window whose time is later than that of the
    // last one given of the same window (source, machine, kind, name, minutes) in this run, grouped by time, with
    // the group's time as `observedMs` and the windows' own times cleared. The same values read again and a value
    // older than the newest one given are skipped.
    std::vector<SnapshotEvent> take(const SnapshotEvent& snapshot);

private:
    using Key = std::tuple<std::string, std::string, Kind, std::string, std::optional<std::int64_t>>;
    std::map<Key, std::int64_t> last_;
};

}  // namespace agentspeed::quota::estimate
