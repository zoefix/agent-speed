import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 4, *Configured capsule*: its form, its place in the column, and what the tooltip and VoiceOver say.
@Suite("Configured capsule")
@MainActor
struct ConfiguredCapsuleTests {
    let zh = Localizer(preference: .zhHant)
    let en = Localizer(preference: .en)

    private func fact(_ kind: ExecutionModeKind, _ token: String, _ evidence: ExecutionModeEvidence) -> ExecutionModeFact {
        ExecutionModeFact(kind: kind, rawValue: token, evidence: evidence, provider: kind == .serviceTier ? "openai" : nil,
                          scope: evidence == .configured ? .configuration : .request)!
    }

    private func capsule(facts: [ExecutionModeFact] = [], configured: [ExecutionModeFact] = []) -> EffortCapsule {
        EffortCapsule(EffortAndSpeed(facts: ExecutionModes(facts), configured: ExecutionModes(configured)), zh)
    }

    @Test func onlyConfiguredPartsMakeTheDashedForm() {
        #expect(capsule(configured: [fact(.effort, "high", .configured)]).isConfiguredForm)
        #expect(capsule(configured: [fact(.effort, "max", .configured), fact(.serviceTier, "fast", .configured)]).isConfiguredForm)
        let speedOnly = capsule(configured: [fact(.serviceTier, "ultrafast", .configured)])
        #expect(speedOnly.isConfiguredForm)
        #expect(speedOnly.showsMark(compact: false) && !speedOnly.showsMark(compact: true), "a capsule with only a speed still says what it is")
        #expect(!capsule(facts: [fact(.effort, "high", .recorded)]).isConfiguredForm)
        // One real part keeps the solid capsule, and the mark would not say which part it means.
        let mixed = capsule(facts: [fact(.effort, "xhigh", .recorded)], configured: [fact(.serviceTier, "fast", .configured)])
        #expect(!mixed.isConfiguredForm && !mixed.showsMark(compact: false) && mixed.speedIsConfigured)
        let mixedEffort = capsule(facts: [fact(.serviceTier, "fast", .recorded)], configured: [fact(.effort, "medium", .configured)])
        #expect(!mixedEffort.isConfiguredForm && mixedEffort.tone == .configured)
        // A real standard speed draws nothing, so the configured effort is all the capsule shows.
        #expect(capsule(facts: [fact(.serviceTier, "default", .served)], configured: [fact(.effort, "high", .configured)]).isConfiguredForm)
        // A configured standard tier beside a real effort draws nothing of its own: the capsule is a real one.
        #expect(!capsule(facts: [fact(.effort, "high", .recorded)], configured: [fact(.serviceTier, "default", .configured)]).isConfiguredForm)
        #expect(!capsule(configured: [fact(.serviceTier, "default", .configured)]).isVisible)
    }

    @Test func nothingMovesWhenARealValueReplacesIt() {
        let configured = capsule(configured: [fact(.effort, "high", .configured)])
        let real = capsule(facts: [fact(.effort, "high", .recorded)])
        #expect(configured.level == real.level && configured.bolts == real.bolts)
        #expect(configured.width(compact: true) == real.width(compact: true), "without its mark it has the same parts")
        #expect(configured.width(compact: false) == real.width(compact: false) + EffortCapsule.partSpacing
            + EffortCapsule.markWidth(zh("mode.configured.mark")))
    }

    @Test func aTopRungGetsNoHeatDetailOrGlowFromAConfigFile() {
        let ultra = capsule(configured: [fact(.effort, "ultra", .configured)])
        #expect(ultra.isTopRung && ultra.level == 5 && ultra.tone == .configured)
        #expect(ultra.tooltipLines(zh) == [zh("effort.tooltip.configured", "ultra"), zh("speed.tooltip", zh("modes.unrecorded")),
                                           zh("modes.configured.note")])
        let real = capsule(facts: [fact(.effort, "ultra", .recorded)])
        #expect(real.tooltipLines(zh).last == zh("effort.ultra.detail"))
    }

    @Test func tooltipAndVoiceOverNameItAsConfigured() {
        let both = EffortCapsule(EffortAndSpeed(facts: .empty, configured: ExecutionModes(
            [fact(.effort, "high", .configured), fact(.serviceTier, "priority", .configured)])), en)
        #expect(both.tooltipLines(en) == ["Reasoning effort: high (configured)", "Speed: Fast (configured)", en("modes.configured.note")])
        #expect(both.accessibilityText(en) == "Reasoning effort: high (configured), Speed: Fast (configured)")
        let mixed = capsule(facts: [fact(.effort, "xhigh", .recorded)], configured: [fact(.serviceTier, "priority", .configured)])
        #expect(mixed.tooltipLines(zh) == [zh("effort.tooltip", "xhigh"), zh("speed.tooltip.configured", zh("speed.value.fast")),
                                           zh("modes.configured.note")])
        #expect(!mixed.accessibilityText(zh).contains(zh("modes.configured.note")))
    }

    @Test func theColumnFitsTheMark() {
        let configured = capsule(configured: [fact(.effort, "high", .configured)])
        let wide = AgentCapsuleColumn(rows: [(configured, 0)], rowWidth: 400, intelligence: 24)
        #expect(!wide.compact && wide.width == configured.width(compact: false))
        let narrow = AgentCapsuleColumn(rows: [(configured, 0)], rowWidth: 200, intelligence: 24)
        #expect(narrow.compact && !configured.showsMark(compact: true) && narrow.width == configured.width(compact: true))
    }

    @Test func aStatusShowsItsCurrentValuesInOrder() {
        var agent = AgentStatus(id: "a", source: .grokBuild, project: "p", model: nil, phase: .idle, isSubagent: false, live: .zero,
                                recentSpeed: nil, lastActivity: Date())
        agent.configuredModes = ExecutionModes([fact(.effort, "low", .configured)])
        #expect(agent.current.effort?.isConfigured == true)
        agent.sessionModes = ExecutionModes([ExecutionModeFact(kind: .effort, rawValue: "high", evidence: .selected, scope: .session)!])
        #expect(agent.current.effort?.mode == "high" && agent.current.effort?.isConfigured == false)
        agent.modes = ExecutionModes([fact(.effort, "max", .recorded)])
        #expect(agent.current.effort?.mode == "max")
    }
}
