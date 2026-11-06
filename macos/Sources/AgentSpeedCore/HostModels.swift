import Foundation

/// One host of the snapshot (DESIGN 13 *Dashboard → Hosts*, the settings status line): this computer or a remote host,
/// with its live value and today's totals over its cells.
public struct HostStatus: Identifiable, Equatable, Sendable {
    /// `local` or the remote host's id.
    public var id: String
    /// The display name (REMOTE 2.1); `local` for this computer, which the UI names `remote.local`.
    public var name: String
    /// The connection status; nil for this computer and for a remote host before its first status.
    public var remote: RemoteHostStatus?
    public var live: Rate
    /// The estimated part of `live` (METRICS 6.3).
    public var liveEstimated: Rate
    public var liveCost: LiveCost
    public var today: TokenTotals
    public var todayCost: CostTally
    /// Today's main sessions (DESIGN 5 `sessionCounts`): the relay's `status` for a remote host, as last reported.
    public var sessions: Int
    /// This computer, or a remote host whose relay is connected (after `ready`).
    public var isConnected: Bool

    public init(
        id: String,
        name: String,
        remote: RemoteHostStatus? = nil,
        live: Rate = .zero,
        liveEstimated: Rate = .zero,
        liveCost: LiveCost = .zero,
        today: TokenTotals = .zero,
        todayCost: CostTally = .zero,
        sessions: Int = 0,
        isConnected: Bool
    ) {
        self.id = id
        self.name = name
        self.remote = remote
        self.live = live
        self.liveEstimated = liveEstimated
        self.liveCost = liveCost
        self.today = today
        self.todayCost = todayCost
        self.sessions = sessions
        self.isConnected = isConnected
    }

    public var isLocal: Bool { id == RemoteHost.localId }

    /// The clock offset `δ = remote − local` in milliseconds (REMOTE 6); nil for this computer and before `hello`.
    public var clockOffsetMs: Int64? { remote?.clockOffsetMs }

    /// `remote.clock.skewed`: `|δ| ≥ 5 s`.
    public var isClockSkewed: Bool { remote?.isClockSkewed ?? false }

    /// Listed on the dashboard (DESIGN 13): connected, or it produced output today.
    public var isListed: Bool { isConnected || today.output > 0 || live.output > 0 }
}

/// One (host, source) cell of the snapshot: the per-source values of `SourceStatus` restricted to one host.
public struct HostSourceStatus: Identifiable, Equatable, Sendable {
    public var cell: HostSource
    /// Detected on its host: this computer's root, or the relay's `status` while the host is connected.
    public var available: Bool
    /// Its host reports the source's data in a format it cannot read.
    public var unsupported: Bool
    public var sessionCount: Int
    public var live: Rate
    public var liveEstimated: Rate
    public var liveCost: LiveCost
    public var intelligence: IntelligenceSums
    public var today: TokenTotals
    public var todayCost: CostTally
    /// Models of today's responses without a known price, most unpriced requests first.
    public var unpricedModels: [String]
    /// The configured values the host reports for the source.
    public var configuredModes: ExecutionModes

    public init(
        cell: HostSource,
        available: Bool,
        unsupported: Bool = false,
        sessionCount: Int = 0,
        live: Rate = .zero,
        liveEstimated: Rate = .zero,
        liveCost: LiveCost = .zero,
        intelligence: IntelligenceSums = .zero,
        today: TokenTotals = .zero,
        todayCost: CostTally = .zero,
        unpricedModels: [String] = [],
        configuredModes: ExecutionModes = .empty
    ) {
        self.cell = cell
        self.available = available
        self.unsupported = unsupported
        self.sessionCount = sessionCount
        self.live = live
        self.liveEstimated = liveEstimated
        self.liveCost = liveCost
        self.intelligence = intelligence
        self.today = today
        self.todayCost = todayCost
        self.unpricedModels = unpricedModels
        self.configuredModes = configuredModes
    }

    public var id: HostSource { cell }
    public var host: String { cell.host }
    public var source: SourceKind { cell.source }
}
