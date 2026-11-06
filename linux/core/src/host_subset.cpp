#include "agentspeed/host_subset.hpp"

#include <map>
#include <set>

namespace agentspeed::hosts {

namespace {

bool selected(const std::vector<std::string>& hosts, const std::string& id) { return std::find(hosts.begin(), hosts.end(), id) != hosts.end(); }

// The agents of `hosts` in list order; a parent that is not kept is cleared and the depths follow.
std::vector<AgentRow> filteredAgents(const std::vector<AgentRow>& agents, const std::vector<std::string>& hosts) {
    std::vector<AgentRow> kept;
    std::map<std::string, int> depth;
    for (const auto& a : agents) {
        if (!selected(hosts, a.host)) continue;
        auto row = a;
        const auto parent = row.parentId ? depth.find(*row.parentId) : depth.end();
        if (parent == depth.end()) {
            row.parentId.reset();
            row.depth = 0;
        } else {
            row.depth = parent->second + 1;
        }
        depth[row.id] = row.depth;
        kept.push_back(std::move(row));
    }
    return kept;
}

}  // namespace

Snapshot restricted(const Snapshot& snapshot, const std::vector<std::string>& hosts) {
    if (snapshot.hosts.empty() && selected(hosts, kLocal)) return snapshot;
    std::map<std::pair<std::string, SourceKind>, const HostSourceRow*> cells;
    for (const auto& cell : snapshot.hostSources) {
        if (selected(hosts, cell.host)) cells[{cell.host, cell.source}] = &cell;
    }
    std::vector<std::string> order;
    for (const auto& host : snapshot.hosts) {
        if (selected(hosts, host.id)) order.push_back(host.id);
    }
    Snapshot view = snapshot;
    view.agents = filteredAgents(snapshot.agents, hosts);
    view.liveOutput = view.liveTotal = view.estimatedShare = 0;
    view.todayOutput = view.todayInput = view.todayRequests = 0;
    view.liveCost = 0;
    view.liveCostKnownShare = 1;
    view.todayCostMicroUsd = view.todayUnpricedRequests = 0;
    double estimated = 0, known = 0;
    for (auto& row : view.sources) {
        row.available = false;
        row.sessions = 0;
        row.liveOutput = row.liveTotal = row.liveEstimatedShare = 0;
        row.liveCost = 0;
        row.liveCostKnownShare = 1;
        row.todayOutput = row.todayInput = row.todayRequests = 0;
        row.todayCostMicroUsd = row.unpricedRequests = 0;
        double rowEstimated = 0, rowKnown = 0;
        for (const auto& host : order) {
            const auto it = cells.find({host, row.source});
            if (it == cells.end()) continue;
            const auto& cell = *it->second;
            row.available = row.available || cell.available;
            row.sessions += cell.sessions;
            row.liveOutput += cell.liveOutput;
            row.liveTotal += cell.liveTotal;
            rowEstimated += cell.liveEstimated;
            row.liveCost += cell.liveCost;
            rowKnown += cell.liveCostKnownShare * cell.liveOutput;
            row.todayOutput += cell.todayOutput;
            row.todayInput += cell.todayInput;
            row.todayRequests += cell.todayRequests;
            row.todayCostMicroUsd += cell.todayCostMicroUsd;
            row.unpricedRequests += cell.unpricedRequests;
        }
        row.liveEstimatedShare = row.liveOutput > 0 ? rowEstimated / row.liveOutput : 0;
        row.liveCostKnownShare = row.liveOutput > 0 ? rowKnown / row.liveOutput : 1;
        row.activeAgents = static_cast<int>(std::count_if(view.agents.begin(), view.agents.end(), [&](const AgentRow& a) {
            return a.source == row.source && a.phase == Phase::Generating;
        }));
        view.liveOutput += row.liveOutput;
        view.liveTotal += row.liveTotal;
        estimated += rowEstimated;
        view.todayOutput += row.todayOutput;
        view.todayInput += row.todayInput;
        view.todayRequests += row.todayRequests;
        view.liveCost += row.liveCost;
        known += rowKnown;
        view.todayCostMicroUsd += row.todayCostMicroUsd;
        view.todayUnpricedRequests += row.unpricedRequests;
    }
    view.estimatedShare = view.liveOutput > 0 ? estimated / view.liveOutput : 0;
    view.liveCostKnownShare = view.liveOutput > 0 ? known / view.liveOutput : 1;
    view.generating = static_cast<int>(std::count_if(view.agents.begin(), view.agents.end(), [](const AgentRow& a) { return a.phase == Phase::Generating; }));
    view.tools = static_cast<int>(std::count_if(view.agents.begin(), view.agents.end(), [](const AgentRow& a) { return a.phase == Phase::Tools; }));
    return view;
}

bool isOffline(const Snapshot& snapshot, const std::vector<std::string>& hosts) {
    if (selected(hosts, kLocal)) return false;
    return std::none_of(snapshot.hosts.begin(), snapshot.hosts.end(), [&](const HostRow& h) { return selected(hosts, h.id) && h.connected; });
}

bool accepts(const Completion& completion, const std::optional<std::vector<SourceKind>>& sources,
             const std::optional<std::vector<std::string>>& hosts) {
    const bool source = !sources || std::find(sources->begin(), sources->end(), completion.source) != sources->end();
    return source && (!hosts || selected(*hosts, completion.host));
}

}  // namespace agentspeed::hosts
