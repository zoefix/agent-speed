import Foundation
import Testing
@testable import AgentSpeedCore

/// The shared Devin CLI fixtures (`spec/fixtures/devin`, `devin-expected.json`, METRICS 4.17) through the live source
/// and the daily history.
@Suite("Devin fixtures")
struct DevinFixtureTests {
    static var expected: [String: Any] { (try? Fixtures.json("devin-expected.json")) ?? [:] }

    /// A copy of `spec/fixtures/devin`.
    final class Copy {
        let directory: TempDirectory
        var root: String { directory.path }

        init() throws {
            directory = try TempDirectory()
            try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent("devin"),
                                             to: directory.file("devin"))
        }

        func path(_ relative: String) -> String { directory.file(relative).path }
        func relative(_ path: String) -> String { String(path.dropFirst(root.count + 1)) }

        func roots(_ scenario: [String: Any]) -> [DevinRoot] {
            (scenario["locations"] as? [String] ?? []).map { .location(path($0)) }
        }

        var createdFiles: [String] {
            let names = FileManager.default.enumerator(atPath: root)?.allObjects as? [String] ?? []
            return names.filter { $0.hasSuffix("-wal") || $0.hasSuffix("-shm") || $0.hasSuffix("-journal") }
        }
    }

    static func scenario(_ name: String) throws -> [String: Any] {
        let scenarios = try #require(expected["scenarios"] as? [String: Any])
        return try #require(scenarios[name] as? [String: Any])
    }

    static func compare(_ record: ResponseRecord, _ wanted: [String: Any], agent: String, copy: Copy) {
        let label = record.key
        #expect(copy.relative(record.agentId) == agent, "\(label) agent")
        #expect(record.startMs == wanted.int64("startMs"), "\(label) start")
        #expect(record.endMs == wanted.int64("endMs"), "\(label) end")
        #expect(record.firstMs == wanted.int64("firstMs"), "\(label) first")
        #expect(record.usage.input == wanted.int("input"), "\(label) input")
        #expect(record.usage.cachedInput == wanted.int("cachedInput"), "\(label) cached")
        #expect(record.usage.cacheWriteInput == wanted.int("cacheWriteInput"), "\(label) cache write")
        #expect(record.usage.cacheWrite1hInput == wanted.int("cacheWrite1hInput"), "\(label) 1h")
        #expect(record.usage.output == wanted.int("output"), "\(label) output")
        #expect(record.usage.reasoning == wanted.int("reasoning"), "\(label) reasoning")
        #expect(record.model == wanted.string("model"), "\(label) model")
        #expect(record.provider == wanted.string("provider"), "\(label) provider")
        #expect(record.isFinal == wanted["final"] as? Bool && record.fast == wanted["fast"] as? Bool, "\(label) flags")
        #expect(record.requestCount == 1 && record.source == .devin, "\(label) source")
    }

    @Test(arguments: ["one", "two"])
    func theLiveSourceReadsEveryScenario(_ name: String) throws {
        let scenario = try Self.scenario(name)
        let copy = try Copy()
        let source = DevinSource(roots: copy.roots(scenario))
        var deduper = Deduper()
        // A start of day before every fixture row: the startup window covers them all.
        let update = source.seed(todayStartMs: 0, deduper: &deduper)
        #expect(source.detected)
        let agents = try #require(scenario["agents"] as? [String: [String: Any]])
        var latest: [String: ResponseRecord] = [:]
        var order: [String] = []
        for entry in update.upserts {
            #expect(entry.seeded, "startup responses raise no live event")
            if latest.updateValue(entry.upsert.record, forKey: entry.upsert.record.key) == nil { order.append(entry.upsert.record.key) }
        }
        let byAgent = Dictionary(grouping: order.compactMap { latest[$0] }, by: { copy.relative($0.agentId) })
        #expect(Set(byAgent.keys) == Set(agents.keys), "\(name) agents with responses")
        let states = source.agentStates
        for (agent, wanted) in agents {
            let records = byAgent[agent] ?? []
            let responses = try #require(wanted["responses"] as? [[String: Any]])
            #expect(records.map(\.key) == responses.compactMap { $0.string("key") }, "\(agent) keys")
            for (record, want) in zip(records, responses) { Self.compare(record, want, agent: agent, copy: copy) }
            let state = try #require(states[copy.path(agent)], "\(agent) state")
            #expect(state.project == wanted.string("project"), "\(agent) project")
            #expect(state.cwdPath == wanted.string("cwdPath"), "\(agent) cwd")
            #expect(state.model == wanted.string("model"), "\(agent) model")
            #expect(state.phase.rawValue == wanted.string("phase"), "\(agent) phase")
            #expect(state.inFlightStartMs == wanted.int64("inFlightStartMs"), "\(agent) in flight")
            #expect(state.lastLineMs == wanted.int64("lastLineMs"), "\(agent) last line")
            #expect(state.malformedLines == wanted.int("malformedLines"), "\(agent) malformed")
            #expect(update.agents[copy.path(agent)] != nil, "\(agent) handed to the engine")
        }
        #expect(copy.createdFiles.isEmpty, "reading creates no -wal, -shm or journal")
    }

    @Test func locationsResolveToTheCliDatabaseOnly() throws {
        let copy = try Copy()
        let resolve = try #require(Self.expected["resolve"] as? [String: Any])
        for (location, wanted) in resolve {
            #expect(DevinRoot.location(copy.path(location)).database.map(copy.relative) == wanted as? String, "\(location)")
        }
        for name in ["one", "two"] {
            let scenario = try Self.scenario(name)
            #expect(DevinPaths.databases(copy.roots(scenario)).map(copy.relative) == scenario["databases"] as? [String])
        }
    }

    @Test(arguments: ["one", "two"])
    func theHistoryMatchesTheScenarioDays(_ name: String) throws {
        let scenario = try Self.scenario(name)
        let copy = try Copy()
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        var configuration = MonitorConfiguration(enabledSources: [.devin])
        configuration.locations[.devin] = (scenario["locations"] as? [String] ?? []).map { .path(copy.path($0)) }
        let first = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: HistoryTests.utc))
        let days = analyzer.days(sources: [.devin])
        let wanted = try #require(scenario["history"] as? [String: [String: Any]])
        #expect(days.map(\.day) == wanted.keys.sorted())
        for day in days {
            let want = try #require(wanted[day.day])
            #expect(day.totals.requests == want.int("requests"), "\(day.day) requests")
            #expect(day.totals.input == want.int("input") && day.totals.output == want.int("output"), "\(day.day) tokens")
            #expect(day.totals.cachedInput == want.int("cachedInput"), "\(day.day) cached")
            #expect(day.totals.cacheWriteInput == want.int("cacheWriteInput"), "\(day.day) cache write")
            #expect(day.measuredOutput == want.int("measuredOutput") && day.measuredMs == want.int64("measuredMs"),
                    "\(day.day) measured")
            #expect(day.agents == want.int("agents"), "\(day.day) agents")
            let models = try #require(want["models"] as? [String: [String: Any]])
            #expect(Dictionary(uniqueKeysWithValues: day.models.map { ($0.model, $0.requests) })
                == models.mapValues { $0.int("requests") }, "\(day.day) models")
        }
        #expect(first.sessionsAnalyzed > 0)
        let second = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: HistoryTests.utc))
        #expect(second.sessionsAnalyzed == 0, "nothing changed: nothing is read again")
        #expect(copy.createdFiles.isEmpty)
    }

    /// The second database added to a stored history: its copy of a session adds nothing, its own session counts.
    @Test func addingASecondDatabaseAddsOnlyItsOwnSessions() throws {
        let copy = try Copy()
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        var configuration = MonitorConfiguration(enabledSources: [.devin])
        let options = HistoryAnalyzer.Options(timeZone: HistoryTests.utc)
        for name in ["one", "two"] {
            let scenario = try Self.scenario(name)
            configuration.locations[.devin] = (scenario["locations"] as? [String] ?? []).map { .path(copy.path($0)) }
            _ = try analyzer.run(configuration: configuration, options: options)
            let wanted = try #require((scenario["history"] as? [String: [String: Any]])?["2026-10-20"])
            let day = try #require(analyzer.days(sources: [.devin]).first)
            #expect(day.totals.requests == wanted.int("requests") && day.totals.input == wanted.int("input"), "\(name)")
        }
    }
}
