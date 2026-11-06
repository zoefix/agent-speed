import Foundation
import Testing
@testable import AgentSpeedCore

/// The hosts' rows in the daily and performance history (REMOTE 8.3).
@Suite("Remote history merge")
struct RemoteHistoryMergeTests {
    typealias Lines = RemoteHistoryLines

    /// Every fixture day, encoded as the relay sends it and read back, is the row the local analysis gives (bar its
    /// host and price version, which the line does not carry).
    @Test func aRemoteAggregateGivesTheLocalAnalysisRow() throws {
        let (directory, copy) = try HistoryTests.fixtureCopy()
        defer { _ = directory }
        let configuration = HistoryTests.configuration(fixtures: copy)
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory(), pricing: try HistoryTests.fixturePricing())
        _ = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: HistoryTests.utc))
        // The Linux core sums the contributions' unpriced facts with the rest (`DayAggregate::merge`).
        var merged: [UsageKey: DayAggregate] = [:]
        var facts: [UsageKey: [UnpricedFactKey: Int]] = [:]
        for entry in analyzer.entries.values {
            for (key, day) in entry.days {
                merged[key, default: DayAggregate()].merge(day)
                for (fact, count) in day.unpricedFacts { facts[key, default: [:]][fact, default: 0] += count }
            }
        }
        var checkedModes = false
        var checkedUnpriced = false
        let local = analyzer.days(sources: configuration.enabledSources)
        #expect(local.count == merged.count)
        for day in local {
            let key = UsageKey(day: day.day, source: day.source)
            var aggregate = try #require(merged[key])
            aggregate.unpricedFacts = facts[key] ?? [:]
            let json = Lines.linuxJSON(aggregate)
            let decoded = try #require(RemoteHistoryMerge.aggregate(json: json), "\(key)")
            var remote = decoded.usage(day: day.day, source: day.source, host: Lines.host, peak: day.peak)
            #expect(remote.host == Lines.host && remote.priceVersion == nil)
            remote.host = RemoteHost.localId
            remote.priceVersion = day.priceVersion
            #expect(remote == day, "\(day.day) \(day.source.rawValue)")
            checkedModes = checkedModes || !day.modeUsage.isEmpty
            checkedUnpriced = checkedUnpriced || !day.unpriced.facts.isEmpty
        }
        #expect(checkedModes && checkedUnpriced, "the fixtures cover mode usage and unpriced facts")
    }

    @Test func nullCostsStayUnknown() throws {
        let aggregate = try #require(RemoteHistoryMerge.aggregate(json: Lines.aggregate(cost: nil)))
        let usage = aggregate.usage(day: "2026-11-03", source: .codex, host: Lines.host)
        #expect(usage.cost == nil && usage.models.allSatisfy { $0.cost == nil })
        #expect(usage.unpriced.noCostData == 3)
        #expect(usage.modeUsage.map(\.attribution.mode) == ["fast"])
        #expect(usage.id == "2026-11-03/codex/\(Lines.host)")
    }

    @Test func daysAreSortedByDaySourceAndHost() {
        func row(_ day: String, _ source: SourceKind, _ host: String = RemoteHost.localId) -> DailyUsage {
            DailyUsage(day: day, source: source, totals: TokenTotals(requests: 1, output: 1), measuredOutput: 0, measuredMs: 0,
                       unfinalized: 0, agents: 1, peak: nil, models: [], host: host)
        }
        let local = [row("2026-11-02", .claudeCode), row("2026-11-02", .codex), row("2026-11-03", .codex)]
        #expect(RemoteHistoryMerge.days(local: local, remote: []) == local)
        let merged = RemoteHistoryMerge.days(local: local, remote: [
            row("2026-11-03", .codex, Lines.otherHost), row("2026-11-01", .codex, Lines.host),
            row("2026-11-03", .codex, Lines.host), row("2026-11-02", .claudeCode, Lines.host),
        ])
        // Remote hosts of one day and source follow this computer in id order (`1F2E…` before `8C0D…`).
        #expect(merged.map(\.id) == [
            "2026-11-01/codex/\(Lines.host)", "2026-11-02/claudeCode", "2026-11-02/claudeCode/\(Lines.host)",
            "2026-11-02/codex", "2026-11-03/codex", "2026-11-03/codex/\(Lines.otherHost)", "2026-11-03/codex/\(Lines.host)",
        ])
    }

    @Test func performanceRecordsArePooledAcrossHosts() throws {
        var a = PerfRecord()
        a.n = 3
        a.toolLnSum = 1.5
        var b = PerfRecord()
        b.n = 4
        b.toolLnSum = 2.25
        let key = PerfRecordKey(day: "2026-11-02", source: .codex, model: "gpt-5.5")
        let other = PerfRecordKey(day: "2026-11-01", source: .codex, model: "gpt-5.5")
        let local = [PerfDayRecord(key: key, record: a)]
        #expect(RemoteHistoryMerge.perf(local: local, remote: []) == local)
        let pooled = RemoteHistoryMerge.perf(local: local, remote: [PerfDayRecord(key: key, record: b),
                                                                     PerfDayRecord(key: other, record: b)])
        #expect(pooled.map(\.key) == [other, key])
        #expect(pooled[1].record.n == 7 && pooled[1].record.toolLnSum == 3.75)
    }
}

/// The history service with the remote hosts' sync (REMOTE 8.2, 8.3).
@MainActor
@Suite("Remote history service", .serialized)
struct RemoteHistoryServiceTests {
    typealias Lines = RemoteHistoryLines

    private final class Requests: @unchecked Sendable {
        private let lock = NSLock()
        private var items: [(host: String, digests: [String: String])] = []
        func add(_ host: String, _ digests: [String: String]) { lock.withLock { items.append((host, digests)) } }
        var all: [(host: String, digests: [String: String])] { lock.withLock { items } }
    }

    private func makeService(_ directory: TempDirectory, store: HistoryStore, requests: Requests) -> HistoryService {
        let configuration = MonitorConfiguration(enabledSources: [.codex], codexRoot: directory.file("codex/sessions"))
        return HistoryService(store: store, configuration: configuration, storeURL: directory.file("history.sqlite"),
                              remote: RemoteHistorySync { requests.add($0, $1) })
    }

    private func ready(_ store: HistoryStore) async -> Bool {
        await LiveMonitorTests.waitUntil { if case .ready = store.state { true } else { false } }
    }

    @Test func syncedDaysArePublishedWithTheLocalOnes() async throws {
        let directory = try TempDirectory()
        let store = HistoryStore()
        let requests = Requests()
        let service = makeService(directory, store: store, requests: requests)
        // Events before the service starts wait for it.
        service.remote.setHosts([(id: Lines.host, name: "devbox")])
        service.remote.receive(Lines.status(.connected))
        service.remote.receive(Lines.due())
        service.start()
        defer { service.stop() }
        #expect(await ready(store))
        #expect(await LiveMonitorTests.waitUntil { requests.all.count == 1 })
        #expect(requests.all.first?.host == Lines.host && requests.all.first?.digests == [:])

        for message in [Lines.day("2026-11-02"), Lines.perf("2026-11-02"), Lines.day("2026-11-02", .gemini), Lines.done] {
            service.remote.receive(Lines.event(message))
        }
        #expect(await LiveMonitorTests.waitUntil { store.days.contains { $0.host == Lines.host } })
        #expect(store.days.map(\.id) == ["2026-11-02/codex/\(Lines.host)"], "only enabled sources")
        #expect(store.hostNames == [Lines.host: "devbox"])
        #expect(await LiveMonitorTests.waitUntil { store.perfRecords.map(\.model) == ["gpt-5.5"] })

        service.remote.receive(Lines.due())
        #expect(await LiveMonitorTests.waitUntil { requests.all.count == 2 })
        #expect(requests.all.last?.digests == ["2026-11-02/codex": "aaaaaaaaaaaaaaa2", "2026-11-02/gemini": "aaaaaaaaaaaaaaa2"])

        service.remote.setHosts([(id: Lines.host, name: "build")])
        #expect(await LiveMonitorTests.waitUntil { store.hostNames == [Lines.host: "build"] })
    }

    @Test func clearHistoryClearsTheHostsRowsAndAsksAgain() async throws {
        let directory = try TempDirectory()
        let store = HistoryStore()
        let requests = Requests()
        let service = makeService(directory, store: store, requests: requests)
        service.start()
        defer { service.stop() }
        #expect(await ready(store))
        for event in [Lines.status(.connected), Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.done)] {
            service.remote.receive(event)
        }
        #expect(await LiveMonitorTests.waitUntil { store.days.contains { $0.host == Lines.host } })

        store.clear()
        #expect(await LiveMonitorTests.waitUntil { !store.days.contains { $0.host == Lines.host } })
        #expect(await LiveMonitorTests.waitUntil { requests.all.contains { $0.host == Lines.host && $0.digests.isEmpty } })
        let database = try HistoryDatabase(path: directory.file("history.sqlite").path)
        #expect(try database.remoteDayRows().isEmpty)
    }

    @Test func deletingAHostsHistoryRemovesItsRows() async throws {
        let directory = try TempDirectory()
        let store = HistoryStore()
        let service = makeService(directory, store: store, requests: Requests())
        service.start()
        defer { service.stop() }
        #expect(await ready(store))
        for event in [Lines.event(Lines.day("2026-11-02")), Lines.event(Lines.done),
                      Lines.event(Lines.day("2026-11-02"), host: Lines.otherHost), Lines.event(Lines.done, host: Lines.otherHost)] {
            service.remote.receive(event)
        }
        #expect(await LiveMonitorTests.waitUntil { store.days.count == 2 })
        service.remote.deleteHistory(ofHost: Lines.host)
        #expect(await LiveMonitorTests.waitUntil { store.days.map(\.host) == [Lines.otherHost] })
    }
}
