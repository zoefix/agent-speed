import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Board live feed")
struct BoardLiveTests {
    static let layout = BoardLayoutMetrics.of(.neon, .medium)

    static func agent(_ id: String, parent: String? = nil, output: Double = 0, active: Double = 100,
                      phase: AgentPhase = .idle, source: SourceKind = .claudeCode) -> AgentStatus {
        BoardGroupingTests.agent(id, parent: parent, output: output, active: active, phase: phase, source: source)
    }

    static func source(_ kind: SourceKind, output: Double, today: Int = 0, enabled: Bool = true) -> SourceStatus {
        SourceStatus(kind: kind, enabled: enabled, rootPath: "/x", available: true, sessionCount: 1,
                     live: Rate(output: output, total: output * 10), today: TokenTotals(requests: 1, output: today),
                     activeAgents: 1)
    }

    /// A running snapshot at `seconds` (since 1970) with the given sources and agents.
    static func snapshot(at seconds: Double, sources: [SourceStatus], agents: [AgentStatus],
                         peak: Rate = .zero, subsetPeaks: [SourceSet: Double] = [:]) -> SpeedSnapshot {
        var snapshot = SpeedSnapshot.empty
        snapshot.timestamp = Date(timeIntervalSince1970: seconds)
        snapshot.state = .running
        snapshot.sources = sources
        snapshot.agents = agents
        snapshot.live = Rate(output: sources.reduce(0) { $0 + $1.live.output }, total: sources.reduce(0) { $0 + $1.live.total })
        snapshot.today = TokenTotals(requests: sources.count, output: sources.reduce(0) { $0 + $1.today.output })
        snapshot.peakToday = peak
        snapshot.subsetPeaks = Dictionary(uniqueKeysWithValues: subsetPeaks.map { (SubsetKey($0.key), $0.value) })
        snapshot.dayStart = Date(timeIntervalSince1970: 0)
        return snapshot
    }

    static let codexOnly = OrbSourceSelection.explicit([.codex])

    @Test func aSubsetBoardCountsOnlyItsSources() {
        var live = BoardLive(sources: Self.codexOnly)
        let snapshot = Self.snapshot(
            at: 1000,
            sources: [Self.source(.claudeCode, output: 50, today: 900), Self.source(.codex, output: 30, today: 400)],
            agents: [
                Self.agent("c1", output: 50, phase: .generating),
                Self.agent("x1", output: 20, phase: .generating, source: .codex),
                Self.agent("x2", parent: "x1", output: 10, phase: .tools, source: .codex),
            ])
        let frame = live.ingest(snapshot, metric: .output, layout: Self.layout)
        #expect(frame.value == 30)
        #expect(frame.groups.map(\.id) == ["x1"])
        #expect(frame.groups[0].live == 30 && frame.groups[0].subs.map(\.id) == ["x2"])
        #expect(frame.generating == 1 && frame.tools == 1)
        #expect(frame.todayOutput == 400)
        #expect(frame.state == .available)
    }

    @Test func anAllSourcesBoardReadsTheSnapshot() {
        var live = BoardLive(sources: .all)
        let snapshot = Self.snapshot(
            at: 1000,
            sources: [Self.source(.claudeCode, output: 50, today: 900), Self.source(.codex, output: 30, today: 400)],
            agents: [Self.agent("c1", output: 50), Self.agent("x1", output: 30, source: .codex)],
            peak: Rate(output: 120, total: 1300))
        let frame = live.ingest(snapshot, metric: .total, layout: Self.layout)
        #expect(frame.value == 800)
        #expect(frame.todayOutput == 1300)
        #expect(frame.peak == 1300)
        #expect(frame.groups.map(\.id) == ["c1", "x1"])
    }

    @Test func groupsPassOnlyAfterTwoTicksOfLeading() {
        var live = BoardLive(sources: .all)
        let sources = [Self.source(.claudeCode, output: 100)]
        func tick(_ t: Double, a: Double, b: Double) -> BoardLiveFrame {
            live.ingest(Self.snapshot(at: t, sources: sources,
                                      agents: [Self.agent("a", output: a), Self.agent("b", output: b)]),
                        metric: .output, layout: Self.layout)
        }
        #expect(tick(1000, a: 50, b: 40).groups.map(\.id) == ["a", "b"])
        let first = tick(1000.5, a: 40, b: 60)
        #expect(first.groups.map(\.id) == ["a", "b"] && first.events.isEmpty)
        let second = tick(1001, a: 40, b: 60)
        #expect(second.groups.map(\.id) == ["b", "a"])
        #expect(second.events == [BoardRankEvent(kind: .newLeader, id: "b", from: 2, to: 1)])
    }

    @Test func aSnapshotSeenBeforeNeitherAdvancesTheRankingNorEmits() {
        var live = BoardLive(sources: .all)
        let sources = [Self.source(.claudeCode, output: 100)]
        let agents = [Self.agent("a", output: 40), Self.agent("b", output: 60)]
        _ = live.ingest(Self.snapshot(at: 1000, sources: sources, agents: [Self.agent("a", output: 50), Self.agent("b", output: 45)]),
                        metric: .output, layout: Self.layout)
        let snapshot = Self.snapshot(at: 1000.5, sources: sources, agents: agents)
        _ = live.ingest(snapshot, metric: .output, layout: Self.layout)
        // A settings change re-reads the same snapshot: still one tick of pressure.
        let again = live.ingest(snapshot, metric: .total, layout: Self.layout)
        #expect(again.groups.map(\.id) == ["a", "b"] && again.events.isEmpty)
        #expect(again.metric == .total)
    }

    @Test func rowsFollowTheLayoutAndTheExpansion() {
        var live = BoardLive(sources: .all)
        let subs = (0..<5).map { Self.agent("s\($0)", parent: "r", output: Double(5 - $0), phase: .generating) }
        let snapshot = Self.snapshot(at: 1000, sources: [Self.source(.claudeCode, output: 100)],
                                     agents: [Self.agent("r", output: 10, phase: .generating)] + subs)
        let collapsed = live.ingest(snapshot, metric: .output, layout: Self.layout)
        #expect(collapsed.rows.map(\.key) == ["r", "s0", "s1", "r#more"])
        let expanded = live.replan(layout: Self.layout, expanded: "r")
        #expect(expanded.rows.map(\.key) == ["r", "s0", "s1", "s2", "s3", "s4"])
        // An expanded group that left the board is ignored.
        #expect(live.replan(layout: Self.layout, expanded: "gone").rows.map(\.key) == collapsed.rows.map(\.key))
    }

    @Test func unavailableSourcesShowNothing() {
        var live = BoardLive(sources: .explicit([.gemini]))
        let snapshot = Self.snapshot(at: 1000, sources: [Self.source(.gemini, output: 30, today: 5, enabled: false)],
                                     agents: [Self.agent("g", output: 30, source: .gemini)])
        let frame = live.ingest(snapshot, metric: .output, layout: Self.layout)
        #expect(frame.value == 0 && frame.todayOutput == 0 && frame.groups.isEmpty)
        #expect(frame.state == .allDisabled && frame.unavailable == [.gemini])
    }

    @Test func theHistoryKeepsOneValuePerSecond() {
        var live = BoardLive(sources: .all)
        for step in 0..<130 {
            let value = Double(step)
            _ = live.ingest(Self.snapshot(at: 1000 + Double(step) * 0.5, sources: [Self.source(.claudeCode, output: value)],
                                          agents: []), metric: .output, layout: Self.layout)
        }
        let history = live.frame.history
        #expect(history.count == 60)
        #expect(history.last == 129)
        #expect(history.first == 11)
    }

    @Test func theHistoryRepeatsAcrossAGap() {
        var history = BoardHistory()
        history.record(5, atMs: 10_000)
        history.record(8, atMs: 13_200)
        #expect(history.values == [5, 8, 8, 8])
        history.record(1, atMs: 9_000)
        #expect(history.values == [1])
    }

    @Test func theNumberPunchesOnASharpRise() {
        var live = BoardLive(sources: .all)
        func tick(_ t: Double, _ value: Double) -> BoardLiveFrame {
            live.ingest(Self.snapshot(at: t, sources: [Self.source(.claudeCode, output: value)], agents: []),
                        metric: .output, layout: Self.layout)
        }
        #expect(tick(1000, 100).punch == nil)
        #expect(tick(1000.5, 120).punch == nil)
        let punch = tick(1001, 160).punch
        #expect(punch != nil && abs(punch! - 40.0 / 120) < 1e-9)
    }

    @Test func anAllSourcesBoardCelebratesTheEnginesPeakAfterItsQuiet() {
        var live = BoardLive(sources: .all)
        let sources = [Self.source(.claudeCode, output: 300)]
        let created = live.ingest(Self.snapshot(at: 1000, sources: sources, agents: []), metric: .output,
                                  layout: Self.layout, engineNewPeak: true)
        #expect(!created.newPeak)
        let later = live.ingest(Self.snapshot(at: 1061, sources: sources, agents: []), metric: .output,
                                layout: Self.layout, engineNewPeak: true)
        #expect(later.newPeak)
        let quiet = live.ingest(Self.snapshot(at: 1061.5, sources: sources, agents: []), metric: .output,
                                layout: Self.layout)
        #expect(!quiet.newPeak)
    }

    @Test func aSubsetBoardRunsItsOwnPeak() {
        var live = BoardLive(sources: Self.codexOnly)
        let set = SourceSet([.codex])
        func tick(_ t: Double, _ value: Double, measured: Double = 50) -> BoardLiveFrame {
            live.ingest(Self.snapshot(at: t, sources: [Self.source(.codex, output: value)], agents: [],
                                      subsetPeaks: [set: measured]),
                        metric: .output, layout: Self.layout, engineNewPeak: true)
        }
        #expect(!tick(1000, 40).newPeak)
        #expect(!tick(1030, 200).newPeak)
        #expect(tick(1030, 200).peak == 200)
        // After the 60 s quiet, a value above the peak fires once, then the cooldown holds.
        #expect(tick(1061, 260).newPeak)
        #expect(!tick(1062, 400).newPeak)
        #expect(live.frame.peak == 400)
    }

    @Test func changingTheSourcesStartsOver() {
        var live = BoardLive(sources: .all)
        let sources = [Self.source(.claudeCode, output: 30), Self.source(.codex, output: 20)]
        _ = live.ingest(Self.snapshot(at: 1000, sources: sources, agents: [Self.agent("a", output: 30)]),
                        metric: .output, layout: Self.layout)
        live.setSources(Self.codexOnly)
        let frame = live.ingest(Self.snapshot(at: 1000.5, sources: sources,
                                              agents: [Self.agent("a", output: 30), Self.agent("x", output: 20, source: .codex)]),
                                metric: .output, layout: Self.layout)
        #expect(frame.groups.map(\.id) == ["x"])
        #expect(frame.history == [20])
        #expect(frame.value == 20)
    }

    @Test func thePreviewShowsEverySource() {
        var live = BoardLive(sources: Self.codexOnly)
        let sources = [Self.source(.claudeCode, output: 30), Self.source(.codex, output: 20)]
        let frame = live.ingest(Self.snapshot(at: 1000, sources: sources, agents: []), metric: .output,
                                layout: Self.layout, previewing: true)
        #expect(frame.value == 50)
    }
}

@Suite("Board expansion and announcements")
struct BoardExpansionTests {
    @Test func aClickTogglesAndSixtySecondsWithoutInteractionCollapse() {
        var expansion = BoardExpansion()
        expansion.toggle("a", now: 0)
        #expect(expansion.group == "a" && expansion.deadline == 60)
        expansion.touch(now: 30)
        let early = expansion.expire(now: 80)
        #expect(!early)
        let late = expansion.expire(now: 90)
        #expect(late && expansion.group == nil)
        expansion.toggle("a", now: 100)
        expansion.toggle("b", now: 101)
        #expect(expansion.group == "b")
        expansion.toggle("b", now: 102)
        #expect(expansion.group == nil)
    }

    @Test func anExpandedGroupThatLeftCollapses() {
        var expansion = BoardExpansion()
        expansion.toggle("a", now: 0)
        let expired = expansion.expire(now: 1, present: ["b"])
        #expect(expired && expansion.group == nil)
    }

    @Test func newLeadersAreAnnouncedAtMostEveryTenSeconds() {
        var announcer = BoardAnnouncer()
        var results: [Bool] = []
        for now in [0, 9.9, 10] { results.append(announcer.mayAnnounce(now: now)) }
        #expect(results == [true, false, true])
    }
}
