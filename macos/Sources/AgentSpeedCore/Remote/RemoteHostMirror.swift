import Foundation

/// A configured remote host as the live views name it.
struct RemoteHostInfo: Equatable, Sendable {
    var id: String
    /// The display name (REMOTE 2.1).
    var name: String
}

/// What a monitor session applies for the remote hosts (REMOTE 7), in order.
enum RemoteFeedChange: Sendable {
    /// The configured hosts in settings order; an empty list means no remote host is configured.
    case hosts([RemoteHostInfo])
    /// The host left the settings: its agents and responses leave every live value.
    case removeHost(String)
    case status(host: String, RemoteHostStatus)
    case report(host: String, RemoteSourceReport)
    case agent(RemoteAgent, parentId: String?)
    case agentRemoved(id: String)
    /// The host's complete parent map, replacing the previous one.
    case parents(host: String, [String: String])
    /// `live` is false for seeded lines (before `ready`) and for a replay: no events.
    case response(RemoteResponse, live: Bool)
    case tool(RemoteToolCompletion)
    /// The host cannot be seen: every agent of it becomes idle without an in-flight start.
    case idle(host: String)
}

/// Everything received from one host that a new monitor session needs (REMOTE 7 *Monitor restart*): its agents,
/// parents, responses of today and the retention window, status and source report. It also turns each event into the
/// changes a session applies, so a session and a replay see the same state.
struct RemoteHostMirror: Sendable {
    let hostId: String
    /// The connection the latest messages came from (`RemoteHostEvent.connection`).
    private(set) var connection = 0
    private(set) var status: RemoteHostStatus?
    /// The current connection sent `ready`.
    private(set) var ready = false
    private(set) var agents: [String: RemoteAgent] = [:]
    private(set) var parents: [String: String] = [:]
    private(set) var responses: [String: RemoteResponse] = [:]
    private(set) var report: RemoteSourceReport?
    /// Agents the current connection sent; at its `ready` every other agent of the host is removed.
    private var sentAgents: Set<String> = []
    private var sentParents = false
    private var lastPruneMs: Int64?
    var responseLimit = RemoteLimits.responses

    static let pruneIntervalMs: Int64 = 60_000

    init(hostId: String) {
        self.hostId = hostId
    }

    mutating func apply(_ event: RemoteHostEvent, nowMs: Int64, todayStartMs: Int64) -> [RemoteFeedChange] {
        if event.connection != 0, event.connection != connection {
            connection = event.connection
            ready = false
            sentAgents = []
            sentParents = false
        }
        switch event.kind {
        case .status(let value):
            status = value
            var changes: [RemoteFeedChange] = [.status(host: hostId, value)]
            if !Self.isLive(value.status), idleAgents() { changes.append(.idle(host: hostId)) }
            return changes
        case .message(let message, let seeded):
            return apply(message, seeded: seeded, nowMs: nowMs, todayStartMs: todayStartMs)
        case .historyDue:
            return []
        }
    }

    private mutating func apply(_ message: RelayMessage, seeded: Bool, nowMs: Int64, todayStartMs: Int64) -> [RemoteFeedChange] {
        switch message {
        case .agent(let agent):
            sentAgents.insert(agent.id)
            agents[agent.id] = agent
            return [.agent(agent, parentId: parents[agent.id])]
        case .agentRemoved(let id):
            sentAgents.remove(id)
            guard agents.removeValue(forKey: id) != nil else { return [] }
            return [.agentRemoved(id: id)]
        case .parents(let links):
            sentParents = true
            parents = links
            return [.parents(host: hostId, links)]
        case .response(let response):
            store(response, nowMs: nowMs, todayStartMs: todayStartMs)
            return [.response(response, live: !seeded)]
        case .tool(let tool):
            return [.tool(tool)]
        case .status(let value):
            report = value
            return [.report(host: hostId, value)]
        case .ready:
            ready = true
            var changes: [RemoteFeedChange] = []
            for id in agents.keys.sorted() where !sentAgents.contains(id) {
                agents[id] = nil
                changes.append(.agentRemoved(id: id))
            }
            if !sentParents, !parents.isEmpty {
                parents = [:]
                changes.append(.parents(host: hostId, [:]))
            }
            return changes
        default:
            return []
        }
    }

    /// The host's state as seeded changes, for a new session.
    func replay() -> [RemoteFeedChange] {
        var changes: [RemoteFeedChange] = []
        if let status { changes.append(.status(host: hostId, status)) }
        if let report { changes.append(.report(host: hostId, report)) }
        if !parents.isEmpty { changes.append(.parents(host: hostId, parents)) }
        for id in agents.keys.sorted() {
            changes.append(.agent(agents[id]!, parentId: parents[id]))
        }
        for response in responses.values.sorted(by: Self.olderFirst) {
            changes.append(.response(response, live: false))
        }
        return changes
    }

    /// `connected` or `seeding`: the relay is running and its agents are current.
    static func isLive(_ status: RemoteStatus) -> Bool {
        status == .connected || status.isSeeding
    }

    /// Sets every agent idle without an in-flight start; true when one changed.
    private mutating func idleAgents() -> Bool {
        var changed = false
        for (id, agent) in agents where agent.phase != .idle || agent.inFlightStartMs != nil {
            agents[id]?.phase = .idle
            agents[id]?.inFlightStartMs = nil
            changed = true
        }
        return changed
    }

    /// Keeps today's responses and the retention window, at most `responseLimit` (the oldest go first).
    private mutating func store(_ response: RemoteResponse, nowMs: Int64, todayStartMs: Int64) {
        responses[response.record.key] = response
        if lastPruneMs.map({ nowMs - $0 >= Self.pruneIntervalMs }) ?? true {
            lastPruneMs = nowMs
            let cutoff = min(todayStartMs, nowMs - SpeedEngine.retentionMs)
            responses = responses.filter { $0.value.record.endMs >= cutoff }
        }
        guard responses.count > responseLimit else { return }
        let excess = responses.count - responseLimit + max(1, responseLimit / 100)
        for old in responses.values.sorted(by: Self.olderFirst).prefix(excess) {
            responses[old.record.key] = nil
        }
    }

    private static func olderFirst(_ lhs: RemoteResponse, _ rhs: RemoteResponse) -> Bool {
        (lhs.record.endMs, lhs.record.key) < (rhs.record.endMs, rhs.record.key)
    }
}
