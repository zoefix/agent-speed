import AgentSpeedCore
import SwiftUI

/// `AGENT_SPEED_PREVIEWS=estimate`: the Sources rows with quota estimates (QUOTAS 10.7, 10.8; DESIGN 4 *Percent*,
/// *Amount*): the live percent with `≈`, the amount used with `≈` or `≥` the cap, and a line's tooltip with the
/// estimate's details, light and dark, in Traditional Chinese.
@MainActor
enum QuotaEstimatePreviews {
    static func render(model: AppModel, into directory: URL) {
        let now = QuotaController.nowMs()
        let settings = model.settings
        let language = settings.language
        defer {
            settings.language = language
            model.l10n.apply(language)
        }
        settings.language = .zhHant
        model.l10n.apply(.zhHant)
        settings.quotaCodex = true
        settings.quotaClaudeDesktop = true
        settings.quotaClaudeCli = true
        settings.quotaCliInviteDismissed = true
        QuotaPreviews.publish(model, now: now)
        let desktop = QuotaPreviews.desktop(now), cli = QuotaPreviews.cli(now)
        model.quota.preview(desktop: desktop, cli: cli, cliReceivedMs: now - 2 * 60_000, status: .ok, nowMs: now)
        model.quota.estimateLines = estimates(desktop: desktop, cli: cli, codex: QuotaPreviews.codex(now))
        for dark in [false, true] {
            write(sources(model), dark: dark, to: directory, "estimate-sources-\(dark ? "dark" : "light")")
        }
        // The lines as the section fits them: the tooltip then opens with a reset the row had no room for.
        let kinds: [SourceKind] = [.codex, .claudeCode, .claudeDesktop]
        let (fitted, _) = SourceQuotaColumns.fit(Dictionary(uniqueKeysWithValues: kinds.map { ($0, model.quotaLines($0)) }),
                                                 separator: " · ")
        guard let line = fitted[.claudeDesktop]?.first(where: { !$0.estimateDetails.isEmpty }) else { return }
        let tooltip = QuotaTooltipView(tooltip: QuotaTooltip(head: line.tooltipHead, details: line.estimateDetails,
                                                             note: line.estimateNote))
            .padding(16)
            .appEnvironment(model)
        for dark in [false, true] {
            write(tooltip, dark: dark, to: directory, "estimate-tooltip-\(dark ? "dark" : "light")")
        }
        // English has the narrower `$`: the short resets fit.
        settings.language = .en
        model.l10n.apply(.en)
        write(sources(model), dark: false, to: directory, "estimate-sources-en-light")
        settings.language = .zhHant
        model.l10n.apply(.zhHant)
        model.quota.estimateLines = [:]
        renderReport(model: model, now: now, into: directory)
    }

    /// The reporting pane with the 額度 switch and the preview window's body with `quotas` from `demoBuild`.
    private static func renderReport(model: AppModel, now: Int64, into directory: URL) {
        let entries = model.report.quotaEntries
        defer {
            model.report.quotaEntries = entries
            model.report.preview(.off, username: "")
        }
        let build = demoBuild(now: now)
        model.report.quotaEntries = { _ in build.entries(nowMs: now) }
        model.report.preview(.reporting, username: "小明的 MacBook Pro")
        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            write(SettingsPaneView(pane: .report, model: model).appEnvironment(model), dark: dark, to: directory,
                  "estimate-report-pane-\(scheme)")
            write(ReportPreviewBox(model: model, preview: model.report.preview(), showsRequest: true, maxHeight: 9000)
                    .frame(width: 528).padding(Spacing.m).appEnvironment(model),
                  dark: dark, to: directory, "estimate-report-preview-\(scheme)")
        }
        let text = model.report.preview()?.copyText ?? ""
        try? text.write(to: directory.appendingPathComponent("estimate-report-preview.json"), atomically: true,
                        encoding: .utf8)
    }

    static func estimates(desktop: QuotaSnapshot, cli: QuotaSnapshot, codex: QuotaSnapshot)
        -> [QuotaEstimateLineKey: QuotaEstimateDisplay] {
        func window(_ snapshot: QuotaSnapshot, _ kind: QuotaWindowKind) -> QuotaWindow {
            snapshot.windows.first { $0.kind == kind }!
        }
        let week = QuotaEstimateDisplay(
            percent: 95.8, estimated: true, resetsMs: window(desktop, .sevenDay).resetsAtMs, usedMicroUsd: 5_431_200_300,
            confidence: .medium, capMicroUsd: 5_742_000_000, lowMicroUsd: 5_629_000_000, highMicroUsd: 5_857_000_000,
            coverage: 1, accountUsedMicroUsd: 5_500_800_000, windows: 2, plan: "claude_max", tier: "default_claude_max_20x")
        return [
            QuotaEstimateLineKey(source: .claudeDesktop, window: window(desktop, .sevenDay)): week,
            QuotaEstimateLineKey(source: .claudeDesktop, window: window(desktop, .modelWeek)): QuotaEstimateDisplay(
                percent: 27, estimated: false, resetsMs: window(desktop, .modelWeek).resetsAtMs, usedMicroUsd: 161_400_000,
                confidence: .low, lowMicroUsd: 597_800_000, windows: 1, plan: "claude_max", tier: "default_claude_max_20x"),
            QuotaEstimateLineKey(source: .claudeCode, window: window(cli, .fiveHour)): QuotaEstimateDisplay(
                percent: 25.1, estimated: true, resetsMs: window(cli, .fiveHour).resetsAtMs, usedMicroUsd: 208_900_000,
                confidence: .low, capMicroUsd: 832_000_000, lowMicroUsd: 728_000_000, highMicroUsd: 951_000_000,
                coverage: 1, accountUsedMicroUsd: 208_800_000, windows: 1, plan: "claude_max", tier: "default_claude_max_20x"),
            QuotaEstimateLineKey(source: .claudeCode, window: window(cli, .sevenDay)): QuotaEstimateDisplay(
                percent: 86, estimated: false, resetsMs: window(cli, .sevenDay).resetsAtMs, usedMicroUsd: 4_938_100_000,
                confidence: .medium, capMicroUsd: 5_742_000_000, lowMicroUsd: 5_629_000_000, highMicroUsd: 5_857_000_000,
                coverage: 1, accountUsedMicroUsd: 4_938_100_000, windows: 2, plan: "claude_max", tier: "default_claude_max_20x"),
            QuotaEstimateLineKey(source: .codex, window: window(codex, .sevenDay)): QuotaEstimateDisplay(
                percent: 26.2, estimated: true, resetsMs: window(codex, .sevenDay).resetsAtMs, usedMicroUsd: 98_300_000,
                confidence: .medium, capMicroUsd: 397_000_000, lowMicroUsd: 390_000_000, highMicroUsd: 405_000_000,
                coverage: 0.94, accountUsedMicroUsd: 104_000_000, windows: 3, plan: "promax"),
        ]
    }

    /// A Claude Max account and a Codex Pro account over a closed week and the current one, through the real
    /// estimator: spend every 20 (30) minutes, the vendor's integer percent every 2 hours (after each response), caps of
    /// $5,742 and $397. For the report previews (REPORTING-EXTENSIONS 11.2).
    static func demoBuild(now: Int64) -> QuotaEstimateBuild {
        let minute: Int64 = 60_000, day: Int64 = 86_400_000, week = 7 * day
        let salt = Data(repeating: 7, count: 32)
        let at = now / minute * minute
        var inputs = QuotaEstimateInputs()
        inputs.priceVersion = 1
        inputs.claudeProfiles = [QuotaClaudeProfile(at: at - 9 * day, organization: "demo-org", account: "demo-user",
                                                    organizationType: "claude_max", tier: "default_claude_max_20x",
                                                    salt: salt)]
        for reset in [at - day + 17 * minute, at + 6 * day + 17 * minute] {
            var used = 0.0, step = 0
            var t = reset - week + 30 * minute
            while t < min(reset, now - 2 * minute) {
                inputs.spend.append(QuotaEstimateSpend(source: "claudeCode", model: "claude-opus-5-5", startMs: t - minute,
                                                       endMs: t, costMicroUsd: 8_000_000, tokens: 40_000))
                used += 8_000_000
                if step % 6 == 0 {
                    let percent = (100 * used / 5_742_000_000).rounded(.down)
                    inputs.snapshots.append(QuotaEstimateSnapshot(source: "claudeCode", observedMs: t + 30_000, windows: [
                        QuotaWindow(kind: .sevenDay, usedPercent: percent, resetsAtMs: reset)]))
                }
                step += 1
                t += 20 * minute
            }
        }
        for reset in [at - 3 * day + 41 * minute, at + 4 * day + 41 * minute] {
            var used = 0.0
            var t = reset - week + 45 * minute
            while t < min(reset, now - 2 * minute) {
                inputs.spend.append(QuotaEstimateSpend(source: "codex", model: "gpt-5.5", startMs: t - 40_000, endMs: t,
                                                       costMicroUsd: 950_000, tokens: 30_000, file: "/demo/rollout.jsonl"))
                used += 950_000
                inputs.codexLines.append(QuotaCodexLine(file: "/demo/rollout.jsonl", ts: t + 1_500, windows: [
                    QuotaWindow(kind: .sevenDay, usedPercent: (100 * used / 397_000_000).rounded(.down), resetsAtMs: reset)],
                    plan: "pro"))
                t += 30 * minute
            }
        }
        inputs.codexKeys = [QuotaKeyEntry(at: at - 9 * day, key: QuotaAccounts.key(salt: salt, vendor: "codex", id: "demo"))]
        return QuotaEstimateBuild(inputs, salt: salt, nowMs: now)
    }

    private static func sources(_ model: AppModel) -> some View {
        SourcesSection(model: model)
            .padding(DashboardLayout.padding)
            .frame(width: DashboardController.width)
            .appEnvironment(model)
    }

    private static func write(_ view: some View, dark: Bool, to directory: URL, _ name: String) {
        PreviewSnapshot.writeHosted(view, dark: dark, to: directory.appendingPathComponent("\(name).png"))
    }
}
