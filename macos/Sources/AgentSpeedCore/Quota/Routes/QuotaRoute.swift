import Foundation

/// The phase 2 readers (QUOTAS 9.1), one per switch: Grok Build's log and its live request share the `grokBuild`
/// snapshot (the newer wins); Qoder's one account serves both Qoder sources.
public enum QuotaRoute: String, CaseIterable, Sendable {
    case grokBuild, grokBuildLive, devin, antigravity, kimi, gemini, droid, zcode, qoder, workbuddy

    public var source: SourceKind {
        switch self {
        case .grokBuild, .grokBuildLive: .grokBuild
        case .devin: .devin
        case .antigravity: .antigravity
        case .kimi: .kimi
        case .gemini: .gemini
        case .droid: .droid
        case .zcode: .zcode
        case .qoder: .qoder
        case .workbuddy: .workbuddy
        }
    }

    /// The agent processes that make the source run (QUOTAS 6, 9.2–9.9).
    public var agents: Set<SourceKind> {
        switch self {
        case .qoder: [.qoder, .qoderCli]
        default: [source]
        }
    }

    /// Reads a file on this computer; no network.
    public var isLocal: Bool { self == .grokBuild || self == .devin }

    /// Reads the browser's session (QUOTAS 9.10): off by default, turned on only after the user agrees.
    public var readsBrowser: Bool { self == .qoder || self == .workbuddy }

    /// The host a refusal names (`settings.quota.status.denied`).
    func host(region: QuotaRequests.ZaiRegion, site: String?) -> String {
        switch self {
        case .grokBuild, .grokBuildLive: QuotaHosts.grok
        case .devin, .antigravity: QuotaHosts.loopback
        case .kimi: QuotaHosts.kimi
        case .gemini: QuotaHosts.gemini
        case .droid: QuotaHosts.factory
        case .zcode: region.host
        case .qoder: site ?? QuotaHosts.qoder
        case .workbuddy: QuotaHosts.workbuddy
        }
    }

    /// The sites whose cookies a browser route reads, in the order tried.
    var cookieSites: [String] {
        switch self {
        case .qoder: [QuotaHosts.qoder, QuotaHosts.qoderCn]
        case .workbuddy: ["workbuddy.cn"]
        default: []
        }
    }
}

/// What one round may do (QUOTAS 9.1): the master switch, the switches that are on, the agents that run and whether
/// the dashboard is open.
public struct QuotaVendorRound: Equatable, Sendable {
    public var nowMs: Int64
    public var networkEnabled: Bool
    public var routes: Set<QuotaRoute>
    public var running: Set<SourceKind>
    public var dashboardOpen: Bool
    public var zaiRegion: QuotaRequests.ZaiRegion
    /// Local files are read again at once (a switch turned on, Settings opened).
    public var readsLocalNow: Bool

    public init(nowMs: Int64, networkEnabled: Bool, routes: Set<QuotaRoute>, running: Set<SourceKind>,
                dashboardOpen: Bool, zaiRegion: QuotaRequests.ZaiRegion = .global, readsLocalNow: Bool = false) {
        self.nowMs = nowMs
        self.networkEnabled = networkEnabled
        self.routes = routes
        self.running = running
        self.dashboardOpen = dashboardOpen
        self.zaiRegion = zaiRegion
        self.readsLocalNow = readsLocalNow
    }
}

/// A route's state as the UI shows it: its snapshot, its status line and whether it counts as signed in.
public struct QuotaRouteReading: Equatable, Sendable {
    public var snapshot: QuotaSnapshot?
    public var status: QuotaRouteStatus?
    public var signedIn = false
    /// The host a refusal or failure concerns.
    public var host: String?

    public init(snapshot: QuotaSnapshot? = nil, status: QuotaRouteStatus? = nil, signedIn: Bool = false, host: String? = nil) {
        self.snapshot = snapshot
        self.status = status
        self.signedIn = signedIn
        self.host = host
    }
}

/// Every route's reading after a round.
public struct QuotaVendorReadings: Equatable, Sendable {
    public var routes: [QuotaRoute: QuotaRouteReading] = [:]
    /// An Antigravity language server runs (QUOTAS 9.4 *Runs*).
    public var antigravityRuns = false

    public init(routes: [QuotaRoute: QuotaRouteReading] = [:], antigravityRuns: Bool = false) {
        self.routes = routes
        self.antigravityRuns = antigravityRuns
    }

    /// The source's snapshot: Grok Build's newer of log and live, Qoder's for both Qoder sources.
    public func snapshot(_ source: SourceKind) -> QuotaSnapshot? {
        let key: SourceKind = source == .qoderCli ? .qoder : source
        let found = QuotaRoute.allCases.filter { $0.source == key }.compactMap { routes[$0]?.snapshot }
        guard var newest = found.max(by: { $0.observedMs < $1.observedMs }) else { return nil }
        newest.source = source
        return newest
    }

    public func signedIn(_ source: SourceKind) -> Bool {
        let key: SourceKind = source == .qoderCli ? .qoder : source
        return QuotaRoute.allCases.contains { $0.source == key && routes[$0]?.signedIn == true }
    }
}
