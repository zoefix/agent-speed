import Foundation
import Testing
@testable import AgentSpeedCore

/// The side tables of REMOTE 8.3 and the batch rules of the sync (8.2, 8.3).
@Suite("Remote history store")
struct RemoteHistoryStoreTests {
    typealias Lines = RemoteHistoryLines

    private static func tables(_ database: HistoryDatabase) throws -> Set<String> {
        var names: Set<String> = []
        try database.connection.prepare("SELECT name FROM sqlite_master WHERE type = 'table'").forEachRow { row in
            if let name = row.string(0) { names.insert(name) }
        }
        return names
    }

    private static func version(_ database: HistoryDatabase) throws -> Int64? {
        var version: Int64?
        try database.connection.prepare("PRAGMA user_version").forEachRow { version = $0.int64(0) }
        return version
    }

    private static func count(_ database: HistoryDatabase, _ table: String) throws -> Int64 {
        var count: Int64 = 0
        try database.connection.prepare("SELECT count(*) FROM \(table)").forEachRow { count = $0.int64(0) }
        return count
    }

    private static let remoteTables: Set<String> = ["remote_hosts", "remote_days", "remote_perf"]

    @Test func sideTablesAreCreatedOnAnExistingStoreWithoutChangingItsVersion() throws {
        let directory = try TempDirectory()
        let path = directory.file("history.sqlite").path
        do {
            let database = try HistoryDatabase(path: path)
            try database.connection.execute("DROP TABLE remote_hosts; DROP TABLE remote_days; DROP TABLE remote_perf;")
            #expect(try Self.tables(database).isDisjoint(with: Self.remoteTables))
            database.connection.close()
        }
        let reopened = try HistoryDatabase(path: path)
        #expect(try Self.tables(reopened).isSuperset(of: Self.remoteTables))
        #expect(try Self.version(reopened) == HistoryDatabase.schemaVersion)
        try reopened.connection.execute("DROP TABLE remote_hosts; DROP TABLE remote_days; DROP TABLE remote_perf;")
        reopened.connection.close()

        // The traffic writer opens without migrating and creates them too.
        try HistoryDatabase.saveTraffic([TrafficDay(day: "2026-11-03", key: "codex", rx: 1, tx: 1, rxTools: 0, txTools: 0,
                                                    sampledMs: 1)], at: path)
        let again = try HistoryDatabase(path: path)
        #expect(try Self.tables(again).isSuperset(of: Self.remoteTables))
        #expect(try Self.version(again) == HistoryDatabase.schemaVersion)
    }

    @Test func aBatchCommitsAtHistoryDone() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        #expect(ledger.receive([Lines.status(.connected), Lines.event(Lines.day("2026-11-02")),
                                Lines.event(Lines.perf("2026-11-02")), Lines.event(Lines.day("2026-11-03"))],
                               database: database) == .none)
        #expect(try Self.count(database, "remote_days") == 0)
        #expect(ledger.rows.isEmpty)

        #expect(ledger.receive(Lines.event(Lines.done), database: database) == .changed)
        #expect(try Self.count(database, "remote_days") == 2)
        #expect(try Self.count(database, "remote_perf") == 1)
        let days = ledger.rows.usage(sources: [.codex]) { _ in nil }
        #expect(days.count == 2 && days.allSatisfy { $0.host == Lines.host && $0.totals.output == 340 })
        #expect(ledger.rows.perfRecords(sources: [.codex]).map(\.key)
            == [PerfRecordKey(day: "2026-11-02", source: .codex, model: "gpt-5.5")])

        // A reopened ledger reads the same rows back.
        let reloaded = RemoteHistoryLedger()
        reloaded.load(from: database)
        #expect(reloaded.rows.usage(sources: [.codex]) { _ in nil }.sorted { $0.day < $1.day }
            == days.sorted { $0.day < $1.day })
        #expect(reloaded.rows.digests(host: Lines.host) == ledger.rows.digests(host: Lines.host))
    }

    @Test func aConnectionChangeRollsTheBatchBack() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.event(Lines.day("2026-11-01"), connection: 1),
                        Lines.event(Lines.day("2026-11-02"), connection: 2),
                        Lines.event(Lines.done, connection: 2)], database: database)
        #expect(Set(ledger.rows.digests(host: Lines.host).keys) == ["2026-11-02/codex"])

        // A done of the old connection after the new one started commits nothing.
        ledger.receive([Lines.event(Lines.day("2026-11-03"), connection: 3),
                        Lines.event(Lines.done, connection: 2)], database: database)
        #expect(Set(ledger.rows.digests(host: Lines.host).keys) == ["2026-11-02/codex"])
    }

    @Test func aStatusLeavingConnectedRollsTheBatchBack() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.status(.connected), Lines.event(Lines.day("2026-11-01")), Lines.status(.unreachable),
                        Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.isEmpty)
        #expect(try Self.count(database, "remote_days") == 0)

        // A repeated `connected` (a new clock offset) keeps it; a failed analysis drops it.
        ledger.receive([Lines.status(.connected), Lines.event(Lines.day("2026-11-01")), Lines.status(.connected),
                        Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.digests(host: Lines.host).count == 1)
        ledger.receive([Lines.event(Lines.day("2026-11-02")),
                        Lines.event(.error(RelayError(code: .historyFailed, detail: "x"))),
                        Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.digests(host: Lines.host).count == 1)
    }

    @Test func aDayReplacesItsRowAndItsRecords() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.perf("2026-11-02", model: "a")),
                        Lines.event(Lines.perf("2026-11-02", model: "b")), Lines.event(Lines.day("2026-11-03")),
                        Lines.event(Lines.perf("2026-11-03", model: "a")), Lines.event(Lines.done)], database: database)
        #expect(try Self.count(database, "remote_perf") == 3)

        ledger.receive([Lines.event(Lines.day("2026-11-02", aggregate: Lines.aggregate(output: 999), digest: "0123456789abcdef")),
                        Lines.event(Lines.perf("2026-11-02", model: "b", record: Lines.record(n: 9))),
                        Lines.event(Lines.done)], database: database)
        let records = ledger.rows.perfRecords(sources: [.codex])
        #expect(records.map { "\($0.day) \($0.model) \($0.record.n)" } == ["2026-11-02 b 9", "2026-11-03 a 4"])
        #expect(try Self.count(database, "remote_perf") == 2)
        #expect(ledger.rows.digests(host: Lines.host) == ["2026-11-02/codex": "0123456789abcdef",
                                                          "2026-11-03/codex": "aaaaaaaaaaaaaaa3"])
        #expect(ledger.rows.usage(sources: [.codex]) { _ in nil }.first { $0.day == "2026-11-02" }?.totals.output == 999)
    }

    @Test func recordsOnlyFollowTheirOwnDay() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.event(Lines.perf("2026-11-01")), Lines.event(Lines.day("2026-11-02")),
                        Lines.event(Lines.perf("2026-11-01")), Lines.event(Lines.perf("2026-11-02", .claudeCode)),
                        Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.perfRecords(sources: Set(SourceKind.allCases)).isEmpty)
    }

    @Test func aPerformanceOnlyDayTravels() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.event(Lines.day("2026-11-03", aggregate: Lines.emptyAggregate)),
                        Lines.event(Lines.perf("2026-11-03")), Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.perfRecords(sources: [.codex]).count == 1)
        #expect(ledger.rows.usage(sources: [.codex]) { _ in nil }.map(\.totals) == [.zero])
    }

    @Test func historyDueSendsTheStoredDigests() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        #expect(ledger.receive(Lines.due(), database: database) == .request(host: Lines.host, digests: [:]))
        ledger.receive([Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.day("2026-11-03", .claudeCode)),
                        Lines.event(Lines.day("2026-11-03"), host: Lines.otherHost),
                        Lines.event(Lines.done), Lines.event(Lines.done, host: Lines.otherHost)], database: database)
        let stored = try database.remoteDayRows().filter { $0.host == Lines.host }
        let expected = Dictionary(uniqueKeysWithValues: stored.map { ("\($0.day)/\($0.source)", $0.digest) })
        #expect(expected.count == 2)
        #expect(ledger.receive(Lines.due(), database: database) == .request(host: Lines.host, digests: expected))
        #expect(ledger.receive(Lines.due(host: Lines.otherHost), database: database)
            == .request(host: Lines.otherHost, digests: ["2026-11-03/codex": "aaaaaaaaaaaaaaa3"]))
    }

    @Test func aDayTheRelayDidNotSendIsNeverDeleted() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        ledger.receive([Lines.event(Lines.day("2026-11-01")), Lines.event(Lines.day("2026-11-02")),
                        Lines.event(Lines.done)], database: database)
        // The relay's cache was deleted: it now only knows the second day, which changed.
        ledger.receive([Lines.event(Lines.day("2026-11-02", aggregate: Lines.aggregate(output: 5), digest: "bbbbbbbbbbbbbbbb")),
                        Lines.event(Lines.done)], database: database)
        // An answer without days changes nothing either.
        #expect(ledger.receive(Lines.event(Lines.done), database: database) == .none)
        #expect(ledger.rows.digests(host: Lines.host) == ["2026-11-01/codex": "aaaaaaaaaaaaaaa1",
                                                          "2026-11-02/codex": "bbbbbbbbbbbbbbbb"])
        #expect(try Self.count(database, "remote_days") == 2)
    }

    @Test func deletingAHostRemovesOnlyItsRows() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        _ = ledger.setHosts([Lines.host: "devbox", Lines.otherHost: "gpu"], database: database)
        ledger.receive([Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.perf("2026-11-02")),
                        Lines.event(Lines.day("2026-11-02"), host: Lines.otherHost),
                        Lines.event(Lines.done), Lines.event(Lines.done, host: Lines.otherHost)], database: database)
        #expect(ledger.rows.names == [Lines.host: "devbox", Lines.otherHost: "gpu"])

        #expect(ledger.deleteHost(Lines.host, database: database) == .changed)
        #expect(ledger.rows.hosts == [Lines.otherHost])
        #expect(ledger.rows.names == [Lines.otherHost: "gpu"])
        #expect(try database.remoteDayRows().map(\.host) == [Lines.otherHost])
        #expect(try Self.count(database, "remote_perf") == 0)
        #expect(try database.remoteHostNames().keys.sorted() == [Lines.otherHost])
        #expect(ledger.deleteHost(Lines.host, database: database) == .none)
    }

    @Test func hostsAreRenamedAndRemovedOnesKeepTheirName() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = RemoteHistoryLedger()
        ledger.load(from: database)
        _ = ledger.setHosts([Lines.host: "devbox"], database: database)
        ledger.receive([Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.done)], database: database)
        #expect(ledger.setHosts([Lines.host: "build server"], database: database) == .changed)
        #expect(try database.remoteHostNames()[Lines.host] == "build server")
        // Removed from the settings: the rows and the last name stay.
        #expect(ledger.setHosts([:], database: database) == .none)
        #expect(ledger.rows.names == [Lines.host: "build server"])
        #expect(ledger.rows.digests(host: Lines.host).count == 1)
    }

    @Test func aLargeBatchStoresItsCompleteDaysEarly() {
        var batch = RemoteHistoryBatch(host: Lines.host, connection: 1)
        for day in ["2026-11-01", "2026-11-02", "2026-11-03"] {
            guard case .historyDay(let line) = Lines.day(day) else { return }
            batch.add(line)
        }
        guard case .historyPerf(let perf) = Lines.perf("2026-11-03") else { return }
        batch.add(perf)
        let early = batch.takeCompleted()
        #expect(early.map(\.day) == ["2026-11-01", "2026-11-02"])
        #expect(batch.days.map(\.day) == ["2026-11-03"] && batch.days[0].perf.count == 1)
        #expect(batch.byteCount == batch.days[0].byteCount)
    }
}
