@testable import AgentSpeed
import AgentSpeedCore
import Foundation
import Testing

/// The quota estimate in the UI (QUOTAS 10.7, 10.8; DESIGN 4 *Percent*, *Amount*, *Tooltip*, 2a.6, 11): the live
/// percent with `≈`, the amount with the cap, the tooltip's details, what accessibility reads, the orb card's live percent
/// and the reporting pane's switch.
@Suite("Quota estimate UI")
@MainActor
struct QuotaEstimateUITests {
    static let now: Int64 = 1_794_338_773_000

    private static func model() -> AppModel {
        let suite = "QuotaEstimateUITests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(settings: AppSettings(defaults: defaults))
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        return model
    }

    static let desktop = QuotaSnapshot(source: .claudeDesktop, observedMs: now - 7 * 60_000, windows: [
        QuotaWindow(kind: .sevenDay, usedPercent: 94, resetsAtMs: now + 38_340_000),
        QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 27, resetsAtMs: now + 38_340_000),
        QuotaWindow(kind: .fiveHour, usedPercent: 12, resetsAtMs: now + 3_600_000),
    ])

    static let week = QuotaEstimateDisplay(
        percent: 95.4, estimated: true, resetsMs: now + 38_340_000, usedMicroUsd: 161_000_000, confidence: .medium,
        capMicroUsd: 5_742_000_000, lowMicroUsd: 5_629_000_000, highMicroUsd: 5_857_000_000, coverage: 0.981,
        accountUsedMicroUsd: 5_477_900_000, windows: 2, plan: "claude_max", tier: "default_claude_max_20x")
    static let fable = QuotaEstimateDisplay(percent: 27, estimated: false, resetsMs: now + 38_340_000,
                                            usedMicroUsd: 22_000_000, confidence: .low, lowMicroUsd: 737_000_000, windows: 1)
    static let fiveHour = QuotaEstimateDisplay(percent: 12, estimated: false, resetsMs: now + 3_600_000,
                                               usedMicroUsd: 3_100_000, confidence: .none)

    /// The snapshot orders its windows: 5h, 7d, Fable 7d.
    static func window(_ kind: QuotaWindowKind) -> QuotaWindow {
        desktop.windows.first { $0.kind == kind }!
    }

    static func estimates() -> [QuotaEstimateLineKey: QuotaEstimateDisplay] {
        [
            QuotaEstimateLineKey(source: .claudeDesktop, window: window(.sevenDay)): week,
            QuotaEstimateLineKey(source: .claudeDesktop, window: window(.modelWeek)): fable,
            QuotaEstimateLineKey(source: .claudeDesktop, window: window(.fiveHour)): fiveHour,
        ]
    }

    @Test func linesShowTheLivePercentTheAmountAndTheCap() {
        let model = Self.model()
        model.quota.preview(desktop: Self.desktop, cli: nil, nowMs: Self.now)
        model.quota.estimateLines = Self.estimates()
        let lines = model.quotaLines(.claudeDesktop)
        let cost = model.cost
        #expect(lines.map(\.label) == ["5h", "7d", "Fable 7d"])
        #expect(lines.map(\.percentText) == ["12%", "95%", "27%"])
        #expect(lines.map(\.estimated) == [false, true, false])
        #expect(abs(lines[1].fraction - 0.954) < 1e-9, "the bar follows the live percent")
        // The cells take the rate's digits and the cap no symbol (DESIGN 4 *Amount*: `US$161 / ≈5,742`); the tooltip
        // the full amounts.
        #expect(lines[1].amount == "US$161" && lines[1].amountCap == " / ≈5,742")
        #expect(lines[2].amountCap == " / ≥737")
        // At confidence none only the used amount shows, and the tooltip has nothing to add.
        #expect(lines[0].amount == "US$3.10" && lines[0].amountCap == nil)
        #expect(lines[0].estimateDetails.isEmpty && lines[0].estimateNote == nil)
        let details = lines[1].estimateDetails
        #expect(details.first == model.l10n("quota.estimate.used", cost.amountWithUSD(microUsd: 161_000_000)))
        #expect(details.contains(model.l10n("quota.estimate.coverage", 98)))
        #expect(details.contains(model.l10n("quota.estimate.confidence.medium") + " · " + model.l10n("quota.estimate.windows", 2)))
        #expect(details.contains(model.l10n("quota.estimate.plan", "claude_max · default_claude_max_20x")))
        #expect(details.last == model.l10n("quota.estimate.live"))
        #expect(lines[1].estimateNote == model.l10n("quota.estimate.note"))
        #expect(lines[2].estimateDetails.contains(model.l10n("quota.estimate.atLeast", cost.amountWithUSD(microUsd: 737_000_000))))
        #expect(!lines[2].estimateDetails.contains(model.l10n("quota.estimate.live")))
        let separator = model.l10n("list.separator")
        #expect(lines[1].spoken.hasSuffix(separator + model.l10n("a11y.quota.amount",
            cost.spoken(cost.amount(microUsd: 161_000_000).number), cost.spoken(cost.amount(microUsd: 5_742_000_000).number))
            + separator + model.l10n("a11y.quota.estimated")))
        #expect(lines[1].spoken.hasPrefix("7d 已用 95%"))
    }

    /// An inferred plan reads `quota.estimate.planInferred` in place of `quota.estimate.plan` (DESIGN 4 *Tooltip*,
    /// QUOTAS 10.2), with ` · ` and the tier when there is one.
    @Test func theTooltipMarksAnInferredPlan() {
        let model = Self.model()
        let text = QuotaEstimateText(l10n: model.l10n, cost: model.cost)
        var ultra = Self.week
        ultra.plan = "claude_ultra"
        ultra.tier = nil
        ultra.planInferred = true
        let details = text.details(ultra)
        #expect(details.contains("方案：claude_ultra（依額度推算）"))
        #expect(!details.contains(model.l10n("quota.estimate.plan", "claude_ultra")))
        var max5 = ultra
        max5.plan = "claude_max"
        max5.tier = "default_claude_max_5x"
        #expect(text.details(max5).contains(model.l10n("quota.estimate.planInferred", "claude_max · default_claude_max_5x")))
        #expect(text.details(Self.week).contains(model.l10n("quota.estimate.plan", "claude_max · default_claude_max_20x")),
                "a plan the vendor names stays unmarked")
    }

    @Test func theAmountColumnKeepsTheResetsInOneColumn() {
        let model = Self.model()
        model.quota.preview(desktop: Self.desktop, cli: nil, nowMs: Self.now)
        let plain = SourceQuotaColumns.fit([SourceKind.claudeDesktop: model.quotaLines(.claudeDesktop)], separator: " · ")
        #expect(plain.columns.amount == 0)
        model.quota.estimateLines = Self.estimates()
        let lines = model.quotaLines(.claudeDesktop)
        let (_, columns) = SourceQuotaColumns.fit([SourceKind.claudeDesktop: lines], separator: " · ")
        let widest = lines.map { SourceQuotaColumns.width(($0.amount ?? "") + ($0.amountCap ?? ""), SourceQuotaColumns.amountFont) }
        #expect(columns.amount == widest.max())
        #expect(columns.percent > SourceQuotaColumns.width("100%", SourceQuotaColumns.percentFont), "a cell as wide as ≈100%")
    }

    /// Beside amounts the section shows the short resets, or none when even those do not fit beside the shortest bar,
    /// and each line's tooltip opens with its long reset; without amounts the long resets stay.
    @Test func theResetsGiveWayToTheAmounts() {
        let model = Self.model()
        model.quota.preview(desktop: Self.desktop, cli: nil, nowMs: Self.now)
        let format = model.l10n.quotaFormat
        let long = Self.desktop.windows.sorted { $0.resetsAtMs! < $1.resetsAtMs! }
            .map { format.resetLong($0.resetsAtMs!, nowMs: Self.now) }
        let plain = SourceQuotaColumns.fit([SourceKind.claudeDesktop: model.quotaLines(.claudeDesktop)], separator: " · ")
        #expect(plain.lines[.claudeDesktop]!.map(\.reset) == [long[0], long[1], long[1]], "without amounts nothing changes")
        #expect(plain.lines[.claudeDesktop]!.allSatisfy { $0.tooltipReset == nil })

        model.quota.estimateLines = Self.estimates()
        let fitted = SourceQuotaColumns.fit([SourceKind.claudeDesktop: model.quotaLines(.claudeDesktop)], separator: " · ")
        let lines = fitted.lines[.claudeDesktop]!
        let rest = SourceQuotaColumns.lineWidth - fitted.columns.label - fitted.columns.percent - fitted.columns.amount
            - 4 * SourceQuotaColumns.gap - SourceQuotaColumns.compactBar
        #expect(lines.map(\.tooltipReset) == [long[0], long[1], long[1]])
        #expect(lines.map(\.tooltipHead.first) == [long[0], long[1], long[1]])
        #expect(lines.map(\.reset) == lines.map(\.resetShort), "the short resets fit in zh-Hant with US$")
        #expect(lines.map(\.reset) == ["1h", "10h", "10h"])
        #expect(fitted.columns.reset <= max(0, rest))
    }

    /// A cap of another currency drops its symbol too (`NT$1,235 / ≈4,560`).
    @Test func theCapNeverRepeatsTheSymbol() {
        let model = Self.model()
        model.settings.currency = "TWD"
        let text = QuotaEstimateText(l10n: model.l10n, cost: model.cost)
        let (used, cap) = text.amountCell(Self.week)
        #expect(used.hasPrefix("NT$") && !(cap ?? "").contains("$"))
        #expect(cap == " / ≈" + model.cost.compactAmountNumber(microUsd: 5_742_000_000))
    }

    @Test func aWindowAfterItsResetTakesTheNewInstance() {
        let passed = QuotaShownWindow(QuotaWindow(kind: .sevenDay, usedPercent: 80, resetsAtMs: Self.now - 1), nowMs: Self.now)
        #expect(passed.passed)
        let next = passed.live(QuotaEstimateDisplay(percent: 3.2, estimated: true, resetsMs: Self.now + 86_400_000,
                                                    usedMicroUsd: 1, confidence: .medium))
        #expect(!next.passed && next.percent == 3 && next.resetsAtMs == Self.now + 86_400_000)
        let waiting = passed.live(QuotaEstimateDisplay(percent: 0, estimated: false, resetsMs: nil, usedMicroUsd: 0,
                                                       confidence: .none))
        #expect(waiting.passed && waiting.percent == 0)
    }

    /// The orb card shows the live percent but no amount (user's choice 2026-11-11: the dashboard has it).
    @Test func theOrbCardShowsTheLivePercentWithoutAmounts() {
        let model = Self.model()
        var estimates: [String: QuotaEstimateDisplay] = [:]
        for (key, value) in Self.estimates() { estimates[key.window] = value }
        let item = QuotaCardItem(snapshot: Self.desktop, estimates: estimates)
        let source = CharacterQuotaText.sources([item], nowMs: Self.now, l10n: model.l10n)[0]
        #expect(source.lines.map(\.percentText) == ["12%", "95%", "27%"])
        #expect(source.lines.map(\.estimated) == [false, true, false])
        let sentence = CharacterQuotaText.sentence([item], nowMs: Self.now, l10n: model.l10n) ?? ""
        #expect(sentence.contains(model.l10n("a11y.quota.estimated")))
        #expect(!sentence.contains("161") && !sentence.contains("5,742"), "the card reads no amount and no cap")
        // Without estimates the card is as before.
        let plain = CharacterQuotaText.sources([QuotaCardItem(snapshot: Self.desktop)], nowMs: Self.now, l10n: model.l10n)[0]
        #expect(plain.lines.map(\.percentText) == ["12%", "94%", "27%"])
    }

    @Test func theCardItemsCarryTheirEstimates() {
        let model = Self.model()
        model.settings.quotaClaudeDesktop = true
        model.quota.preview(desktop: Self.desktop, cli: nil, apps: QuotaApps(claudeDesktop: true), nowMs: Self.now)
        model.quota.estimateLines = Self.estimates()
        let items = model.quota.cardItems(within: nil, hosts: nil, connected: [])
        #expect(items.first?.estimate(Self.window(.sevenDay)) == Self.week)
    }

    // MARK: Reporting (DESIGN 11)

    @Test func theQuotaSwitchFollowsTheExtension() throws {
        #expect(ReportSectionToggles.acceptsQuota(nil), "usable before any document is known")
        let service = try ReportServiceURL.parse("https://example.com").get()
        let assumed = try #require(ReportDiscovery.assumed(for: service, name: "x"))
        #expect(ReportSectionToggles.acceptsQuota(assumed))
        let old = try JSONDecoder().decode(ReportPreferences.self, from: Data(#"{"enabled":true,"sections":["models"]}"#.utf8))
        #expect(old.shareQuota, "on by default, also for preferences stored before the switch existed")
        var off = ReportPreferences()
        off.shareQuota = false
        let back = try JSONDecoder().decode(ReportPreferences.self, from: JSONEncoder().encode(off))
        #expect(!back.shareQuota)
    }
}
