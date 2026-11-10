import Foundation

/// Owns one connection per enabled host and keeps them in step with the settings (REMOTE 2.3, 4.4, 7 *Sources*).
/// UI-agnostic: the app forwards wake-from-sleep with `noteWake()`; network changes come from `RemoteNetworkWatcher`
/// (or `noteNetworkAvailable()`).
///
/// `@unchecked Sendable`: every mutable property is only touched on `queue`.
public final class RemoteHub: @unchecked Sendable {
    private let environment: RemoteEnvironment
    private let eventQueue: DispatchQueue
    private let onEvent: @Sendable (RemoteHostEvent) -> Void
    private let queue = DispatchQueue(label: "app.agentspeed.remote.hub", qos: .utility)

    private var connections: [String: RemoteConnection] = [:]
    /// The host value each connection was made for.
    private var connectedHosts: [String: RemoteHost] = [:]
    /// The status the hub itself reported for a host without a connection (`disabled`, `networkOff`, invalid).
    private var idleStatuses: [String: RemoteStatus] = [:]
    private var settings: RemoteSessionSettings?
    private var networkWatcher: RemoteNetworkWatcher?
    /// Removed hosts whose relay restores their Claude Code status line before it stops (REMOTE 2.4).
    private var retiring: [String: RemoteConnection] = [:]

    public init(
        environment: RemoteEnvironment = RemoteEnvironment(),
        eventQueue: DispatchQueue,
        onEvent: @escaping @Sendable (RemoteHostEvent) -> Void
    ) {
        self.environment = environment
        self.eventQueue = eventQueue
        self.onEvent = onEvent
    }

    deinit {
        networkWatcher?.stop()
        for connection in connections.values { connection.stop() }
        for connection in retiring.values { connection.stop() }
    }

    /// Applies the settings: starts the connections of enabled, valid hosts and stops the others (removed or disabled
    /// hosts; every host while the master switch is off, which every host then shows, REMOTE 2.3). A host whose
    /// destination or folder changed reconnects, and every host does when the enabled sources change. A new price
    /// table goes to the running relays without a reconnect. `priceTable` is the current table's JSON; an invalid one
    /// keeps the previous (else the bundled) table.
    /// `quota`: the app's quota switches; each host's config adds its own Claude Code bridge (REMOTE 11.1) and goes
    /// to its relay without a reconnect. A removed host whose bridge is on restores its status line first.
    public func update(hosts: [RemoteHost], networkEnabled: Bool, enabledSources: Set<SourceKind>, priceTable: Data,
                       quota: RemoteQuotaSwitches = RemoteQuotaSwitches()) {
        queue.sync {
            let prices = RemotePriceTableJSON(priceTable) ?? settings?.prices ?? Self.bundledPrices
            let watch = settings?.watch ?? false
            let shared = RemoteSessionSettings(sources: enabledSources, prices: prices, watch: watch)
            let sourcesChanged = settings.map { $0.sources != enabledSources } ?? false
            settings = shared

            let valid = RemoteHostsDocument.normalized(hosts)
            let ids = Set(valid.map(\.id))
            for id in connections.keys where !ids.contains(id) {
                if connectedHosts[id]?.quotaClaudeCli == true, let connection = connections.removeValue(forKey: id) {
                    connectedHosts[id] = nil
                    retire(id, connection)
                } else {
                    removeConnection(id, finalStatus: nil)
                }
            }
            for id in idleStatuses.keys where !ids.contains(id) { idleStatuses[id] = nil }

            for host in valid {
                let idle: RemoteStatus? = !networkEnabled ? .networkOff
                    : !host.enabled ? .disabled
                    : !host.isConnectable ? .invalidDestination
                    : nil
                if let idle {
                    if connections[host.id] != nil {
                        removeConnection(host.id, finalStatus: idle)
                        idleStatuses[host.id] = idle
                    } else {
                        reportIdle(host.id, idle)
                    }
                    continue
                }
                idleStatuses[host.id] = nil
                var next = shared
                next.quota = RemoteQuotaConfig.forHost(host, switches: quota, networkEnabled: networkEnabled,
                                                       enabled: enabledSources)
                if let existing = connections[host.id] {
                    let edited = connectedHosts[host.id]?.connectionIdentity != host.connectionIdentity
                    if edited || sourcesChanged {
                        removeConnection(host.id, finalStatus: nil)
                    } else {
                        connectedHosts[host.id] = host
                        existing.updatePrices(prices)
                        if let config = next.quota { existing.updateQuota(config) }
                        continue
                    }
                }
                let connection = RemoteConnection(host: host, settings: next, environment: environment,
                                                  eventQueue: eventQueue, onEvent: onEvent)
                connections[host.id] = connection
                connectedHosts[host.id] = host
                connection.start()
            }
        }
    }

    /// Sends the host's quota config off and on again: its relay turns the bridge off (leaving a replaced status line
    /// alone) and installs it again (QUOTAS 5.2, 5.3).
    public func reapplyQuota(hostId: String) {
        queue.sync { connections[hostId]?.reapplyQuota() }
    }

    /// *Retry now* (or a fresh look after the user fixed something on the host).
    public func retryNow(hostId: String) {
        queue.sync { connections[hostId]?.retryNow() }
    }

    /// Answers `historyDue` with the digests the app holds for the host (`"<day>/<source>"` → digest).
    public func requestHistory(hostId: String, digests: [String: String]) {
        queue.sync { connections[hostId]?.requestHistory(digests: digests) }
    }

    /// Live reporting's watch (METRICS 11.10): `tool` lines are sent only while on.
    public func setWatch(_ on: Bool) {
        queue.sync {
            settings?.watch = on
            for connection in connections.values { connection.setWatch(on) }
        }
    }

    /// The Mac woke from sleep: hosts waiting for their back-off retry at once.
    public func noteWake() {
        queue.sync { for connection in connections.values { connection.noteWake() } }
    }

    /// The network came back (same as a wake for the connections).
    public func noteNetworkAvailable() {
        noteWake()
    }

    public func noteTimeZoneChange() {
        queue.sync { for connection in connections.values { connection.noteTimeZoneChange() } }
    }

    /// Quitting: every relay's stdin closes and every `ssh` ends.
    public func stopAll() {
        queue.sync {
            for id in Array(connections.keys) { removeConnection(id, finalStatus: nil) }
            for connection in retiring.values { connection.stop() }
            retiring.removeAll()
        }
    }

    /// Hosts that are restoring their status line before they stop (tests).
    var retiringHostIds: Set<String> {
        queue.sync { Set(retiring.keys) }
    }

    func retiringConnection(_ hostId: String) -> RemoteConnection? {
        queue.sync { retiring[hostId] }
    }

    /// Watches the network path (NWPathMonitor) and calls `noteNetworkAvailable()` when it becomes usable again.
    public func startNetworkMonitoring() {
        queue.sync {
            guard networkWatcher == nil else { return }
            let watcher = RemoteNetworkWatcher { [weak self] in self?.noteNetworkAvailable() }
            networkWatcher = watcher
            watcher.start()
        }
    }

    public func stopNetworkMonitoring() {
        queue.sync {
            networkWatcher?.stop()
            networkWatcher = nil
        }
    }

    /// Hosts with a connection (tests and diagnostics).
    public var connectedHostIds: Set<String> {
        queue.sync { Set(connections.keys) }
    }

    func connection(_ hostId: String) -> RemoteConnection? {
        queue.sync { connections[hostId] }
    }

    // MARK: - Private

    private func retire(_ id: String, _ connection: RemoteConnection) {
        retiring[id]?.stop()
        retiring[id] = connection
        connection.retire()
        queue.asyncAfter(deadline: .now() + RemoteConnection.retireLimit + 5) { [weak self] in
            guard let self, self.retiring[id] === connection else { return }
            connection.stop()
            self.retiring[id] = nil
        }
    }

    private func removeConnection(_ id: String, finalStatus: RemoteStatus?) {
        connections.removeValue(forKey: id)?.stop(finalStatus: finalStatus)
        connectedHosts[id] = nil
    }

    private func reportIdle(_ id: String, _ status: RemoteStatus) {
        guard idleStatuses[id] != status else { return }
        idleStatuses[id] = status
        let event = RemoteHostEvent(hostId: id, connection: 0, kind: .status(RemoteHostStatus(status: status)))
        let onEvent = onEvent
        eventQueue.async { onEvent(event) }
    }

    private static let bundledPrices = RemotePriceTableJSON(Data(BundledPriceAssets.prices.utf8))!
}
