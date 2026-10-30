import AgentSpeedCore
import AppKit
import SwiftUI

/// `--render-previews` group `configured`: the agents list with every configured capsule (DESIGN 4, *Configured capsule*)
/// beside real ones, in both appearances, with Increase Contrast, narrow, and in every language. Boards show it through
/// the `configured` scenario of `BoardScorePreviews`.
@MainActor
enum ConfiguredCapsulePreviews {
    static func render(model: AppModel, into directory: URL) {
        let saved = model.settings.language, expanded = model.settings.agentsExpanded
        defer {
            model.settings.language = saved
            model.settings.agentsExpanded = expanded
            model.l10n.apply(saved)
        }
        model.settings.agentsExpanded = true
        PreviewRenderer.publish(model, snapshot: snapshot())
        for (name, language, _) in ReportPreviews.languages {
            model.settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let scheme = dark ? "dark" : "light"
                let width = DashboardController.width - 2 * DashboardLayout.padding
                write(section(model, width: width), dark: dark, to: directory, "configured-\(name)-\(scheme)")
                write(section(model, width: 236), dark: dark, to: directory, "configured-narrow-\(name)-\(scheme)")
                write(section(model, width: width).environment(\._colorSchemeContrast, .increased), dark: dark, to: directory,
                      "configured-contrast-\(name)-\(scheme)")
            }
        }
    }

    private static func section(_ model: AppModel, width: CGFloat) -> some View {
        AgentsSection(model: model, rowWidth: width).padding(20).frame(width: width + 40).appEnvironment(model)
    }

    private static func write<V: View>(_ view: V, dark: Bool, to directory: URL, _ name: String) {
        PreviewSnapshot.writeHosted(view, dark: dark, to: directory.appendingPathComponent(name + ".png"))
    }

    private static func snapshot() -> SpeedSnapshot {
        var snapshot = PreviewData.snapshot(speed: 420, redline: 2000, agents: .crowd(0))
        let now = snapshot.timestamp
        func agent(_ id: String, _ source: SourceKind, _ model: String, _ live: Double,
                   facts: [ExecutionModeFact] = [], session: [ExecutionModeFact] = [], configured: [ExecutionModeFact] = []) -> AgentStatus {
            var agent = AgentStatus(id: "/preview/\(id).jsonl", source: source, project: id, model: model,
                                    phase: live > 0 ? .generating : .idle, isSubagent: false,
                                    live: Rate(output: live, total: live * 26), recentSpeed: live > 0 ? live : nil,
                                    lastActivity: now.addingTimeInterval(live > 0 ? 0 : -90))
            agent.modes = ExecutionModes(facts)
            agent.sessionModes = ExecutionModes(session)
            agent.configuredModes = ExecutionModes(configured)
            agent.intelligence = PreviewData.intelligence(output: live, score: PreviewData.previewScore(model, source: source),
                                                          id: agent.id, idle: live == 0, thinkingShare: 0.3, source: source)
            return agent
        }
        func effort(_ token: String, _ evidence: ExecutionModeEvidence, _ scope: ExecutionModeScope) -> ExecutionModeFact {
            ExecutionModeFact(kind: .effort, rawValue: token, evidence: evidence, scope: scope)!
        }
        func tier(_ token: String, _ evidence: ExecutionModeEvidence, _ scope: ExecutionModeScope) -> ExecutionModeFact {
            ExecutionModeFact(kind: .serviceTier, rawValue: token, evidence: evidence, provider: "openai", scope: scope)!
        }
        snapshot.agents = [
            agent("recorded", .claudeCode, "claude-opus-5-5", 142, facts: [effort("max", .recorded, .request)]),
            agent("gemini", .gemini, "gemini-3.5-pro", 96, configured: [effort("high", .configured, .configuration)]),
            agent("hermes", .hermes, "gpt-6.1-sol", 88, configured: [effort("max", .configured, .configuration),
                                                                     tier("fast", .configured, .configuration)]),
            agent("codex-ultra", .codex, "gpt-6.1-sol", 61, configured: [effort("ultra", .configured, .configuration)]),
            agent("antigravity", .antigravity, "gemini-3.7-flash", 44, configured: [tier("ultrafast", .configured, .configuration)]),
            agent("mixed-tier", .codex, "gpt-6.1-sol", 33, facts: [effort("xhigh", .recorded, .turn)],
                  configured: [tier("fast", .configured, .configuration)]),
            agent("mixed-effort", .workbuddy, "deep-model", 21, facts: [tier("fast", .recorded, .request)],
                  configured: [effort("medium", .configured, .configuration)]),
            agent("session", .grokBuild, "grok-code-fast-1", 12, session: [effort("high", .selected, .session)]),
        ]
        return snapshot
    }
}
