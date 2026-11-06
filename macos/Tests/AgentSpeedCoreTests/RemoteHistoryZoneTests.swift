import Foundation
import Testing
@testable import AgentSpeedCore

/// REMOTE 8.3 *Time zone* and *Bounds*, and the staging of a large batch.
@Suite("Remote history zone and bounds")
struct RemoteHistoryZoneTests {
    typealias Lines = RemoteHistoryLines

    private final class Zone: @unchecked Sendable {
        var id = "Asia/Taipei"
    }

    private static func count(_ database: HistoryDatabase, _ table: String) throws -> Int64 {
        var count: Int64 = 0
        try database.connection.prepare("SELECT count(*) FROM \(table)").forEachRow { count = $0.int64(0) }
        return count
    }

    private static func ledger(_ database: HistoryDatabase, zone: Zone, limits: RemoteHistoryLimits = RemoteHistoryLimits(),
                               flushBytes: Int = 32 << 20) -> RemoteHistoryLedger {
        let ledger = RemoteHistoryLedger(limits: limits, flushBytes: flushBytes, zone: { zone.id })
        ledger.load(from: database)
        return ledger
    }

    @discardableResult
    private static func sync(_ ledger: RemoteHistoryLedger, _ messages: [RelayMessage], database: HistoryDatabase,
                             host: String = Lines.host) -> RemoteHistoryLedger.Effect {
        _ = ledger.receive(Lines.due(host: host), database: database)
        return ledger.receive(messages.map { Lines.event($0, host: host) } + [Lines.event(Lines.done, host: host)],
                              database: database)
    }

    @Test func theZoneColumnIsAddedToAnEarlierTable() throws {
        let directory = try TempDirectory()
        let path = directory.file("history.sqlite").path
        do {
            let database = try HistoryDatabase(path: path)
            try database.connection.execute("""
                DROP TABLE remote_hosts; CREATE TABLE remote_hosts (id TEXT PRIMARY KEY, name TEXT);
                INSERT INTO remote_hosts VALUES ('\(Lines.host)', 'devbox');
                """)
            database.connection.close()
        }
        let reopened = try HistoryDatabase(path: path)
        #expect(try reopened.remoteHostNames() == [Lines.host: "devbox"])
        #expect(try reopened.remoteHostZones().isEmpty)
        try reopened.connection.execute("UPDATE remote_hosts SET zone = 'UTC'")
        #expect(try reopened.remoteHostZones() == [Lines.host: "UTC"])
    }

    @Test func rowsKeepTheZoneTheyWereComputedIn() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone)
        Self.sync(ledger, [Lines.day("2026-11-01"), Lines.day("2026-11-02")], database: database)
        #expect(try database.remoteHostZones() == [Lines.host: "Asia/Taipei"])
        #expect(Self.ledger(database, zone: zone).rows.zones == [Lines.host: "Asia/Taipei"])
        #expect(ledger.receive(Lines.due(), database: database)
            == .request(host: Lines.host, digests: ["2026-11-01/codex": "aaaaaaaaaaaaaaa1", "2026-11-02/codex": "aaaaaaaaaaaaaaa2"]))
    }

    @Test func aSyncInAnotherZoneSendsNoDigestsAndReplacesEveryRow() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone)
        Self.sync(ledger, [Lines.day("2026-11-01"), Lines.perf("2026-11-01"), Lines.day("2026-11-02")], database: database)
        Self.sync(ledger, [Lines.day("2026-11-02")], database: database, host: Lines.otherHost)

        zone.id = "America/Los_Angeles"
        #expect(ledger.receive(Lines.due(), database: database) == .request(host: Lines.host, digests: [:]))
        // The relay relabelled its days: the second one moved, the first one is gone with its logs.
        let effect = ledger.receive([Lines.event(Lines.day("2026-11-03", digest: "cccccccccccccccc")), Lines.event(Lines.done)],
                                    database: database)
        #expect(effect == .changed)
        #expect(ledger.rows.digests(host: Lines.host) == ["2026-11-03/codex": "cccccccccccccccc"])
        #expect(try database.remoteDayRows().filter { $0.host == Lines.host }.map(\.day) == ["2026-11-03"])
        #expect(try Self.count(database, "remote_perf") == 0)
        #expect(try database.remoteHostZones()[Lines.host] == "America/Los_Angeles")
        // Another host is only replaced at its own sync.
        #expect(ledger.rows.digests(host: Lines.otherHost).count == 1)
        #expect(try database.remoteHostZones()[Lines.otherHost] == "Asia/Taipei")

        // Back in step: digests again, and the next batch merges.
        #expect(ledger.receive(Lines.due(), database: database)
            == .request(host: Lines.host, digests: ["2026-11-03/codex": "cccccccccccccccc"]))
        ledger.receive([Lines.event(Lines.day("2026-11-04")), Lines.event(Lines.done)], database: database)
        #expect(ledger.rows.digests(host: Lines.host).count == 2)
    }

    @Test func anEmptyAnswerInAnotherZoneLeavesTheHostWithoutRows() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone)
        Self.sync(ledger, [Lines.day("2026-11-01")], database: database)
        zone.id = "Europe/Berlin"
        #expect(Self.sync(ledger, [], database: database) == .changed)
        #expect(ledger.rows.isEmpty && ledger.rows.zones.isEmpty)
        #expect(try database.remoteHostNames().isEmpty)
    }

    @Test func rowsStoredWithoutAZoneTakeTheNextSyncsZone() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone)
        Self.sync(ledger, [Lines.day("2026-11-01")], database: database)
        try database.connection.execute("UPDATE remote_hosts SET zone = NULL")
        let reloaded = Self.ledger(database, zone: zone)
        #expect(reloaded.receive(Lines.due(), database: database)
            == .request(host: Lines.host, digests: ["2026-11-01/codex": "aaaaaaaaaaaaaaa1"]))
        #expect(reloaded.receive(Lines.event(Lines.done), database: database) == .changed)
        #expect(try database.remoteHostZones() == [Lines.host: "Asia/Taipei"])
        #expect(reloaded.rows.digests(host: Lines.host).count == 1)
    }

    @Test func aBatchBeyondTheBoundsIsDiscardedUntilASyncSucceeds() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone, limits: RemoteHistoryLimits(days: 3, perf: 2))
        Self.sync(ledger, [Lines.day("2026-11-01"), Lines.perf("2026-11-01"), Lines.day("2026-11-02")], database: database)
        let stored = try database.remoteDayRows()

        // Two new days would leave four rows.
        #expect(Self.sync(ledger, [Lines.day("2026-11-03"), Lines.day("2026-11-04")], database: database) == .tooLargeChanged)
        #expect(ledger.tooLarge == [Lines.host])
        #expect(try database.remoteDayRows() == stored)
        #expect(ledger.rows.digests(host: Lines.host).count == 2)
        // Records count too; still too large changes nothing more.
        #expect(Self.sync(ledger, [Lines.day("2026-11-02"), Lines.perf("2026-11-02", model: "a"),
                                   Lines.perf("2026-11-02", model: "b")], database: database) == .none)
        #expect(try Self.count(database, "remote_perf") == 1)

        #expect(Self.sync(ledger, [Lines.day("2026-11-03")], database: database) == .changed)
        #expect(ledger.tooLarge.isEmpty)
        #expect(ledger.rows.digests(host: Lines.host).count == 3)
        #expect(Self.sync(ledger, [Lines.day("2026-11-04")], database: database) == .tooLargeChanged)
        #expect(Self.sync(ledger, [], database: database) == .tooLargeChanged)
        #expect(ledger.tooLarge.isEmpty)
    }

    @Test func aBatchAloneBeyondTheBoundsStopsBeingKept() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = Self.ledger(database, zone: Zone(), limits: RemoteHistoryLimits(days: 2, perf: 10))
        let days = ["2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04"].map { Lines.day($0) }
        _ = ledger.receive(Lines.due(), database: database)
        ledger.receive(days.map { Lines.event($0) }, database: database)
        #expect(ledger.receive(Lines.event(Lines.done), database: database) == .tooLargeChanged)
        #expect(ledger.rows.isEmpty)
        #expect(try Self.count(database, "remote_days") == 0)

        // Deleting the host's history forgets it.
        #expect(ledger.deleteHost(Lines.host, database: database) == .tooLargeChanged)
        #expect(ledger.tooLarge.isEmpty)
    }

    @Test func aLargeBatchWaitsInTheStagingTablesUntilItCommits() throws {
        let database = try HistoryDatabase.inMemory()
        let zone = Zone()
        let ledger = Self.ledger(database, zone: zone, flushBytes: 1)
        _ = ledger.receive(Lines.due(), database: database)
        ledger.receive([Lines.event(Lines.day("2026-11-01")), Lines.event(Lines.perf("2026-11-01")),
                        Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.day("2026-11-03"))], database: database)
        #expect(try Self.count(database, "temp.remote_stage_days") == 2)
        #expect(try Self.count(database, "temp.remote_stage_perf") == 1)
        #expect(try Self.count(database, "remote_days") == 0)

        #expect(ledger.receive(Lines.event(Lines.done), database: database) == .changed)
        #expect(try Self.count(database, "remote_days") == 3)
        #expect(try Self.count(database, "remote_perf") == 1)
        #expect(try Self.count(database, "temp.remote_stage_days") == 0)
        #expect(ledger.rows.digests(host: Lines.host).count == 3)
        #expect(ledger.rows.perfRecords(sources: [.codex]).count == 1)
        #expect(ledger.rows.zones == [Lines.host: "Asia/Taipei"])

        // A staged day sent again replaces its row and its records; a broken connection leaves the store as it was.
        ledger.receive([Lines.event(Lines.day("2026-11-01", aggregate: Lines.aggregate(output: 7), digest: "dddddddddddddddd")),
                        Lines.event(Lines.day("2026-11-05")), Lines.event(Lines.day("2026-11-06")),
                        Lines.status(.unreachable), Lines.event(Lines.done)], database: database)
        #expect(try Self.count(database, "remote_days") == 3)
        ledger.receive([Lines.status(.connected), Lines.event(Lines.day("2026-11-07")),
                        Lines.event(Lines.day("2026-11-01", aggregate: Lines.aggregate(output: 7), digest: "dddddddddddddddd")),
                        Lines.event(Lines.day("2026-11-08")), Lines.event(Lines.done)], database: database)
        #expect(try Self.count(database, "remote_days") == 5)
        #expect(try Self.count(database, "remote_perf") == 0)
        #expect(ledger.rows.digests(host: Lines.host)["2026-11-01/codex"] == "dddddddddddddddd")
        #expect(try Self.count(database, "temp.remote_stage_days") == 0)
    }

    @Test func aStagedBatchBeyondTheBoundsChangesNothing() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = Self.ledger(database, zone: Zone(), limits: RemoteHistoryLimits(days: 3, perf: 10), flushBytes: 1)
        Self.sync(ledger, [Lines.day("2026-11-01"), Lines.day("2026-11-02")], database: database)
        #expect(Self.sync(ledger, [Lines.day("2026-11-03"), Lines.day("2026-11-04"), Lines.day("2026-11-05")],
                          database: database) == .tooLargeChanged)
        #expect(try Self.count(database, "remote_days") == 2)
        #expect(try Self.count(database, "temp.remote_stage_days") == 0)
        #expect(ledger.rows.digests(host: Lines.host).count == 2)
    }

    @Test func aBatchThatCouldNotBeStagedCommitsNothing() throws {
        let database = try HistoryDatabase.inMemory()
        let ledger = Self.ledger(database, zone: Zone(), flushBytes: 1)
        try database.connection.execute("DROP TABLE temp.remote_stage_perf; DROP TABLE temp.remote_stage_days;")
        #expect(Self.sync(ledger, [Lines.day("2026-11-01"), Lines.day("2026-11-02"), Lines.day("2026-11-03")],
                          database: database) == .none)
        #expect(try Self.count(database, "remote_days") == 0)
        #expect(ledger.rows.isEmpty)
    }

    @Test func batchCountsFollowRepeatedDaysAndRecords() {
        var batch = RemoteHistoryBatch(host: Lines.host, connection: 1)
        func add(_ message: RelayMessage) {
            switch message {
            case .historyDay(let line): batch.add(line)
            case .historyPerf(let line): batch.add(line)
            default: break
            }
        }
        add(Lines.day("2026-11-01"))
        add(Lines.perf("2026-11-01", model: "a"))
        add(Lines.perf("2026-11-01", model: "b"))
        add(Lines.day("2026-11-02"))
        _ = batch.takeCompleted()
        add(Lines.perf("2026-11-02", model: "a"))
        add(Lines.perf("2026-11-02", model: "a"))
        #expect(batch.dayCount == 2 && batch.perfCount == 3)
        add(Lines.day("2026-11-01"))
        #expect(batch.dayCount == 2 && batch.perfCount == 1)
        add(Lines.day("2026-11-02"))
        #expect(batch.dayCount == 2 && batch.perfCount == 0)
        #expect(batch.exceeds(RemoteHistoryLimits(days: 1, perf: 10)) && !batch.exceeds(RemoteHistoryLimits(days: 2, perf: 0)))
    }
}
