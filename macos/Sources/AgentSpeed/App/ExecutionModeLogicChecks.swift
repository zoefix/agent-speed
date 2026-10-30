import AgentSpeedCore
import Foundation

@MainActor
enum ExecutionModeLogicChecks {
    static func run(l10n: Localizer) -> [(String, Bool)] {
        let configured = ExecutionModeFact(kind: .serviceTier, rawValue: "priority", evidence: .configured,
                                           provider: "openai", scope: .configuration)!
        let standard = ExecutionModeFact(kind: .serviceTier, rawValue: "default", evidence: .served,
                                         provider: "openai", scope: .response)!
        let fastXhigh = capsule("xhigh", tier: "fast")
        let ultracode = capsule("ultracode")
        let future = capsule("warp-2")
        let configuredHigh = EffortCapsule(effort: "high", speed: nil, effortIsConfigured: true, mark: l10n("mode.configured.mark"))
        let column = AgentCapsuleColumn(rows: [(ultracode, 2), (fastXhigh, 0)], rowWidth: 328, intelligence: 24)
        let narrow = AgentCapsuleColumn(rows: [(ultracode, 2), (fastXhigh, 0)], rowWidth: 200, intelligence: 24)
        return [
            ("modes: missing shows no capsule", !EffortCapsule(.empty).isVisible),
            ("modes: configured is not a task mode", EffortCapsule(ExecutionModes([configured])) == EffortCapsule(effort: nil, speed: nil)),
            ("modes: a standard speed draws nothing", !EffortCapsule(ExecutionModes([standard])).isVisible),
            ("modes: xhigh lights four bars with one bolt", fastXhigh.level == 4 && fastXhigh.bolts == 1 && fastXhigh.isVisible),
            ("modes: ultrafast has two bolts", capsule(nil, tier: "ultrafast").bolts == 2),
            ("modes: ultracode is the top rung with its detail",
             ultracode.isTopRung && ultracode.level == 5 && ultracode.tooltipLines(l10n).last == l10n("effort.ultracode.detail")),
            ("modes: unknown future tokens stay distinct, without a meter", future.effort == "warp-2" && future.level == nil),
            ("modes: tooltip spells speed through speed.value",
             fastXhigh.tooltipLines(l10n).prefix(2) == [l10n("effort.tooltip", "xhigh"), l10n("speed.tooltip", l10n("speed.value.fast"))]),
            ("modes: unknown values read as unrecorded", capsule(nil, tier: "fast").tooltipLines(l10n).first == l10n("effort.tooltip", l10n("modes.unrecorded"))),
            ("modes: the column fits the widest capsule", !column.compact && column.width == ultracode.width(compact: false)),
            ("modes: a narrow list drops every word", narrow.compact && narrow.width < column.width && !ultracode.showsWord(compact: true)),
            ("modes: no capsule takes no column", AgentCapsuleColumn(rows: [(EffortCapsule(.empty), 0)], rowWidth: 328, intelligence: 24).width == 0),
            ("modes: a configured value keeps its parts' place", configuredHigh.width(compact: true) == capsule("high").width(compact: true)),
            ("modes: the mark follows the word", configuredHigh.showsMark(compact: false) && !configuredHigh.showsMark(compact: true)),
            ("modes: a configured value is announced as configured",
             configuredHigh.accessibilityText(l10n).hasPrefix(l10n("effort.tooltip.configured", "high"))
                 && !configuredHigh.accessibilityText(l10n).contains(l10n("modes.configured.note"))),
        ]
    }

    private static func modes(_ effort: String?, tier: String? = nil) -> ExecutionModes {
        var facts: [ExecutionModeFact] = []
        if let effort, let fact = ExecutionModeFact(kind: .effort, rawValue: effort, evidence: .recorded, provider: nil, scope: .turn) {
            facts.append(fact)
        }
        if let tier, let fact = ExecutionModeFact(kind: .serviceTier, rawValue: tier, evidence: .served, provider: "openai", scope: .response) {
            facts.append(fact)
        }
        return ExecutionModes(facts)
    }

    private static func capsule(_ effort: String?, tier: String? = nil) -> EffortCapsule {
        EffortCapsule(modes(effort, tier: tier))
    }
}
