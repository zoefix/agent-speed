import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Devin CLI and Desktop")
struct DevinTests {
    static let dayStart: Int64 = 1_792_454_400_000
    static let now: Int64 = 1_792_497_700_000

    static var root: URL { Fixtures.root.appendingPathComponent("devin", isDirectory: true) }
    static var configuration: MonitorConfiguration { MonitorConfiguration(enabledSources: [.devin], devinRoot: root) }

    static func compare(_ records: [ResponseRecord], to expected: [[String: Any]]) throws {
        #expect(records.map(\.key) == expected.compactMap { $0.string("key") })
        for (record, wanted) in zip(records, expected) {
            #expect(record.source == .devin)
            #expect(record.startMs == wanted.int64("startMs"), "\(record.key) start")
            #expect(record.endMs == wanted.int64("endMs"), "\(record.key) end")
            #expect(record.usage.input == wanted.int("input"), "\(record.key) input")
            #expect(record.usage.cachedInput == wanted.int("cachedInput"), "\(record.key) cached")
            #expect(record.usage.cacheWriteInput == wanted.int("cacheWriteInput"), "\(record.key) cache write")
            #expect(record.usage.output == wanted.int("output"), "\(record.key) output")
            #expect(record.usage.reasoning == 0)
            #expect(record.model == wanted.string("model"), "\(record.key) model")
            #expect(record.agentId == wanted.string("agentId").map { Fixtures.root.appendingPathComponent($0).path })
        }
    }

    @Test func sharedFixtureMatchesCLIAndDesktopWithoutDoubleCounting() throws {
        let expected = try Fixtures.json("devin-expected.json")
        var deduper = Deduper()
        let source = DevinSource(location: DevinPaths.resolve(Self.root), fileID: -3)
        let update = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        let records = update.upserts.map { $0.0.record }
        try Self.compare(records, to: try #require(expected["responses"] as? [[String: Any]]))
        let agents = try #require(expected["agents"] as? [String: [String: Any]])
        #expect(update.agents.count == agents.count)
        for (relative, wanted) in agents {
            let state = try #require(update.agents[Fixtures.root.appendingPathComponent(relative).path])
            #expect(state.project == wanted.string("project"))
            #expect(state.model == wanted.string("model"))
            #expect(state.lastLineMs == wanted.int64("lastLineMs"))
            #expect(state.malformedLines == wanted.int("malformedLines"))
        }
        #expect(records.filter { $0.key.contains("req-1") }.count == 1)
        #expect(!records.contains { $0.agentId.hasSuffix("desktop-mirror.ndjson") })
        #expect(source.refreshIfChanged(deduper: &deduper) == nil)
        #expect(source.activeSessionCount(todayStartMs: Self.now + 100_000_000_000) == 0)
    }

    @Test func desktopOnlyInstallationStaysAvailableWithoutACliDatabase() throws {
        let directory = try TempDirectory()
        let root = directory.file("devin")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: Self.root.appendingPathComponent("User"), to: root.appendingPathComponent("User"))
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.devin], devinRoot: root), clock: { Self.now })
        #expect(session.hasAvailableRoot)
        _ = session.seed(todayStartMs: Self.dayStart)
        #expect(session.hasAvailableRoot)
        #expect(session.devinLocation.available)
        #expect(session.responses.count == 3)
    }

    @Test func liveAndHistoryShareTotalsAndRestartDoesNotAccumulate() throws {
        let session = TranscriptSession(configuration: Self.configuration, clock: { Self.now })
        #expect(session.hasAvailableRoot)
        _ = session.seed(todayStartMs: Self.dayStart)
        let (output, _) = session.advance(nowMs: Self.now, todayStartMs: Self.dayStart, emitsEvents: false)
        let today = try #require(output.todayBySource[.devin])
        #expect(today.requests == 7 && today.input == 424 && today.output == 98)
        #expect(output.averageToday == 81.0 / 6.5)
        let context = SnapshotContext(session: session, todayStartMs: Self.dayStart)
        #expect(context.sources[.devin]?.rootPath == Self.root.path)
        #expect(context.sources[.devin]?.sessionCount == 6)

        let directory = try TempDirectory()
        let store = directory.file("history.sqlite").path
        let first = try HistoryScan.run(configuration: Self.configuration, timeZone: HistoryTests.utc, storePath: store)
        let second = try HistoryScan.run(configuration: Self.configuration, timeZone: HistoryTests.utc, storePath: store)
        let day = try #require(first.days.first)
        #expect(first.days.count == 1)
        #expect(day.source == .devin && day.totals == today)
        #expect(day.measuredOutput == 81 && day.measuredMs == 6500)
        #expect(second.filesAnalyzed == 0 && second.sessionsAnalyzed == 0)
    }

    @Test func appendedRowsAndUsageUpdatesAreIncremental() throws {
        let directory = try TempDirectory()
        let root = directory.file("devin")
        try FileManager.default.copyItem(at: Self.root, to: root)
        let writer = try SQLiteConnection(path: root.appendingPathComponent("cli/sessions.db").path, mode: .readWriteCreate)
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.devin], devinRoot: root), clock: { Self.now })
        _ = session.seed(todayStartMs: Self.dayStart)
        #expect(session.responses.count == 7)
        let metadata = #"{"request_id":"req-live","generation_model":"swe-2-max","metrics":{"input_tokens":2,"output_tokens":5,"cache_read_tokens":3,"cache_creation_tokens":1,"total_time_ms":750},"finish_reason":"stop"}"#
        let message = #"{"role":"assistant","content":"not read","metadata":\#(metadata)}"#
        try writer.execute("INSERT INTO message_nodes(session_id,node_id,chat_message,created_at) VALUES('orbit-main',99,'\(message.replacingOccurrences(of: "'", with: "''"))',1792490700)")
        #expect(session.pollRecentFiles(nowMs: Self.now))
        #expect(session.responses.count == 8)
        let live = try #require(session.responses.first { $0.key == "devin:orbit-main:request:req-live" })
        #expect(live.startMs == 1_792_490_699_250 && live.usage.input == 6 && live.usage.output == 5)
        #expect(!session.pollRecentFiles(nowMs: Self.now))

        try directory.append("devin/User/acp-events/desktop-only.ndjson", #"{"notification":{"sessionUpdate":"usage_update","_meta":{"cognition.ai/inputTokens":160,"cognition.ai/outputTokens":4},"created_at":"2026-10-20T10:10:04Z"}}"# + "\n")
        #expect(session.pollRecentFiles(nowMs: Self.now))
        let aggregate = try #require(session.responses.first { $0.key == "devin-desktop:desktop-only:usage" })
        #expect(aggregate.usage.input == 162 && aggregate.usage.output == 17)
        #expect(session.responses.count == 8, "an aggregate update is not another request")
    }

    @Test func replacingTheDatabaseResetsItsCursorWithoutDuplicatingResponses() throws {
        let directory = try TempDirectory()
        let root = directory.file("devin")
        try FileManager.default.copyItem(at: Self.root, to: root)
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.devin], devinRoot: root), clock: { Self.now })
        _ = session.seed(todayStartMs: Self.dayStart)
        #expect(session.responses.count == 7)
        let database = root.appendingPathComponent("cli/sessions.db")
        let replacement = directory.file("replacement.db")
        try FileManager.default.copyItem(at: database, to: replacement)
        try FileManager.default.replaceItem(at: database, withItemAt: replacement, backupItemName: nil, resultingItemURL: nil)
        #expect(session.pollRecentFiles(nowMs: Self.now))
        #expect(session.responses.count == 7)
        #expect(!session.pollRecentFiles(nowMs: Self.now))
    }

    @Test func unsupportedSchemaIsReportedWithoutCreatingSidecars() throws {
        let directory = try TempDirectory()
        let path = directory.file("cli/sessions.db")
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        let writer = try SQLiteConnection(path: path.path, mode: .readWriteCreate)
        try writer.execute("CREATE TABLE unrelated(id TEXT)")
        let before = try FileManager.default.contentsOfDirectory(atPath: path.deletingLastPathComponent().path).sorted()
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.devin], devinRoot: directory.url))
        #expect(session.unsupportedSources == [.devin])
        #expect(try FileManager.default.contentsOfDirectory(atPath: path.deletingLastPathComponent().path).sorted() == before)
    }

    @Test func duplicateResponseCanBeRemovedFromTheEngine() {
        var engine = SpeedEngine()
        let record = ResponseRecord(key: "devin-desktop:mirror:usage", source: .devin, agentId: "mirror", model: nil, startMs: nil,
                                    endMs: Self.now, usage: TokenUsage(input: 20, output: 5))
        engine.upsert(record, project: "mirror", isCompletion: true, emitsEvents: false, nowMs: Self.now)
        _ = engine.advance(nowMs: Self.now, todayStartMs: Self.dayStart, emitsEvents: false)
        engine.removeResponse(key: record.key)
        #expect(engine.storedResponses.isEmpty)
        #expect(engine.advance(nowMs: Self.now, todayStartMs: Self.dayStart, emitsEvents: false).today.requests == 0)
    }
}
