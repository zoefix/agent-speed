import AgentSpeedCore
import CoreGraphics
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 12: the score words, tiers and ratio, the component values, the card's texts and the provisional mark's
/// geometry.
@Suite("Performance score text")
@MainActor
struct PerfScoreTextTests {
    let zh = Localizer(preference: .zhHant)
    let en = Localizer(preference: .en)

    static func score(_ shown: Int?, form: PerfValueForm = .live, provisional: Bool = false, topCount: Int = 1,
                      vsUsual: Int? = nil, measures: [PerfComponentId: Double] = [:]) -> PerfCardScore {
        var input: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        for id in PerfComponentId.allCases { input[id] = (measures[id], 20) }
        let components = PerfFormula.combine(source: .codex, measures: input).components
        return PerfCardScore(source: .codex, form: shown == nil ? .none : form, shown: shown, provisional: provisional,
                             n: provisional ? 6 : 20, tools: 24, topCount: topCount, baseline: vsUsual == nil ? nil : 1000,
                             vsUsualPercent: vsUsual, components: components, signals: .none)
    }

    // MARK: Words

    @Test func scoreWordsWrapInDesignOrderInFullForm() {
        #expect(PerfScoreText.words(shown: 1286, provisional: false, form: .live, count: 4, zh) == "1,286，4 個 agent 中最高")
        #expect(PerfScoreText.words(shown: 1048, provisional: true, form: .live, count: 1, zh) == "1,048，初步（樣本少）")
        #expect(PerfScoreText.words(shown: 1452, provisional: false, form: .resting, count: 2, zh) == "1,452，閒置中，2 個 agent 中最高")
        #expect(PerfScoreText.words(shown: 893, provisional: true, form: .resting, count: 1, en)
            == "893, provisional (few samples), idle")
        #expect(PerfScoreText.words(shown: 12_840, provisional: false, form: .live, count: 1, en) == "12,840")
        #expect(PerfScoreText.words(.none, zh) == nil)
    }

    @Test func tiersAndTheRatioFollowTheShownScore() {
        let tiers = [(699, "perf.tier.slow"), (700, "perf.tier.standard"), (1299, "perf.tier.standard"), (1300, "perf.tier.fast"),
                     (1999, "perf.tier.fast"), (2000, "perf.tier.veryFast"), (128_400, "perf.tier.veryFast")]
        for (shown, key) in tiers { #expect(PerfScoreText.tierKey(shown) == key, "\(shown)") }
        #expect(PerfScoreText.ratio(1286) == "1.29" && PerfScoreText.ratio(860) == "0.86")
        #expect(PerfScoreText.ratio(12_840) == "12.84" && PerfScoreText.ratio(995) == "1.00" && PerfScoreText.ratio(0) == "0.00")
    }

    // MARK: Component values

    @Test func durationsTakeOneRoundingFromMilliseconds() {
        #expect(PerfScoreText.duration(ms: 640.4, zh) == "640 毫秒")
        #expect(PerfScoreText.duration(ms: 999.6, en) == "1.0 s", "the unit follows the rounded value")
        #expect(PerfScoreText.duration(ms: 4240, zh) == "4.2 秒")
        #expect(PerfScoreText.duration(ms: 9960, en) == "10 s")
        #expect(PerfScoreText.duration(ms: 42_400, en) == "42 s")
        #expect(PerfScoreText.duration(ms: 59_600, en) == "1.0 min")
        #expect(PerfScoreText.duration(ms: 754_000, en) == "13 min")
    }

    @Test func ratesAndSharesRoundHalfAwayFromZero() {
        #expect(PerfScoreText.value(.T, 77.25, en) == "77.3 tok/s" || PerfScoreText.value(.T, 77.25, en) == "77 tok/s")
        #expect(PerfScoreText.value(.N, 3.45, zh) == "3.5 次/分")
        #expect(PerfScoreText.perMinute(9.96) == "10" && PerfScoreText.perMinute(12.4) == "12")
        #expect(PerfScoreText.percent(0.245) == "25%" && PerfScoreText.percent(0.996) == "99%" && PerfScoreText.percent(1) == "100%")
        #expect(PerfScoreText.value(.S, 0.97, zh) == "97%")
        #expect(PerfScoreText.signedPercent(8) == "+8%" && PerfScoreText.signedPercent(-12) == "\u{2212}12%")
        #expect(PerfScoreText.signedPercent(0) == "\u{00B1}0%")
    }

    // MARK: Card

    @Test func eyebrowNamesRestingAndTheTopOfASet() {
        #expect(PerfCardText.eyebrow(Self.score(1286), zh) == nil)
        #expect(PerfCardText.eyebrow(Self.score(1452, form: .resting), zh) == "閒置")
        #expect(PerfCardText.eyebrow(Self.score(1286, topCount: 4), en) == "Highest of 4 agents")
        #expect(PerfCardText.eyebrow(Self.score(1452, form: .resting, topCount: 2), zh) == "閒置 · 2 個 agent 中最高")
        #expect(PerfCardText.eyebrow(Self.score(nil, topCount: 4), zh) == nil)
    }

    @Test func theSummaryNamesTheTierTheRatioAndTheSlowestPart() {
        let slow = Self.score(1286, measures: [.T: 77, .L: 7400, .N: 3.5])
        #expect(PerfCardText.summary(slow, zh) == "標準 · 參考水準的 1.29 倍 · 最慢的一環：首段延遲 P50")
        let fast = Self.score(2400, measures: [.T: 120, .L: 2000])
        #expect(PerfCardText.summary(fast, en) == "Very fast · 2.40× the reference · every part above it")
        let single = Self.score(1048, measures: [.T: 52])
        #expect(PerfCardText.summary(single, zh) == "標準 · 參考水準的 1.05 倍")
        #expect(PerfCardText.summary(Self.score(nil), zh) == "近 30 分鐘只有 20 個有效計時請求；至少要 3 個才會評分。")
    }

    @Test func theUsualChipIsMintAboveUsualAndNeutralOtherwise() {
        let above = PerfCardText.usual(Self.score(1286, vsUsual: 17), zh)
        #expect(above.text == "比平常 +17%" && above.kind == .above)
        let below = PerfCardText.usual(Self.score(948, vsUsual: -5), en)
        #expect(below.text == "\u{2212}5% vs usual" && below.kind == .neutral, "a slower day is never amber or red")
        let none = PerfCardText.usual(Self.score(948), zh)
        #expect(none.text == "尚無平常值" && none.kind == .dotted)
        #expect(PerfCardText.usualWords(Self.score(948, vsUsual: 0), en) == en("a11y.perf.usual.same"))
    }

    @Test func provisionalScoresExplainTheDottedLine() {
        #expect(PerfCardText.provisionalHint(Self.score(1048, provisional: true), zh)
            == "只有 6 個有效計時請求，分數還不穩定；滿 10 個後虛線就會消失。")
        #expect(PerfCardText.provisionalHint(Self.score(1048), zh) == nil)
    }

    @Test func absentComponentsSayWhy() {
        let score = Self.score(1286, measures: [.T: 77])
        let rows = PerfCardText.rows(score, PerfComponentId.cardOrder, zh)
        let throughput = rows.first { $0.id == .T }
        #expect(throughput?.present == true && throughput?.value == PerfScoreText.value(.T, 77, zh))
        let latency = rows.first { $0.id == .L }
        #expect(latency?.present == false && latency?.value == "樣本不足" && latency?.subScore == "\u{2013}")
        #expect(latency?.meaning == "資料不到 3 筆，暫不計入分數。")
    }

    @Test func accessibilityFollowsTheCardInOrder() {
        let model = PerfCardModel(header: .init(source: .codex, project: "rocket", model: "gpt-6.1", subAgents: 0),
                                  score: Self.score(1286, vsUsual: 17, measures: [.T: 77, .L: 7400]),
                                  facts: ["推理強度：xhigh"])
        let lines = PerfCardText.accessibilityLines(model, zh)
        #expect(lines.first == "性能 1,286，標準")
        #expect(lines[1] == zh("a11y.perf.usual.up", "17%"))
        #expect(lines[2] == "標準 · 參考水準的 1.29 倍 · 最慢的一環：首段延遲 P50")
        #expect(lines.contains { $0.hasPrefix("輸出吞吐：") })
        #expect(lines.suffix(3) == ["有效計時請求 20 個 · 近 30 分鐘", "推理強度：xhigh", zh("perf.card.footnote")])
    }

    // MARK: Provisional mark

    @Test func underlineDotsFollowTheFormula() {
        // 34 pt at 2×: d = 3.4 → 3.5 (device pixels), n over 40 pt = ⌊36.5 / 8.4⌋ + 1 = 5.
        let diameter = ProvisionalMark.dotDiameter(textSize: 34, scale: 2, increasedContrast: false)
        #expect(diameter == 3.5)
        #expect(ProvisionalMark.dotCount(width: 40, diameter: diameter) == 5)
        let centers = ProvisionalMark.dotCenters(width: 40, diameter: diameter)
        #expect(centers.first == diameter / 2)
        #expect(abs((centers.last ?? 0) - (40 - diameter / 2)) < 1e-9)
        // The 7 pt floor at 1×: one device pixel; never fewer than two dots.
        #expect(ProvisionalMark.dotDiameter(textSize: 7, scale: 1, increasedContrast: false) == 1)
        #expect(ProvisionalMark.dotCount(width: 3, diameter: 1) == 2)
        // Increase Contrast: d + 0.5 pt.
        #expect(ProvisionalMark.dotDiameter(textSize: 12, scale: 2, increasedContrast: true) == 1.5)
        #expect(ProvisionalMark.dottedArcStyle(lineWidth: 3).dash == [0, 6])
    }
}
