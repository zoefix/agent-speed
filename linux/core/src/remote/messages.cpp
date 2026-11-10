#include "agentspeed/remote_messages.hpp"

#include <algorithm>

#include "agentspeed/history_store.hpp"

namespace agentspeed::remote {

namespace {

bool sameAction(const work::Action& a, const work::Action& b) {
    return a.callMs == b.callMs && a.resultMs == b.resultMs && a.tool == b.tool && a.digest == b.digest && a.failed == b.failed &&
           a.model == b.model && a.seeded == b.seeded;
}

}  // namespace

std::string agentPrefix(std::string_view hostId) { return "ssh://" + std::string(hostId) + "/"; }

bool RemoteAgent::operator==(const RemoteAgent& o) const {
    if (actions.size() != o.actions.size()) return false;
    for (std::size_t k = 0; k < actions.size(); ++k) {
        if (!sameAction(actions[k], o.actions[k])) return false;
    }
    return hostId == o.hostId && id == o.id && relayId == o.relayId && source == o.source && project == o.project && cwd == o.cwd &&
           model == o.model && subagent == o.subagent && phase == o.phase && inFlightStartMs == o.inFlightStartMs &&
           lastLineMs == o.lastLineMs && modes == o.modes && sessionModes == o.sessionModes && configuredModes == o.configuredModes;
}

bool RemoteTool::operator==(const RemoteTool& o) const {
    return hostId == o.hostId && agentId == o.agentId && source == o.source && sameAction(action, o.action);
}

namespace quotas {

const std::vector<SourceKind>& hostSources() {
    static const std::vector<SourceKind> kSources = {SourceKind::Codex,  SourceKind::ClaudeCode, SourceKind::Gemini, SourceKind::Antigravity,
                                                     SourceKind::Kimi,   SourceKind::GrokBuild,  SourceKind::Droid};
    return kSources;
}

bool hostReads(SourceKind source) {
    const auto& sources = hostSources();
    return std::find(sources.begin(), sources.end(), source) != sources.end();
}

}  // namespace quotas

HostQuotaConfig HostQuotaConfig::forHost(bool hostBridge, const QuotaSwitches& switches) {
    HostQuotaConfig config;
    for (const auto source : switches.sources) {
        if (source != SourceKind::ClaudeCode && quotas::hostReads(source)) config.sources.insert(source);
    }
    if (hostBridge && switches.claudeCode) config.sources.insert(SourceKind::ClaudeCode);
    config.network = switches.network;
    config.grokLive = switches.grokLive && config.sources.contains(SourceKind::GrokBuild);
    config.claudeBridge = hostBridge;
    return config;
}

std::string_view hostQuotaStatusId(HostQuota::Status status) {
    switch (status) {
        case HostQuota::Status::Ok: return "ok";
        case HostQuota::Status::NoCredential: return "noCredential";
        case HostQuota::Status::Expired: return "expired";
        case HostQuota::Status::Denied: return "denied";
        case HostQuota::Status::Failed: return "failed";
        case HostQuota::Status::NetworkOff: return "networkOff";
        case HostQuota::Status::NoCertificates: return "noCertificates";
        case HostQuota::Status::NotRunning: return "notRunning";
    }
    return "failed";
}

std::string_view hostBridgeStateId(HostQuotaBridge::State state) {
    switch (state) {
        case HostQuotaBridge::State::Off: return "off";
        case HostQuotaBridge::State::Waiting: return "waiting";
        case HostQuotaBridge::State::Ok: return "ok";
        case HostQuotaBridge::State::Replaced: return "replaced";
        case HostQuotaBridge::State::Unreadable: return "unreadable";
        case HostQuotaBridge::State::Failed: return "failed";
    }
    return "off";
}

std::string HistoryDay::digestKey() const { return day + "/" + std::string(sourceId(source)); }

bool HistoryDay::operator==(const HistoryDay& o) const {
    return day == o.day && source == o.source && aggregateJson == o.aggregateJson && digest == o.digest &&
           history::encode(aggregate) == history::encode(o.aggregate);
}

}  // namespace agentspeed::remote
