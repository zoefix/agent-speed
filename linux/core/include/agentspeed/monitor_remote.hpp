#pragma once

#include <deque>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/remote_feed.hpp"

namespace agentspeed {

// What a monitor knows about the remote hosts (REMOTE 7) besides their agents and responses: the configured hosts,
// their statuses, source reports and parent maps, and each remote agent's actions and working directory.
struct Monitor::Remote {
    std::vector<remote::HostInfo> hosts;
    std::map<std::string, remote::HostStatus> statuses;
    std::map<std::string, remote::SourceReport> reports;
    std::map<std::string, std::map<std::string, std::string>> parents;
    std::map<std::string, std::deque<work::Action>> actions;
    std::map<std::string, std::optional<std::string>> cwds;

    // Cells are kept only while some remote host is configured.
    bool tracking() const { return !hosts.empty(); }
    // The host's relay is connected (after `ready`).
    bool connected(const std::string& host) const {
        const auto it = statuses.find(host);
        return it != statuses.end() && it->second.status.code == remote::StatusCode::Connected;
    }
    const remote::SourceReport* report(const std::string& host) const {
        const auto it = reports.find(host);
        return it == reports.end() ? nullptr : &it->second;
    }
};

}  // namespace agentspeed
