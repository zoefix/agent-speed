import Foundation

/// The remote hosts inside one monitor session (REMOTE 7): applies the feed's changes to the engine and keeps what
/// the snapshot shows about each host.
struct RemoteIngest: Sendable {
    /// The configured hosts in settings order.
    private(set) var hosts: [RemoteHostInfo] = []
    private(set) var statuses: [String: RemoteHostStatus] = [:]
    private(set) var reports: [String: RemoteSourceReport] = [:]
    private var parentsByHost: [String: [String: String]] = [:]
    /// Every host's parent links (prefixed ids), merged.
    private(set) var parents: [String: String] = [:]

    var hasHosts: Bool { !hosts.isEmpty }

    /// The host's relay is connected (after `ready`).
    func isConnected(_ host: String) -> Bool {
        statuses[host]?.status == .connected
    }

    struct Result {
        var changed = false
        var parentsChanged = false
        var events: [SpeedEvent] = []
    }

    /// `watching`: live reporting's watch is on (tool completions count). `nowMs` is the wall clock for the age rule
    /// of live completions (METRICS 6.4).
    mutating func apply(
        _ changes: [RemoteFeedChange],
        to engine: inout SpeedEngine,
        enabledSources: Set<SourceKind>,
        watching: Bool,
        nowMs: () -> Int64
    ) -> Result {
        var result = Result()
        for change in changes {
            switch change {
            case .hosts(let infos):
                hosts = infos
                engine.tracksCells = !infos.isEmpty
            case .removeHost(let id):
                engine.removeHost(id)
                statuses[id] = nil
                reports[id] = nil
                if parentsByHost.removeValue(forKey: id) != nil {
                    mergeParents()
                    result.parentsChanged = true
                }
            case .status(let host, let status):
                statuses[host] = status
            case .report(let host, let report):
                reports[host] = report
            case .agent(let agent, let parentId):
                if enabledSources.contains(agent.source) {
                    engine.setAgent(agent.engineAgent(parentId: parentId))
                } else {
                    engine.removeAgent(id: agent.id)
                }
            case .agentRemoved(let id):
                engine.removeAgent(id: id)
            case .parents(let host, let links):
                guard parentsByHost[host] ?? [:] != links else { continue }
                parentsByHost[host] = links.isEmpty ? nil : links
                mergeParents()
                result.parentsChanged = true
            case .response(let response, let live):
                let record = response.record
                guard enabledSources.contains(record.source) else { continue }
                let effort = engine.agents[record.agentId].flatMap {
                    EffortAndSpeed(facts: $0.modes, session: $0.sessionModes, configured: $0.configuredModes).effort
                }
                if let event = engine.upsert(record, project: response.project, isCompletion: response.isCompletion,
                                             emitsEvents: live, nowMs: live ? nowMs() : nil, currentEffort: effort) {
                    result.events.append(event)
                }
            case .tool(let tool):
                guard watching, enabledSources.contains(tool.source) else { continue }
                engine.recordToolCompletion(tool.completion, agentId: tool.agentId, source: tool.source, nowMs: nowMs())
            case .idle(let host):
                engine.setIdle(host: host)
            }
            result.changed = true
        }
        return result
    }

    private mutating func mergeParents() {
        var merged: [String: String] = [:]
        for links in parentsByHost.values { merged.merge(links) { first, _ in first } }
        parents = merged
    }
}
