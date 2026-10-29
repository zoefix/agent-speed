import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 4, 性能 column: the fixed cell, the name room it takes, the row's score words and its score card for every
/// form (DESIGN 12).
@Suite("Agent intelligence column")
@MainActor
struct AgentIntelligenceRowTests {
    /// `score`: the agent's performance score (METRICS 6.9), live while it generates, resting otherwise; `n` timed
    /// requests make it provisional below 10.
    static func agent(_ source: SourceKind = .claudeCode, model: String? = "claude-opus-5-5", score: Double?,
                      output: Double = 0, thinkingShare: Double = 0, n: Int = 20) -> AgentStatus {
        var sums = IntelligenceSums()
        if output > 0 {
            sums.output = output
            if source.recordsReasoningApart {
                sums.thinking = output * thinkingShare
                sums.thinkingBase = output
            }
        }
        var perf: PerfEvaluation?
        if let score {
            let evaluation = PerfPreviewData.evaluation(score: score, source: source, model: model ?? "", n: n, tools: 12,
                                                        baseline: 1100)
            perf = evaluation
            sums.score = PerfSetValue(form: output > 0 ? .live : .resting, score: evaluation.score, agentId: "a",
                                      lastLineMs: nil, provisional: evaluation.provisional)
        }
        return AgentStatus(id: "a", source: source, project: "p", model: model, phase: output > 0 ? .generating : .idle,
                           isSubagent: false, live: Rate(output: output, total: output), recentSpeed: nil,
                           lastActivity: Date(), intelligence: sums, perf: perf)
    }

    // MARK: Cell

    @Test func cellIsAsWideAsTheWiderOfTheWidestCompactNumberAndTheCaption() {
        // `8,888` in SF Pro 12 pt medium with tabular digits is wider than 性能 or Perf at 10.5 pt.
        let widths = [LanguagePreference.zhHant, .zhHans, .en, .ja].map { preference in
            AgentIntelligenceColumn.width(caption: Localizer(preference: preference)("intel.glyph"))
        }
        #expect(Set(widths).count == 1, "\(widths)")
        #expect(widths[0] == 35, "DESIGN 12, *Number formats*: `8,888` in SF Pro 12 pt medium is 34.5")
        // A caption wider than the digits sets the width.
        #expect(AgentIntelligenceColumn.width(caption: "Performance score") > widths[0])
    }

    @Test func theNameBlockGivesWayToTheCellAndItsGap() {
        // Rows 328: badge 16, two 12 pt row spacings, cell 24, 6, live value 48.
        #expect(AgentCapsuleColumn.nameWidth(rowWidth: 328, depth: 0, column: 0, intelligence: 24) == 210)
        #expect(AgentCapsuleColumn.nameWidth(rowWidth: 328, depth: 2, column: 0, intelligence: 24) == 186)
        #expect(AgentCapsuleColumn.nameWidth(rowWidth: 328, depth: 0, column: 65, intelligence: 24) == 137)
    }

    @Test func theWordsDropWhenTheCellLeavesTheNameUnder72() {
        let widest = EffortCapsule(effort: "ultracode", speed: "ultrafast")
        let full = widest.width(compact: false)
        let room = AgentCapsuleColumn.nameWidth(rowWidth: 328, depth: 2, column: full, intelligence: 24)
        let column = AgentCapsuleColumn(rows: [(widest, 2)], rowWidth: 328, intelligence: 24)
        #expect(column.compact == (room < AgentCapsuleColumn.minimumName))
        let shallow = AgentCapsuleColumn(rows: [(EffortCapsule(effort: "xhigh", speed: nil), 0)], rowWidth: 328, intelligence: 24)
        #expect(!shallow.compact)
    }

    // MARK: Accessibility

    @Test func rowsSayTheirScoreWordsAfterTheLiveValue() {
        let l10n = Localizer(preference: .zhHant)
        let live = Self.agent(score: 1286.4, output: 40, thinkingShare: 0.245)
        #expect(PerfSetReading(agent: live).shown == 1286)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: live), l10n) == "性能 1,286")
        let resting = Self.agent(score: 1452.5)
        #expect(PerfSetReading(agent: resting).isResting)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: resting), l10n) == "性能 1,453，閒置中")
        let compact = Self.agent(score: 12_840, output: 600)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: compact), l10n) == "性能 12,840",
                "spoken in full form")
    }

    @Test func provisionalScoresSayFewSamples() {
        let provisional = Self.agent(score: 1048.2, output: 30, n: 6)
        #expect(PerfSetReading(agent: provisional).isProvisional)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: provisional), Localizer(preference: .zhHant))
            == "性能 1,048，初步（樣本少）")
        let resting = Self.agent(score: 934, n: 4)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: resting), Localizer(preference: .en))
            == "performance 934, provisional (few samples), idle")
    }

    @Test func rowsWithoutAScoreSayNothing() {
        let l10n = Localizer(preference: .zhHant)
        let quiet = Self.agent(model: nil, score: nil)
        #expect(PerfSetReading(agent: quiet) == .none)
        #expect(AgentSignalText.intelligenceAccessibility(PerfSetReading(agent: quiet), l10n) == nil)
    }

    // MARK: Score card

    @Test func theCardMapsTheAgentsEvaluation() {
        let live = Self.agent(score: 1286.4, output: 30, n: 20)
        let card = PerfCardModel(agent: live, facts: ["推理強度：high"])
        #expect(card.score.form == .live && card.score.shown == 1286 && !card.score.provisional)
        #expect(card.score.n == 20 && card.score.tools == 12)
        #expect(card.score.vsUsualPercent == 17, "1286.4 against 1100")
        #expect(card.header.model == "claude-opus-5-5")
        #expect(card.facts == ["推理強度：high"])
        #expect(PerfCardText.facts(card, Localizer(preference: .zhHant)).first == "有效計時請求 20 個 · 近 30 分鐘")
        let resting = PerfCardModel(agent: Self.agent(score: 934, n: 6), facts: [])
        #expect(resting.score.form == .resting && resting.score.provisional)
    }

    @Test func anAgentWithoutAScoreGetsTheNoneCard() {
        var quiet = Self.agent(score: nil, output: 50)
        var none = PerfEvaluation.none(source: .claudeCode)
        none.n = 2
        quiet.perf = none
        let card = PerfCardModel(agent: quiet, facts: [])
        #expect(card.score.form == .none && card.score.shown == nil && card.score.n == 2)
        #expect(PerfCardText.summary(card.score, Localizer(preference: .zhHant)) == "近 30 分鐘只有 2 個有效計時請求；至少要 3 個才會評分。")
    }

    @Test func percentRoundsHalfAwayFromZero() {
        #expect(AgentSignalText.percent(0.245) == "25%")
        #expect(AgentSignalText.percent(0.004) == "0%")
        #expect(AgentSignalText.percent(1) == "100%")
    }
}
