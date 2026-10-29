import Foundation
import Testing
@testable import AgentSpeedCore

/// The performance score in the live monitor (METRICS 6.9, 6.10): seeded lines and live lines feed the same sample and
/// stored actions, so the score is the same before and after a relaunch; the work signals ignore seeded actions; the
/// agents list, sources and all sources carry the values the readouts show.
@Suite("Performance score in the monitor")
struct PerfSessionTests {
    static let relative = "claude/projects/-w-atlas/s-atlas.jsonl"

    static func line(_ ms: Int64, _ type: String, _ body: String) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let time = formatter.string(from: Date(timeIntervalSince1970: TimeInterval(ms) / 1000))
        return #"{"type":"\#(type)","entrypoint":"cli","cwd":"/w/atlas","timestamp":"\#(time)",\#(body)}"#
    }

    static func prompt(_ ms: Int64, _ text: String) -> String {
        line(ms, "user", #""message":{"role":"user","content":"\#(text)"}"#)
    }

    /// One timed response (`api-request` 2 s before it) that calls a tool, and the tool's result.
    static func call(_ id: String, _ ms: Int64, tool: String, input: String, result: String, error: Bool = false) -> [String] {
        [
            line(ms - 2000, "api-request", #""requestId":"q-\#(id)""#),
            line(ms, "assistant", #""message":{"id":"m-\#(id)","model":"claude-opus-5-5","stop_reason":"tool_use","content":[{"type":"tool_use","id":"\#(id)","name":"\#(tool)","input":\#(input)}],"usage":{"input_tokens":1000,"cache_read_input_tokens":900,"output_tokens":120}}"#),
            line(ms + 500, "user", #""message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"\#(id)","is_error":\#(error),"content":"\#(result)"}]}"#),
        ]
    }

    static func text(_ lines: [String]) -> String { lines.joined(separator: "\n") + "\n" }

    struct Fixture {
        let directory: TempDirectory
        let session: TranscriptSession
        let now: Int64
        let today: Int64

        init(seeded: [String]) throws {
            directory = try TempDirectory()
            now = Clock.milliseconds(Date()) / 1000 * 1000
            today = SessionTests.startOfToday()
            try directory.write(PerfSessionTests.relative, PerfSessionTests.text(seeded))
            let configuration = MonitorConfiguration(enabledSources: [.claudeCode, .codex],
                                                     codexRoot: directory.file("codex"),
                                                     claudeRoot: directory.file("claude/projects"))
            session = TranscriptSession(configuration: configuration)
            _ = session.seed(todayStartMs: today)
        }

        func append(_ lines: [String]) throws {
            try directory.append(PerfSessionTests.relative, PerfSessionTests.text(lines))
            _ = session.rediscover(todayStartMs: today)
        }

        func output(at nowMs: Int64? = nil) -> EngineOutput {
            session.advance(nowMs: nowMs ?? now, todayStartMs: today, emitsEvents: false).output
        }
    }

    static func calls(_ count: Int, from start: Int64, every step: Int64 = 10_000, prefix: String = "c",
                      failing: Bool = false) -> [String] {
        (0..<count).flatMap { index in
            call("\(prefix)\(index)", start + Int64(index) * step, tool: "Bash", input: #"{"command":"make"}"#, result: "ok",
                 error: failing)
        }
    }

    @Test func seededLinesScoreAndLiveLinesMoveTheScore() throws {
        let fixture = try Fixture(seeded: [Self.prompt(Clock.milliseconds(Date()) / 1000 * 1000 - 120_000, "build it")]
            + Self.calls(4, from: Clock.milliseconds(Date()) / 1000 * 1000 - 100_000))
        let seeded = fixture.output()
        let agent = try #require(seeded.agents.first)
        let perf = try #require(agent.perf)
        #expect(perf.n == 4 && perf.tools == 4 && perf.provisional)
        #expect(perf.component(.L)?.isPresent == true, "Claude records first content")
        #expect(perf.component(.N)?.isPresent == true && perf.component(.U)?.isPresent == true)
        #expect(agent.agent.actions.allSatisfy { $0.seeded }, "seeded actions count in the score")
        #expect(agent.signals == .none, "and never in the work signals")
        #expect(agent.intelligence.score.form == .live && agent.intelligence.score.agentId == agent.agent.id)
        #expect(agent.intelligence.score.provisional)
        #expect(seeded.intelligence.score == agent.intelligence.score, "the highest score of all sources")
        #expect(seeded.sourceIntelligence[.claudeCode]?.score == agent.intelligence.score)
        #expect(seeded.sourceIntelligence[.codex]?.score.form != .live)

        try fixture.append(Self.calls(8, from: fixture.now - 60_000, every: 5000, prefix: "live", failing: true))
        let live = try #require(fixture.output().agents.first)
        let livePerf = try #require(live.perf)
        #expect(livePerf.n == 12 && !livePerf.provisional)
        #expect(live.agent.actions.filter { !$0.seeded }.count == 8)
        #expect(try #require(livePerf.score) < (try #require(perf.score)), "failing tools lower it")
        #expect(abs((livePerf.component(.S)?.measure ?? 0) - 1.0 / 3) < 1e-12, "8 of 12 calls failed")

        // An idle agent rests on its score while its timed requests stay in the window.
        let resting = try #require(fixture.output(at: fixture.now + 400_000).agents.first)
        #expect(resting.phase == .idle && resting.intelligence.score.form == .resting)
    }

    @Test func aScoreNeedsThreeTimedRequests() throws {
        let fixture = try Fixture(seeded: Self.calls(2, from: Clock.milliseconds(Date()) / 1000 * 1000 - 30_000))
        let agent = try #require(fixture.output().agents.first)
        #expect(agent.perf?.n == 2 && agent.perf?.score == nil)
        #expect(agent.intelligence.score.form == .none)
        #expect(agent.perf?.components.allSatisfy { !$0.isPresent } == true)
    }

    /// The baseline comes from the stored history of the agent's source and model, 14 days before today.
    @Test func theBaselineComesFromTheStoredHistory() throws {
        let fixture = try Fixture(seeded: Self.calls(5, from: Clock.milliseconds(Date()) / 1000 * 1000 - 100_000))
        let today = DayLabeler(timeZone: .current).label(for: Date(timeIntervalSince1970: TimeInterval(fixture.today) / 1000))
        var record = PerfRecord()
        (record.n, record.outputSum, record.durationMsSum) = (40, 4000, 80_000)
        let yesterday = try #require(PerfDay.adding(-1, to: today))
        fixture.session.setPerfHistory([PerfDayRecord(
            key: PerfRecordKey(day: yesterday, source: .claudeCode, model: "claude-opus-5-5"), record: record)])
        let perf = try #require(fixture.output().agents.first?.perf)
        #expect(perf.baseline != nil && perf.vsUsualPercent != nil)
        fixture.session.setPerfHistory([PerfDayRecord(
            key: PerfRecordKey(day: today, source: .claudeCode, model: "claude-opus-5-5"), record: record)])
        #expect(fixture.output().agents.first?.perf?.baseline == nil, "today never counts")
    }
}
