import Foundation

/// Orbs and boards that track a set of hosts (DESIGN 13): METRICS 6.8 evaluated over the (host, source) cells whose
/// host and source are both selected. Without hosts (`nil`, every host) every function is exactly its source-only form.
extension OrbSubset {
    /// The reading of `sources` (nil = every source) on `hosts`. A source is available when it is enabled and, on a
    /// selected host, detected or showing data. With every selected host remote and disconnected the state is
    /// `.hostsOffline` (its values still count, like a disconnected host's responses).
    public static func reading(_ snapshot: SpeedSnapshot, sources: SourceSet?, hosts: Set<String>?) -> SubsetReading {
        guard let hosts else { return reading(snapshot, sources: sources) }
        var result = reading(restricted(snapshot, to: hosts), sources: sources ?? .everything)
        if sources == nil {
            result.unavailable = []
            result.state = .available
        }
        result.measuredPeakOutput = snapshot.subsetPeaks[SubsetKey(sources ?? .everything, hosts: hosts)]
        if isOffline(snapshot, hosts: hosts) {
            result.state = .hostsOffline
            result.unavailable = sources?.kinds ?? []
        }
        return result
    }

    /// The money-unit reading of `sources` on `hosts` (nil = every host).
    public static func cost(_ snapshot: SpeedSnapshot, sources: SourceSet?, hosts: Set<String>?, billable: SourceSet) -> CostReading {
        guard let hosts else { return cost(snapshot, sources: sources, billable: billable) }
        return cost(restricted(snapshot, to: hosts), sources: sources, billable: billable)
    }

    /// Whether an orb tracking `sources` on `hosts` receives `event`: completions of its own sources read on its own
    /// hosts, and the all-sources `newPeak` only for an orb of every source on every host.
    public static func accepts(_ event: SpeedEvent, sources: SourceSet?, hosts: Set<String>?) -> Bool {
        switch event {
        case .responseCompleted(let summary):
            (sources?.contains(summary.source) ?? true) && (hosts?.contains(summary.host) ?? true)
        case .newPeak:
            sources == nil && hosts == nil
        }
    }

    /// No selected host can be seen: this computer is not selected and no selected remote host is connected.
    public static func isOffline(_ snapshot: SpeedSnapshot, hosts: Set<String>) -> Bool {
        !hosts.contains(RemoteHost.localId) && !snapshot.hosts.contains { hosts.contains($0.id) && $0.isConnected }
    }

    /// The snapshot as seen from `hosts`: every source's values summed over the selected hosts' cells, and the agents
    /// of those hosts. Without remote hosts only this computer exists.
    static func restricted(_ snapshot: SpeedSnapshot, to hosts: Set<String>) -> SpeedSnapshot {
        if snapshot.hosts.isEmpty, hosts.contains(RemoteHost.localId) { return snapshot }
        var cells: [HostSource: HostSourceStatus] = [:]
        for cell in snapshot.hostSources where hosts.contains(cell.host) { cells[cell.cell] = cell }
        let selected = snapshot.hosts.map(\.id).filter(hosts.contains)
        var view = snapshot
        view.agents = filtered(snapshot.agents, to: .everything, hosts: hosts)
        view.live = .zero
        view.today = .zero
        view.liveCost = .zero
        view.todayCost = .zero
        view.intelligence = .zero
        var estimated = 0.0
        view.sources = snapshot.sources.map { status in
            var source = status
            source.available = false
            source.sessionCount = 0
            source.live = .zero
            source.liveEstimated = .zero
            source.liveCost = .zero
            source.today = .zero
            source.todayCost = .zero
            source.intelligence = .zero
            source.unpricedFacts = []
            var unpriced: [String] = []
            for host in selected {
                guard let cell = cells[HostSource(host: host, source: status.kind)] else { continue }
                source.available = source.available || cell.available
                source.sessionCount += cell.sessionCount
                source.live.add(cell.live)
                source.liveEstimated.add(cell.liveEstimated)
                source.liveCost.add(cell.liveCost)
                source.today.add(cell.today)
                source.todayCost.add(cell.todayCost)
                source.intelligence.add(cell.intelligence)
                for model in cell.unpricedModels where !unpriced.contains(model) { unpriced.append(model) }
            }
            source.unpricedModels = unpriced
            source.activeAgents = view.agents.filter { $0.source == status.kind && $0.phase == .generating }.count
            view.live.add(source.live)
            estimated += source.liveEstimated.output
            view.today.add(source.today)
            view.liveCost.add(source.liveCost)
            view.todayCost.add(source.todayCost)
            view.intelligence.add(source.intelligence)
            return source
        }
        view.liveEstimatedShare = view.live.output > 0 ? estimated / view.live.output : 0
        view.generatingCount = view.agents.filter { $0.phase == .generating }.count
        view.toolsCount = view.agents.filter { $0.phase == .tools }.count
        return view
    }
}
