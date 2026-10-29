import Foundation
import Testing
@testable import AgentSpeedCore

/// The performance history (METRICS 6.11 *Records*, *Store*): every fixture log analysed whole gives the records of
/// `history-expected.json` → `perf`, stored in `perf_records` with the file's analysis version; the retired task records
/// are dropped.
@Suite("Performance history")
struct HistoryPerfTests {
    @Test func fixturesMatchTheExpectedRecords() throws {
        let expected = try #require(try Fixtures.json("history-expected.json")["perf"] as? [[String: Any]])
        let (directory, copy) = try HistoryTests.fixtureCopy()
        let report = try HistoryScan.run(configuration: HistoryTests.configuration(fixtures: copy),
                                         timeZone: HistoryTests.utc, pricing: try HistoryTests.fixturePricing())
        let actual = report.perf.map { row -> [String: Any] in
            var object = PerfFixture.record(row.record)
            object["day"] = row.day
            object["source"] = row.source.rawValue
            object["model"] = row.model
            return object
        }
        #expect(actual.count == expected.count)
        let differences = PerfFixture.differences(actual, expected)
        #expect(differences.isEmpty, "\(differences.prefix(10))")
        _ = directory
    }

    @Test func theStoreKeepsRecordsOfTheFilesVersionAndDropsTheTaskRecords() throws {
        let directory = try TempDirectory()
        let path = directory.file("history.sqlite").path
        do {
            let database = try HistoryDatabase(path: path)
            try database.connection.execute("CREATE TABLE IF NOT EXISTS task_records (file_id INTEGER)")
        }
        let database = try HistoryDatabase(path: path)
        let tables = try database.tableNames()
        #expect(tables.contains("perf_records") && !tables.contains("task_records"), "dropped on open")

        var record = PerfRecord()
        (record.n, record.outputSum, record.durationMsSum, record.toolN, record.toolLnSum) = (3, 300, 3000, 2, 1.5)
        record.latencyBins[7] = 2
        let key = PerfRecordKey(day: "2026-10-20", source: .claudeCode, model: "claude-opus-5-5")
        let entry = HistoryDatabase.Entry(id: nil, family: "claude", size: 1, modified: 1,
                                          version: HistoryAnalyzer.analysisVersion, days: [:])
        try database.save([("/a.jsonl", entry, []), ("/b.jsonl", entry, [])],
                          perfRecords: ["/a.jsonl": [PerfDayRecord(key: key, record: record)],
                                        "/b.jsonl": [PerfDayRecord(key: key, record: record)]])
        let stored = try database.perfRecords()
        #expect(stored.count == 1, "a day's record of a source and model sums over files")
        #expect(stored.first?.record.n == 6 && stored.first?.record.latencyBins[7] == 4)
        #expect(stored.first?.record.toolLnSum == 3)

        let ids = try database.save([("/b.jsonl", entry, [])])
        try database.savePerfRecords([PerfDayRecord(key: key, record: record)], fileId: try #require(ids["/b.jsonl"]),
                                     version: HistoryAnalyzer.analysisVersion - 1)
        #expect(try database.perfRecords().first?.record.n == 3, "a row of another version does not count")
        try database.save([("/a.jsonl", entry, [])])
        #expect(try database.perfRecords().isEmpty, "saving a file replaces its rows")
    }
}

extension HistoryDatabase {
    func tableNames() throws -> Set<String> {
        let statement = try connection.prepare("SELECT name FROM sqlite_master WHERE type = 'table'")
        var names = Set<String>()
        try statement.forEachRow { if let name = $0.string(0) { names.insert(name) } }
        return names
    }
}
