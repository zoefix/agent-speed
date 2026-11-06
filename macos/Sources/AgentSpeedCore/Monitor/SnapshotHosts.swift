import Foundation

extension SnapshotBuilder {
    /// `SpeedSnapshot.hosts` and `hostSources` (DESIGN 13): this computer first, then every configured host.
    static func hosts(_ output: EngineOutput, context: SnapshotContext) -> ([HostStatus], [HostSourceStatus]) {
        let remote = context.remote
        let unsupported = Set(context.warnings.compactMap { warning -> SourceKind? in
            if case .unsupportedFormat(let source) = warning { return source }
            return nil
        })
        var hosts: [HostStatus] = []
        var cells: [HostSourceStatus] = []
        let order = [RemoteHostInfo(id: RemoteHost.localId, name: RemoteHost.localId)] + remote.hosts
        for info in order {
            let isLocal = info.id == RemoteHost.localId
            let connected = isLocal || remote.isConnected(info.id)
            let states = Dictionary((remote.reports[info.id]?.sources ?? []).map { ($0.source, $0) }) { first, _ in first }
            var host = HostStatus(id: info.id, name: info.name, remote: isLocal ? nil : remote.statuses[info.id],
                                  isConnected: connected)
            for kind in SourceKind.allCases {
                let cell = HostSource(host: info.id, source: kind)
                let values = output.cells[cell] ?? EngineCell()
                let enabled = context.sources[kind]?.enabled ?? false
                var status = HostSourceStatus(
                    cell: cell, available: false, live: values.live, liveEstimated: values.liveEstimated,
                    liveCost: values.liveCost, intelligence: values.intelligence, today: values.today,
                    todayCost: values.todayCost, unpricedModels: unpricedModels(values.unpriced)
                )
                if isLocal {
                    let local = context.localSources[kind]
                    status.available = local?.available ?? false
                    status.sessionCount = local?.sessionCount ?? 0
                    status.configuredModes = local?.configuredModes ?? .empty
                    status.unsupported = unsupported.contains(kind)
                } else if let state = states[kind] {
                    status.available = connected && state.available
                    status.sessionCount = connected ? state.sessions : 0
                    status.configuredModes = state.configuredModes
                    status.unsupported = state.unsupported
                }
                if !enabled {
                    status.available = false
                    status.sessionCount = 0
                }
                host.live.add(status.live)
                host.liveEstimated.add(status.liveEstimated)
                host.liveCost.add(status.liveCost)
                host.today.add(status.today)
                host.todayCost.add(status.todayCost)
                host.sessions += status.sessionCount
                if status.available || status.sessionCount > 0 || status.today.requests > 0 || status.live != .zero {
                    cells.append(status)
                }
            }
            hosts.append(host)
        }
        return (hosts, cells)
    }

    /// Models with unpriced requests, most first, then by name; the unknown model is left out.
    private static func unpricedModels(_ counts: [String: Int]) -> [String] {
        counts.filter { $0.value > 0 && !$0.key.isEmpty }
            .sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
            .map(\.key)
    }
}
