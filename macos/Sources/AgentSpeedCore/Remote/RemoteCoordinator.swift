import Foundation
import os

/// Receives what the daily history sync needs (REMOTE 8.2, 8.3): `.historyDue`, the `historyDay`, `historyPerf`,
/// `historyDone` and `historyProgress` messages, a `historyFailed` error and every status change of a host, in order,
/// on the coordinator's event queue. A broken connection (a status that is not `connected`) ends a batch.
public protocol RemoteHistorySink: AnyObject, Sendable {
    /// Answer `.historyDue` with `requests.requestHistory(hostId:digests:)` and the digests held for the host.
    func remoteHistory(_ event: RemoteHostEvent, requests: RemoteHistoryRequests)
}

/// Receives every host's `quota`, `quotaRemoved` and `quotaBridge` lines (REMOTE 11.1), times already local, in order
/// on the coordinator's event queue. Quotas are display only: they never reach the monitor.
public protocol RemoteQuotaSink: AnyObject, Sendable {
    func remoteQuota(_ message: RelayMessage, hostId: String)
}

/// Sends `history` requests to the hosts' relays; usable from any thread.
public struct RemoteHistoryRequests: Sendable {
    let hub: RemoteHub

    /// `digests`: `"<day>/<source>"` → digest of every day the app holds for the host. Ignored before `ready`.
    public func requestHistory(hostId: String, digests: [String: String]) {
        hub.requestHistory(hostId: hostId, digests: digests)
    }
}

/// Connects the remote hosts of the settings to the live monitor (REMOTE 2, 4.4, 7): owns the `RemoteHub`, keeps it in
/// step with the hosts, the master switch, the monitor's enabled sources and report watch and the price table, routes
/// live events to the monitor's feed and history events to `historySink`.
@MainActor
public final class RemoteCoordinator {
    private struct Applied: Equatable {
        var hosts: [RemoteHost]
        var networkEnabled: Bool
        var sources: Set<SourceKind>
        var priceTable: Data
        var quota: RemoteQuotaSwitches
    }

    private final class QuotaRouter: Sendable {
        private struct Target {
            weak var sink: (any RemoteQuotaSink)?
        }

        private let target = OSAllocatedUnfairLock(initialState: Target())

        func set(_ sink: (any RemoteQuotaSink)?) { target.withLock { $0.sink = sink } }
        var sink: (any RemoteQuotaSink)? { target.withLock { $0.sink } }

        func deliver(_ event: RemoteHostEvent) {
            guard case .message(let message, _) = event.kind else { return }
            target.withLock { $0.sink }?.remoteQuota(message, hostId: event.hostId)
        }
    }

    /// Hands history events to the sink from the event queue.
    private final class HistoryRouter: Sendable {
        private struct Targets {
            weak var sink: (any RemoteHistorySink)?
            weak var hub: RemoteHub?
        }

        private let targets = OSAllocatedUnfairLock(initialState: Targets())

        func set(sink: (any RemoteHistorySink)?) { targets.withLock { $0.sink = sink } }
        func set(hub: RemoteHub) { targets.withLock { $0.hub = hub } }
        var sink: (any RemoteHistorySink)? { targets.withLock { $0.sink } }

        func deliver(_ event: RemoteHostEvent) {
            let (sink, hub) = targets.withLock { ($0.sink, $0.hub) }
            guard let sink, let hub else { return }
            sink.remoteHistory(event, requests: RemoteHistoryRequests(hub: hub))
        }
    }

    let hub: RemoteHub
    private let feed: RemoteFeed
    private weak var monitor: LiveMonitor?
    private let router: HistoryRouter
    private let quotaRouter: QuotaRouter
    /// The hosts last applied, with their display names.
    public private(set) var document = RemoteHostsDocument.empty
    private var networkEnabled = true
    private var priceTable = Data()
    private var quotaSwitches = RemoteQuotaSwitches()
    private var configured = false
    private var applied: Applied?

    public init(monitor: LiveMonitor, environment: RemoteEnvironment = RemoteEnvironment()) {
        let feed = monitor.remoteFeed
        let router = HistoryRouter()
        let quotaRouter = QuotaRouter()
        self.router = router
        self.quotaRouter = quotaRouter
        self.feed = feed
        self.monitor = monitor
        let queue = DispatchQueue(label: "app.agentspeed.remote.events", qos: .utility)
        hub = RemoteHub(environment: environment, eventQueue: queue) { event in
            switch Self.route(event) {
            case .live: feed.receive(event)
            case .history: router.deliver(event)
            case .quota: quotaRouter.deliver(event)
            case .both:
                feed.receive(event)
                router.deliver(event)
            }
        }
        router.set(hub: hub)
        monitor.remote = self
    }

    /// Receives the history events (the later daily-history sync); held weakly.
    public var historySink: (any RemoteHistorySink)? {
        get { router.sink }
        set { router.set(sink: newValue) }
    }

    /// Receives the hosts' quota lines; held weakly.
    public var quotaSink: (any RemoteQuotaSink)? {
        get { quotaRouter.sink }
        set { quotaRouter.set(newValue) }
    }

    /// The `history` requests, for a sink that answers outside `remoteHistory`.
    public var historyRequests: RemoteHistoryRequests { RemoteHistoryRequests(hub: hub) }

    /// The settings: hosts, the network master switch (REMOTE 2.3) and the current price table's JSON (METRICS 9.1).
    /// Applied only when something changed; a change of hosts or enabled sources may reconnect (`RemoteHub.update`).
    public func update(hosts: RemoteHostsDocument, networkEnabled: Bool, priceTable: Data,
                       quota: RemoteQuotaSwitches = RemoteQuotaSwitches()) {
        document = hosts
        self.networkEnabled = networkEnabled
        self.priceTable = priceTable
        quotaSwitches = quota
        configured = true
        apply()
    }

    /// The monitor's enabled sources or report watch changed.
    func monitorSettingsChanged() {
        apply()
    }

    /// *Retry now*.
    public func retryNow(hostId: String) { hub.retryNow(hostId: hostId) }

    /// The host's relay installs its Claude Code bridge again (a status line the user replaced, REMOTE 11.3).
    public func reapplyQuota(hostId: String) { hub.reapplyQuota(hostId: hostId) }

    public func requestHistory(hostId: String, digests: [String: String]) {
        hub.requestHistory(hostId: hostId, digests: digests)
    }

    /// The Mac woke from sleep (`NSWorkspace.didWakeNotification`).
    public func noteWake() { hub.noteWake() }

    public func noteNetworkAvailable() { hub.noteNetworkAvailable() }

    /// The system time zone changed.
    public func noteTimeZoneChange() { hub.noteTimeZoneChange() }

    public func startNetworkMonitoring() { hub.startNetworkMonitoring() }

    /// Quitting: every relay stops; nothing reconnects until the next `update`.
    public func stop() {
        hub.stopNetworkMonitoring()
        hub.stopAll()
        applied = nil
        configured = false
    }

    private func apply() {
        guard configured, let monitor else { return }
        let next = Applied(hosts: document.hosts, networkEnabled: networkEnabled,
                           sources: monitor.configuration.enabledSources, priceTable: priceTable, quota: quotaSwitches)
        if next != applied {
            applied = next
            let names = document.displayNames()
            feed.setHosts(document.hosts.map { RemoteHostInfo(id: $0.id, name: names[$0.id] ?? $0.automaticName) })
            hub.update(hosts: document.hosts, networkEnabled: networkEnabled, enabledSources: next.sources,
                       priceTable: priceTable, quota: quotaSwitches)
        }
        hub.setWatch(monitor.reportWatch != nil)
    }

    private enum Route {
        case live, history, both, quota
    }

    private nonisolated static func route(_ event: RemoteHostEvent) -> Route {
        switch event.kind {
        case .status:
            return .both
        case .historyDue:
            return .history
        case .message(let message, _):
            switch message {
            case .historyDay, .historyPerf, .historyDone, .historyProgress:
                return .history
            case .quota, .quotaRemoved, .quotaBridge:
                return .quota
            case .error(let error) where error.code == .historyFailed:
                return .history
            default:
                return .live
            }
        }
    }
}
