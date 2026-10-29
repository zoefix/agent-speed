import AgentSpeedCore
import AppKit
import Observation

/// Live reporting (DESIGN 11, METRICS 11, REPORTING): the user's settings and service identities, the discovery
/// document, the consent sheet, the report scheduler fed by every live engine tick, delivery, and the account actions
/// (rename, API key, forget, reset), and the dashboard invitation. Nothing is requested before the user confirmed a
/// service — in the consent sheet or by accepting the invitation — and nothing at all while the network master switch
/// is off.
@MainActor
@Observable
final class ReportController {
    enum ServiceState: Equatable {
        case unknown
        case checking
        case ready(ReportDiscovery)
        case failed(ReportFailure)
        case notSupported
        case invalid
        case gone
    }

    /// The consent sheet's form (DESIGN 11, Consent sheet). The device id exists only in memory until Start.
    struct ConsentDraft: Equatable {
        var service: ReportServiceURL
        var discovery: ReportDiscovery
        var deviceId: String
        var username: String
        var apiKey = ""
        var sections: Set<ReportSection>
        /// The identity is registered; the user reviews changed details only.
        var isReview: Bool
        var isStarting = false
        var usernameError: String?
        var apiKeyError: String?
        /// A localization key (`report.error.*`, `report.status.gone`); never a status code.
        var failure: String?
        /// `username_taken` with an API key set: offer `report.username.transfer`.
        var offersTransfer = false
    }

    enum RenameState: Equatable {
        /// `failed` holds a localization key.
        case idle, working, updated, failed(String)
    }

    enum ForgetState: Equatable {
        case idle, working, done, failed(ReportFailure)
    }

    let profile = ReportProfileController()
    var preferences: ReportPreferences
    var serviceState: ServiceState = .unknown
    var consent: ConsentDraft?
    var lastDeliveryAt: Date?
    var rank: Int?
    var problem: ReportProblem?
    var retryReason: ReportFailure?
    var rateLimitedUntil: Date?
    var registering = false
    /// Setting up after the invitation: the last registration attempt failed for a passing reason.
    var registrationFailure: ReportFailure?
    /// Setting up after the invitation: what the invitation did not cover, so the consent sheet must finish it.
    var inviteGap: ReportInvite.Gap?
    /// The invitation card shows `report.invite.done` for a moment after it was accepted.
    var inviteJustAccepted = false
    var mode: ReportScheduler.Mode = .quiet
    var renameState: RenameState = .idle
    var forgetState: ForgetState = .idle
    /// The WebSocket session's status inputs (DESIGN 11, Status); nil while no session runs.
    var streamState: ReportStreamState?
    /// The WebSocket counters and latency for the stats line, refreshed at most once a second.
    var streamStats: ReportStreamStats?

    @ObservationIgnored let settings: AppSettings
    @ObservationIgnored let store: SpeedStore
    @ObservationIgnored let traffic: TrafficStore
    @ObservationIgnored let priceBook: PriceBook
    @ObservationIgnored let keyStore: ReportKeyStore
    @ObservationIgnored var api: ReportAPI
    @ObservationIgnored let client: ReportClientInfo
    @ObservationIgnored let userAgent: String
    @ObservationIgnored let defaults: UserDefaults
    @ObservationIgnored var run: ReportRun?
    /// The WebSocket transport session; only while WebSocket is effective (HTTP's `run` is nil then).
    @ObservationIgnored var stream: ReportStreamRun?
    @ObservationIgnored let streamTransport: any ReportStreamTransport
    /// The earliest observation of the snapshots since the last stream tick (METRICS 11.9, Observations).
    @ObservationIgnored var pendingObservation: Int64?
    @ObservationIgnored var statsPublishedAt: Date?
    @ObservationIgnored var statsPending = false
    @ObservationIgnored var pendingEvents: [ReportScheduler.Event] = []
    @ObservationIgnored var signer: (any ReportSigner)?
    @ObservationIgnored var pendingSigner: (any ReportSigner)?
    @ObservationIgnored var apiKey: String?
    /// The consent sheet's key, created at Start and deleted again if the registration fails.
    @ObservationIgnored var draftSigner: (any ReportSigner)?
    @ObservationIgnored var clockCorrection: Int64 = 0
    @ObservationIgnored var strikes = 0
    @ObservationIgnored var lastReRegister: Date?
    @ObservationIgnored var renamedForMismatch = false
    @ObservationIgnored var rotating = false
    @ObservationIgnored var networkWasEnabled: Bool
    /// The latest tick's values; built from `lastSnapshot` only when reporting or a preview needs them.
    @ObservationIgnored var cachedSample = ReportSample()
    @ObservationIgnored var lastSnapshot: SpeedSnapshot?
    @ObservationIgnored var monitorRunning = false
    @ObservationIgnored var liveToken: UUID?
    @ObservationIgnored var loops: [ObservationLoop] = []
    @ObservationIgnored var refreshTask: Task<Void, Never>?
    @ObservationIgnored var refreshTimer: Timer?
    @ObservationIgnored var pauseTimer: Timer?
    @ObservationIgnored var quitReply: (() -> Void)?
    @ObservationIgnored var active = false
    @ObservationIgnored var previewWindow: ReportWindow?
    @ObservationIgnored var previewStartedAt: Int64?
    @ObservationIgnored var previewOpenings = 0
    @ObservationIgnored let previewRunId = ReportCanonical.newUUID()
    /// The preview's device id before this device exists for a service (it is created only on consent).
    @ObservationIgnored let previewDeviceId = ReportCanonical.newUUID()
    /// The user asked to turn reporting on in Settings and the consent sheet waits for the document.
    @ObservationIgnored var consentWanted = false
    @ObservationIgnored var setupTimer: Timer?
    /// Used when the computer name leaves nothing valid; stable for the session.
    @ObservationIgnored let fallbackUsername = ReportUsername.random()
    /// The performance score's watch, completions and statistics ledger (METRICS 11.10); never persisted.
    @ObservationIgnored var perfTracker = ReportPerfTracker()
    /// Hands the watch's epoch (nil: off) to the live monitor, which counts completions in it.
    @ObservationIgnored var perfWatchHandler: ((Int?) -> Void)?

    static let rotationAge: TimeInterval = 365 * 86_400
    static let refreshInterval: TimeInterval = 86_400
    static let staleAfter: TimeInterval = 7 * 86_400
    static let reRegisterInterval: TimeInterval = 3600

    init(
        settings: AppSettings, store: SpeedStore, traffic: TrafficStore, priceBook: PriceBook, appData: URL,
        transport: any ReportTransport = URLSessionReportTransport(),
        streamTransport: any ReportStreamTransport = URLSessionReportStream(),
        version: String = UpdateConfiguration.bundleVersion
    ) {
        self.settings = settings
        self.store = store
        self.traffic = traffic
        self.priceBook = priceBook
        defaults = settings.storage
        keyStore = ReportKeyStore.standard(appData: appData)
        client = ReportClientInfo.current(version: version)
        userAgent = "AgentSpeed/\(client.version) (macOS)"
        api = ReportAPI(transport: transport, userAgent: userAgent)
        self.streamTransport = streamTransport
        preferences = ReportPreferences.load(from: settings.storage)
        networkWasEnabled = settings.networkEnabled
        profile.owner = self
    }

    // MARK: - Reading

    /// The selected service URL; nil for an empty or invalid custom URL.
    var selectedService: ReportServiceURL? {
        switch preferences.preset {
        case .codexRadar: try? ReportServiceURL.parse(ReportPreset.codexRadarURL).get()
        case .custom: try? ReportServiceURL.parse(preferences.customURL).get()
        }
    }

    var identity: ReportIdentity? { preferences.identity(for: selectedService) }

    /// The selected service's transport (DESIGN 11, Transport): the user's choice, WebSocket by default.
    var transport: ReportTransportKind { preferences.transport(for: selectedService) }

    /// The transport reports actually use (METRICS 11.9): WebSocket when it is chosen and the document has a valid
    /// WebSocket capability, otherwise HTTP. Runs, the status line and the preview follow it.
    var effectiveTransport: ReportTransportKind {
        transport == .websocket && discovery?.websocket != nil ? .websocket : .http
    }

    /// WebSocket is chosen but the document lacks the capability, so reports go over HTTP meanwhile (DESIGN 11,
    /// item 2b); false while the document is unknown.
    var webSocketUnavailable: Bool {
        transport == .websocket && discovery.map { $0.websocket == nil } == true
    }

    /// The WebSocket transport's own status line, when one applies (DESIGN 11, Status).
    var streamIssue: ReportStreamIssue? {
        guard effectiveTransport == .websocket, isOn, let streamState else { return nil }
        return ReportStreamIssue(streamState, lastDelivery: lastDeliveryAt)
    }

    /// Identities of the services that are not selected (DESIGN 11, Other services).
    var otherIdentities: [ReportIdentity] {
        let selected = selectedService?.origin
        return preferences.identities.filter { $0.id != selected && $0.isRegistered }
    }

    /// The discovery document to show: the fresh one, else the stored copy.
    var discovery: ReportDiscovery? {
        if case .ready(let document) = serviceState { return document }
        return identity?.discovery
    }

    /// The service's display name: its own name, else the preset's.
    var serviceName: String? {
        discovery?.name ?? (preferences.preset == .codexRadar ? ReportPreset.codexRadarName : nil)
    }

    /// The host that receives the reports: the API host, else the service host.
    var destinationHost: String? {
        discovery?.api.host ?? selectedService?.host
    }

    /// Reporting is on and confirmed for the selected service (the menu, dashboard and About show it).
    var isOn: Bool { preferences.enabled && identity?.isRegistered == true }

    var isPaused: Bool { preferences.isPaused }

    /// The pin no longer matches the service's details (REPORTING 3.3).
    var needsReview: Bool {
        guard let identity, let pin = identity.pin, let current = identity.discovery else { return false }
        return pin.needsReview(current, hasAPIKey: identity.hasAPIKey)
    }

    /// The stored document is older than 7 days and could not be refreshed (REPORTING 3.4): reporting waits.
    var isDiscoveryStale: Bool {
        guard let fetched = identity?.discoveryFetchedAt else { return identity?.isRegistered == true }
        return Date().timeIntervalSince(fetched) >= Self.staleAfter
    }

    /// Optional sections that are both listed by the service and switched on (REPORTING 6.1).
    var effectiveSections: Set<ReportSection> {
        guard let discovery else { return [] }
        return preferences.sections.intersection(discovery.sections)
    }

    /// The traffic section is actually reported (the Network Traffic pane's note).
    var reportsTraffic: Bool {
        isOn && effectiveSections.contains(.traffic)
    }

    /// The failed fetch of the selected service, as the pane says it (DESIGN 11, Service).
    var serviceIssue: ReportServiceIssue? {
        switch serviceState {
        case .failed(let failure): ReportServiceIssue(failure)
        case .notSupported: .notSupported
        case .invalid: .invalid
        case .unknown, .checking, .ready, .gone: nil
        }
    }

    /// Reporting is on but this device is not registered yet: the invitation's setup, or a registration to redo.
    var isSettingUp: Bool { preferences.enabled && identity?.isRegistered != true }

    /// The rules a username is checked against: the known document's, else the protocol grammar with the default
    /// lengths (REPORTING 4.3) — a service's narrower rules cannot be known before its document is.
    var usernameRules: ReportUsernameRules { discovery?.usernameRules ?? .standard }

    /// The computer name fitted to `rules` (DESIGN 11, Default username), else a random name.
    func defaultUsername(rules: ReportUsernameRules) -> String {
        ReportComputerName.current.flatMap { ReportUsername.fit(computerName: $0, rules: rules) } ?? fallbackUsername
    }

    /// The name the next registration uses: the user's own, else the default.
    func nextUsername(rules: ReportUsernameRules) -> String {
        preferences.username ?? defaultUsername(rules: rules)
    }

    /// What the always-visible username field shows: the registered name, else the next registration's.
    var fieldUsername: String { identity?.username ?? nextUsername(rules: usernameRules) }

    /// The dashboard invitation is offered (DESIGN 11, Dashboard invitation); the dashboard also waits for the
    /// update banner.
    var invitationOffered: Bool {
        ReportInvite.isOffered(.init(
            answer: preferences.invite, reportingEnabled: preferences.enabled, networkEnabled: settings.networkEnabled,
            presetSelected: preferences.preset == .codexRadar, hasRegisteredIdentity: preferences.hasRegisteredIdentity))
    }

    /// The dashboard footer's indicator: whenever reporting is on, also while it is being set up; it appears once
    /// the invitation card has made way.
    var indicatorShown: Bool { preferences.enabled && !inviteJustAccepted }

    var status: ReportStatus {
        if problem == .gone { return .problem(.gone) }
        guard preferences.enabled else { return .off }
        guard settings.networkEnabled else { return .networkOff }
        if let problem { return .problem(problem) }
        if needsReview { return .changed }
        if let until = preferences.pausedUntil, until > Date() {
            return until == .distantFuture ? .pausedIndefinitely : .paused(until: until)
        }
        if registering { return .registering }
        if identity?.isRegistered != true {
            if consent != nil { return .connecting }
            if inviteGap != nil { return .review }
            if let registrationFailure { return .registerFailed(registrationFailure) }
            if let serviceIssue { return .service(serviceIssue) }
            if serviceState == .gone { return .problem(.gone) }
            return .connecting
        }
        let streamIssue = self.streamIssue
        if let streamIssue, streamIssue.isStop { return .stream(streamIssue) }
        if let until = rateLimitedUntil, until > Date() { return .rateLimited(until: until) }
        if let streamIssue { return .stream(streamIssue) }
        if let retryReason, effectiveTransport == .http { return .retrying(reason: retryReason) }
        if isDiscoveryStale { return .connecting }
        switch mode {
        case .active:
            guard let last = lastDeliveryAt else { return .never }
            return effectiveTransport == .websocket ? .streaming(last: last) : .reporting(last: last)
        case .idle: return lastDeliveryAt == nil ? .never : .idle
        case .quiet: return lastDeliveryAt == nil ? .never : .quiet
        }
    }

    /// The masked API key (`••••` and its last 4 characters), never the key itself.
    var maskedAPIKey: String? {
        guard let apiKey, !apiKey.isEmpty else { return nil }
        return "••••" + String(apiKey.suffix(4))
    }

    var fingerprint: String? { signer?.publicKey.fingerprint }

    // MARK: - Lifecycle

    /// Starts following the live engine, the network switch and the settings.
    func activate() {
        guard !active else { return }
        active = true
        loadSecrets()
        liveToken = store.addLiveListener { [weak self] snapshot in self?.handleLive(snapshot) }
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let enabled = self.settings.networkEnabled
            guard enabled != self.networkWasEnabled else { return }
            self.networkWasEnabled = enabled
            if !enabled { self.profile.networkOff() }
            self.post(enabled ? .networkOn : .networkOff)
            if enabled {
                self.refreshDiscoveryIfDue(force: true)
                self.continueSetup()
            }
        })
        schedulePauseEnd()
        refreshDiscoveryIfDue(force: true)
        continueSetup()
    }

    func shutdown() {
        profile.reset()
        endStream()
        if let liveToken { store.removeLiveListener(liveToken) }
        liveToken = nil
        loops.forEach { $0.cancel() }
        loops.removeAll()
        refreshTask?.cancel()
        refreshTimer?.invalidate()
        pauseTimer?.invalidate()
        setupTimer?.invalidate()
        active = false
    }

    /// At quit: one `leaving` report when it can be sent within a second (REPORTING 7.1), then `reply`.
    func prepareToQuit(_ reply: @escaping () -> Void) {
        if stream != nil {
            prepareStreamToQuit(reply)
            return
        }
        guard let run, run.scheduler.mode != .quiet, isReady(), settings.networkEnabled else {
            reply()
            return
        }
        quitReply = reply
        post(.stop)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.finishQuit() }
        if self.run?.scheduler.isBusy != true { finishQuit() }
    }

    func finishQuit() {
        guard let reply = quitReply else { return }
        quitReply = nil
        reply()
    }

    // MARK: - Persistence

    func savePreferences() {
        preferences.save(to: defaults)
    }

    func updatePreferences(_ change: (inout ReportPreferences) -> Void) {
        var copy = preferences
        change(&copy)
        guard copy != preferences else { return }
        preferences = copy
        savePreferences()
    }

    func updateIdentity(_ origin: String, _ change: (inout ReportIdentity) -> Void) {
        updatePreferences { preferences in
            guard let index = preferences.identities.firstIndex(where: { $0.id == origin }) else { return }
            change(&preferences.identities[index])
        }
    }

    /// Loads the selected identity's keys (no Keychain or Secure Enclave work happens before a key exists).
    func loadSecrets() {
        guard let identity else {
            signer = nil
            pendingSigner = nil
            apiKey = nil
            return
        }
        signer = keyStore.loadKey(deviceId: identity.deviceId)
        pendingSigner = identity.pendingKey ? keyStore.loadKey(deviceId: identity.deviceId, slot: .pending) : nil
        apiKey = identity.hasAPIKey ? keyStore.loadAPIKey(deviceId: identity.deviceId) : nil
    }
}

/// One run (METRICS 11.5, Run): its id, the scheduler, the open window, the title tracker and the bodies of the
/// reports built and not yet delivered or lost.
struct ReportRun {
    let id = ReportCanonical.newUUID()
    let origin: String
    var scheduler: ReportScheduler
    var window: ReportWindow
    var title = ReportTitleTracker()
    var bodies: [Int: Data] = [:]
    /// Tickets whose next attempt is signed with the pending key.
    var signWithPending: Set<Int> = []
}

/// Monotonic milliseconds that keep counting while the Mac sleeps, so the gap rule sees a sleep (METRICS 11.5).
enum ReportClock {
    static var monotonicMs: Int64 { Int64(clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1_000_000) }
    static var unixMs: Int64 { Int64((Date().timeIntervalSince1970 * 1000).rounded()) }
}
