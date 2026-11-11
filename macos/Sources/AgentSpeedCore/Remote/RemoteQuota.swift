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
    /// The account salt as 64 hex digits (QUOTAS 10.1), so that the host computes the same account keys; the relay
    /// keeps it in memory only. Never logged.
    public var salt: String?

    public init(sources: Set<SourceKind>, network: Bool, grokLive: Bool, claudeBridge: Bool, salt: String? = nil) {
        self.sources = sources.intersection(Self.hostSources)
        self.network = network
        self.grokLive = grokLive
        self.claudeBridge = claudeBridge
        self.salt = salt.flatMap { Self.isSalt($0) ? $0 : nil }
    }

    static func isSalt(_ text: String) -> Bool {
        text.utf8.count == 64 && text.utf8.allSatisfy { (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }
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
                                 claudeBridge: host.quotaClaudeCli, salt: switches.salt)
    }
}

/// The app's quota switches as they apply to every host.
public struct RemoteQuotaSwitches: Equatable, Sendable {
    public var sources: Set<SourceKind>
    public var grokLive: Bool
    /// Claude Code is enabled: the hosts' bridges may report its quota.
    public var claudeCode: Bool
    /// The account salt (QUOTAS 10.1) as 64 hex digits.
    public var salt: String?

    public init(sources: Set<SourceKind> = [], grokLive: Bool = false, claudeCode: Bool = true, salt: String? = nil) {
        self.sources = sources
        self.grokLive = grokLive
        self.claudeCode = claudeCode
        self.salt = salt
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
    /// The source's account key (QUOTAS 10.1), plan and tier (10.2); nil from older relays or without a salt.
    public var account: String?
    public var plan: String?
    public var tier: String?

    public init(hostId: String, snapshot: QuotaSnapshot, running: Bool, signedIn: Bool, status: QuotaRouteStatus,
                account: String? = nil, plan: String? = nil, tier: String? = nil) {
        self.hostId = hostId
        self.snapshot = snapshot
        self.running = running
        self.signedIn = signedIn
        self.status = status
        self.account = account
        self.plan = plan
        self.tier = tier
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
