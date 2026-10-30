import Foundation
import Testing
@testable import AgentSpeedCore

/// The side table `contribution_unpriced` and the engine's live facts (METRICS 9.10).
@Suite("Unpriced facts in the history store and the engine")
struct HistoryUnpricedTests {
    /// A Claude transcript with one response per `(id, model, output, fast)`.
    static func claude(_ responses: [(String, String, Int, Bool)], base: Int64 = 1_792_490_400) -> String {
        let formatter = ISO8601DateFormatter()
        var lines: [String] = []
        for (index, (id, model, output, fast)) in responses.enumerated() {
            let start = formatter.string(from: Date(timeIntervalSince1970: TimeInterval(base + Int64(index) * 10)))
            let end = formatter.string(from: Date(timeIntervalSince1970: TimeInterval(base + Int64(index) * 10 + 4)))
            lines.append(#"{"type":"user","cwd":"/w/p","message":{"role":"user","content":"q"},"timestamp":"\#(start)"}"#)
            lines.append(#"{"type":"assistant","cwd":"/w/p","message":{"id":"\#(id)","model":"\#(model)","stop_reason":"end_turn","usage":{"input_tokens":5000,"output_tokens":\#(output),"speed":"\#(fast ? "fast" : "standard")"}},"timestamp":"\#(end)"}"#)
        }
        return lines.joined(separator: "\n") + "\n"
    }

    static func fact(_ model: String, provider: String? = nil, fast: Bool = false, _ requests: Int) -> UnpricedFact {
        UnpricedFact(key: UnpricedFactKey(model: model, provider: provider, fast: fast), requests: requests)
    }

    static func sideRows(at path: String) throws -> [[String]] {
        let connection = try SQLiteConnection(path: path, mode: .readOnly)
        defer { connection.close() }
        var rows: [[String]] = []
        try connection.prepare("""
            SELECT day, source, model, provider, fast, requests, version FROM contribution_unpriced ORDER BY model, fast
            """).forEachRow { row in
            rows.append([row.string(0) ?? "", row.string(1) ?? "", row.string(2) ?? "", row.string(3) ?? "",
                         String(row.int64(4)), String(row.int64(5)), String(row.int64(6))])
        }
        return rows
    }

    static func execute(_ sql: String, at path: String) throws {
        let connection = try SQLiteConnection(path: path, mode: .readWriteCreate)
        defer { connection.close() }
        try connection.execute(sql)
    }

    @Test func theAnalysisVersionCoversTheSideTables() {
        // 7: the unpriced facts (METRICS 9.10); 8: the task records (6.11).
        #expect(HistoryAnalyzer.analysisVersion == 10)
    }

    @Test func sideRowsAreWrittenWithTheContributionsAndReadBack() throws {
        let (_, v2) = try HistoryCostTests.tables()
        let scratch = try HistoryCostTests.Scratch()
        try scratch.directory.write("claude/p/a.jsonl", Self.claude([
            ("r1", "mystery-model", 100, false), ("r2", "mystery-model", 50, false),
            ("r3", "claude-opus-5-5", 100, false), ("r4", "claude-sonnet-5-5", 40, true),
        ]))
        let (report, days, _) = try scratch.run([v2])
        #expect(report.filesAnalyzed == 1)
        let facts = [Self.fact("claude-sonnet-5-5", fast: true, 1), Self.fact("mystery-model", 2)]
        #expect(days.count == 1)
        #expect(days[0].unpriced == UnpricedReading(requests: 4, facts: facts))
        #expect(try Self.sideRows(at: scratch.storePath) == [
            ["2026-10-20", "claudeCode", "claude-sonnet-5-5", "", "1", "1", "10"],
            ["2026-10-20", "claudeCode", "mystery-model", "", "0", "2", "10"],
        ])

        let (unchanged, again, _) = try scratch.run([v2])
        #expect(unchanged.filesAnalyzed == 0)
        #expect(again == days, "the facts come back from the side table")
    }

    @Test func staleSideRowsAreIgnoredUntilTheFileIsAnalysedAgain() throws {
        let (_, v2) = try HistoryCostTests.tables()
        let scratch = try HistoryCostTests.Scratch()
        try scratch.directory.write("claude/p/a.jsonl", Self.claude([
            ("r1", "mystery-model", 100, false), ("r4", "claude-sonnet-5-5", 40, true),
        ]))
        _ = try scratch.run([v2])
        // An older build analysed the file again: its own version, the side rows left as they were.
        try Self.execute("UPDATE files SET version = 6", at: scratch.storePath)
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase(path: scratch.storePath), pricing: PriceBook(tables: [v2]))
        let derived = analyzer.days(sources: [.claudeCode])
        #expect(derived[0].unpriced == UnpricedReading(
            requests: 2, facts: [Self.fact("claude-sonnet-5-5", 1), Self.fact("mystery-model", 1)], derived: 2),
                "derived from the per-model counts: no provider, no fast flag")

        let (report, days, _) = try scratch.run([v2])
        #expect(report.filesAnalyzed == 1, "a file of another analysis version is analysed again")
        #expect(days[0].unpriced == UnpricedReading(
            requests: 2, facts: [Self.fact("claude-sonnet-5-5", fast: true, 1), Self.fact("mystery-model", 1)]))
        #expect(try Self.sideRows(at: scratch.storePath).allSatisfy { $0[6] == "10" })
    }

    @Test func readingChecksEveryRowAndDerivesOtherwise() throws {
        let database = try HistoryDatabase.inMemory()
        var day = DayAggregate()
        let record = ResponseRecord(key: "k", source: .codex, agentId: "a", model: " neko-model-1 ", startMs: nil,
                                    endMs: 1_792_490_400_000, usage: TokenUsage(input: 1, output: 1), provider: "custom")
        day.add(record, cost: nil)
        day.add(ResponseRecord(key: "k2", source: .codex, agentId: "a", model: "gpt-5.5", startMs: nil, endMs: 1_792_490_400_000,
                               usage: TokenUsage(input: 1, output: 1)), cost: 12)
        let key = UsageKey(day: "2026-10-20", source: .codex)
        let entry = HistoryDatabase.Entry(id: nil, family: "codex", size: 1, modified: 1, version: 7, days: [key: day])
        let ids = try database.save([("/a.jsonl", entry, [])])
        let id = try #require(ids["/a.jsonl"])

        let valid = UnpricedReading(requests: 2, facts: [Self.fact("neko-model-1", provider: "custom", 1)])
        #expect(try database.loadEntries()["/a.jsonl"]?.days[key]?.unpricedReading == valid)

        // A row of a contribution that does not exist is ignored.
        try database.connection.execute("""
            INSERT INTO contribution_unpriced VALUES (\(id), '2026-10-21', 'codex', 'x', '', 0, 5, 7);
            INSERT INTO contribution_unpriced VALUES (999, '2026-10-20', 'codex', 'x', '', 0, 5, 7);
            """)
        let loaded = try database.loadEntries()
        #expect(loaded["/a.jsonl"]?.days.count == 1)
        #expect(loaded["/a.jsonl"]?.days[key]?.unpricedReading == valid)

        let derived = UnpricedReading(requests: 2, facts: [Self.fact("neko-model-1", 1)], derived: 1)
        try database.connection.execute("UPDATE contribution_unpriced SET requests = 0 WHERE day = '2026-10-20'")
        #expect(try database.loadEntries()["/a.jsonl"]?.days[key]?.unpricedReading == derived, "a row without requests")
        try database.connection.execute("DELETE FROM contribution_unpriced")
        #expect(try database.loadEntries()["/a.jsonl"]?.days[key]?.unpricedReading == derived, "no rows")

        _ = try database.save([("/a.jsonl", entry, [])])
        #expect(try database.loadEntries()["/a.jsonl"]?.days[key]?.unpricedReading == valid, "saving rewrites the rows")
    }

    @Test func everyOpenCreatesTheTableWithoutChangingTheSchemaVersion() throws {
        let directory = try TempDirectory()
        let path = HistoryTrafficTests.storePath(in: directory)
        _ = try HistoryDatabase(path: path)
        #expect(try HistoryTrafficTests.tables(at: path).contains("contribution_unpriced"))
        try HistoryTrafficTests.handBuiltStore(at: path, "DROP TABLE contribution_unpriced")
        _ = try HistoryDatabase(path: path)
        #expect(try HistoryTrafficTests.tables(at: path).contains("contribution_unpriced"))
        #expect(try HistoryTrafficTests.pragma("user_version", at: path) == String(HistoryDatabase.schemaVersion))

        let older = directory.file("older/history.sqlite").path
        try HistoryTrafficTests.handBuiltStore(at: older, """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            PRAGMA user_version = 2;
            """)
        try HistoryDatabase.saveTraffic(HistoryTrafficTests.rows, at: older)
        #expect(try HistoryTrafficTests.tables(at: older).contains("contribution_unpriced"))
        #expect(try HistoryTrafficTests.pragma("user_version", at: older) == "2")

        let missing = directory.file("missing/history.sqlite").path
        try HistoryDatabase.saveTraffic(HistoryTrafficTests.rows, at: missing)
        #expect(try HistoryTrafficTests.tables(at: missing).contains("contribution_unpriced"))
    }

    // MARK: - Live

    @Test func theDayLedgerMovesFactsWithReplacementsRepricingAndTheDayChange() throws {
        let (v1, v2) = try HistoryCostTests.tables()
        let book = PriceBook(tables: [v1])
        var engine = SpeedEngine(pricing: book)
        let end: Int64 = 1_792_490_405_000
        let todayStart: Int64 = 1_792_454_400_000
        let usage = TokenUsage(input: 5000, output: 500)
        func upsert(_ key: String, _ model: String, provider: String? = nil, at time: Int64 = end) {
            engine.upsert(ResponseRecord(key: key, source: .codex, agentId: "x", model: model, startMs: time - 4000, endMs: time,
                                         usage: usage, provider: provider),
                          project: "p", isCompletion: true, emitsEvents: false)
        }
        upsert("a", "gpt-6.1-sol-mini")
        upsert("b", "neko-model-7", provider: "custom")
        upsert("c", "neko-model-7", provider: "custom")
        var output = engine.advance(nowMs: end + 1000, todayStartMs: todayStart, emitsEvents: false)
        #expect(output.todayUnpricedFacts[.codex] == [Self.fact("gpt-6.1-sol-mini", 1), Self.fact("neko-model-7", provider: "custom", 2)])

        upsert("c", "claude-opus-5-5")
        output = engine.advance(nowMs: end + 1100, todayStartMs: todayStart, emitsEvents: false)
        #expect(output.todayUnpricedFacts[.codex] == [Self.fact("gpt-6.1-sol-mini", 1), Self.fact("neko-model-7", provider: "custom", 1)],
                "a replacement moves the fact")

        book.add(v2)
        output = engine.advance(nowMs: end + 1200, todayStartMs: todayStart, emitsEvents: false)
        #expect(output.todayUnpricedFacts[.codex] == [Self.fact("neko-model-7", provider: "custom", 1)], "re-pricing removes it")
        #expect(output.todayCostBySource[.codex]?.unpricedRequests == 1)

        output = engine.advance(nowMs: todayStart + 86_400_000 + 1000, todayStartMs: todayStart + 86_400_000, emitsEvents: false)
        #expect(output.todayUnpricedFacts.isEmpty, "a new day starts at zero")
    }
}
