import Foundation
import os

/// The phase 2 readers (QUOTAS 9): local files when they change (checked every 60 s), the loopback language server,
/// and the vendor requests with the agents' own sign-ins, the user's ZCode key and the browser's session, each under
/// the rules of 9.1 and NETWORK 9. Credentials are read right before their request and live only inside it.
public actor QuotaVendorService {
    static let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "quota")
    static let localIntervalMs: Int64 = 60_000
    /// A local route counts as signed in while its snapshot is this fresh (QUOTAS 9.1).
    static let localSignedInMs: Int64 = 24 * 3_600_000
    /// Grok's token is not used within a minute of its expiry (QUOTAS 9.2).
    static let grokExpiryMarginMs: Int64 = 60_000

    struct RouteState {
        var cadence = QuotaCadence()
        /// The credential the snapshot belongs to.
        var account: QuotaFingerprint?
        var reading = QuotaRouteReading()
        /// Browser routes: when the session was last read (they only read it when a request is due).
        var checkedMs: Int64?
        /// Gemini: the project of the current token, learned once per token.
        var geminiProject: (credential: QuotaFingerprint, project: String?)?
    }

    let environment: QuotaVendorEnvironment
    var states: [QuotaRoute: RouteState] = [:]
    var grokLog: GrokLogReader
    var devin: DevinPlanReader
    var localCheckedMs: Int64?
    /// Antigravity processes whose token a 401/403 made the reader forget.
    var forgottenTokens: Set<Int32> = []
    var antigravityRuns = false
    private var inRound = false

    public init(environment: QuotaVendorEnvironment) {
        self.environment = environment
        grokLog = GrokLogReader(file: GrokLogReader.defaultFile(home: environment.home, environment: environment.variables))
        devin = DevinPlanReader(folders: DevinPlanReader.defaultFolders(home: environment.home))
    }

    /// One round: routes that are off forget their state; the others read, ask when due, and report.
    public func refresh(_ round: QuotaVendorRound) async -> QuotaVendorReadings {
        guard !inRound else { return readings }
        inRound = true
        defer { inRound = false }
        for route in QuotaRoute.allCases where !round.routes.contains(route) { states[route] = nil }
        // Turning the browser routes off and on again asks the keychain again.
        if !round.routes.contains(where: \.readsBrowser) { environment.browser.resetRefusals() }
        readLocal(round)
        var plans: [FetchPlan] = []
        for route in round.routes.sorted(by: { $0.rawValue < $1.rawValue }) where !route.isLocal {
            if let plan = plan(route, round) { plans.append(plan) }
        }
        if !plans.isEmpty {
            let transport = environment.transport
            let now = environment.now
            let results = await withTaskGroup(of: FetchResult.self) { group -> [FetchResult] in
                for plan in plans { group.addTask { await plan.execute(transport, now: now) } }
                var collected: [FetchResult] = []
                for await result in group { collected.append(result) }
                return collected
            }
            for result in results { apply(result, round) }
        }
        return readings
    }

    /// Forgets a route's state: its credential changed in the app (a new ZCode key or region).
    public func forget(_ route: QuotaRoute) {
        states[route] = nil
    }

    public var readings: QuotaVendorReadings {
        QuotaVendorReadings(routes: states.mapValues(\.reading), antigravityRuns: antigravityRuns)
    }

    // MARK: Local files (QUOTAS 9.2 log, 9.3)

    private func readLocal(_ round: QuotaVendorRound) {
        let due = round.readsLocalNow || localCheckedMs.map { round.nowMs - $0 >= Self.localIntervalMs } ?? true
        guard due else {
            refreshLocalSignIn(round.nowMs)
            return
        }
        localCheckedMs = round.nowMs
        if round.routes.contains(.grokBuild) {
            grokLog.refresh()
            update(.grokBuild, snapshot: grokLog.snapshot, nowMs: round.nowMs)
        }
        if round.routes.contains(.devin) {
            devin.refresh(timeZone: environment.timeZone())
            update(.devin, snapshot: devin.snapshot, nowMs: round.nowMs)
        }
    }

    private func update(_ route: QuotaRoute, snapshot: QuotaSnapshot?, nowMs: Int64) {
        var state = states[route] ?? RouteState()
        state.reading.snapshot = snapshot
        state.reading.status = snapshot.map { .ok($0.observedMs) } ?? .noCredential
        state.reading.signedIn = snapshot.map { nowMs - $0.observedMs <= Self.localSignedInMs } ?? false
        states[route] = state
    }

    private func refreshLocalSignIn(_ nowMs: Int64) {
        for route in [QuotaRoute.grokBuild, .devin] {
            guard var state = states[route], let snapshot = state.reading.snapshot else { continue }
            state.reading.signedIn = nowMs - snapshot.observedMs <= Self.localSignedInMs
            states[route] = state
        }
    }

    // MARK: Gates (QUOTAS 9.1)

    /// Whether a request may go out now for `credential`; otherwise the status says why not.
    func gate(_ route: QuotaRoute, _ round: QuotaVendorRound, credential: QuotaFingerprint, state: inout RouteState,
              site: String? = nil) -> Bool {
        if let account = state.account, account != credential {
            state.reading.snapshot = nil
            state.reading.status = nil
        }
        state.account = credential
        state.reading.host = route.host(region: round.zaiRegion, site: site)
        guard round.networkEnabled else {
            state.reading.status = .networkOff
            return false
        }
        if state.cadence.isDenied(credential) {
            state.reading.status = .denied(host: route.host(region: round.zaiRegion, site: site))
            return false
        }
        guard asks(route, round) else {
            if state.reading.status == nil || state.reading.status == .networkOff { state.reading.status = .notRunning }
            return false
        }
        return state.cadence.isDue(nowMs: round.nowMs, credential: credential)
    }

    /// The agent runs, or the dashboard is open.
    func asks(_ route: QuotaRoute, _ round: QuotaVendorRound) -> Bool {
        round.dashboardOpen || !route.agents.isDisjoint(with: round.running)
            || (route == .antigravity && antigravityRuns)
    }

    // MARK: Results

    /// A browser route's session as found: none, the refused one again, or a session (another sign-in drops the old
    /// account's numbers).
    private func applyBrowser(_ result: FetchResult, _ round: QuotaVendorRound, _ state: inout RouteState) {
        switch result.skipped {
        case .noSession:
            state.reading.status = .noBrowser
            state.reading.signedIn = false
            state.cadence.record(.failure, nowMs: round.nowMs, credential: state.cadence.credential ?? result.credential)
        case .refused:
            state.reading.status = .denied(host: result.route.host(region: round.zaiRegion, site: result.site))
            state.reading.signedIn = false
        case nil:
            if let account = state.account, account != result.credential { state.reading.snapshot = nil }
            state.account = result.credential
            state.reading.host = result.route.host(region: round.zaiRegion, site: result.site)
            state.reading.signedIn = result.outcome != .denied
        }
    }

    private func apply(_ result: FetchResult, _ round: QuotaVendorRound) {
        var state = states[result.route] ?? RouteState()
        if result.route.readsBrowser {
            applyBrowser(result, round, &state)
            states[result.route] = state
            if result.skipped != nil { return }
        } else {
            guard state.account == result.credential || state.account == nil else { return }
        }
        state.cadence.record(result.outcome, nowMs: round.nowMs, credential: result.credential)
        if let project = result.geminiProject { state.geminiProject = (result.credential, project) }
        forgottenTokens.formUnion(result.forgetTokens)
        let host = state.reading.host ?? result.route.host(region: round.zaiRegion, site: result.site)
        switch result.outcome {
        case .success:
            let observed = result.observedMs ?? round.nowMs
            state.reading.snapshot = QuotaSnapshot(source: result.route.source, observedMs: observed, windows: result.windows)
            state.reading.status = .ok(observed)
            if result.route == .antigravity { state.reading.signedIn = true }
        case .denied:
            state.reading.status = .denied(host: host)
            if result.route == .antigravity || result.route.readsBrowser { state.reading.signedIn = false }
        case .failure:
            state.reading.status = .failed
            if result.route == .antigravity { state.reading.signedIn = false }
        }
        if result.outcome != .success {
            Self.logger.notice("Quota request failed: \(host, privacy: .public) \(result.statusCode.map(String.init) ?? "-", privacy: .public)")
        }
        states[result.route] = state
    }
}
