#pragma once

#include <algorithm>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/snapshot.hpp"

// Orbs and boards that track a set of hosts (DESIGN 13): METRICS 6.8 evaluated over the (host, source) cells whose host
// and source are both selected. Without hosts (nullopt: every host) everything is its source-only form.
namespace agentspeed::hosts {

inline constexpr const char* kLocal = "local";

// A set of sources on a set of hosts: the score and peak sets a monitor computes (header-only, so the base library
// can use it).
struct Subset {
    std::optional<std::vector<SourceKind>> sources;  // nullopt: every enabled source
    std::optional<std::vector<std::string>> hosts;   // nullopt: every host

    bool containsSource(SourceKind source) const {
        return !sources || std::find(sources->begin(), sources->end(), source) != sources->end();
    }
    bool containsHost(const std::string& host) const { return !hosts || std::find(hosts->begin(), hosts->end(), host) != hosts->end(); }
    bool contains(SourceKind source, const std::string& host) const { return containsSource(source) && containsHost(host); }
    bool operator==(const Subset&) const = default;
};

// The snapshot as seen from `hosts`: every source row summed over the selected hosts' cells, the agents of those hosts
// (a parent outside them cleared) and the totals of those rows. Without remote hosts only this computer exists.
Snapshot restricted(const Snapshot& snapshot, const std::vector<std::string>& hosts);

// No selected host can be seen: this computer is not selected and no selected remote host is connected.
bool isOffline(const Snapshot& snapshot, const std::vector<std::string>& hosts);

// Whether a surface tracking `sources` on `hosts` receives a completion: its own sources read on its own hosts.
bool accepts(const Completion& completion, const std::optional<std::vector<SourceKind>>& sources,
             const std::optional<std::vector<std::string>>& hosts);

// The all-sources newPeak reaches only a surface of every source on every host.
inline bool acceptsNewPeak(const std::optional<std::vector<SourceKind>>& sources, const std::optional<std::vector<std::string>>& hosts) {
    return !sources && !hosts;
}

}  // namespace agentspeed::hosts
