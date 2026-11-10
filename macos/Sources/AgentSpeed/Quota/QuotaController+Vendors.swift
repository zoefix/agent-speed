import AgentSpeedCore
import Foundation

/// The phase 2 readers (QUOTAS 9) as the UI drives them: a round every 15 s on the vendor service (which keeps the
/// cadence of 9.1), the ZCode key in the keychain, and each switch's status line (DESIGN 5, *額度, phase 2*).
extension QuotaController {
    func runVendorRound(nowMs: Int64) {
        guard isLive else { return }
        guard !vendorInFlight else {
            vendorRerun = true
            return
        }
        let routes = settings.quotaRoutes
        guard !routes.isEmpty || !vendor.routes.isEmpty else { return }
        let round = QuotaVendorRound(nowMs: nowMs, networkEnabled: settings.networkEnabled, routes: routes,
                                     running: apps.running, dashboardOpen: dashboardOpen,
                                     zaiRegion: settings.quotaZcodeRegion, readsLocalNow: readsLocalNext)
        readsLocalNext = false
        vendorInFlight = true
        let service = vendorService
        Task { [weak self] in
            let readings = await service.refresh(round)
            await MainActor.run {
                guard let self else { return }
                self.vendorInFlight = false
                if self.vendor != readings { self.vendor = readings }
                if self.vendorRerun {
                    self.vendorRerun = false
                    self.runVendorRound(nowMs: QuotaController.nowMs())
                }
            }
        }
    }

    /// The status line under a route's switch; nil when there is nothing to say yet.
    func status(_ route: QuotaRoute) -> QuotaRouteStatus? {
        guard isRouteOn(route) else { return nil }
        // Without a saved key the field itself says what to do.
        if route == .zcode, zcodeKeySaved != true { return nil }
        if !settings.networkEnabled, !route.isLocal { return .networkOff }
        return vendor.routes[route]?.status
    }

    func isRouteOn(_ route: QuotaRoute) -> Bool { settings.quotaRoutes.contains(route) }

    // MARK: ZCode's key (QUOTAS 9.8)

    /// Saves a pasted key to the keychain; the field then shows it as saved and never shows it again.
    @discardableResult
    func saveZcodeKey(_ text: String) -> Bool {
        guard let key = ZcodeKey.cleaned(text) else { return false }
        guard isLive ? zcodeKeys.save(key) : true else { return false }
        zcodeKeySaved = true
        forgetZcode()
        return true
    }

    func removeZcodeKey() {
        if isLive { zcodeKeys.remove() }
        zcodeKeySaved = false
        forgetZcode()
    }

    /// A new key (or region) asks at once and never shows the previous key's numbers.
    func forgetZcode() {
        guard isLive else { return }
        let service = vendorService
        Task { [weak self] in
            await service.forget(.zcode)
            await MainActor.run { self?.tick(force: true) }
        }
    }
}

/// The source names and sites the status lines and the cookie dialog say (DESIGN 5).
extension QuotaRoute {
    /// The site a browser route reads, as the dialog names it: `Qoder` / `qoder.com`, `WorkBuddy` / `www.workbuddy.cn`.
    var site: (name: String, host: String)? {
        switch self {
        case .qoder: ("Qoder", QuotaHosts.qoder)
        case .workbuddy: ("WorkBuddy", QuotaHosts.workbuddy)
        default: nil
        }
    }

    /// The agent `settings.quota.status.expired` and `.notRunning` name; Qoder's account serves the IDE and the CLI.
    @MainActor
    func agentName(_ l10n: Localizer) -> String {
        self == .qoder ? "Qoder" : l10n(source.titleKey)
    }
}
