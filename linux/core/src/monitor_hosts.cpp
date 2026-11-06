#include <algorithm>
#include <map>
#include <utility>

#include "agentspeed/monitor.hpp"
#include "agentspeed/monitor_remote.hpp"

// DESIGN 13: the snapshot's hosts and (host, source) cells, and the remote part of the source rows.
namespace agentspeed {

namespace {

constexpr std::int64_t kRetentionMs = 30 * 60'000;

using CellKey = std::pair<std::string, SourceKind>;

// Models with unpriced requests, most first, then by name; the unknown model is left out.
std::vector<std::string> unpricedModels(const std::map<std::string, std::int64_t>& counts) {
    std::vector<std::pair<std::string, std::int64_t>> rows;
    for (const auto& [model, count] : counts) {
        if (count > 0 && !model.empty()) rows.emplace_back(model, count);
    }
    std::sort(rows.begin(), rows.end(), [](const auto& a, const auto& b) { return a.second != b.second ? a.second > b.second : a.first < b.first; });
    std::vector<std::string> out;
    out.reserve(rows.size());
    for (const auto& row : rows) out.push_back(row.first);
    return out;
}

void addTo(HostRow& host, const HostSourceRow& cell, double& knownOutput) {
    host.liveOutput += cell.liveOutput;
    host.liveTotal += cell.liveTotal;
    host.liveEstimated += cell.liveEstimated;
    host.liveCost += cell.liveCost;
    knownOutput += cell.liveCostKnownShare * cell.liveOutput;
    host.todayOutput += cell.todayOutput;
    host.todayInput += cell.todayInput;
    host.todayRequests += cell.todayRequests;
    host.todayCostMicroUsd += cell.todayCostMicroUsd;
    host.unpricedRequests += cell.unpricedRequests;
    host.sessions += cell.sessions;
}

}  // namespace

// A source is available when it is on this computer or on a connected host; the hosts' sessions add up.
void Monitor::addRemoteSources(Snapshot& snap) const {
    for (const auto& host : remote_->hosts) {
        if (!remote_->connected(host.id)) continue;
        const auto* report = remote_->report(host.id);
        if (!report) continue;
        for (const auto& state : report->sources) {
            const auto row = std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == state.source; });
            if (row == snap.sources.end() || !row->enabled) continue;
            if (state.available) row->available = true;
            row->sessions += state.sessions;
        }
    }
}

void Monitor::addHosts(Snapshot& snap, const std::vector<engine::Response>& responses, const std::vector<engine::Agent>& agents,
                       std::int64_t nowMs, std::int64_t todayStartMs) const {
    std::map<CellKey, HostSourceRow> values;
    const auto cell = [&](const std::string& host, SourceKind kind) -> HostSourceRow& {
        auto& row = values[{host, kind}];
        row.host = host;
        row.source = kind;
        return row;
    };
    for (const auto kind : MonitorConfig::allSources()) {
        if (!config_.enabled.contains(kind)) continue;
        std::vector<engine::Response> own;
        std::map<std::string, std::vector<engine::Response>> todayByHost;
        for (const auto& r : responses) {
            if (r.source != kind) continue;
            if (r.endMs >= nowMs - kRetentionMs) own.push_back(r);
            if (r.endMs >= todayStartMs) todayByHost[r.host].push_back(r);
        }
        std::vector<engine::Agent> ownAgents;
        for (const auto& a : agents) {
            if (a.source == kind) ownAgents.push_back(a);
        }
        // The estimates' references are the source's, as for its row, so the cells add up to it.
        for (const auto& [host, part] : engine::liveByHost(own, ownAgents, nowMs)) {
            auto& row = cell(host, kind);
            row.liveOutput = part.rate.output;
            row.liveTotal = part.rate.total;
            row.liveEstimated = part.estimatedOutput;
            row.liveCost = part.cost.microUsdPerSecond;
            row.liveCostKnownShare = part.cost.knownShare;
            row.thinking = part.thinking;
        }
        for (const auto& [host, today] : todayByHost) {
            auto& row = cell(host, kind);
            const auto stats = engine::todayStats(today, todayStartMs, nowMs);
            row.todayOutput = stats.output;
            row.todayInput = stats.input;
            row.todayRequests = stats.requests;
            row.todayCostMicroUsd = stats.costMicroUsd;
            row.unpricedRequests = stats.unpricedRequests;
            std::map<std::string, std::int64_t> unpriced;
            for (const auto& r : today) {
                if (!r.costMicroUsd) unpriced[r.model.value_or("")] += r.requestCount;
            }
            row.unpricedModels = unpricedModels(unpriced);
        }
    }

    std::map<SourceKind, SourceState> local;
    for (auto& state : sourceStates(todayStartMs)) local[state.source] = std::move(state);
    std::vector<remote::HostInfo> order{{std::string(remote::kLocalHost), std::string(remote::kLocalHost)}};
    order.insert(order.end(), remote_->hosts.begin(), remote_->hosts.end());
    for (const auto& info : order) {
        const bool isLocal = info.id == remote::kLocalHost;
        const bool connected = isLocal || remote_->connected(info.id);
        std::map<SourceKind, const remote::RemoteSourceState*> states;
        if (const auto* report = isLocal ? nullptr : remote_->report(info.id)) {
            for (const auto& state : report->sources) states.try_emplace(state.source, &state);
        }
        HostRow host;
        host.id = info.id;
        host.name = info.name;
        host.connected = connected;
        if (!isLocal) {
            if (const auto status = remote_->statuses.find(info.id); status != remote_->statuses.end()) host.remote = status->second;
        }
        double knownOutput = 0;
        for (const auto kind : MonitorConfig::allSources()) {
            HostSourceRow row;
            if (const auto it = values.find({info.id, kind}); it != values.end()) row = it->second;
            row.host = info.id;
            row.source = kind;
            if (isLocal) {
                if (const auto it = local.find(kind); it != local.end()) {
                    row.available = it->second.available;
                    row.sessions = it->second.sessions;
                    row.configuredModes = it->second.configuredModes;
                }
                row.unsupported = std::find(snap.warnings.unsupported.begin(), snap.warnings.unsupported.end(), kind) != snap.warnings.unsupported.end();
            } else if (const auto it = states.find(kind); it != states.end()) {
                row.available = connected && it->second->available;
                row.sessions = connected ? it->second->sessions : 0;
                row.configuredModes = it->second->configuredModes;
                row.unsupported = it->second->unsupported;
            }
            if (!config_.enabled.contains(kind)) {
                row.available = false;
                row.sessions = 0;
            }
            addTo(host, row, knownOutput);
            if (row.available || row.sessions > 0 || row.todayRequests > 0 || row.liveOutput != 0 || row.liveTotal != 0) {
                snap.hostSources.push_back(std::move(row));
            }
        }
        host.liveCostKnownShare = host.liveOutput > 0 ? knownOutput / host.liveOutput : 1.0;
        snap.hosts.push_back(std::move(host));
    }
}

}  // namespace agentspeed
