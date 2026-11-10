import Foundation

/// What the app asks a host's relay to read (REMOTE 11.1): the sources whose quota switch is on, the master switch,
/// Grok's live request and the host's Claude Code bridge.
public struct RemoteQuotaConfig: Equatable, Sendable {
    /// The sources a host can read (REMOTE 11); every other key is left out.
    public static let hostSources: [SourceKind] = [.codex, .claudeCode, .grokBuild, .kimi, .gemini, .droid, .antigravity]

    public var sources: Set<SourceKind>
    public var network: Bool
    public var grokLive: Bool
    public var claudeBridge: Bool

    public init(sources: Set<SourceKind>, network: Bool, grokLive: Bool, claudeBridge: Bool) {
        self.sources = sources.intersection(Self.hostSources)
        self.network = network
        self.grokLive = grokLive
        self.claudeBridge = claudeBridge
    }

    public static let off = RemoteQuotaConfig(sources: [], network: false, grokLive: false, claudeBridge: false)

    /// The app-wide switches for one host: Claude Code is that host's own bridge switch. A source the app does not
    /// read (`enabled`) reads no quota on the host either (QUOTAS 9.1); the bridge itself stays as the host's switch.
    public static func forHost(_ host: RemoteHost, switches: RemoteQuotaSwitches, networkEnabled: Bool,
                               enabled: Set<SourceKind>? = nil) -> RemoteQuotaConfig {
        var sources = switches.sources.subtracting([.claudeCode])
        if host.quotaClaudeCli, switches.claudeCode { sources.insert(.claudeCode) }
        if let enabled { sources.formIntersection(enabled) }
        return RemoteQuotaConfig(sources: sources, network: networkEnabled, grokLive: switches.grokLive && sources.contains(.grokBuild),
                                 claudeBridge: host.quotaClaudeCli)
    }
}

/// The app's quota switches as they apply to every host.
public struct RemoteQuotaSwitches: Equatable, Sendable {
    public var sources: Set<SourceKind>
    public var grokLive: Bool
    /// Claude Code is enabled: the hosts' bridges may report its quota.
    public var claudeCode: Bool

    public init(sources: Set<SourceKind> = [], grokLive: Bool = false, claudeCode: Bool = true) {
        self.sources = sources
        self.grokLive = grokLive
        self.claudeCode = claudeCode
    }
}

/// A `quota` line (REMOTE 11.1): one source's snapshot on a host, with whether its agent runs and is signed in there.
public struct RemoteQuotaReport: Equatable, Sendable {
    public static let maxWindows = 16
    public static let maxNameCharacters = 64
    public static let maxMinutes = 527_040
    public static let maxUsedPercent = 1000.0

    public var hostId: String
    public var snapshot: QuotaSnapshot
    public var running: Bool
    public var signedIn: Bool
    public var status: QuotaRouteStatus

    public init(hostId: String, snapshot: QuotaSnapshot, running: Bool, signedIn: Bool, status: QuotaRouteStatus) {
        self.hostId = hostId
        self.snapshot = snapshot
        self.running = running
        self.signedIn = signedIn
        self.status = status
    }

    public var source: SourceKind { snapshot.source }
}

/// A `quotaBridge` line: the host's Claude Code status line bridge (QUOTAS 5.4 on the host).
public struct RemoteQuotaBridge: Equatable, Sendable {
    public enum State: String, Sendable, CaseIterable {
        case off, waiting, ok, replaced, unreadable, failed
    }

    public var hostId: String
    public var state: State
    /// Abbreviated with `~`; displayed only.
    public var settingsPath: String

    public init(hostId: String, state: State, settingsPath: String) {
        self.hostId = hostId
        self.state = state
        self.settingsPath = settingsPath
    }
}
