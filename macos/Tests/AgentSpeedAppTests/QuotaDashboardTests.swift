import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// The quota switches, which sources the dashboard and the 額度 card take, and the Sources rows' quota lines
/// (QUOTAS 6; DESIGN 4 *Quota lines*, 5 *額度*).
@Suite("Quota dashboard")
@MainActor
struct QuotaDashboardTests {
    private static func model() -> AppModel {
        let suite = "QuotaDashboardTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(settings: AppSettings(defaults: defaults))
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        return model
    }

    static let now: Int64 = 1_794_338_773_000

    static func desktop(observedAgoMinutes: Int64 = 7) -> QuotaSnapshot {
        QuotaSnapshot(source: .claudeDesktop, observedMs: now - observedAgoMinutes * 60_000, windows: [
            QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 27, resetsAtMs: now + 38_340_000),
            QuotaWindow(kind: .sevenDay, usedPercent: 94.6, resetsAtMs: now + 38_340_000),
        ])
    }

    static let cli = QuotaSnapshot(source: .claudeCode, observedMs: now - 60_000, windows: [
        QuotaWindow(kind: .sevenDay, usedPercent: 86, resetsAtMs: now + 2 * 86_400_000),
    ])

    @Test func switchesStartAsSpecifiedAndPersist() {
        let suite = "QuotaDashboardTests.defaults.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.quotaCodex && settings.quotaClaudeDesktop && !settings.quotaClaudeCli)
        #expect(!settings.quotaCliInviteDismissed)
        settings.quotaCliInviteDismissed = true
        settings.quotaClaudeDesktop = false
        #expect(defaults.string(forKey: "quotaCliInvite") == "dismissed")
        let reloaded = AppSettings(defaults: defaults)
        #expect(reloaded.quotaCliInviteDismissed && !reloaded.quotaClaudeDesktop)
        #expect(!reloaded.showsQuota(.opencode))
    }

    @Test func theCardTakesRunningSignedInSourcesInSourceOrder() {
        let model = Self.model()
        model.settings.quotaClaudeCli = true
        let apps = QuotaApps(codex: false, claudeCode: true, claudeDesktop: true)
        model.quota.preview(desktop: Self.desktop(), cli: Self.cli, apps: apps, signedIn: [.claudeCode], nowMs: Self.now)
        #expect(model.quota.cardSnapshots(within: nil).map(\.source) == [.claudeCode, .claudeDesktop])
        #expect(model.quota.cardSnapshots(within: SourceSet(.claudeDesktop)).map(\.source) == [.claudeDesktop])

        // Desktop counts as signed in while it refreshed its response in the last 24 hours.
        model.quota.preview(desktop: Self.desktop(observedAgoMinutes: 25 * 60), cli: Self.cli, apps: apps,
                            signedIn: [.claudeCode], nowMs: Self.now)
        #expect(model.quota.cardSnapshots(within: nil).map(\.source) == [.claudeCode])

        // Running without a sign-in, or a switch off, leaves the source out.
        model.quota.preview(desktop: Self.desktop(), cli: Self.cli, apps: apps, signedIn: [], nowMs: Self.now)
        model.settings.quotaClaudeDesktop = false
        #expect(model.quota.cardSnapshots(within: nil).isEmpty)
    }

    /// Regression (2026-11-11): Claude Desktop fetches its usage only while its window is in front, so after an hour in
    /// the background its snapshot is an hour old; the dashboard listed it, the 額度 card dropped it.
    @Test func desktopLeftInTheBackgroundStaysOnTheCard() {
        let model = Self.model()
        let apps = QuotaApps(codex: true, claudeCode: true, claudeDesktop: true)
        for minutes: Int64 in [46, 66, 3 * 60, 23 * 60] {
            model.quota.preview(desktop: Self.desktop(observedAgoMinutes: minutes), cli: nil, apps: apps,
                                signedIn: [.codex, .claudeCode], nowMs: Self.now)
            #expect(model.quota.listedSources.contains(.claudeDesktop))
            #expect(model.quota.cardSnapshots(within: nil).map(\.source) == [.claudeDesktop], "\(minutes) min old")
        }
        // Desktop quit: the dashboard keeps its quota, the card does not.
        model.quota.preview(desktop: Self.desktop(observedAgoMinutes: 66), cli: nil,
                            apps: QuotaApps(codex: true, claudeCode: true), signedIn: [.codex], nowMs: Self.now)
        #expect(model.quota.listedSources.contains(.claudeDesktop) && model.quota.cardSnapshots(within: nil).isEmpty)
    }

    @Test func theDashboardListsQuotasOfTheLastDay() {
        let model = Self.model()
        model.quota.preview(desktop: Self.desktop(observedAgoMinutes: 23 * 60), cli: nil, nowMs: Self.now)
        #expect(model.quota.listedSources == [.claudeDesktop])
        model.quota.preview(desktop: Self.desktop(observedAgoMinutes: 25 * 60), cli: nil, nowMs: Self.now)
        #expect(model.quota.listedSources.isEmpty)
        #expect(model.quota.invitesCli)
        model.quota.dismissInvite()
        #expect(!model.quota.invitesCli)
    }

    @Test func linesFollowTheWindowsTheAccountHas() {
        let model = Self.model()
        model.quota.preview(desktop: Self.desktop(), cli: nil, nowMs: Self.now)
        let lines = model.quotaLines(.claudeDesktop)
        #expect(lines.map(\.label) == ["7d", "Fable 7d"])
        #expect(lines.map(\.percentText) == ["95%", "27%"])
        #expect(lines.map(\.level) == [.critical, .normal])
        #expect(lines[0].reset == "10 小時 39 分後重置")
        #expect(lines[0].age == nil)
        #expect(lines[0].spoken == "7d 已用 95%，10 小時 39 分後重置")
        #expect(model.quotaLines(.claudeCode).isEmpty, "the bridge is off")
    }

    @Test func anAgeThatDoesNotFitMovesToTheTooltip() {
        let model = Self.model()
        let short = QuotaSnapshot(source: .claudeDesktop, observedMs: Self.now - 5 * 3_600_000, windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: 5, resetsAtMs: Self.now + 5 * 60_000),
        ])
        model.quota.preview(desktop: short, cli: nil, nowMs: Self.now)
        var lines = model.quotaLines(.claudeDesktop)
        #expect(lines.first?.age == "5 小時前更新")
        lines[0].reset = String(repeating: "很長的重置時間", count: 4)
        let (fitted, columns) = SourceQuotaColumns.fit([SourceKind.claudeDesktop: lines], separator: " · ")
        #expect(fitted[.claudeDesktop]?.first?.age == nil)
        #expect(fitted[.claudeDesktop]?.first?.tooltip.contains("5 小時前更新") == true)
        #expect(columns.label > 0 && columns.percent > 0)
    }

    @Test func quotaLevels() {
        #expect(QuotaLevel(percent: 79) == .normal)
        #expect(QuotaLevel(percent: 80) == .warning)
        #expect(QuotaLevel(percent: 94) == .warning)
        #expect(QuotaLevel(percent: 95) == .critical)
        #expect(QuotaLevel.normal.fill(dark: true) == nil)
        #expect(QuotaLevel.warning.fill(dark: true) == RGBColor(hex: 0xF5A524))
        #expect(QuotaLevel.warning.fill(dark: false) == RGBColor(hex: 0xB86E00))
    }
}
