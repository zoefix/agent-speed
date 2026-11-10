import AgentSpeedCore
import AppKit
import Observation

/// The subscription quotas the UI shows (QUOTAS; DESIGN 4 *Quota lines*, 2a.6 *額度*, 5 *額度*): Codex from the
/// engine's snapshot, Claude Desktop from its cache, the Claude CLI through the status line bridge, and which of their
/// apps run and are signed in. Display only: nothing here is reported or sent anywhere (QUOTAS 1).
@MainActor
@Observable
final class QuotaController {
    static let sources: [SourceKind] = [.codex, .claudeCode, .claudeDesktop]
    static let tickInterval: TimeInterval = 5
    /// Desktop only refreshes its cached response while signed in (QUOTAS 6).
    static let desktopSignedInMs: Int64 = 45 * 60_000
    /// The dashboard lists a source with a quota observed this recently (DESIGN 4).
    static let listedWithinMs: Int64 = 24 * 3_600_000

    enum BridgeFailure: Equatable {
        case unreadable
        case failed
    }

    private(set) var desktop: QuotaSnapshot?
    private(set) var cli: QuotaSnapshot?
    /// When the Claude CLI's quota file last changed a value (`settings.quota.cli.ok`).
    private(set) var cliReceivedMs: Int64?
    /// QUOTAS 5.4 while the bridge is on; nil while it is off or not checked yet.
    private(set) var bridgeStatus: StatusLineSettings.Status?
    /// Why the last attempt to turn the bridge on changed nothing, shown under the switch until the next attempt.
    private(set) var bridgeFailure: BridgeFailure?
    private(set) var apps = QuotaApps.none
    private(set) var signedIn: Set<SourceKind> = []
    /// The clock the texts follow, in whole minutes (resets and ages move on each minute).
    private(set) var minuteMs: Int64
    /// Previews fix the clock.
    @ObservationIgnored private var fixedNowMs: Int64?

    /// Now, for texts that must move on: reading it makes a view follow the minute.
    var clockMs: Int64 {
        _ = minuteMs
        return fixedNowMs ?? Self.nowMs()
    }

    /// The dashboard's invitation asked to connect the status line: Settings → Data Sources shows the confirmation.
    var confirmsCli = false

    /// An orb shows (or could show) the 額度 card: the light process listing runs while traffic keeps no table.
    @ObservationIgnored var wantsCard = false
    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let store: SpeedStore
    @ObservationIgnored private let traffic: TrafficStore
    @ObservationIgnored private let worker: QuotaWorker
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var ticks = 0
    @ObservationIgnored private var roundInFlight = false
    @ObservationIgnored private var desktopWasRunning: Bool?
    @ObservationIgnored private var loops: [ObservationLoop] = []
    @ObservationIgnored private(set) var isLive = false

    /// The helper copied into the data folder when the bridge is turned on.
    @ObservationIgnored var helperSource: URL? = QuotaController.bundledHelper

    init(settings: AppSettings, store: SpeedStore, traffic: TrafficStore, data: URL,
         claudeSettings: URL = ClaudeStatusLineBridge.defaultSettings,
         desktopFolder: URL = DesktopQuotaReader.defaultFolder) {
        self.settings = settings
        self.store = store
        self.traffic = traffic
        worker = QuotaWorker(bridge: ClaudeStatusLineBridge(settings: claudeSettings, data: data), desktopFolder: desktopFolder)
        minuteMs = Self.minute(Self.nowMs())
    }

    var bridge: ClaudeStatusLineBridge { worker.bridge }

    // MARK: Snapshots

    /// The source's quota while its switch is on.
    func snapshot(_ source: SourceKind) -> QuotaSnapshot? {
        guard settings.showsQuota(source) else { return nil }
        switch source {
        case .codex: return store.snapshot.codexQuota
        case .claudeDesktop: return desktop
        case .claudeCode: return cli
        default: return nil
        }
    }

    /// A row's quota lines (DESIGN 4): a snapshot with windows whose switch is on.
    func lines(_ source: SourceKind) -> QuotaSnapshot? {
        snapshot(source).flatMap { $0.hasWindows ? $0 : nil }
    }

    /// The sources the dashboard lists for their quota even without tokens today: observed within 24 hours.
    var listedSources: Set<SourceKind> {
        let now = clockMs
        return Set(Self.sources.filter { source in
            lines(source).map { now - $0.observedMs <= Self.listedWithinMs } ?? false
        })
    }

    /// Whether the source's app runs and is signed in (QUOTAS 6).
    func isActive(_ source: SourceKind) -> Bool {
        guard apps.runs(source) else { return false }
        if source == .claudeDesktop {
            return desktop.map { clockMs - $0.observedMs <= Self.desktopSignedInMs } ?? false
        }
        return signedIn.contains(source)
    }

    /// The 額度 card's sources for an orb tracking `sources` (nil: all): running, signed in and with a window, in
    /// source order (DESIGN 2a.6).
    func cardSnapshots(within sources: SourceSet?) -> [QuotaSnapshot] {
        Self.sources.compactMap { source in
            guard sources?.contains(source) ?? true, isActive(source) else { return nil }
            return lines(source)
        }
    }

    /// The invitation under the Claude Code row (DESIGN 4): the bridge is off and the invitation was not dismissed.
    var invitesCli: Bool { !settings.quotaClaudeCli && !settings.quotaCliInviteDismissed }

    // MARK: Lifecycle

    /// Starts the readers (live runs only). While the bridge is on, the helper copy follows the app's.
    func activate() {
        guard !isLive else { return }
        isLive = true
        if settings.quotaClaudeCli, let helper = helperSource {
            worker.perform({ $0.installHelper(from: helper) }, completion: { _ in })
        }
        let timer = Timer(timeInterval: Self.tickInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        timer.tolerance = 1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        // A switch turned on reads at once.
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            _ = (self.settings.quotaClaudeDesktop, self.settings.quotaClaudeCli, self.settings.quotaCodex)
            DispatchQueue.main.async { [weak self] in self?.tick(force: true) }
        })
        tick(force: true)
    }

    func shutdown() {
        timer?.invalidate()
        timer = nil
        loops.forEach { $0.cancel() }
        loops.removeAll()
        isLive = false
    }

    /// Settings opened: the bridge's status is checked at once (QUOTAS 5.4).
    func refreshBridgeStatus() {
        guard isLive, settings.quotaClaudeCli else { return }
        worker.perform({ $0.status() }) { status in
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated { self?.apply(status: status) }
            }
        }
    }

    private func tick(force: Bool = false) {
        let now = Self.nowMs()
        let minute = Self.minute(now)
        if minute != minuteMs { minuteMs = minute }
        guard !roundInFlight else { return }
        if !force { ticks += 1 }
        let every15s = force || ticks % 3 == 0
        let everyMinute = force || ticks % 12 == 0
        let tableApps = traffic.snapshot.enabled ? traffic.snapshot.quotaApps : nil
        if let tableApps { applyApps(tableApps) }
        let request = QuotaWorker.Request(
            nowMs: now,
            desktop: settings.quotaClaudeDesktop,
            desktopRunning: apps.claudeDesktop,
            desktopStarted: desktopStartedSinceLastRound(),
            cli: settings.quotaClaudeCli,
            bridgeStatus: settings.quotaClaudeCli && everyMinute,
            signIn: every15s && (settings.quotaCodex || settings.quotaClaudeCli),
            scanProcesses: every15s && tableApps == nil && wantsCard
        )
        roundInFlight = true
        worker.run(request) { result in
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated { self?.apply(result) }
            }
        }
    }

    private func apply(_ result: QuotaWorker.Result) {
        roundInFlight = false
        if let desktop = result.desktop, settings.quotaClaudeDesktop, self.desktop != desktop { self.desktop = desktop }
        if !settings.quotaClaudeDesktop, desktop != nil { desktop = nil }
        if let cli = result.cli, settings.quotaClaudeCli, self.cli != cli { self.cli = cli }
        if let received = result.cliReceivedMs, settings.quotaClaudeCli, cliReceivedMs != received { cliReceivedMs = received }
        if !settings.quotaClaudeCli {
            if cli != nil { cli = nil }
            if cliReceivedMs != nil { cliReceivedMs = nil }
            if bridgeStatus != nil { bridgeStatus = nil }
        }
        if let status = result.bridgeStatus { apply(status: status) }
        if let signedIn = result.signedIn, self.signedIn != signedIn { self.signedIn = signedIn }
        if let apps = result.apps { applyApps(apps) }
    }

    private func apply(status: StatusLineSettings.Status) {
        guard settings.quotaClaudeCli else { return }
        if bridgeStatus != status { bridgeStatus = status }
    }

    private func applyApps(_ next: QuotaApps) {
        if apps != next { apps = next }
    }

    /// Claude Desktop's main process appeared since the last round: its cache is searched again (QUOTAS 4.4).
    private func desktopStartedSinceLastRound() -> Bool {
        defer { desktopWasRunning = apps.claudeDesktop }
        return desktopWasRunning == false && apps.claudeDesktop
    }

    // MARK: The Claude CLI bridge (QUOTAS 5.2, 5.3)

    /// Turns the bridge on after the user confirmed; the switch stays off when it changed nothing.
    func turnOnCli(completion: @escaping @MainActor (Bool) -> Void = { _ in }) {
        guard let helper = helperSource else {
            bridgeFailure = .failed
            completion(false)
            return
        }
        bridgeFailure = nil
        worker.perform({ $0.turnOn(helper: helper) }) { outcome in
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    switch outcome {
                    case .on:
                        self.settings.quotaClaudeCli = true
                        self.bridgeStatus = nil
                        self.tick(force: true)
                    case .unreadable: self.bridgeFailure = .unreadable
                    case .failed: self.bridgeFailure = .failed
                    }
                    completion(outcome == .on)
                }
            }
        }
    }

    /// Restores the previous status line at once, without a dialog.
    func turnOffCli() {
        settings.quotaClaudeCli = false
        bridgeFailure = nil
        cli = nil
        cliReceivedMs = nil
        bridgeStatus = nil
        worker.perform({ $0.turnOff() }, completion: { _ in })
    }

    func requestCliConfirmation() {
        confirmsCli = true
    }

    func dismissInvite() {
        settings.quotaCliInviteDismissed = true
    }

    func clearBridgeFailure() {
        bridgeFailure = nil
    }

    /// The settings file as the confirmation shows it, abbreviated with `~`.
    var settingsPath: String { (bridge.settings.path as NSString).abbreviatingWithTildeInPath }

    /// The helper inside the app (`Contents/Helpers`), else beside the executable (development builds).
    static var bundledHelper: URL? {
        let name = StatusLineSettings.helperName
        let candidates = [
            Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/\(name)"),
            Bundle.main.executableURL?.deletingLastPathComponent().appendingPathComponent(name),
        ]
        return candidates.compactMap { $0 }.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    // MARK: Previews

    /// Sample state for previews and screenshots; never used by live runs.
    func preview(desktop: QuotaSnapshot?, cli: QuotaSnapshot?, cliReceivedMs: Int64? = nil,
                 status: StatusLineSettings.Status? = nil, failure: BridgeFailure? = nil, apps: QuotaApps = .none,
                 signedIn: Set<SourceKind> = [], nowMs: Int64 = QuotaController.nowMs()) {
        guard !isLive else { return }
        self.desktop = desktop
        self.cli = cli
        self.cliReceivedMs = cliReceivedMs
        bridgeStatus = status
        bridgeFailure = failure
        self.apps = apps
        self.signedIn = signedIn
        fixedNowMs = nowMs
        minuteMs = Self.minute(nowMs)
    }

    static func nowMs() -> Int64 { Int64((Date().timeIntervalSince1970 * 1000).rounded(.down)) }
    static func minute(_ ms: Int64) -> Int64 { ms - ms % 60_000 }
}
