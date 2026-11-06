#include <string_view>
#include <variant>

#include "agentspeed/antigravity.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/monitor_remote.hpp"
#include "agentspeed/monitor_store.hpp"

// REMOTE 7: the remote hosts' lines applied between reads of local files, as the feed hands them over.
namespace agentspeed {

namespace {

std::string_view lastComponent(std::string_view path) {
    const auto slash = path.find_last_of('/');
    return slash == std::string_view::npos ? path : path.substr(slash + 1);
}

std::string_view parentOf(std::string_view path) {
    const auto slash = path.find_last_of('/');
    return slash == std::string_view::npos ? std::string_view() : path.substr(0, slash);
}

// The display fallback of an agent without a project: the folder its id names on the host (an Antigravity store's
// name), else the id's last component.
std::optional<std::string> fallbackProject(const std::string& relayId) {
    if (auto store = antigravity::storeNameOf(relayId)) return store;
    const auto name = lastComponent(parentOf(relayId));
    const auto chosen = name.empty() ? lastComponent(relayId) : name;
    if (chosen.empty()) return std::nullopt;
    return std::string(chosen);
}

template <typename... Ts>
struct Overloaded : Ts... {
    using Ts::operator()...;
};
template <typename... Ts>
Overloaded(Ts...) -> Overloaded<Ts...>;

}  // namespace

bool Monitor::applyRemote(const std::vector<remote::FeedChange>& changes, std::int64_t nowMs) {
    if (changes.empty()) return false;
    store_->setClock(nowMs);
    clockMs_ = nowMs;
    bool changed = false;
    for (const auto& change : changes) {
        changed |= std::visit(
            Overloaded{
                [&](const remote::feed::Hosts& c) {
                    remote_->hosts = c.hosts;
                    return true;
                },
                [&](const remote::feed::RemoveHost& c) {
                    removeHost(c.id);
                    return true;
                },
                [&](const remote::feed::Status& c) {
                    remote_->statuses[c.host] = c.status;
                    return true;
                },
                [&](const remote::feed::Report& c) {
                    remote_->reports[c.host] = c.report;
                    return true;
                },
                [&](const remote::feed::Agent& c) {
                    if (config_.enabled.contains(c.agent.source)) {
                        setRemoteAgent(c.agent);
                    } else {
                        removeRemoteAgent(c.agent.id);
                    }
                    return true;
                },
                [&](const remote::feed::AgentRemoved& c) {
                    removeRemoteAgent(c.id);
                    return true;
                },
                [&](const remote::feed::Parents& c) { return setRemoteParents(c.host, c.links); },
                [&](const remote::feed::Response& c) {
                    const auto& r = c.response;
                    if (!config_.enabled.contains(r.record.source)) return false;
                    store_->onRemoteResponse(r.record, r.hostId, r.completion, !c.live);
                    return true;
                },
                [&](const remote::feed::Tool& c) {
                    remoteTool(c.tool);
                    return true;
                },
                [&](const remote::feed::Idle& c) { return idleHost(c.host); },
            },
            change);
    }
    return changed;
}

void Monitor::setRemoteAgent(const remote::RemoteAgent& a) {
    AgentInfo info;
    info.id = a.id;
    info.host = a.hostId;
    info.source = a.source;
    info.project = a.project ? a.project : fallbackProject(a.relayId);
    info.model = a.model;
    info.isSubagent = a.subagent;
    info.phase = a.phase;
    info.inFlightStartMs = a.inFlightStartMs;
    info.lastLineMs = a.lastLineMs;
    info.modes = a.modes;
    info.sessionModes = a.sessionModes;
    info.configuredModes = a.configuredModes;
    agents_[a.id] = std::move(info);
    remote_->actions[a.id] = std::deque<work::Action>(a.actions.begin(), a.actions.end());
    remote_->cwds[a.id] = a.cwd;
}

void Monitor::removeRemoteAgent(const std::string& id) {
    const auto it = agents_.find(id);
    if (it != agents_.end() && it->second.host != remote::kLocalHost) agents_.erase(it);
    remote_->actions.erase(id);
    remote_->cwds.erase(id);
}

void Monitor::removeHost(const std::string& host) {
    for (auto it = agents_.begin(); it != agents_.end();) {
        if (it->second.host != host) {
            ++it;
            continue;
        }
        remote_->actions.erase(it->first);
        remote_->cwds.erase(it->first);
        it = agents_.erase(it);
    }
    store_->removeHost(host);
    remote_->statuses.erase(host);
    remote_->reports.erase(host);
    if (remote_->parents.erase(host) > 0) remoteParentsChanged_ = true;
}

bool Monitor::setRemoteParents(const std::string& host, const std::map<std::string, std::string>& links) {
    const auto it = remote_->parents.find(host);
    if (it == remote_->parents.end() ? links.empty() : it->second == links) return false;
    if (links.empty()) {
        remote_->parents.erase(host);
    } else {
        remote_->parents[host] = links;
    }
    remoteParentsChanged_ = true;
    return true;
}

// METRICS 11.10 Counting: the ledger takes the call while the watch is on, as for a local result.
void Monitor::remoteTool(const remote::RemoteTool& tool) {
    if (!config_.enabled.contains(tool.source)) return;
    const report::ModelKey key{store_->reportModelId(tool.action.model, store_->providerOf(tool.agentId)), tool.source};
    perfLedger_.call(tool.agentId, key, tool.action, clockMs_, tool.action.seeded);
}

// A host that cannot be seen: no estimate keeps running for its agents.
bool Monitor::idleHost(const std::string& host) {
    bool changed = false;
    for (auto& [id, info] : agents_) {
        if (info.host != host || (info.phase == Phase::Idle && !info.inFlightStartMs)) continue;
        info.phase = Phase::Idle;
        info.inFlightStartMs.reset();
        changed = true;
    }
    return changed;
}

const std::deque<work::Action>* Monitor::remoteActions(const std::string& id) const {
    const auto it = remote_->actions.find(id);
    return it == remote_->actions.end() ? nullptr : &it->second;
}

}  // namespace agentspeed
