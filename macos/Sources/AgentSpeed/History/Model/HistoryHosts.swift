import AgentSpeedCore
import Foundation

/// A host of the Usage History window's host picker (DESIGN 13): this Mac or a remote host with stored history.
struct HistoryHost: Equatable, Sendable, Identifiable {
    let id: String
    /// The remote host's last name (its id when none was stored); nil for this Mac, which reads `remote.local`.
    let name: String?

    var isLocal: Bool { id == RemoteHost.localId }
}

enum HistoryHosts {
    /// This Mac, then every remote host with usage by name (removed hosts included under their last name), plus the
    /// selected remote host; empty while no remote host has history and none is selected, so nothing about hosts shows
    /// (DESIGN 13).
    static func withHistory(_ usage: [DailyUsage], names: [String: String], selected: String? = nil) -> [HistoryHost] {
        var remote = Set<String>()
        for record in usage where !record.isLocal && HistoryAggregator.hasUsage(record) { remote.insert(record.host) }
        if let selected, selected != RemoteHost.localId { remote.insert(selected) }
        guard !remote.isEmpty else { return [] }
        let hosts = remote.map { HistoryHost(id: $0, name: names[$0] ?? $0) }.sorted { lhs, rhs in
            let order = (lhs.name ?? "").localizedStandardCompare(rhs.name ?? "")
            return order == .orderedSame ? lhs.id < rhs.id : order == .orderedAscending
        }
        return [HistoryHost(id: RemoteHost.localId, name: nil)] + hosts
    }

    /// The CSV `host` value of a filter (DESIGN 13): the host's name, `local` for this Mac, `all` for every host.
    static func csvValue(_ host: String?, hosts: [HistoryHost]) -> String {
        guard let host else { return "all" }
        if host == RemoteHost.localId { return "local" }
        return hosts.first { $0.id == host }?.name ?? host
    }
}
