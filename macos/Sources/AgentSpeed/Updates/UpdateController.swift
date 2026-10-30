import AgentSpeedCore
import AppKit
import Observation

/// Drives the update states of UPDATES section 4 for every surface: quick menu, dashboard, notification, update
/// window and settings.
@MainActor
@Observable
final class UpdateController {
    enum Status: Equatable {
        case idle
        case checking
        case upToDate
        case available(Release)
        case failed(UpdateFailure)
    }

    enum Installation: Equatable {
        case idle
        case downloading(Double)
        case verifying
        case installing
        case relaunching
        case failed(UpdateFailure)

        var isActive: Bool {
            switch self {
            case .downloading, .verifying, .installing, .relaunching: true
            case .idle, .failed: false
            }
        }

        /// Install steps only move forward; late progress reports never move the state back.
        fileprivate var rank: Int {
            switch self {
            case .idle, .failed: -1
            case .downloading: 0
            case .verifying: 1
            case .installing: 2
            case .relaunching: 3
            }
        }
    }

    private(set) var status: Status = .idle
    private(set) var installation: Installation = .idle
    private(set) var lastChecked: Date?
    /// Version this instance was just updated to; `update.done` is shown once while it is set.
    private(set) var justUpdatedTo: String?

    let currentVersion: String
    @ObservationIgnored let configuration: UpdateConfiguration
    @ObservationIgnored private let store: UpdateStore
    /// The network master switch (NETWORK 1, UPDATES): off, no check of any kind runs.
    var networkEnabled = true
    @ObservationIgnored private var checkTask: Task<Void, Never>?
    @ObservationIgnored private var checkIsManual = false
    @ObservationIgnored private var statusBeforeCheck: Status = .idle
    @ObservationIgnored private var installTask: Task<Void, Never>?

    /// An automatic check found a version that has not been announced yet (one notification per version).
    @ObservationIgnored var onNewRelease: (@MainActor (Release) -> Void)?
    /// The new version is in place and the relaunch helper runs; the app should quit now.
    @ObservationIgnored var onReadyToRelaunch: (@MainActor () -> Void)?

    init(configuration: UpdateConfiguration = .current(), store: UpdateStore = UpdateStore()) {
        self.configuration = configuration
        self.store = store
        currentVersion = configuration.currentVersion
        lastChecked = store.lastCheck
        if let installed = store.installedVersion {
            let previous = store.previousVersion ?? "?"
            if SemanticVersion(installed) == SemanticVersion(currentVersion) {
                justUpdatedTo = currentVersion
                UpdateLog.logger.notice("Running \(self.currentVersion, privacy: .public) after updating from \(previous, privacy: .public)")
            } else {
                UpdateLog.logger.error("Expected \(installed, privacy: .public) after updating but running \(self.currentVersion, privacy: .public)")
            }
            store.installedVersion = nil
            store.previousVersion = nil
        }
    }

    /// The release on offer: found by the last check and neither skipped nor superseded.
    var availableRelease: Release? {
        if case .available(let release) = status { return release }
        return nil
    }

    var isChecking: Bool { status == .checking }
    var isBusy: Bool { isChecking || installation.isActive }
    var checksAutomatically: Bool { UpdateSchedule.checksAutomatically(currentVersion: currentVersion) }

    // MARK: Checking

    /// Starts a check, or joins the running one. A manual request shows `checking` at once and upgrades a running
    /// automatic check, so its outcome is reported. Manual checks report every outcome, including skipped versions
    /// and errors; automatic checks only announce new versions and fail silently.
    @discardableResult
    func startCheck(manual: Bool) -> Task<Void, Never> {
        guard !installation.isActive, networkEnabled else { return Task {} }
        if manual, !checkIsManual {
            checkIsManual = true
            statusBeforeCheck = status
            status = .checking
        }
        if let checkTask { return checkTask }
        let task = Task { [weak self] in
            guard let self else { return }
            await self.performCheck()
            self.checkTask = nil
            self.checkIsManual = false
        }
        checkTask = task
        return task
    }

    func check(manual: Bool) async {
        await startCheck(manual: manual).value
    }

    private func performCheck() async {
        let result: Result<Release, any Error>
        do {
            result = .success(try await ReleaseFeedClient(configuration: configuration).fetchLatest())
        } catch {
            result = .failure(error)
        }
        let manual = checkIsManual
        let previous = manual ? statusBeforeCheck : status
        let kind = manual ? "manual" : "automatic"
        switch result {
        case .success(let release):
            markChecked()
            let offer = UpdateDecision.evaluate(
                currentVersion: currentVersion,
                latest: release,
                skippedTag: store.skippedTag,
                manual: manual
            )
            UpdateLog.logger.notice("Update check (\(kind, privacy: .public)): current \(self.currentVersion, privacy: .public), latest \(release.tag, privacy: .public), \(String(describing: offer), privacy: .public)")
            switch offer {
            case .available:
                status = .available(release)
                if !manual, store.notifiedTag != release.tag {
                    store.notifiedTag = release.tag
                    onNewRelease?(release)
                }
            case .upToDate:
                status = .upToDate
            case .skipped:
                status = previous.withoutOffer(of: release)
            }
        case .failure(let error as ReleaseFeedError):
            markChecked()
            UpdateLog.logger.notice("Update check (\(kind, privacy: .public)): latest release is not offered (\(String(describing: error), privacy: .public))")
            status = .upToDate
        case .failure(let error):
            let failure = UpdateFailure(error)
            UpdateLog.logger.error("Update check (\(kind, privacy: .public)) failed: \(failure.logLabel, privacy: .public)")
            status = manual ? .failed(failure) : previous
        }
    }

    private func markChecked() {
        let now = Date()
        lastChecked = now
        store.lastCheck = now
    }

    // MARK: Choices

    /// "Skip This Version": automatic checks never offer this tag again.
    func skip() {
        guard let release = availableRelease, !installation.isActive else { return }
        store.skippedTag = release.tag
        UpdateLog.logger.notice("Skipped \(release.tag, privacy: .public)")
        status = .idle
        installation = .idle
    }

    func clearUpdatedNotice() {
        justUpdatedTo = nil
    }

    /// Retries what failed last: the install when it failed, otherwise the check.
    func retry() {
        if case .failed = installation {
            install()
        } else {
            startCheck(manual: true)
        }
    }

    // MARK: Installing

    func install() {
        guard let release = availableRelease, !installation.isActive else { return }
        UpdateLog.logger.notice("Installing \(release.tag, privacy: .public) over \(self.currentVersion, privacy: .public)")
        installation = .downloading(0)
        let installer = UpdateInstaller(configuration: configuration, target: Bundle.main.bundleURL)
        let progress: @Sendable (UpdateInstaller.Step) -> Void = { [weak self] step in
            let owner = self
            Task { @MainActor in owner?.advance(step) }
        }
        installTask = Task { [weak self] in
            do {
                try await installer.install(release, progress: progress)
                self?.relaunch(into: release, using: installer.replacer)
            } catch {
                self?.failInstall(release, error)
            }
        }
    }

    private func advance(_ step: UpdateInstaller.Step) {
        let next: Installation = switch step {
        case .downloading(let fraction): .downloading(fraction)
        case .verifying: .verifying
        case .installing: .installing
        }
        guard installation.isActive, next.rank >= installation.rank else { return }
        if case .downloading(let old) = installation, case .downloading(let new) = next, new < old { return }
        installation = next
    }

    private func failInstall(_ release: Release, _ error: any Error) {
        let failure = UpdateFailure(error)
        UpdateLog.logger.error("Update to \(release.tag, privacy: .public) failed: \(failure.logLabel, privacy: .public)")
        installation = .failed(failure)
    }

    private func relaunch(into release: Release, using replacer: BundleReplacer) {
        installation = .relaunching
        store.installedVersion = release.version.description
        store.previousVersion = currentVersion
        UpdateLog.logger.notice("Installed \(release.tag, privacy: .public); relaunching")
        do {
            try replacer.spawnRelaunchHelper(waitingFor: ProcessInfo.processInfo.processIdentifier)
        } catch {
            UpdateLog.logger.error("Relaunch helper failed to start; opening the new version directly")
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.createsNewApplicationInstance = true
            NSWorkspace.shared.openApplication(at: replacer.target, configuration: configuration)
        }
        // AppKit can wait in a nested run loop for asynchronous termination approval. Enter it outside the main
        // dispatch executor, so the approval callback and its timeout can still run on the main queue.
        let ready = onReadyToRelaunch
        RunLoop.main.perform(inModes: [.common]) {
            MainActor.assumeIsolated { ready?() }
        }
    }

    // MARK: Previews

    /// Puts the controller into a given state for `--render-previews`.
    func preview(status: Status, installation: Installation = .idle, lastChecked: Date? = nil, justUpdatedTo: String? = nil) {
        self.status = status
        self.installation = installation
        self.lastChecked = lastChecked
        self.justUpdatedTo = justUpdatedTo
    }
}

private extension UpdateController.Status {
    /// The state to keep when an automatic check finds only a skipped version.
    func withoutOffer(of release: Release) -> Self {
        guard case .available(let offered) = self else { return self }
        return offered.tag == release.tag ? self : .idle
    }
}
