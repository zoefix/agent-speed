import AgentSpeedCore
import Foundation

/// One shown window: a source of this Mac (`host` nil) or of a remote host.
struct QuotaEstimateLineKey: Hashable {
    var source: SourceKind
    var host: String?
    var window: String

    init(source: SourceKind, host: String? = nil, window: QuotaWindow) {
        self.source = source
        self.host = host
        self.window = QuotaEstimateWindow.id(kind: window.kind, name: window.name, minutes: window.minutes ?? 0)
    }
}

/// The quota estimate (QUOTAS 10) as the quota lines use it: the estimator gets every snapshot and sign-in key the
/// readers see, and each build becomes one display value per shown window, read by the dashboard and the orb.
extension QuotaController {
    func attach(_ estimator: QuotaEstimator) {
        self.estimator = estimator
        worker.setKeys(estimator.keys)
        estimator.onBuild = { [weak self] build in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.estimateBuild = build
                    self.refreshEstimateLines()
                }
            }
        }
        estimator.setEnabled(Set(Self.sources.filter(settings.showsQuota)))
    }

    /// A window's estimate, when the account has one.
    func estimateLine(_ source: SourceKind, host: String? = nil, window: QuotaWindow) -> QuotaEstimateDisplay? {
        estimateLines[QuotaEstimateLineKey(source: source, host: host, window: window)]
    }

    /// Every shown window of this Mac and of the hosts, from the newest build, at the controller's clock.
    func refreshEstimateLines() {
        guard let build = estimateBuild else { return }
        let now = clockMs
        var next: [QuotaEstimateLineKey: QuotaEstimateDisplay] = [:]
        func add(_ snapshot: QuotaSnapshot, host: String?) {
            let source = QuotaEstimateSource(source: snapshot.source.rawValue, host: host)
            for window in snapshot.windows {
                guard let display = build.display(source: source, window: window, nowMs: now) else { continue }
                next[QuotaEstimateLineKey(source: snapshot.source, host: host, window: window)] = display
            }
        }
        for source in Self.sources {
            if let snapshot = lines(source) { add(snapshot, host: nil) }
        }
        for (host, state) in hostQuotas {
            for report in state.reports.values { add(report.snapshot, host: host) }
        }
        if next != estimateLines { estimateLines = next }
    }

    /// A quota round's snapshots and sign-in keys.
    func feedEstimate(_ result: QuotaWorker.Result, desktop readsDesktop: Bool, cli readsCli: Bool) {
        guard let estimator else { return }
        if readsDesktop, let snapshot = result.desktop ?? nil {
            estimator.snapshot(snapshot, orgKey: result.desktopOrgKey)
        }
        if readsCli, let snapshot = result.cli ?? nil { estimator.snapshot(snapshot) }
        if let codex = result.codexKey { estimator.codexSignIn(key: codex.key, at: codex.at) }
        if let profile = result.claudeProfile { estimator.claudeSignIn(profile) }
        let changed = (result.desktop.map { $0 != desktop } ?? false) || (result.cli.map { $0 != cli } ?? false)
        if changed { DispatchQueue.main.async { [weak self] in self?.refreshEstimateLines() } }
    }

    /// The phase 2 readers' snapshots (QUOTAS 10.4: each vendor response).
    func feedEstimate(vendor readings: QuotaVendorReadings) {
        guard let estimator else { return }
        for source in Self.sources where !Self.phaseOneSources.contains(source) {
            if let snapshot = readings.snapshot(source) { estimator.snapshot(snapshot) }
        }
        refreshEstimateLines()
    }
}
