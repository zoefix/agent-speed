import AgentSpeedCore
import AppKit
import Observation

/// Commands the UI can trigger; implemented by `AppDelegate`.
@MainActor
protocol AppCommands: AnyObject {
    func toggleDashboard()
    func showDashboard()
    func closeDashboard()
    func showSettings(_ pane: SettingsPane?)
    /// Opens the Usage History window filtered to `source` (all sources when nil).
    func showHistory(source: SourceKind?)
    /// Opens the Usage History window on `metric` (the dashboard's traffic rows open the traffic chart).
    func showHistory(source: SourceKind?, metric: HistoryChartMetric)
    /// The floating orbs' master switch (DESIGN 2b).
    func setOrbsVisible(_ visible: Bool)
    func resetOrbPosition(_ id: String)
    /// Opens Settings → Floating orbs with `id` selected.
    func showOrbSettings(_ id: String?)
    /// `boards.add`, placed next to the orb or board it came from (DESIGN 2c).
    func addBoard(from origin: BoardAddOrigin)
    /// Opens Settings → 懸浮窗 with board `id` selected in the Boards list.
    func showBoardSettings(_ id: String?)
    /// Opens the update window on the current update state.
    func showUpdate()
    /// Opens the live reporting preview window (DESIGN 11, Preview window), e.g. from the dashboard invitation.
    func showReportPreview()
    /// Manual check from a menu: opens the update window, which reports the result.
    func checkForUpdates()
    func quit()
}

/// The app's shared state: one store, the settings, the live gauge, the data monitor and the daily history.
@MainActor
@Observable
final class AppModel {
    let store: SpeedStore
    /// Daily usage (METRICS 8) for the history window, filled by `HistoryService` (or previews).
    let history: HistoryStore
    let settings: AppSettings
    let l10n: Localizer
    let gauge: LiveGauge
    /// The all-sources orb feed: the dashboard hero and every orb tracking all sources read it.
    let mainFeed: OrbFeed
    /// The floating orbs (DESIGN 2b).
    let orbs: OrbsController
    /// The leaderboard boards (DESIGN 2c).
    let boards: BoardsController
    /// The title themes and custom title sets (DESIGN 1).
    let titles: TitleThemesController
    let accessibility: AccessibilityState
    let launchAtLogin: LaunchAtLogin
    let updates: UpdateController
    /// Price tables and day bindings shared by the live engine and the history (METRICS 9.7).
    let priceBook: PriceBook
    /// Price-table sync and exchange rates (NETWORK 3, 4) and the money formatting built on them (DESIGN 9).
    let prices: PriceSyncController
    let rates: ExchangeRateController
    let cost: CostDisplay
    /// Network traffic (METRICS 10, DESIGN 10): the live snapshot and the stored days.
    let traffic: TrafficStore
    /// Live reporting (DESIGN 11): off until the user confirms a service.
    let report: ReportController
    /// The menu bar intelligence block's thinking line, hidden after its rate has read 0 for 3 s (DESIGN 10).
    let barThinking = StatusItemThinkingHold()

    /// True while the 20 s effects preview (or the `--demo` loop) drives the store.
    private(set) var isPreviewing = false
    /// The dashboard popover is open (traffic samples every second while it shows per-source traffic).
    var isDashboardShown = false

    @ObservationIgnored weak var commands: AppCommands?
    @ObservationIgnored private(set) var monitor: LiveMonitor?
    @ObservationIgnored private(set) var historyService: HistoryService?
    /// Network traffic measuring (METRICS 10); live runs only.
    @ObservationIgnored private var trafficController: TrafficController?
    @ObservationIgnored private var historyStartTimer: Timer?
    @ObservationIgnored private var demo: DemoDriver?
    @ObservationIgnored private var demoLoops = false
    @ObservationIgnored private var loops: [ObservationLoop] = []
    @ObservationIgnored private var appliedConfiguration: MonitorConfiguration?
    @ObservationIgnored private var orbSubsets: Set<SourceSet> = []
    @ObservationIgnored private var boardSubsets: Set<SourceSet> = []

    init(
        store: SpeedStore = SpeedStore(),
        settings: AppSettings = AppSettings(),
        updates: UpdateController? = nil,
        history: HistoryStore = HistoryStore()
    ) {
        self.store = store
        self.history = history
        self.settings = settings
        self.updates = updates ?? UpdateController()
        l10n = Localizer(preference: settings.language)
        gauge = LiveGauge()
        mainFeed = OrbFeed(gauge: gauge)
        orbs = OrbsController(defaults: settings.storage)
        boards = BoardsController(defaults: settings.storage)
        titles = TitleThemesController(defaults: settings.storage)
        accessibility = AccessibilityState()
        launchAtLogin = LaunchAtLogin()
        let userAgent = "AgentSpeed/\(UpdateConfiguration.bundleVersion) (macOS)"
        priceBook = PriceBook()
        prices = PriceSyncController(book: priceBook, userAgent: userAgent)
        rates = ExchangeRateController(userAgent: userAgent)
        cost = CostDisplay(settings: settings, l10n: l10n, rates: rates, prices: prices)
        traffic = TrafficStore()
        report = ReportController(settings: settings, store: store, traffic: traffic, priceBook: priceBook,
                                  appData: Self.appDataDirectory)
        gauge.redline = settings.redline
    }

    /// `~/Library/Application Support/AgentSpeed`; debug runs may use another folder (`AGENT_SPEED_APP_DATA`), so
    /// a development run never writes report keys next to the installed app's.
    static var appDataDirectory: URL {
        #if DEBUG
        if let path = ProcessInfo.processInfo.environment["AGENT_SPEED_APP_DATA"], !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        #endif
        return PriceSyncController.defaultAppDataDirectory
    }

    var snapshot: SpeedSnapshot { store.snapshot }

    /// Effects level after Reduce Motion.
    var effectsLevel: EffectsLevel { accessibility.effectiveLevel(settings.effects) }

    func value(of rate: Rate) -> Double { rate.value(for: settings.metric) }

    /// Starts reacting to settings and data. `live` starts reading transcripts.
    func activate(live: Bool) {
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            self.l10n.apply(self.settings.language)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            Self.applyAppearance(self.settings.appearance)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            self.gauge.redline = self.settings.redline
            let snapshot = self.store.snapshot
            self.gauge.ingest(self.value(of: snapshot.live), now: Clock.now)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            self.barThinking.update(self.store.snapshot.intelligence, at: Clock.now)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let snapshot = self.store.snapshot
            let today = self.settings.metric == .output ? snapshot.today.output : snapshot.today.total
            self.mainFeed.set(
                generating: snapshot.agents.filter { $0.phase == .generating }.map(\.source),
                todayTokens: today,
                agentCount: snapshot.agents.count
            )
            self.mainFeed.redlineOutput = self.settings.redline(for: .output)
        })
        store.addEventListener { [weak self] event in
            guard let feed = self?.mainFeed else { return }
            switch event {
            case .responseCompleted(let response): feed.emit(.response(output: response.outputTokens, speed: response.speed))
            case .newPeak: feed.emit(.newPeak)
            }
        }
        loops.append(ObservationLoop { [weak self] in
            // The effects preview sweeps every title; it must not replace the vehicle the agents last reached.
            guard let self, let key = self.gauge.title.vehicleKey, !self.store.isPreviewing else { return }
            if self.settings.heroLastTitle != key { self.settings.heroLastTitle = key }
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let previewing = self.store.isPreviewing
            if self.isPreviewing != previewing { self.isPreviewing = previewing }
        })
        orbs.activate(model: self)
        boards.activate(model: self)
        titles.activate(model: self)
        guard live else { return }
        // Stored day bindings first, so the engine never re-binds a day that was priced before (METRICS 9.7).
        HistoryService.restorePricing(into: priceBook)
        activateNetwork()
        let traffic = TrafficController(model: self)
        trafficController = traffic
        trafficRetryHandler = { [weak traffic] in traffic?.retry() }
        traffic.start()
        let monitor = LiveMonitor(store: store, configuration: settings.monitorConfiguration, pricing: priceBook)
        self.monitor = monitor
        appliedConfiguration = settings.monitorConfiguration
        monitor.setSubsets(orbSubsets.union(boardSubsets))
        monitor.start()
        report.perfWatchHandler = { [weak monitor] epoch in monitor?.setReportWatch(epoch) }
        report.activate()
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let configuration = self.settings.monitorConfiguration
            guard configuration != self.appliedConfiguration else { return }
            self.appliedConfiguration = configuration
            self.monitor?.update(configuration: configuration)
            self.historyService?.update(configuration: configuration)
        })
        scheduleHistory()
    }

    /// The history analysis reads every log, so it starts only once the live monitor has read today's transcripts
    /// (or after a grace period, or as soon as the history window asks for it), never slowing the live start.
    private func scheduleHistory() {
        let timer = Timer(timeInterval: Self.historyGracePeriod, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.startHistory() }
        }
        timer.tolerance = 2
        RunLoop.main.add(timer, forMode: .common)
        historyStartTimer = timer
        loops.append(ObservationLoop { [weak self] in
            guard let self, self.historyService == nil else { return }
            if case .starting = self.store.snapshot.state { return }
            DispatchQueue.main.async { [weak self] in self?.startHistory() }
        })
    }

    private static let historyGracePeriod: TimeInterval = 20

    /// The price-table sync and exchange rates follow the master switch, the sync setting and the currency
    /// (NETWORK 1, 3, 4); a newly accepted table re-prices what no table could price (METRICS 9.7).
    private func activateNetwork() {
        prices.onTableAccepted = { [weak self] _ in self?.historyService?.refresh(showsProgress: false) }
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let network = self.settings.networkEnabled
            self.prices.update(networkEnabled: network, autoSync: self.settings.syncPrices)
            self.rates.update(networkEnabled: network, currency: self.settings.currency)
            self.updates.networkEnabled = network
        })
        prices.start()
        rates.start()
    }

    /// Starts the daily history analysis if it is not running yet.
    func startHistory() {
        guard historyService == nil, monitor != nil else { return }
        historyStartTimer?.invalidate()
        historyStartTimer = nil
        let service = HistoryService(store: history, configuration: settings.monitorConfiguration, liveStore: store,
                                     pricing: priceBook)
        // The stored performance records give the score card its baselines (METRICS 6.11).
        history.perfRecordsHandler = { [weak self] records in self?.monitor?.setPerfHistory(records) }
        historyService = service
        service.start()
    }

    /// Settings → 網路流量 `traffic.retry`: starts the per-process sampler again after it gave up (METRICS 10.3).
    func retryTraffic() {
        trafficRetryHandler?()
    }

    @ObservationIgnored var trafficRetryHandler: (() -> Void)?

    /// Deletes the stored daily statistics and analyses the current logs again (Settings → Data Sources).
    func clearHistory() {
        startHistory()
        // Traffic history cannot be rebuilt from logs: today's totals start over with the deleted store (METRICS 10.9).
        trafficController?.clearHistory()
        history.clear()
    }

    /// The source sets of the orbs tracking a subset, whose peaks the engine reports (METRICS 6.8).
    func setOrbSubsets(_ sets: Set<SourceSet>) {
        guard sets != orbSubsets else { return }
        orbSubsets = sets
        monitor?.setSubsets(orbSubsets.union(boardSubsets))
    }

    /// The source sets of the boards tracking a subset (METRICS 6.8), reported with the orbs'.
    func setBoardSubsets(_ sets: Set<SourceSet>) {
        guard sets != boardSubsets else { return }
        boardSubsets = sets
        monitor?.setSubsets(orbSubsets.union(boardSubsets))
    }

    func shutdown() {
        report.shutdown()
        trafficController?.stop()
        trafficController = nil
        orbs.shutdown()
        boards.shutdown()
        demo?.stop()
        historyStartTimer?.invalidate()
        historyService?.stop()
        monitor?.stop()
        loops.forEach { $0.cancel() }
        loops.removeAll()
    }

    /// Runs the scripted sweep through every tier (DESIGN section 5, Preview effects).
    func previewEffects(duration: TimeInterval = 20) {
        let driver = demo ?? DemoDriver(store: store)
        demo = driver
        driver.onFinish = { [weak self] in
            guard let self, self.demoLoops else { return }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.demoLoops else { return }
                self.demo?.start(redline: self.demoRedline, duration: duration)
            }
        }
        driver.start(redline: demoRedline, duration: duration)
    }

    /// `DemoDriver` scales output speed to the redline it gets and reports total throughput as 24× output, so the
    /// total metric needs a proportionally smaller input to sweep through the same tiers.
    private var demoRedline: Double {
        settings.metric == .output ? settings.redline : settings.redline / DemoDriverRatio.totalPerOutput
    }

    func stopPreview() {
        demoLoops = false
        demo?.stop()
    }

    /// `--demo`: loop the preview forever.
    func startDemoLoop() {
        demoLoops = true
        previewEffects()
    }

    private static func applyAppearance(_ mode: AppearanceMode) {
        let appearance: NSAppearance? = switch mode {
        case .auto: nil
        case .light: NSAppearance(named: .aqua)
        case .dark: NSAppearance(named: .darkAqua)
        }
        if NSApp.appearance?.name != appearance?.name { NSApp.appearance = appearance }
    }
}

enum DemoDriverRatio {
    static let totalPerOutput = 24.0
}

/// Settings window panes (DESIGN section 5).
enum SettingsPane: String, CaseIterable, Identifiable, Sendable {
    case general, menuBar, orb, effects, cost, traffic, report, sources, about

    var id: String { rawValue }

    var titleKey: String {
        switch self {
        case .general: "settings.general"
        case .menuBar: "settings.bar"
        case .orb: "settings.orb"
        case .effects: "settings.effects"
        case .cost: "settings.cost"
        case .traffic: "settings.traffic"
        case .report: "settings.report"
        case .sources: "settings.sources"
        case .about: "settings.about"
        }
    }

    /// The toolbar tab's label: shorter where the full title would push tabs into the overflow menu at the window's
    /// width (English and Japanese); the window title keeps the full name.
    var tabTitleKey: String {
        switch self {
        case .orb: "mac.settings.tab.orb"
        case .traffic: "mac.settings.tab.traffic"
        default: titleKey
        }
    }

    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .menuBar: "menubar.rectangle"
        case .orb: "circle.circle"
        case .effects: "sparkles"
        case .cost: "banknote"
        case .traffic: "arrow.up.arrow.down"
        case .report: "dot.radiowaves.left.and.right"
        case .sources: "folder"
        case .about: "info.circle"
        }
    }
}
