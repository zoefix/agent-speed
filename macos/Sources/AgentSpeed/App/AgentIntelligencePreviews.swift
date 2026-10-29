import AgentSpeedCore
import AppKit
import SwiftUI

/// `--render-previews` group `agentintel`: the agents list with its intelligence column (DESIGN 4) — a root with a
/// sub-agent and a sub-sub-agent carrying the widest capsule, a long project name at rest, a model-less agent, a
/// DeepSeek agent, an agent running tools — in Chinese, English and Japanese, both appearances and Increase Contrast,
/// and the same rows with every form held on one value, to check that a form change moves nothing.
@MainActor
enum AgentIntelligencePreviews {
    static func render(model: AppModel, into directory: URL) {
        let saved = (model.settings.language, model.settings.agentsExpanded)
        defer {
            model.settings.language = saved.0
            model.l10n.apply(saved.0)
            model.settings.agentsExpanded = saved.1
        }
        model.settings.agentsExpanded = true
        PreviewRenderer.publish(model, snapshot: snapshot())
        for language in [LanguagePreference.zhHant, .en, .ja] {
            model.settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let name = "agentintel-\(language.rawValue)-\(dark ? "dark" : "light")"
                write(section(model), dark: dark, to: directory.appendingPathComponent("\(name).png"))
                if language == .zhHant {
                    write(section(model).environment(\._colorSchemeContrast, .increased), dark: dark,
                          to: directory.appendingPathComponent("\(name)-contrast.png"))
                }
            }
        }
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        PreviewRenderer.publish(model, snapshot: steadySnapshot())
        for dark in [false, true] {
            write(section(model), dark: dark, to: directory.appendingPathComponent("agentintel-forms-\(dark ? "dark" : "light").png"))
        }
        model.settings.agentsExpanded = false
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.4)
        for dark in [false, true] {
            write(DashboardView(model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now), dark: dark,
                  to: directory.appendingPathComponent("agentintel-dashboard-\(dark ? "dark" : "light").png"))
        }
    }

    private static func section(_ model: AppModel) -> some View {
        let width = DashboardController.width - 2 * DashboardLayout.padding
        return AgentsSection(model: model, rowWidth: width)
            .padding(DashboardLayout.padding)
            .frame(width: DashboardController.width)
            .appEnvironment(model)
    }

    private static func write<V: View>(_ view: V, dark: Bool, to url: URL) {
        PreviewSnapshot.writeHosted(view, dark: dark, to: url)
    }

    /// Every form and placement of the column: the prototype's agents (live, provisional, absent components, resting,
    /// none), a compact `11.9k`, a long project name at rest and a model-less agent.
    static func snapshot() -> SpeedSnapshot {
        var snapshot = PreviewData.snapshot(speed: 420, redline: 2000, agents: .crowd(0))
        let now = snapshot.timestamp
        var specs = PerfPreviewData.specs
        var atlas = PerfPreviewData.spec("orbit")
        atlas.id = "atlas"
        atlas.project = "intelligence-column-with-a-very-long-project-name"
        specs.append(atlas)
        var halo = PerfPreviewData.spec("lumen")
        halo.id = "halo"
        halo.project = "halo"
        halo.model = ""
        specs.append(halo)
        snapshot.agents = specs.map { spec in
            var agent = PerfPreviewData.agent(spec, now: now)
            if spec.model.isEmpty { agent.model = nil }
            return agent
        }
        snapshot.intelligence = snapshot.agents.reduce(into: IntelligenceSums()) { $0.add($1.intelligence) }
        return snapshot
    }

    /// Live, resting, provisional and none on rows whose shown value is the same, so the digits of a form change can be
    /// compared in place.
    static func steadySnapshot() -> SpeedSnapshot {
        var snapshot = PreviewData.snapshot(speed: 420, redline: 2000, agents: .crowd(0))
        let now = snapshot.timestamp
        func steady(_ id: String, _ phase: AgentPhase, n: Int) -> AgentStatus {
            var spec = PerfPreviewData.spec("nova")
            (spec.id, spec.project, spec.phase, spec.n, spec.parent) = (id, "\(id)-1286", phase, n, nil)
            return PerfPreviewData.agent(spec, now: now)
        }
        snapshot.agents = [
            steady("live", .generating, n: 20),
            steady("resting", .idle, n: 20),
            steady("provisional", .generating, n: 6),
            steady("none", .idle, n: 2),
        ]
        return snapshot
    }
}
