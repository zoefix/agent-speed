#include "agentspeed/remote_messages.hpp"

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

std::string HistoryDay::digestKey() const { return day + "/" + std::string(sourceId(source)); }

bool HistoryDay::operator==(const HistoryDay& o) const {
    return day == o.day && source == o.source && aggregateJson == o.aggregateJson && digest == o.digest &&
           history::encode(aggregate) == history::encode(o.aggregate);
}

}  // namespace agentspeed::remote
