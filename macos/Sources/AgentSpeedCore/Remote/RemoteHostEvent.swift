import Foundation

/// What a host's connection reports, delivered in order on the caller's queue.
public struct RemoteHostEvent: Sendable {
    public enum Kind: Sendable {
        case status(RemoteHostStatus)
        /// A validated relay line, times already converted to local time (REMOTE 6). `seeded` is true before
        /// `ready` (REMOTE 5.5): apply it with `emitsEvents = false`.
        case message(RelayMessage, seeded: Bool)
        /// The history schedule (REMOTE 8.2: after `ready`, every 10 minutes, after local midnight) wants a sync:
        /// answer with `requestHistory(hostId:digests:)` and the digests held for the host.
        case historyDue
    }

    public var hostId: String
    /// Counts the host's connection attempts; the messages of one connection share it, so a reconnect is visible.
    public var connection: Int
    public var kind: Kind
}

/// What a connection sends with `start` and keeps in step afterwards.
public struct RemoteSessionSettings: Equatable, Sendable {
    /// The app's enabled sources (REMOTE 3.3); a change means a new connection (REMOTE 7 *Sources*).
    public var sources: Set<SourceKind>
    public var prices: RemotePriceTableJSON
    /// Live reporting's watch (METRICS 11.10).
    public var watch: Bool
    /// What the relay reads for quotas (REMOTE 11.1); sent with `start` and whenever it changes.
    public var quota: RemoteQuotaConfig?

    public init(sources: Set<SourceKind>, prices: RemotePriceTableJSON, watch: Bool = false, quota: RemoteQuotaConfig? = nil) {
        self.sources = sources
        self.prices = prices
        self.watch = watch
        self.quota = quota
    }
}

/// Everything a connection uses from the outside world, replaceable in tests.
public struct RemoteEnvironment: Sendable {
    public var transport: any RemoteTransport
    public var relays: RemoteRelayBundle
    public var scheduling: any RemoteScheduling
    public var timeZone: @Sendable () -> TimeZone
    public var processEnvironment: [String: String]

    public init(
        transport: any RemoteTransport = ProcessRemoteTransport(),
        relays: RemoteRelayBundle = .current(),
        scheduling: any RemoteScheduling = SystemRemoteScheduling(),
        timeZone: @escaping @Sendable () -> TimeZone = { TimeZone.current },
        processEnvironment: [String: String] = ProcessInfo.processInfo.environment
    ) {
        self.transport = transport
        self.relays = relays
        self.scheduling = scheduling
        self.timeZone = timeZone
        self.processEnvironment = processEnvironment
    }

    /// Local midnight of the day containing `date` in the app's time zone, in Unix milliseconds.
    func todayStartMs(at date: Date) -> Int64 {
        Clock.milliseconds(calendar.startOfDay(for: date))
    }

    /// The next local midnight after `date`.
    func nextMidnight(after date: Date) -> Date {
        let calendar = calendar
        let start = calendar.startOfDay(for: date)
        return calendar.date(byAdding: .day, value: 1, to: start) ?? date.addingTimeInterval(86_400)
    }

    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone()
        return calendar
    }
}
