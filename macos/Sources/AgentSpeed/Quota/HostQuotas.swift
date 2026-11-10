import AgentSpeedCore
import Foundation

/// One remote host's quotas (REMOTE 11.1): each source's last `quota` line and the Claude Code bridge's state.
struct HostQuotaState: Equatable {
    var reports: [SourceKind: RemoteQuotaReport] = [:]
    var bridge: RemoteQuotaBridge?
}

/// A source of the 額度 card: this Mac's (no host) or a remote host's, whose header reads `<source> · <host>`.
struct QuotaCardItem: Equatable {
    var snapshot: QuotaSnapshot
    /// The host's id and display name; nil for this Mac.
    var hostId: String?
    var hostName: String?

    init(snapshot: QuotaSnapshot, hostId: String? = nil, hostName: String? = nil) {
        self.snapshot = snapshot
        self.hostId = hostId
        self.hostName = hostName
    }

    var source: SourceKind { snapshot.source }
    /// Distinct for the same source on several hosts.
    var key: String { hostId.map { "\(snapshot.source.rawValue)@\($0)" } ?? snapshot.source.rawValue }
}

/// Hands the coordinator's quota lines (on its event queue) to the controller on the main actor.
final class HostQuotaRelay: RemoteQuotaSink, @unchecked Sendable {
    private weak var controller: QuotaController?

    init(controller: QuotaController) {
        self.controller = controller
    }

    func remoteQuota(_ message: RelayMessage, hostId: String) {
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated { self?.controller?.applyRemote(message, hostId: hostId) }
        }
    }
}

extension QuotaController {
    /// A host's line: a new snapshot replaces the source's, `quotaRemoved` drops it, `quotaBridge` is the bridge's state.
    /// Lines of hosts that are not configured, or of sources whose switch is off, are dropped.
    func applyRemote(_ message: RelayMessage, hostId: String) {
        guard let host = settings.remoteHosts.host(hostId) else { return }
        var state = hostQuotas[hostId] ?? HostQuotaState()
        switch message {
        case .quota(let report):
            guard Self.hostShows(report.source, host: host, switches: settings.remoteQuotaSwitches) else { return }
            state.reports[report.source] = report
        case .quotaRemoved(_, let source):
            state.reports[source] = nil
        case .quotaBridge(let bridge):
            state.bridge = bridge
        default:
            return
        }
        if hostQuotas[hostId] != state { hostQuotas[hostId] = state }
    }

    /// Removed hosts and switched-off sources leave at once.
    func pruneHostQuotas(_ document: RemoteHostsDocument, switches: RemoteQuotaSwitches) {
        var next: [String: HostQuotaState] = [:]
        for (id, state) in hostQuotas {
            guard let host = document.host(id) else { continue }
            var kept = state
            kept.reports = state.reports.filter { Self.hostShows($0.key, host: host, switches: switches) }
            if !host.quotaClaudeCli, kept.bridge?.state != nil { kept.bridge = nil }
            next[id] = kept
        }
        if next != hostQuotas { hostQuotas = next }
    }

    /// A disabled source shows no quota from any host either (QUOTAS 9.1).
    static func hostShows(_ source: SourceKind, host: RemoteHost, switches: RemoteQuotaSwitches) -> Bool {
        source == .claudeCode ? host.quotaClaudeCli && switches.claudeCode : switches.sources.contains(source)
    }

    /// A host's quota lines in Dashboard → Hosts (DESIGN 13 *Host quotas*): every source with windows observed within
    /// a day, in source order.
    func hostSnapshots(_ hostId: String) -> [QuotaSnapshot] {
        let now = clockMs
        let reports = hostQuotas[hostId]?.reports ?? [:]
        return Self.sources.compactMap { source in
            guard let snapshot = reports[source]?.snapshot, snapshot.hasWindows,
                  now - snapshot.observedMs <= Self.listedWithinMs else { return nil }
            return snapshot
        }
    }

    /// The 額度 card's sources for an orb tracking `sources` and `hosts` (nil: all): this Mac's first, then each
    /// connected host's whose agent runs and is signed in there, in settings order.
    func cardItems(within sources: SourceSet?, hosts: Set<String>?, connected: Set<String>) -> [QuotaCardItem] {
        var items: [QuotaCardItem] = []
        if hosts?.contains(RemoteHost.localId) ?? true {
            items = cardSnapshots(within: sources).map { QuotaCardItem(snapshot: $0) }
        }
        let document = settings.remoteHosts
        let names = document.displayNames()
        for host in document.hosts where (hosts?.contains(host.id) ?? true) && connected.contains(host.id) {
            let reports = hostQuotas[host.id]?.reports ?? [:]
            for source in Self.sources {
                guard sources?.contains(source) ?? true, let report = reports[source], report.running, report.signedIn,
                      report.snapshot.hasWindows else { continue }
                items.append(QuotaCardItem(snapshot: report.snapshot, hostId: host.id,
                                           hostName: names[host.id] ?? host.automaticName))
            }
        }
        return items
    }
}
