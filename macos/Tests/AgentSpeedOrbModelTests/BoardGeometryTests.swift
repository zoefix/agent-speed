import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 2c Live metrics: the hero and ranking geometry per style and size, the rows kept, the sub-gauge tables.
@Suite("Board live metrics geometry")
struct BoardGeometryTests {
    static let sizes: [BoardSize] = [.small, .medium, .large]

    @Test func withEverySwitchOffTheBoardIsAsBeforeButForTheSecondLine() {
        for style in BoardStyle.allCases {
            for size in Self.sizes {
                let legacy = BoardGeometry(style, size, readouts: .none)
                #expect(legacy.isLegacy && !legacy.hasStrip && legacy.captionsHeight == 0)
                // Group rows keep their second line at M and L (no 2 pt gap); S keeps one-line rows.
                #expect(legacy.twoLine == (size != .small) && BoardGeometry(style, size, readouts: .all).twoLine == legacy.twoLine)
                let oneLine = (size == .large ? 32.0 : 30) + (style == .timing ? 0 : 2)
                #expect(legacy.groupRow + legacy.groupGap == (size == .small ? oneLine : size == .large ? 40 : 38), "\(style) \(size)")
            }
        }
        #expect(BoardGeometry(.neon, .medium).bandHeight == 92)
        #expect(BoardGeometry(.neon, .medium, readouts: .all).bandHeight == 100)
    }

    @Test func theRankingFollowsTheHeroTable() {
        let expected: [BoardStyle: [Double]] = [.glass: [138, 186, 254], .timing: [139, 187, 257], .neon: [136, 186, 256]]
        let bands: [BoardStyle: [(Double, Double)]] = [.glass: [(12, 78), (14, 86), (16, 94)], .timing: [(14, 72), (16, 80), (18, 88)],
                                                       .neon: [(12, 92), (14, 100), (16, 108)]]
        for style in BoardStyle.allCases {
            for (index, size) in Self.sizes.enumerated() {
                let geometry = BoardGeometry(style, size, readouts: .all)
                #expect(geometry.rankingHeight == expected[style]![index], "\(style) \(size) ranking height")
                #expect(geometry.bandTop == bands[style]![index].0 && geometry.bandHeight == bands[style]![index].1, "\(style) \(size) band")
                #expect(geometry.firstRow == geometry.captionsHeight + 2)
            }
        }
        let glass = BoardGeometry(.glass, .medium, readouts: .all)
        #expect(glass.stripTop == 100 && glass.stripHeight == 16 && glass.rankingTop == 116)
        let timing = BoardGeometry(.timing, .medium, readouts: .all)
        #expect(timing.stripTop == 104 && timing.stripHeight == 12 && timing.rankingTop == 121)
        let neon = BoardGeometry(.neon, .large, readouts: .all)
        #expect(!neon.hasStrip && neon.rankingTop == 128 && neon.gaugeArea == 91)
        #expect(BoardGeometry(.timing, .small, readouts: .all).gaugeWidth == 110)
        #expect(BoardGeometry(.glass, .large, readouts: .all).gaugeWidth == 128)
    }

    @Test func rowColumnsFollowTheTable() {
        let glass = BoardGeometry(.glass, .medium, readouts: .all)
        // DESIGN 12, *Number formats*: `8,888` at 13 pt on the two-line group rows of M and L, at 11 pt at S.
        #expect(glass.intelligenceColumn == 39 && glass.valueColumn == 44 && glass.groupRow == 38 && glass.groupGap == 0)
        #expect(BoardGeometry(.glass, .small, readouts: .all).intelligenceColumn == 33)
        #expect(BoardGeometry(.neon, .small, readouts: .all).intelligenceColumn == 34)
        #expect(glass.secondLineBaseline == 32 && BoardGeometry(.neon, .medium, readouts: .all).secondLineBaseline == 31)
        let timing = BoardGeometry(.timing, .large, readouts: .all)
        #expect(timing.intelligenceColumn == 40 && timing.valueColumn == 42 && timing.intervalColumn == 34 && timing.groupRow == 40)
        let small = BoardGeometry(.timing, .small, readouts: .all)
        #expect(small.valueColumn == 40 && small.intervalColumn == 0 && !small.twoLine && small.groupRow == 30)
        let noIntelligence = BoardGeometry(.glass, .medium, readouts: BoardReadouts(intelligence: false, thinking: true, cost: true))
        #expect(noIntelligence.valueColumn == 46 && noIntelligence.intelligenceColumn == 0 && noIntelligence.firstRow == 6)
        #expect(BoardGeometry(.timing, .medium, readouts: BoardReadouts(intelligence: false, thinking: true, cost: true))
            .intervalColumn == 38)
    }

    /// The `team` scenario's shape: five groups, the first three with 3, 2 and 4 working sub-agents.
    static let team: [BoardPlanGroup] = [("a0", 3), ("a1", 2), ("a2", 4), ("a3", 0), ("a4", 0)].map { id, subs in
        BoardPlanGroup(id: id, live: 150, subs: (0..<subs).map { BoardPlanSub(id: "\(id)s\($0)", live: Double(100 - $0)) })
    }

    static func kept(_ style: BoardStyle, _ size: BoardSize) -> (groups: Int, subs: Int, more: Bool) {
        let rows = BoardLayoutPlanner.plan(team, metrics: BoardLayoutMetrics(BoardGeometry(style, size, readouts: .all)))
        var groups = 0, subs = 0, more = false
        for row in rows {
            switch row.kind {
            case .group: groups += 1
            case .sub: subs += 1
            case .moreGroups: more = true
            case .moreSubs: break
            }
        }
        return (groups, subs, more)
    }

    @Test func rowsKeptMatchTheTable() {
        for style in BoardStyle.allCases {
            let small = Self.kept(style, .small)
            #expect(small.groups == 3 && small.subs == 0 && small.more, "\(style) S")
            let medium = Self.kept(style, .medium)
            #expect(medium.groups == 4 && medium.subs == 0 && medium.more, "\(style) M")
            let large = Self.kept(style, .large)
            #expect(large.groups == 5 && large.subs == (style == .timing ? 1 : 0), "\(style) L")
        }
    }

    @Test func subGaugeStacksMatchTheTables() {
        // (height, hidden height, 思考 baseline, 費用 baseline) per size.
        let tables: [BoardStyle: [(Double, Double, Double, Double)]] = [
            .glass: [(75, 58, 51, 68), (84, 67, 62, 79), (90, 73, 69, 86)],
            .neon: [(74.5, 57.5, 50.5, 67.5), (83, 65, 59, 77), (87.5, 69.5, 64.5, 82.5)],
            .timing: [(51, 36, 32, 47), (58, 41, 36.5, 53.5), (65, 46, 41, 60)],
        ]
        for style in BoardStyle.allCases {
            for (index, size) in Self.sizes.enumerated() {
                let metrics = BoardGaugeMetrics(style, size)
                let (height, hidden, thinking, cost) = tables[style]![index]
                let all = metrics.stack(intelligence: true, thinking: true, cost: true)
                #expect(all.height == height, "\(style) \(size) height")
                #expect(all.row(.thinking)?.baseline == thinking && all.row(.cost)?.baseline == cost, "\(style) \(size) baselines")
                let folded = metrics.stack(intelligence: true, thinking: false, cost: true)
                #expect(folded.height == hidden, "\(style) \(size) hidden height")
                // 費用 takes 思考's baseline; the dial moves down and 費用 up by half the difference once re-centred.
                #expect(folded.row(.cost)?.baseline == thinking, "\(style) \(size) 費用 on 思考's baseline")
                // Folding: without 性能 the table (or the box's small rows) alone; with 性能 alone its part only.
                let table = metrics.stack(intelligence: false, thinking: true, cost: true)
                #expect(table.main == nil && table.rows.first?.top == 0)
                #expect(metrics.stack(intelligence: true, thinking: false, cost: false).height == (style == .timing ? metrics.mainRow : metrics.mainHeight))
            }
        }
        #expect(BoardGaugeMetrics(.timing, .medium).stack(intelligence: true, thinking: true, cost: true).mainBaseline == 17.5)
        #expect(BoardGaugeMetrics(.glass, .medium).mainNumberBaseline == 42.5 && BoardGaugeMetrics(.neon, .large).meterTop == 45.5)
        #expect(BoardGaugeMetrics(.timing, .medium).stack(intelligence: false, thinking: true, cost: true).rows.map(\.baseline) == [12, 29.5])
    }

    /// DESIGN 12, *Log scale*: the arc lights `f = clamp(log2(v / 250) / 4, 0, 1)` of its sweep, the meter `round(f × 12)`
    /// segments; 1000 is half, 4000 and above full.
    @Test func theArcAndTheMeterFollowTheLogScale() {
        #expect(BoardIntelligenceScale.arc(1000) == 0.5 && BoardIntelligenceScale.arc(2000) == 0.75)
        #expect(BoardIntelligenceScale.arc(4000) == 1 && BoardIntelligenceScale.arc(16_000) == 1)
        #expect(BoardIntelligenceScale.arc(nil) == 0 && BoardIntelligenceScale.arc(0) == 0 && BoardIntelligenceScale.arc(250) == 0)
        #expect(BoardIntelligenceScale.segments(1000) == 6 && BoardIntelligenceScale.segments(2000) == 9)
        #expect(BoardIntelligenceScale.segments(4000) == 12 && BoardIntelligenceScale.segments(99_999) == 12)
        #expect(BoardIntelligenceScale.segments(nil) == 0 && BoardIntelligenceScale.segments(0) == 0)
        // f × 12 for 270 is 0.33, for 300 0.79: dark, then the first segment.
        #expect(BoardIntelligenceScale.segments(270) == 0 && BoardIntelligenceScale.segments(300) == 1)
    }

    @Test func theStripSparklineKeepsItsMargins() {
        let points = BoardHistoryShape.strip([0, 50, 100], width: 300, height: 16)
        #expect(points.first == SIMD2(0, 14.5))
        #expect(abs(points[2].y - (16 - 1.5 - 100 / 112 * 13)) < 1e-9)
        #expect(BoardHistoryShape.stripBars([0, 10], count: 4, height: 12) == [1.5, 1.5, 1.5, 12])
    }
}

/// METRICS 6.9 *Live thinking* (group sums) and *Sets and groups* (the highest score) as the board's grouping fills them.
@Suite("Board group sums")
struct BoardGroupSumsTests {
    static func agent(_ id: String, parent: String? = nil, source: SourceKind = .claudeCode, output: Double,
                      thinking: Double? = nil, score: Double? = nil) -> AgentStatus {
        var sums = IntelligenceSums()
        sums.output = output
        // A source that does not record reasoning apart adds its output but neither reasoning nor base.
        sums.thinking = source == .deepseek ? 0 : thinking ?? output * 0.25
        sums.thinkingBase = source == .deepseek ? 0 : output
        return AgentStatus(id: id, source: source, project: id, model: "m", phase: output > 0 ? .generating : .idle,
                           isSubagent: parent != nil, live: Rate(output: output, total: output), recentSpeed: nil,
                           lastActivity: Date(), parentId: parent,
                           intelligence: Self.scored(sums, id: id, score: score, live: output > 0))
    }

    static func scored(_ sums: IntelligenceSums, id: String, score: Double?, live: Bool,
                       provisional: Bool = false) -> IntelligenceSums {
        guard let score else { return sums }
        var sums = sums
        sums.score = PerfSetValue(form: live ? .live : .resting, score: score, agentId: id, lastLineMs: 0,
                                  provisional: provisional)
        return sums
    }

    /// `agent` with a provisional score (fewer than 10 timed requests): the mark follows the supplying agent.
    static func provisional(_ agent: AgentStatus) -> AgentStatus {
        var agent = agent
        agent.intelligence.score.provisional = true
        return agent
    }

    /// DESIGN 12: a group row shows its highest member score, its provisional mark follows the member that supplies it,
    /// N counts the members scored in that form, and its card is the supplier's (a group without a score: its root's).
    @Test func theGroupsReadingFollowsItsSupplier() throws {
        let agents = [Self.agent("r", output: 10, score: 1123.4),
                      Self.provisional(Self.agent("s", parent: "r", output: 4, score: 1286.4)),
                      Self.agent("t", parent: "r", output: 0, score: 1452)]
        let group = try #require(BoardGrouping.groups(agents, metric: .output).first)
        let reading = group.intelligenceReading
        #expect(reading.form == .live && reading.shown == 1286 && reading.isProvisional)
        #expect(reading.count == 2, "two members are live; the resting 1452 does not count")
        #expect(group.cardAgent.id == "s")
        let none = try #require(BoardGrouping.groups([Self.agent("r", output: 3), Self.agent("s", parent: "r", output: 1)],
                                                     metric: .output).first)
        #expect(none.intelligenceReading == .none && none.cardAgent.id == "r")
        // A sub row's own value: resting, no mark.
        let own = PerfSetReading(agent: agents[2])
        #expect(own.form == .resting && own.shown == 1452 && !own.isProvisional && own.count == 1)
    }

    @Test func membersAddTheirSumsNeverAnAverageOfTheirShares() throws {
        let agents = [Self.agent("r", output: 30, thinking: 3), Self.agent("s", parent: "r", output: 10, thinking: 7)]
        let group = try #require(BoardGrouping.groups(agents, metric: .output).first)
        #expect(group.sums.intelligence.output == 40 && group.sums.intelligence.thinking == 10)
        // (3 + 7) / (30 + 10), not the mean of 0.1 and 0.7.
        #expect(group.sums.intelligence.thinkingShare == 0.25)
    }

    @Test func theGroupShowsItsHighestLiveScoreElseItsHighestResting() throws {
        let mixed = [Self.agent("r", output: 0, score: 1452.5), Self.agent("s", parent: "r", output: 12, score: 947.25)]
        #expect(try #require(BoardGrouping.groups(mixed, metric: .output).first).intelligence == .live(947.25))
        let resting = [Self.agent("r", output: 0, score: 1000), Self.agent("s", parent: "r", output: 0, score: 1286.5)]
        #expect(try #require(BoardGrouping.groups(resting, metric: .output).first).intelligence == .resting(1286.5))
        let none = [Self.agent("r", output: 9), Self.agent("s", parent: "r", output: 3)]
        #expect(try #require(BoardGrouping.groups(none, metric: .output).first).intelligence == .none)
    }

    @Test func outputWithoutSeparateReasoningLeavesTheShareAlone() throws {
        let agents = [Self.agent("r", output: 20, thinking: 5), Self.agent("d", parent: "r", source: .deepseek, output: 60)]
        let group = try #require(BoardGrouping.groups(agents, metric: .output).first)
        #expect(group.sums.intelligence.output == 80 && group.sums.intelligence.thinkingBase == 20)
        #expect(group.sums.intelligence.thinkingShare == 0.25)
    }

    /// The groups of the `thinking` `groupWindows` cases of `spec/fixtures/perf-score-cases.json`: a root and all its
    /// descendants.
    @Test func theFixtureGroupsAreRootsWithTheirDescendants() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("spec/fixtures/perf-score-cases.json")
        let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let thinking = try #require(root["thinking"] as? [String: Any])
        var checked = 0
        for spec in try #require(thinking["groupWindows"] as? [[String: Any]]) {
            let agents = try #require(spec["agents"] as? [[String: Any]]).map { item in
                AgentStatus(id: item["id"] as? String ?? "", source: SourceKind(rawValue: item["source"] as? String ?? "") ?? .codex,
                            project: "p", model: item["model"] as? String, phase: .idle, isSubagent: item["parentId"] is String,
                            live: .zero, recentSpeed: nil, lastActivity: Date(), parentId: item["parentId"] as? String)
            }
            let groups = BoardGrouping.groups(agents, metric: .output)
            for query in try #require(spec["queries"] as? [[String: Any]]) {
                let expected = try #require(query["groups"] as? [String: [String: Any]])
                #expect(Set(groups.map(\.id)) == Set(expected.keys))
                for (id, group) in expected {
                    let found = try #require(groups.first { $0.id == id })
                    #expect(Set([found.root.id] + found.subs.map(\.id)) == Set(group["members"] as? [String] ?? []), "\(id)")
                    checked += 1
                }
            }
        }
        #expect(checked > 0)
    }
}
