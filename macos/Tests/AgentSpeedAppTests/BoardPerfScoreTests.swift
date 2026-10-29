import AgentSpeedCore
import AgentSpeedOrbModel
import CoreGraphics
import Foundation
import Testing
@testable import AgentSpeed

/// The boards' 性能 (DESIGN 2c Live metrics; DESIGN 12): the forms of the hero and the rows, the provisional mark
/// following the supplying agent, the motion of a change (counts, crossfades, Reduce Motion), the dotted arc that never
/// cuts a dot, and the score cards with their facts and accessibility.
@Suite("Board performance score")
@MainActor
struct BoardPerfScoreTests {
    static let l10n = Localizer(preference: .zhHant)

    static func reading(_ shown: Int?, _ form: PerfValueForm = .live, provisional: Bool = false,
                        count: Int = 1) -> PerfSetReading {
        PerfSetReading(form: shown == nil ? .none : form, shown: shown, provisional: provisional, count: count, agentId: "a")
    }

    /// The `team` simulation after 40 ticks with the scores of `scenario`.
    static func board(_ style: BoardStyle, _ scenario: BoardScorePreviewData.Scenario, size: BoardSize = .medium,
                      readouts: BoardReadouts = .all) -> (tick: BoardTick, layout: BoardContentLayout) {
        let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: style, size: size, dark: true, l10n: l10n,
                                metrics: .init(readouts: readouts))
        for _ in 0..<40 { feed.step() }
        let tick = BoardScorePreviewData.tick(feed.tick, scenario: scenario)
        let builder = BoardContentBuilders.make(style: style, size: size, dark: true, l10n: l10n, capsule: nil, highContrast: false)
        return (tick, builder.layout(tick))
    }

    static func item(_ layout: BoardContentLayout, _ key: String) -> BoardContentItem? {
        layout.items.first { $0.key == key }
    }

    // MARK: Forms

    @Test func theHeroShowsTheHighestLiveScoreElseTheHighestResting() {
        let live = Self.board(.glass, .live).tick.metrics.intelligence
        #expect(live.form == .live && live.shown == 1286 && !live.isProvisional && live.count >= 2)
        let resting = Self.board(.glass, .resting).tick.metrics.intelligence
        #expect(resting.form == .resting && resting.shown == 1452 && resting.isResting)
        #expect(Self.board(.glass, .none).tick.metrics.intelligence == .none)
        let provisional = Self.board(.glass, .provisional).tick.metrics.intelligence
        #expect(provisional.shown == 1048 && provisional.isProvisional)
        #expect(Self.board(.glass, .compact).tick.metrics.intelligence.shown == 12_840)
    }

    @Test func rowsShowTheirGroupsHighestAndTheirOwnValue() throws {
        let (tick, _) = Self.board(.neon, .live, size: .large)
        let models = BoardRowModels.make(tick, l10n: Self.l10n, twoLine: true)
        for (row, model) in zip(tick.rows, models) {
            switch row.kind {
            case .group(let index):
                #expect(model.intel == BoardRowScore(tick.groups[index].intelligenceReading))
            case .sub(let index, let sub):
                #expect(model.intel == BoardRowScore(PerfSetReading(agent: tick.groups[index].subs[sub])))
            case .moreSubs, .moreGroups:
                #expect(model.intel == nil, "the more lines leave the cell empty")
            }
        }
        // A score shows live in the live color, resting pale, none as `–`, five digits compact; the mark only under a
        // provisional number.
        let none = BoardRowScore(.none)
        #expect(none.text == "–" && !none.live && !none.provisional)
        let resting = BoardRowScore(Self.reading(1452, .resting, provisional: true))
        #expect(resting.text == "1,452" && !resting.live && resting.provisional)
        #expect(BoardRowScore(Self.reading(12_840)).text == "12.8k")
        // 性能 off: no column.
        let off = Self.board(.glass, .live, size: .medium, readouts: BoardReadouts(intelligence: false, thinking: true, cost: true)).tick
        #expect(BoardRowModels.make(off, l10n: Self.l10n).allSatisfy { $0.intel == nil })
    }

    /// 思考's quiet is Σ w = 0, not the absence of a score: 性能 keeps its own form when work stops.
    @Test func thinkingQuietIsIndependentOfTheScore() {
        let resting = Self.board(.glass, .resting).tick.metrics
        #expect(resting.quiet && resting.thinking == "–" && resting.intelligence.shown == 1452)
        let none = Self.board(.glass, .none).tick.metrics
        #expect(!none.quiet && none.thinking == "174" && none.intelligence == .none)
    }

    // MARK: Provisional mark

    @Test func theProvisionalMarkFollowsTheSupplyingAgent() throws {
        for style in BoardStyle.allCases {
            let key = style == .timing ? "box.intel.n" : "gauge.iq.n"
            #expect(Self.item(Self.board(style, .provisional).layout, key + ".mark") != nil, "\(style) provisional")
            #expect(Self.item(Self.board(style, .live).layout, key + ".mark") == nil, "\(style) live")
            #expect(Self.item(Self.board(style, .none).layout, key + ".mark") == nil, "\(style) none")
        }
        // Glass draws the provisional value arc as dots, the settled one solid, each on the log scale.
        let dotted = try #require(Self.item(Self.board(.glass, .provisional).layout, "gauge.arc")?.sweep)
        let solid = try #require(Self.item(Self.board(.glass, .live).layout, "gauge.arc")?.sweep)
        #expect(dotted.dots != nil && solid.dots == nil)
        #expect(abs(solid.fraction - PerfFormat.scale(1286)) < 1e-9 && abs(dotted.fraction - PerfFormat.scale(1048)) < 1e-9)
    }

    /// The dotted arc's reveal ends after a whole dot that clears the tip ring, never inside a dot.
    @Test func theDottedArcNeverCutsADot() {
        let stroke: CGFloat = 3, radius: CGFloat = 29
        let dots = BoardContentItem.Sweep.Dots(pitch: 2 * stroke, radius: stroke / 2, clearance: 2.31 + 1.5)
        let sweep = BoardContentItem.Sweep(center: .zero, radius: radius, width: stroke + 10, start: 0, sweep: 4 * .pi / 3,
                                           fraction: 0, dots: dots)
        for shown in 0...100 {
            let fraction = Double(shown) / 100
            let end = sweep.revealed(fraction) * radius
            let tip = sweep.sweep * CGFloat(fraction) * radius
            guard end > 0 else { continue }
            // The end lies in the gap after a dot: past its far edge, before the next one starts.
            let phase = end.truncatingRemainder(dividingBy: dots.pitch)
            #expect(phase >= dots.radius - 1e-6 && phase <= dots.pitch - dots.radius + 1e-6, "\(shown)")
            #expect(tip - end >= dots.clearance - 0.5 - 1e-6, "\(shown): the last dot keeps clear of the ring")
        }
        var plain = sweep
        plain.dots = nil
        #expect(plain.revealed(0.5) == sweep.sweep / 2)
    }

    // MARK: Motion

    @Test func theHeroCountsChangesOfOnePercentAndThree() {
        let rise = BoardScoreMotion.hero(from: Self.reading(902), to: Self.reading(1286), reduceMotion: false)
        #expect(rise.count == PerfMotion.countSeconds(from: 902, to: 1286) && rise.numberFade == nil)
        let fall = BoardScoreMotion.hero(from: Self.reading(1286), to: Self.reading(948), reduceMotion: false)
        #expect(fall.count == PerfMotion.countSeconds(from: 1286, to: 948) && fall.numberFade == nil, "falls count too")
        #expect(BoardScoreMotion.hero(from: Self.reading(1286), to: Self.reading(1290), reduceMotion: false) == .none,
                "below 1 % the digits are replaced in place")
        #expect(BoardScoreMotion.hero(from: .none, to: Self.reading(948), reduceMotion: false).numberFade == 0.15)
        let reduced = BoardScoreMotion.hero(from: Self.reading(902), to: Self.reading(1286), reduceMotion: true)
        #expect(reduced.count == nil && reduced.numberFade == 0.15, "Reduce Motion: a 0.15 s crossfade")
        // Live ↔ resting crossfades the color in 0.2 s, kept with Reduce Motion.
        let rest = BoardScoreMotion.hero(from: Self.reading(1452), to: Self.reading(1452, .resting), reduceMotion: true)
        #expect(rest.numberFade == 0.2 && rest.shapeFade == 0.2)
        // The provisional mark fades (0.25 s), at once with Reduce Motion.
        let marked = BoardScoreMotion.hero(from: Self.reading(1048, provisional: true), to: Self.reading(1048),
                                           reduceMotion: false)
        #expect(marked.shapeFade == ProvisionalMark.fadeSeconds && marked.numberFade == nil)
        #expect(BoardScoreMotion.hero(from: Self.reading(1048, provisional: true), to: Self.reading(1048), reduceMotion: true)
            == .none)
    }

    @Test func rowsChangeInPlace() {
        func row(_ shown: Int?, live: Bool = true, provisional: Bool = false) -> BoardRowScore {
            BoardRowScore(shown: shown, live: live, provisional: provisional)
        }
        #expect(BoardScoreMotion.row(from: row(902), to: row(1286), reduceMotion: false) == nil, "no count or roll in a row")
        #expect(BoardScoreMotion.row(from: row(1286), to: row(902), reduceMotion: false) == nil)
        #expect(BoardScoreMotion.row(from: row(nil), to: row(902), reduceMotion: false) == 0.15)
        #expect(BoardScoreMotion.row(from: row(902), to: row(1286), reduceMotion: true) == 0.15)
        #expect(BoardScoreMotion.row(from: row(902), to: row(902, live: false), reduceMotion: true) == 0.2)
        #expect(BoardScoreMotion.row(from: row(902), to: row(902, provisional: true), reduceMotion: false)
            == ProvisionalMark.fadeSeconds)
        #expect(BoardScoreMotion.row(from: row(902), to: row(902, provisional: true), reduceMotion: true) == nil)
    }

    /// A count moves monotonically with easeOutCubic over `clamp(0.3 + 0.35 |log2(new/old)|, 0.3, 0.7)` s; between ticks
    /// each layout shows the integer of its instant; a further change counts on from the number shown.
    @Test func theHeroCountRunsBetweenTicks() {
        var hero = BoardHeroScore()
        let start = 100.0
        #expect(hero.update(Self.reading(902), previous: nil, fresh: false, now: start, reduceMotion: false).shown == 902)
        let first = hero.update(Self.reading(1286), previous: Self.reading(902), fresh: true, now: start, reduceMotion: false)
        #expect(first.shown == 902 && first.motion.count != nil)
        let end = start + PerfMotion.countSeconds(from: 902, to: 1286)
        #expect(hero.climbsUntil(after: start) == end)
        var last = 902
        var seen: Set<Int> = []
        var time = start
        while time < end + 0.05 {
            let shown = hero.update(Self.reading(1286), previous: nil, fresh: false, now: time, reduceMotion: false).shown ?? 0
            #expect(shown >= last, "a rise never counts down")
            last = shown
            seen.insert(shown)
            time += 1.0 / 60
        }
        #expect(last == 1286 && hero.climbsUntil(after: time) == nil)
        #expect(seen.count > 20, "a 60 fps count shows many intermediate integers")
        // A further change while counting counts on from the number shown.
        _ = hero.update(Self.reading(900), previous: Self.reading(1286), fresh: true, now: 200, reduceMotion: false)
        _ = hero.update(Self.reading(1500), previous: Self.reading(900), fresh: true, now: 200, reduceMotion: false)
        let middle = hero.update(Self.reading(1500), previous: nil, fresh: false, now: 200.15, reduceMotion: false).shown ?? 0
        let further = hero.update(Self.reading(2000), previous: Self.reading(1500), fresh: true, now: 200.15,
                                  reduceMotion: false)
        #expect(further.shown == middle && further.motion.count != nil && middle > 900 && middle < 1500)
        // Reduce Motion ends the count and crossfades to the new value.
        let reduced = hero.update(Self.reading(700), previous: Self.reading(2000), fresh: true, now: 200.2, reduceMotion: true)
        #expect(reduced.shown == 700 && reduced.motion.numberFade == 0.15 && hero.climbsUntil(after: 200.2) == nil)
    }

    @Test func aBuilderReportsItsCountUp() {
        let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: .neon, size: .medium, dark: true,
                                l10n: Self.l10n, metrics: .init())
        for _ in 0..<40 { feed.step() }
        let builder = BoardContentBuilders.make(style: .neon, size: .medium, dark: true, l10n: Self.l10n, capsule: nil,
                                                highContrast: false)
        var base = feed.tick
        base.metrics.intelligence = Self.reading(902)
        _ = builder.layout(base)
        var next = base
        next.metrics.intelligence = Self.reading(1286)
        _ = builder.layout(next)
        #expect(builder.climbsUntil(after: Clock.now) != nil)
        builder.reduceMotion = true
        var reduced = next
        reduced.metrics.intelligence = Self.reading(1452)
        _ = builder.layout(reduced)
        #expect(builder.climbsUntil(after: Clock.now) == nil, "Reduce Motion: no count-up")
    }

    // MARK: Cards

    @Test func cardsBelongToTheAgentWhoseScoreTheRowShows() throws {
        let (tick, layout) = Self.board(.glass, .live, size: .large)
        for group in tick.groups {
            let card = BoardScoreCards.group(group, l10n: Self.l10n)
            #expect(card.header.project == group.cardAgent.project && card.header.subAgents == group.subs.count)
            #expect(card.score.shown == group.intelligenceReading.shown || group.intelligenceReading.form == .none)
            #expect(card.score.topCount == 1, "perf.card.top is the hero's eyebrow only")
            #expect(card.facts.last?.hasPrefix("速度 ") == true)
        }
        let hero = try #require(BoardScoreCards.hero(tick, l10n: Self.l10n))
        #expect(hero.score.topCount == tick.metrics.intelligence.count && hero.score.shown == 1286)
        #expect(BoardScoreCards.hero(Self.board(.glass, .none, size: .large).tick, l10n: Self.l10n) == nil, "nothing while none")
        // Every shown row has its card; the more lines have none.
        for row in tick.rows {
            let card = BoardScoreCards.row(row.key, in: tick, l10n: Self.l10n)
            switch row.kind {
            case .group, .sub: #expect(card != nil, "\(row.key)")
            case .moreSubs, .moreGroups: #expect(card == nil)
            }
        }
        #expect(layout.scoreArea != nil)
        #expect(Self.board(.glass, .live, size: .large, readouts: BoardReadouts(intelligence: false, thinking: true, cost: true))
            .layout.scoreArea == nil)
    }

    @Test func aGroupWithoutAScoreShowsItsRootsCard() throws {
        let (tick, _) = Self.board(.timing, .none, size: .medium)
        let group = try #require(tick.groups.first)
        let card = BoardScoreCards.group(group, l10n: Self.l10n)
        #expect(card.header.project == group.root.project && card.score.form == .none && card.score.shown == nil)
    }

    @Test func rowsAnnounceTheirScoreWordsAndCarryTheCard() throws {
        let (tick, _) = Self.board(.glass, .provisional, size: .medium)
        let index = try #require(tick.groups.firstIndex { $0.intelligenceReading.shown == 1048 })
        #expect(tick.groups[index].intelligenceReading.isProvisional)
        let group = tick.groups[index]
        let label = BoardAccessibilityReporter.rowLabel(group, rank: index + 1, l10n: Self.l10n, intelligence: true)
        #expect(label.contains("性能 1,048，初步（樣本少）"))
        let none = try #require(tick.groups.first { $0.intelligenceReading.form == .none })
        #expect(!BoardAccessibilityReporter.rowLabel(none, rank: 4, l10n: Self.l10n, intelligence: true).contains("性能"),
                "nothing for none")
        let content = BoardAccessibilityReporter.cardContent(BoardScoreCards.group(group, l10n: Self.l10n), l10n: Self.l10n)
        #expect(content.first?.value.hasPrefix("性能 1,048，初步（樣本少）") == true && content.first?.importance == .high)
        #expect(content.allSatisfy { $0.label == Self.l10n("a11y.perf.card") } && content.count > 8)
        let words = BoardAccessibilityReporter.metricWords(tick.metrics, currency: "美元", l10n: Self.l10n)
        #expect(words.first?.hasPrefix("性能 1,048，初步（樣本少），") == true)
        let resting = Self.board(.glass, .resting, size: .medium).tick.metrics
        #expect(BoardAccessibilityReporter.metricWords(resting, currency: "美元", l10n: Self.l10n).first?
            .hasPrefix("性能 1,452，閒置中") == true)
        let compact = Self.board(.glass, .compact, size: .medium).tick.metrics
        #expect(BoardAccessibilityReporter.metricWords(compact, currency: "美元", l10n: Self.l10n).first?
            .hasPrefix("性能 12,840") == true, "spoken in full form")
        // While none, `intel.none` replaces only the 性能 words: a shown 思考 is still read.
        let quiet = Self.board(.glass, .none, size: .medium).tick.metrics
        let quietWords = BoardAccessibilityReporter.metricWords(quiet, currency: "美元", l10n: Self.l10n).first
        #expect(quietWords == (quiet.showsThinking ? "性能：尚無分數，思考每秒 \(quiet.thinking) 個 token" : "性能：尚無分數"))
    }
}
