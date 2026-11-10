import AgentSpeedCore
import SwiftUI

/// `AGENT_SPEED_PREVIEWS=quota`: the dashboard's Sources rows with their quota lines (Claude Desktop 7d + Fable 7d,
/// Claude Code 5h + 7d, Codex 7d, light and dark), the Claude Code invitation, and the Settings quota group in each
/// status of the Claude CLI bridge (DESIGN 4, 5).
@MainActor
enum QuotaPreviews {
    static func render(model: AppModel, into directory: URL) {
        let now = QuotaController.nowMs()
        let settings = model.settings
        settings.quotaCodex = true
        settings.quotaClaudeDesktop = true
        settings.quotaClaudeCli = true
        settings.quotaCliInviteDismissed = false
        publish(model, now: now)
        model.quota.preview(desktop: desktop(now), cli: cli(now), cliReceivedMs: now - 2 * 60_000, status: .ok, nowMs: now)
        for dark in [false, true] {
            write(sources(model), dark: dark, to: directory, "quota-dashboard-sources-\(dark ? "dark" : "light")")
            write(DashboardView(model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now), dark: dark,
                  to: directory, "quota-dashboard-\(dark ? "dark" : "light")")
        }
        write(sources(model).environment(\.previewHoveredSource, .claudeDesktop), dark: false, to: directory,
              "quota-dashboard-sources-hover-light")

        // Before the bridge is on: the invitation under Claude Code.
        settings.quotaClaudeCli = false
        model.quota.preview(desktop: desktop(now), cli: nil, nowMs: now)
        for dark in [false, true] {
            write(sources(model), dark: dark, to: directory, "quota-dashboard-invitation-\(dark ? "dark" : "light")")
        }

        // Settings → Data Sources, the Codex and Claude groups in each state of the bridge.
        let states: [(String, Bool, StatusLineSettings.Status?, QuotaController.BridgeFailure?)] = [
            ("off", false, nil, nil), ("waiting", true, .waiting, nil), ("ok", true, .ok, nil),
            ("replaced", true, .replaced, nil), ("unreadable", false, nil, .unreadable), ("failed", false, nil, .failed),
        ]
        for (name, on, status, failure) in states {
            settings.quotaClaudeCli = on
            model.quota.preview(desktop: desktop(now), cli: on ? cli(now) : nil, cliReceivedMs: now - 2 * 60_000,
                                status: status, failure: failure, nowMs: now)
            for dark in name == "ok" || name == "replaced" ? [false, true] : [false] {
                write(settingsGroup(model), dark: dark, to: directory, "quota-settings-\(name)-\(dark ? "dark" : "light")")
            }
        }
        settings.quotaClaudeCli = false
        write(SettingsPaneView(pane: .about, model: model).appEnvironment(model), dark: false, to: directory,
              "quota-settings-about-light")
    }

    static func desktop(_ now: Int64) -> QuotaSnapshot {
        let resets = now + (10 * 60 + 39) * 60_000
        return QuotaSnapshot(source: .claudeDesktop, observedMs: now - 7 * 60_000, windows: [
            QuotaWindow(kind: .sevenDay, usedPercent: 95, resetsAtMs: resets),
            QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 27, resetsAtMs: resets),
        ])
    }

    static func cli(_ now: Int64) -> QuotaSnapshot {
        QuotaSnapshot(source: .claudeCode, observedMs: now - 2 * 60_000, windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: 23.4, resetsAtMs: now + (2 * 60 + 12) * 60_000),
            QuotaWindow(kind: .sevenDay, usedPercent: 86, resetsAtMs: now + (2 * 24 + 5) * 3_600_000),
        ])
    }

    static func codex(_ now: Int64) -> QuotaSnapshot {
        QuotaSnapshot(source: .codex, observedMs: now - 80 * 60_000, windows: [
            QuotaWindow(kind: .sevenDay, usedPercent: 25, resetsAtMs: now + (5 * 24 + 3) * 3_600_000),
        ])
    }

    /// The three quota sources and Gemini, which has no quota, producing today.
    static func publish(_ model: AppModel, now: Int64) {
        var snapshot = PreviewData.snapshot(speed: model.settings.redline * 0.3, redline: model.settings.redline)
        let kept: Set<SourceKind> = [.codex, .claudeCode, .claudeDesktop, .gemini]
        snapshot.sources = snapshot.sources.map { source in
            guard !kept.contains(source.kind) else { return source }
            var idle = source
            idle.today = TokenTotals()
            idle.live = .zero
            return idle
        }
        snapshot.agents = snapshot.agents.filter { kept.contains($0.source) }
        snapshot.codexQuota = codex(now)
        PreviewRenderer.publish(model, snapshot: snapshot)
    }

    private static func sources(_ model: AppModel) -> some View {
        SourcesSection(model: model)
            .padding(DashboardLayout.padding)
            .frame(width: DashboardController.width)
            .appEnvironment(model)
    }

    private static func settingsGroup(_ model: AppModel) -> some View {
        Form {
            Section {
                SourceToggleRow(kind: .codex, model: model)
                QuotaSwitches(folder: .codex, model: model)
            }
            Section {
                SourceToggleRow(kind: .claudeCode, model: model)
                SourceToggleRow(kind: .claudeDesktop, model: model)
                QuotaSwitches(folder: .claude, model: model)
            }
        }
        .settingsForm()
        .frame(width: SettingsPaneView.width(for: .sources))
        .fixedSize(horizontal: false, vertical: true)
        .appEnvironment(model)
    }

    private static func write(_ view: some View, dark: Bool, to directory: URL, _ name: String) {
        PreviewSnapshot.writeHosted(view, dark: dark, to: directory.appendingPathComponent("\(name).png"))
    }
}
