import Foundation
import Testing
@testable import AgentSpeedCore

/// The monitor's side of METRICS 11.10 *Counting*: while the report watch is on, a response the report ledger counted
/// counts once when it first becomes measured, and a tool call when its first result is read live, under the resolved id
/// of their model and their agent's source, in the watch's epoch. History (seeded, older than 2 minutes, before the
/// watch) is never counted.
@Suite("Performance score watch in the monitor")
struct ReportPerfWatchSessionTests {
    typealias Lines = PerfSessionTests

    struct Fixture {
        let directory: TempDirectory
        let session: TranscriptSession
        let now: Int64
        let today: Int64

        init(seeded: [String] = []) throws {
            directory = try TempDirectory()
            let now = Clock.milliseconds(Date()) / 1000 * 1000
            self.now = now
            today = SessionTests.startOfToday()
            try directory.write(Lines.relative, seeded.isEmpty ? "" : Lines.text(seeded))
            let configuration = MonitorConfiguration(enabledSources: [.claudeCode],
                                                     claudeRoot: directory.file("claude/projects"))
            session = TranscriptSession(configuration: configuration, clock: { now })
            _ = session.seed(todayStartMs: today)
        }

        func append(_ lines: [String]) throws {
            try directory.append(Lines.relative, Lines.text(lines))
            _ = session.rediscover(todayStartMs: today)
        }

        func completions() -> [ReportPerfCompletion] {
            session.advance(nowMs: now, todayStartMs: today, emitsEvents: true).output.report.perf.completions.entries
        }
    }

    static let opus = PriceBook.bundled.reportModelId(model: "claude-opus-5-5", provider: nil)

    @Test func responsesAndToolResultsCountOnceInTheirEpoch() throws {
        let fixture = try Fixture()
        fixture.session.setReportWatch(3)
        try fixture.append(Lines.call("a", fixture.now - 20_000, tool: "Bash", input: #"{"command":"make"}"#, result: "ok"))
        let entries = fixture.completions()
        #expect(entries.count == 2)
        #expect(entries.allSatisfy { $0.epoch == 3 && $0.modelId == Self.opus && $0.source == .claudeCode })
        guard case .response(let start, let end, let first, let output, _, let input, let cached)? = entries.first?.kind else {
            Issue.record("the response first")
            return
        }
        #expect(start == fixture.now - 22_000 && end == fixture.now - 20_000 && first == fixture.now - 20_000)
        #expect(output == 120 && input == 1900 && cached == 900)
        guard case .call(let call, let result, let failed)? = entries.last?.kind else {
            Issue.record("then the call")
            return
        }
        #expect(call == fixture.now - 20_000 && result == fixture.now - 19_500 && failed == false)
        try fixture.append([Lines.prompt(fixture.now - 10_000, "more")])
        #expect(fixture.completions().count == 2, "counted once")
    }

    @Test func historyIsNeverCounted() throws {
        let fixture = try Fixture(seeded: Lines.call("s", Clock.milliseconds(Date()) / 1000 * 1000 - 30_000, tool: "Bash",
                                                     input: #"{"command":"make"}"#, result: "ok"))
        fixture.session.setReportWatch(1)
        #expect(fixture.completions().isEmpty, "read while seeding")

        // A call answered more than 2 minutes before it was read.
        try fixture.append(Lines.call("old", fixture.now - 200_000, tool: "Read", input: #"{"file_path":"/w/x"}"#,
                                      result: "x"))
        #expect(fixture.completions().isEmpty)
    }

    @Test func nothingCountsWhileTheWatchIsOff() throws {
        let fixture = try Fixture()
        try fixture.append(Lines.call("a", fixture.now - 20_000, tool: "Bash", input: #"{"command":"make"}"#, result: "ok"))
        #expect(fixture.completions().isEmpty)
        fixture.session.setReportWatch(2)
        try fixture.append(Lines.call("b", fixture.now - 8000, tool: "Bash", input: #"{"command":"make"}"#, result: "ok"))
        #expect(fixture.completions().map(\.epoch) == [2, 2])
    }

    @Test func anOpenClawWindowCountsItsCalls() throws {
        typealias Claw = OpenClawSessionTests
        let base = Claw.base
        let gateway = Claw.Gateway()
        try gateway.addWindow("w1", at: base)
        let wal = gateway.canonical(gateway.database("main")) + "-wal"
        let session = TranscriptSession(configuration: gateway.configuration(), clock: { base + 30_000 })
        _ = session.seed(todayStartMs: base)
        session.setReportWatch(5)
        try gateway.append("w1", [Claw.user("u1", base + 20_000),
                                  Claw.assistant("a1", start: base + 20_005, end: base + 21_000, stop: "toolUse",
                                                 calls: ["c1"]),
                                  Claw.toolResult("r1", call: "c1", base + 21_500)], at: base + 21_500)
        #expect(session.handleChanges(atCanonicalPaths: [wal]))
        let entries = session.advance(nowMs: base + 30_000, todayStartMs: base, emitsEvents: true)
            .output.report.perf.completions.entries
        let calls = entries.filter { if case .call = $0.kind { true } else { false } }
        #expect(calls.count == 1)
        #expect(calls.first?.source == .openclaw && calls.first?.epoch == 5)
        #expect(calls.first?.modelId == PriceBook.bundled.reportModelId(model: "claude-opus-5-5", provider: "anthropic"))
    }
}
