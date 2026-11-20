import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("OpenBitFun usage and timing")
struct OpenBitFunTests {
    @Test func sharedTracesUseAttemptTimingAndDeduplicateCopies() throws {
        let expected = try Fixtures.json("openbitfun-expected.json")
        var deduper = Deduper()
        var ledger = DayLedger(dayStartMs: try #require(expected.int64("todayStartMs")))
        for (index, wanted) in try #require(expected["files"] as? [[String: Any]]).enumerated() {
            let path = Fixtures.root.appendingPathComponent(try #require(wanted.string("path"))).path
            var reader = OpenBitFunReader(path: path, start: .beginning)
            var parser = OpenBitFunParser(agentId: path, fileID: FileID(index))
            var records: [ResponseRecord] = []
            let (batch, result) = reader.read()
            parser.consume(batch, deduper: &deduper) { upsert in
                #expect(upsert.isCompletion)
                records.append(upsert.record)
                ledger.replace(nil, with: upsert.record)
            }
            #expect(!result.failed)
            try HarnessFixtureTests.compare(records, try #require(wanted["responses"] as? [[String: Any]]))
            #expect(parser.state.project == wanted.string("project"))
            #expect(parser.state.isSubagent == wanted["isSubagent"] as? Bool)
            #expect(parser.state.thread?.parentId == wanted.string("parent"))
            #expect(parser.state.phase.rawValue == wanted.string("phase"))
            #expect(reader.read().1.bytesRead == 0)
        }
        let totals = try #require(expected["totals"] as? [String: Any])
        #expect(ledger.totals.requests == totals.int("requests"))
        #expect(ledger.totals.input == totals.int("input") && ledger.totals.output == totals.int("output"))
        #expect(ledger.average == totals.double("average"))
    }

    @Test(arguments: ["completed", "cancelled", "error"], [true, false])
    func ledgerRoundsUseDistinctAttemptsAndStopAtTurnEnd(status: String, useLedger: Bool) throws {
        let expected = try Fixtures.json("openbitfun-expected.json")
        let fixture = try #require(expected["ledger"] as? [String: Any])
        let source = Fixtures.root.appendingPathComponent(try #require(fixture.string("metadata"))).deletingLastPathComponent()
        let directory = try TempDirectory()
        let daily = Fixtures.root.appendingPathComponent(try #require(fixture.string("root")))
        let ledger = OpenBitFunUsageLedger(root: useLedger ? daily : directory.file("no-ledger"))
        try FileManager.default.copyItem(at: source, to: directory.file("session"))
        let path = directory.file("session/metadata.json").path
        var reader = OpenBitFunReader(path: path, start: .beginning, ledger: ledger)
        var parser = OpenBitFunParser(agentId: path, fileID: 0)
        var deduper = Deduper()
        var records: [ResponseRecord] = []
        parser.consume(reader.read().0, deduper: &deduper) { records.append($0.record) }
        if useLedger {
            try HarnessFixtureTests.compare(records, try #require(fixture["responses"] as? [[String: Any]]))
        } else {
            #expect(records.count == 1 && records.first?.usage.output == 25)
        }
        #expect(parser.state.phase == .generating && parser.state.inFlightStartMs == 1_792_497_605_000)

        let turn = directory.file("session/turns/turn-000001.json")
        var document = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: turn)) as? [String: Any])
        document["status"] = status
        try JSONSerialization.data(withJSONObject: document).write(to: turn, options: .atomic)
        #expect(OpenBitFunPaths.metadata(forChanged: turn.path) == path)
        parser.consume(reader.read().0, deduper: &deduper) { _ in Issue.record("Turn end emitted extra usage") }
        #expect(parser.state.phase == .idle && parser.state.inFlightStartMs == nil)
        #expect(reader.read().1.bytesRead == 0)
    }

    @Test func switchingUsageSourcesPreservesLiveReportCredit() async throws {
        let fixture = try #require(Fixtures.json("openbitfun-expected.json")["ledger"] as? [String: Any])
        let path = Fixtures.root.appendingPathComponent(try #require(fixture.string("metadata"))).path
        let directory = try TempDirectory()
        let ledger = OpenBitFunUsageLedger(root: URL(fileURLWithPath: directory.path))
        var reader = OpenBitFunReader(path: path, start: .beginning, ledger: ledger)
        var parser = OpenBitFunParser(agentId: path, fileID: 0)
        var deduper = Deduper()
        var engine = SpeedEngine()
        let now: Int64 = 1_792_497_606_000
        func check(_ requests: Int, _ output: Int, reportedRequests: Int, reportedOutput: Int) {
            parser.consume(reader.read().0, deduper: &deduper) { event in
                engine.upsert(event.record, project: "", isCompletion: event.isCompletion, emitsEvents: true, nowMs: now)
            }
            let frame = engine.advance(nowMs: now, todayStartMs: 0, emitsEvents: true)
            #expect(frame.today.requests == requests && frame.today.output == output)
            #expect(frame.report.counters.total.requests == reportedRequests && frame.report.counters.total.output == reportedOutput)
        }
        check(1, 25, reportedRequests: 1, reportedOutput: 25)
        let original = Fixtures.root.appendingPathComponent(try #require(fixture.string("root"))).appendingPathComponent("2026-10-20.json")
        let day = directory.file("2026-10-20.json")
        try FileManager.default.copyItem(at: original, to: day)
        try await Task.sleep(for: .milliseconds(1100))
        check(2, 200, reportedRequests: 2, reportedOutput: 200)
        let own = ledger.snapshot(for: "session")
        let sibling = ledger.snapshot(for: "other")
        let document = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: day)) as? [String: Any])
        var rows = try #require(document["records"] as? [[String: Any]])
        rows[rows.count - 1]["output_tokens"] = 901
        try JSONSerialization.data(withJSONObject: ["records": rows]).write(to: day, options: .atomic)
        try await Task.sleep(for: .milliseconds(1100))
        #expect(ledger.snapshot(for: "session") === own && ledger.snapshot(for: "other") !== sibling)
        #expect(reader.read().1.bytesRead == 0)
        rows.remove(at: 1)
        try JSONSerialization.data(withJSONObject: ["records": rows]).write(to: day, options: .atomic)
        try await Task.sleep(for: .milliseconds(1100))
        check(0, 0, reportedRequests: 2, reportedOutput: 200)
        let revised = ledger.snapshot(for: "session")
        let reopened = OpenBitFunUsageLedger(root: day.deletingLastPathComponent()).snapshot(for: "session")
        #expect(revised.signature != own.signature && reopened.signature == revised.signature)
        try FileManager.default.removeItem(at: day)
        try await Task.sleep(for: .milliseconds(1100))
        check(1, 25, reportedRequests: 2, reportedOutput: 200)
    }

    @Test func liveAndHistoryRefreshWhenOnlyARequestChanges() throws {
        let directory = try TempDirectory()
        try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent("openbitfun/projects"), to: directory.file("projects"))
        let turns = directory.file("projects/demo/sessions/main/turns")
        try FileManager.default.createDirectory(at: turns, withIntermediateDirectories: true)
        let oversized = turns.appendingPathComponent("turn-000000.json")
        try Data().write(to: oversized)
        let handle = try FileHandle(forWritingTo: oversized)
        try handle.truncate(atOffset: UInt64(DocumentReader.defaultMaxBytes + 1))
        try handle.close()
        var configuration = MonitorConfiguration(enabledSources: [.openbitfun])
        configuration.locations[.openbitfun] = [.path(directory.path)]
        let day: Int64 = 1_792_454_400_000, now: Int64 = 1_792_497_613_000
        let session = TranscriptSession(configuration: configuration, limits: .init(largeFileBytes: 1, tailWindowBytes: 1), clock: { now })
        _ = session.seed(todayStartMs: day)
        let (output, _) = session.advance(nowMs: now, todayStartMs: day, emitsEvents: false)
        #expect(output.today.requests == 4 && output.today.output == 240)
        #expect(SnapshotContext(session: session, todayStartMs: day).sources[.openbitfun]?.available == true)
        let store = directory.file("history.sqlite").path
        let first = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        let row = try #require(first.days.first { $0.source == .openbitfun })
        #expect(row.totals == output.today && row.measuredMs == 4000)
        let repeated = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(repeated.filesAnalyzed == 0)

        let metadata = directory.file("projects/demo/sessions/main/metadata.json").path
        let before = FileStat.lstat(metadata)
        let request = directory.file("projects/demo/request-traces/main/request-000005.json")
        try Data(contentsOf: Fixtures.root.appendingPathComponent("openbitfun/completed-request.json")).write(to: request, options: .atomic)
        #expect(FileStat.lstat(metadata) == before)
        #expect(OpenBitFunPaths.metadata(forChanged: request.path) == metadata)
        #expect(session.pollRecentFiles(nowMs: now))
        let (updated, events) = session.advance(nowMs: now, todayStartMs: day, emitsEvents: true)
        #expect(updated.today.requests == 5 && updated.today.output == 320)
        #expect(events.filter { if case .responseCompleted = $0 { true } else { false } }.count == 1)
        let history = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(history.days.first { $0.source == .openbitfun }?.totals == updated.today)
    }
}
