import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, AppCommands {
    private let options: LaunchOptions
    private var model: AppModel?
    private var statusItem: StatusItemController?
    private var orbs: OrbWindowManager?
    private var boardWindows: BoardWindowManager?
    private var dashboard: DashboardController?
    private var settingsWindow: SettingsWindowController?
    private var historyWindow: HistoryWindowController?
    private var updateWindow: UpdateWindowController?
    private var updateScheduler: UpdateScheduler?
    private let updateNotifier = UpdateNotifier()

    init(options: LaunchOptions) {
        self.options = options
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        CharacterLayers.factory = { CharacterRenderer() }
        if let directory = options.previewDirectory {
            PreviewRenderer.run(into: directory)
            NSApp.terminate(nil)
            return
        }
        if let directory = options.orbFramesDirectory {
            OrbFrameHarness.run(into: directory)
            NSApp.terminate(nil)
            return
        }
        if let directory = options.boardFramesDirectory {
            BoardFrameHarness.run(into: directory)
            NSApp.terminate(nil)
            return
        }
        if let directory = options.boardBenchDirectory {
            BoardBenchmark.start(into: directory)
            return
        }
        if let demo = options.boardDemo {
            BoardDemoHarness.start(demo)
            return
        }
        if let directory = options.orbBenchDirectory {
            OrbBenchmark.start(into: directory)
            return
        }
        #if DEBUG
        if let directory = options.heroCPUDirectory {
            HeroLiveHarness.start(into: directory)
            return
        }
        if let directory = options.heroFramesDirectory {
            HeroFrameHarness.run(into: directory)
            NSApp.terminate(nil)
            return
        }
        if let directory = options.orbsDemoDirectory {
            OrbsDemoHarness.start(into: directory)
            return
        }
        if let directory = options.nameplatesDemoDirectory {
            NameplatesDemoHarness.start(into: directory)
            return
        }
        if let directory = options.motionHarnessDirectory {
            MotionHarness.start(into: directory)
            return
        }
        if let url = options.reportEndToEndURL {
            ReportHarness.start(url: url)
            return
        }
        if options.lanEndToEnd {
            LanHarness.start()
            return
        }
        if let directory = options.screenshotsDirectory {
            ScreenshotHarness.run(into: directory, onlyHistory: options.screenshotsOnlyHistory)
            return
        }
        #endif
        let model = Self.makeModel()
        model.commands = self
        self.model = model
        model.activate(live: true)

        let dashboard = DashboardController(model: model)
        self.dashboard = dashboard
        settingsWindow = SettingsWindowController(model: model)
        historyWindow = HistoryWindowController(model: model)
        let statusItem = StatusItemController(model: model)
        self.statusItem = statusItem
        dashboard.onVisibilityChange = { [weak statusItem, weak model] shown in
            statusItem?.setHighlighted(shown)
            if model?.isDashboardShown != shown { model?.isDashboardShown = shown }
            if !shown { model?.updates.clearUpdatedNotice() }
        }
        orbs = OrbWindowManager(model: model)
        boardWindows = BoardWindowManager(model: model) { [weak self] in self?.orbShapes() ?? [] }
        orbs?.boardShapes = { [weak self] in self?.boardWindows?.shapes() ?? [] }

        MainMenu.install(model: model)
        if options.demo { model.startDemoLoop() }
        startUpdates(model: model)
        #if DEBUG
        scheduleDevelopmentActions()
        #endif
    }

    #if DEBUG
    /// Development runs: `AGENT_SPEED_OPEN_DASHBOARD=SECONDS` opens the dashboard after a delay,
    /// `AGENT_SPEED_OPEN_SETTINGS=PANE` opens Settings on that pane (`sources`, …) after two seconds,
    /// `AGENT_SPEED_QUIT_AFTER=SECONDS` quits through the normal path (so shutdown runs as on ⌘Q),
    /// `AGENT_SPEED_DASHBOARD_ANCHOR=SECONDS` opens the dashboard without the status item (`DashboardAnchorHarness`).
    private func scheduleDevelopmentActions() {
        let environment = ProcessInfo.processInfo.environment
        if let delay = environment["AGENT_SPEED_OPEN_DASHBOARD"].flatMap(Double.init) {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in self?.showDashboard() }
        }
        if let pane = environment["AGENT_SPEED_OPEN_SETTINGS"].flatMap(SettingsPane.init(rawValue:)) {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.showSettings(pane) }
        }
        if let delay = environment["AGENT_SPEED_DASHBOARD_ANCHOR"].flatMap(Double.init) {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                if let model = self?.model { DashboardAnchorHarness.show(model: model) }
            }
        }
        if let delay = environment["AGENT_SPEED_QUIT_AFTER"].flatMap(Double.init) {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { NSApp.terminate(nil) }
        }
    }
    #endif

    /// The app's model on the standard defaults. Debug builds can run against another suite
    /// (`AGENT_SPEED_DEFAULTS_SUITE`), so a development run never touches the settings of the app in use.
    private static func makeModel() -> AppModel {
        #if DEBUG
        if let suite = ProcessInfo.processInfo.environment["AGENT_SPEED_DEFAULTS_SUITE"], let defaults = UserDefaults(suiteName: suite) {
            return AppModel(settings: AppSettings(defaults: defaults),
                            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults)))
        }
        #endif
        return AppModel()
    }

    /// Notifications, the schedule, the relaunch hand-off, the developer flags and the one-time `update.done`.
    private func startUpdates(model: AppModel) {
        let updates = model.updates
        updateWindow = UpdateWindowController(model: model)
        updateNotifier.install()
        updateNotifier.onOpen = { [weak self] in self?.showUpdate() }
        updates.onNewRelease = { [weak self, weak model] release in
            guard let self, let model else { return }
            let version = release.version.description
            self.updateNotifier.announce(
                release,
                title: model.l10n("update.available.title", version),
                body: model.l10n("update.notification.body")
            )
        }
        updates.onReadyToRelaunch = {
            NSApp.terminate(nil)
        }
        UpdateWorkspace.removeStale()

        let scheduler = UpdateScheduler(controller: updates, settings: model.settings)
        updateScheduler = scheduler
        scheduler.start()

        if options.installUpdateNow {
            Task { [weak self] in
                await updates.check(manual: true)
                guard updates.availableRelease != nil else { return }
                self?.showUpdate()
                updates.install()
            }
        } else if options.checkUpdatesNow {
            checkForUpdates()
        }
        if updates.justUpdatedTo != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in self?.showDashboard() }
        }
    }

    @objc func openSettingsFromMenu(_ sender: Any?) {
        showSettings(nil)
    }

    @objc func openHistoryFromMenu(_ sender: Any?) {
        showHistory(source: nil)
    }

    /// Live reporting gets up to a second for its leaving report (REPORTING 7.1).
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let report = model?.report else { return .terminateNow }
        var replied = false
        var later = false
        report.prepareToQuit {
            replied = true
            if later { NSApp.reply(toApplicationShouldTerminate: true) }
        }
        if replied { return .terminateNow }
        later = true
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        updateScheduler?.stop()
        boardWindows?.shutdown()
        orbs?.shutdown()
        model?.shutdown()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings(nil)
        return false
    }

    // MARK: AppCommands

    func toggleDashboard() {
        guard let anchor = statusItem?.anchorView else { return }
        dashboard?.toggle(relativeTo: anchor)
    }

    func showDashboard() {
        guard let anchor = statusItem?.anchorView else { return }
        dashboard?.show(relativeTo: anchor)
    }

    func closeDashboard() {
        dashboard?.close()
    }

    func showSettings(_ pane: SettingsPane?) {
        dashboard?.close()
        settingsWindow?.show(pane: pane)
    }

    func showHistory(source: SourceKind?) {
        dashboard?.close()
        model?.startHistory()
        historyWindow?.show(source: source)
    }

    func showHistory(source: SourceKind?, metric: HistoryChartMetric) {
        dashboard?.close()
        model?.startHistory()
        historyWindow?.show(source: source, metric: metric)
    }

    func showHistory(host: String) {
        dashboard?.close()
        model?.startHistory()
        historyWindow?.show(source: nil, host: host)
    }

    func setOrbsVisible(_ visible: Bool) {
        model?.orbs.masterVisible = visible
    }

    func resetOrbPosition(_ id: String) {
        model?.orbs.resetPosition(id)
    }

    func showOrbSettings(_ id: String?) {
        if let id { model?.orbs.settingsSelection = id }
        showSettings(.orb)
    }

    func addBoard(from origin: BoardAddOrigin) {
        let anchor: FloatingShape? = switch origin {
        case .none: nil
        case .orb(let id): orbs?.window(for: id).map { FloatingShape($0.disc) }
        case .board(let id): boardWindows?.window(for: id)?.shape
        }
        model?.boards.add(near: anchor)
    }

    func showBoardSettings(_ id: String?) {
        if let id { model?.boards.settingsSelection = id }
        showSettings(.orb)
    }

    /// The shown orbs as obstacles for the boards (DESIGN 2c: boards and orbs never overlap).
    private func orbShapes() -> [FloatingShape] {
        guard let orbs else { return [] }
        return NSScreen.screens.flatMap { orbs.otherDiscs(excluding: "", on: $0) }.map(FloatingShape.init)
    }

    func showUpdate() {
        dashboard?.close()
        updateWindow?.show()
    }

    func showReportPreview() {
        guard let model else { return }
        dashboard?.close()
        ReportPreviewWindow.show(model: model)
    }

    func checkForUpdates() {
        model?.updates.startCheck(manual: true)
        showUpdate()
    }

    func quit() {
        NSApp.terminate(nil)
    }
}
