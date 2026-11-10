import Foundation

/// The protocol version this app speaks (REMOTE 5.1).
public enum RemoteProtocol {
    public static let version: Int64 = 1
}

/// The per-host limits of REMOTE 5.4 and 5.6.
public enum RemoteLimits {
    public static let agents = 4096
    /// Responses the app holds for today and the retention window; the ingestion prunes a host's oldest first.
    public static let responses = 200_000
    public static let actionsPerAgent = 64
    public static let factsPerModeList = 20
    public static let invalidLines = 100
    public static let agentIdBytes = 4096
    public static let keyBytes = 512
    public static let projectBytes = 256
    public static let cwdBytes = 4096
    public static let modelBytes = 256
    public static let providerBytes = 64
    public static let toolBytes = 128
    public static let errorDetailCharacters = 200
}

/// A decoded relay line (REMOTE 5.2). Agent ids are already prefixed with `ssh://<host id>/` (REMOTE 2.2); times are
/// the host's until the connection converts them (REMOTE 6).
public enum RelayMessage: Equatable, Sendable {
    case hello(RelayHello)
    /// Seeding progress, 0–1.
    case progress(Double)
    case agent(RemoteAgent)
    /// The prefixed id of an agent that left.
    case agentRemoved(id: String)
    /// The complete parent map, child → parent, both prefixed.
    case parents([String: String])
    case response(RemoteResponse)
    case tool(RemoteToolCompletion)
    case ready
    case status(RemoteSourceReport)
    case clock(id: Int64, nowMs: Int64)
    case historyDay(RemoteHistoryDay)
    case historyPerf(RemoteHistoryPerf)
    case historyDone(days: Int, analyzed: Int)
    /// History analysis progress, 0–1.
    case historyProgress(Double)
    case error(RelayError)
    /// A source's quota on the host (REMOTE 11.1); times are the host's until converted.
    case quota(RemoteQuotaReport)
    case quotaRemoved(hostId: String, source: SourceKind)
    case quotaBridge(RemoteQuotaBridge)
}

public struct RelayHello: Equatable, Sendable {
    public var protocolVersion: Int64
    /// The relay's app version string; empty when a relay of another protocol sent none.
    public var version: String
    public var arch: String
    /// The host's clock at the time of writing.
    public var nowMs: Int64
}

/// An agent's complete state (REMOTE 5.4): everything an `EngineAgent` needs, with the host alongside.
public struct RemoteAgent: Equatable, Sendable {
    public var hostId: String
    /// `ssh://<host id>/<relay id>`.
    public var id: String
    /// The id as the relay sent it (a path on the host); only used for display fallbacks.
    public var relayId: String
    public var source: SourceKind
    public var project: String?
    public var cwdPath: String?
    public var model: String?
    public var isSubagent: Bool
    public var phase: AgentPhase
    public var inFlightStartMs: Int64?
    public var lastLineMs: Int64?
    public var modes: ExecutionModes
    public var sessionModes: ExecutionModes
    public var configuredModes: ExecutionModes
    /// At most 64, oldest first.
    public var actions: [ToolAction]
}

/// A `response` line: the record with the prefixed agent id, its host and the relay's project name.
public struct RemoteResponse: Equatable, Sendable {
    public var hostId: String
    public var record: ResponseRecord
    /// True for the first emission of the key (METRICS 4.4).
    public var isCompletion: Bool
    public var project: String
}

/// A `tool` line (METRICS 11.10 *Counting*).
public struct RemoteToolCompletion: Equatable, Sendable {
    public var hostId: String
    /// Prefixed.
    public var agentId: String
    public var source: SourceKind
    public var completion: ToolCompletion
}

public struct RemoteSourceState: Equatable, Sendable {
    public var source: SourceKind
    public var available: Bool
    /// `sessionCounts` for the app's `todayStartMs`.
    public var sessions: Int
    public var unsupported: Bool
    public var configuredModes: ExecutionModes
}

/// A `status` line: the host's source availability and warnings.
public struct RemoteSourceReport: Equatable, Sendable {
    /// Sources this app knows, in the relay's order; entries for unknown sources are skipped.
    public var sources: [RemoteSourceState]
    public var malformed: Int
    public var unreadable: Int
    public var partialHistory: Bool
}

public struct RelayError: Equatable, Sendable {
    public enum Code: Equatable, Sendable {
        case badStart, historyFailed, `internal`
        case other(String)
    }

    public var code: Code
    /// At most 200 characters of plain text; displayed only, never logged.
    public var detail: String

    /// Every code but `historyFailed` ends the relay.
    public var isFatal: Bool { code != .historyFailed }
}
