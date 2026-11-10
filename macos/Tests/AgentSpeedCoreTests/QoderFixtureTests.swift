import Foundation
import Testing
@testable import AgentSpeedCore

/// The shared Qoder fixtures (`spec/fixtures/qoder`, `qoder-expected.json`, METRICS 4.18; `spec/fixtures/qoder-cli`,
/// METRICS 4.19) through the live source, the history and the Qoder CLI parser.
@Suite("Qoder fixtures")
struct QoderFixtureTests {
    static var expected: [String: Any] { (try? Fixtures.json("qoder-expected.json")) ?? [:] }

    /// A copy of `spec/fixtures/qoder`, whose paths read as `qoder/…` relative to the copy.
    final class Copy {
        let directory: TempDirectory
        var root: String { directory.path }

        init() throws {
            directory = try TempDirectory()
            try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent("qoder"), to: directory.file("qoder"))
        }

        func path(_ relative: String) -> String { directory.file(relative).path }
        func relative(_ path: String) -> String { String(path.dropFirst(root.count + 1)) }

        /// `@auto` is the automatic entry with the scenario's `home` as the macOS home folder.
        func roots(_ scenario: [String: Any]) -> [QoderRoot] {
            (scenario["locations"] as? [String] ?? []).flatMap { location -> [QoderRoot] in
                guard location == "@auto" else { return [.location(path(location))] }
                return QoderPaths.automaticRoots(home: path(scenario.string("home") ?? ""))
            }
        }

        func configuration(_ scenario: [String: Any]) -> MonitorConfiguration {
            var configuration = MonitorConfiguration(enabledSources: [.qoder])
            configuration.locations[.qoder] = (scenario["locations"] as? [String] ?? []).map {
                $0 == "@auto" ? .automatic : .path(path($0))
            }
            configuration.qoderAutomaticRoots = QoderPaths.automaticRoots(home: path(scenario.string("home") ?? "qoder/home"))
            return configuration
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

    static func compare(_ record: ResponseRecord, _ wanted: [String: Any], copy: Copy) {
        let label = record.key
        #expect(copy.relative(record.agentId) == wanted.string("agentId"), "\(label) agent")
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
        #expect(record.requestCount == 1 && record.source == .qoder, "\(label) source")
    }

    // MARK: - Qoder IDE

    /// The scenarios read whole (`one`, `two`) and the startup read of a day (`startup`).
    @Test(arguments: ["one", "two", "startup"])
    func theLiveSourceReadsEveryScenario(_ name: String) throws {
        let scenario = try Self.scenario(name)
        let copy = try Copy()
        let source = QoderSource(roots: copy.roots(scenario))
        var deduper = Deduper()
        // Without `todayStartMs`, a start of day before every fixture row: the startup window covers them all.
        let update = source.seed(todayStartMs: scenario.int64("todayStartMs") ?? 0, deduper: &deduper)
        #expect(source.detected)
        #expect(source.hasUnsupported == !(scenario["unsupported"] as? [String] ?? []).isEmpty, "\(name) unsupported")
        let agents = try #require(scenario["agents"] as? [String: [String: Any]])
        var latest: [String: ResponseRecord] = [:]
        var order: [String] = []
        for entry in update.upserts {
            #expect(entry.seeded, "startup responses raise no live event")
            if latest.updateValue(entry.upsert.record, forKey: entry.upsert.record.key) == nil { order.append(entry.upsert.record.key) }
        }
        let byAgent = Dictionary(grouping: order.compactMap { latest[$0] }, by: { copy.relative($0.agentId) })
        let states = source.agentStates
        #expect(Set(states.keys.map(copy.relative)) == Set(agents.keys), "\(name) agents")
        let parents = source.parentLinks
        for (agent, wanted) in agents {
            let records = byAgent[agent] ?? []
            let responses = try #require(wanted["responses"] as? [[String: Any]])
            #expect(records.map(\.key) == responses.compactMap { $0.string("key") }, "\(agent) keys")
            for (record, want) in zip(records, responses) { Self.compare(record, want, copy: copy) }
            let state = try #require(states[copy.path(agent)], "\(agent) state")
            #expect(state.source == .qoder)
            #expect(state.project == wanted.string("project"), "\(agent) project")
            #expect(state.cwdPath == wanted.string("cwdPath"), "\(agent) cwd")
            #expect(state.model == wanted.string("model"), "\(agent) model")
            #expect(state.isSubagent == wanted["isSubagent"] as? Bool, "\(agent) sub-agent")
            #expect(parents[copy.path(agent)].map(copy.relative) == wanted.string("parentId"), "\(agent) parent")
            #expect(state.phase.rawValue == wanted.string("phase"), "\(agent) phase")
            #expect(state.inFlightStartMs == wanted.int64("inFlightStartMs"), "\(agent) in flight")
            #expect(state.lastLineMs == wanted.int64("lastLineMs"), "\(agent) last line")
            #expect(state.malformedLines == wanted.int("malformedLines"), "\(agent) malformed")
            #expect(update.agents[copy.path(agent)] != nil, "\(agent) handed to the engine")
        }
        #expect(source.poll(deduper: &deduper).isEmpty, "nothing changed: nothing is read again")
        #expect(copy.createdFiles.isEmpty, "reading creates no -wal, -shm or journal")
    }

    @Test func locationsResolveToTheDatabaseOfEachLayout() throws {
        let copy = try Copy()
        let resolve = try #require(Self.expected["resolve"] as? [String: Any])
        for (location, wanted) in resolve {
            #expect(QoderRoot.location(copy.path(location)).database.map(copy.relative) == wanted as? String, "\(location)")
        }
        for name in ["one", "two", "startup"] {
            let scenario = try Self.scenario(name)
            #expect(QoderPaths.databases(copy.roots(scenario)).map(copy.relative) == scenario["databases"] as? [String], "\(name)")
            #expect(QoderPaths.databases(copy.configuration(scenario).resolvedQoderRoots).map(copy.relative)
                == scenario["databases"] as? [String], "\(name) through the location list")
        }
    }

    @Test func theAutomaticEntryNamesBothEditionsOnMacOS() throws {
        let cases = try #require(Self.expected["automatic"] as? [[String: Any]])
        let macos = cases.filter { $0.string("platform") == "macos" }
        #expect(macos.count == 2)
        for item in macos {
            #expect(QoderPaths.automaticDatabases(home: try #require(item.string("home"))) == item["databases"] as? [String])
        }
    }

    /// The history identities (row count, highest rowid) and the UTC day totals of the scenarios read whole.
    @Test(arguments: ["one", "two"])
    func theHistoryMatchesTheScenario(_ name: String) throws {
        let scenario = try Self.scenario(name)
        let copy = try Copy()
        let planner = QoderHistoryPlanner()
        let entries = planner.plan(roots: copy.roots(scenario)) { _, _, _ in false }
        let sessions = try #require(scenario["sessions"] as? [String: [String: Any]])
        #expect(Set(entries.map { copy.relative($0.key) }) == Set(sessions.keys), "\(name) sessions")
        for entry in entries {
            let want = try #require(sessions[copy.relative(entry.key)])
            #expect(entry.size == want.int64("rows") && entry.modified == want.int64("highestRowid"), "\(entry.key)")
        }

        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        let configuration = copy.configuration(scenario)
        let options = HistoryAnalyzer.Options(timeZone: HistoryTests.utc)
        let first = try analyzer.run(configuration: configuration, options: options)
        let days = analyzer.days(sources: [.qoder])
        let wanted = try #require(scenario["history"] as? [String: [String: Any]])
        #expect(days.map(\.day) == wanted.keys.sorted())
        for day in days {
            let want = try #require(wanted[day.day])
            #expect(day.totals.requests == want.int("requests"), "\(day.day) requests")
            #expect(day.totals.input == want.int("input") && day.totals.output == want.int("output"), "\(day.day) tokens")
            #expect(day.totals.cachedInput == want.int("cachedInput"), "\(day.day) cached")
            #expect(day.totals.cacheWriteInput == want.int("cacheWriteInput"), "\(day.day) cache write")
            #expect(day.totals.reasoning == want.int("reasoning"), "\(day.day) reasoning")
            #expect(day.measuredOutput == want.int("measuredOutput") && day.measuredMs == want.int64("measuredMs"),
                    "\(day.day) measured")
            #expect(day.agents == want.int("agents"), "\(day.day) agents")
            let models = try #require(want["models"] as? [String: [String: Any]])
            #expect(Dictionary(uniqueKeysWithValues: day.models.map { ($0.model, $0.requests) })
                == models.mapValues { $0.int("requests") }, "\(day.day) model requests")
            #expect(Dictionary(uniqueKeysWithValues: day.models.map { ($0.model, $0.output) })
                == models.mapValues { $0.int("output") }, "\(day.day) model output")
        }
        #expect(first.sessionsAnalyzed == sessions.count)
        let second = try analyzer.run(configuration: configuration, options: options)
        #expect(second.sessionsAnalyzed == 0, "nothing changed: nothing is read again")
        #expect(copy.createdFiles.isEmpty)
    }

    /// The portable database added to a stored history: its copy of `aurora` and the call the first database owns add
    /// nothing, its own session counts.
    @Test func addingADatabaseAddsOnlyItsOwnSessions() throws {
        let copy = try Copy()
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        let options = HistoryAnalyzer.Options(timeZone: HistoryTests.utc)
        for name in ["one", "two"] {
            let scenario = try Self.scenario(name)
            _ = try analyzer.run(configuration: copy.configuration(scenario), options: options)
            let wanted = try #require(scenario["history"] as? [String: [String: Any]])
            let days = analyzer.days(sources: [.qoder])
            #expect(days.map(\.totals.requests) == wanted.keys.sorted().map { wanted[$0]!.int("requests") }, "\(name)")
            #expect(days.map(\.totals.output) == wanted.keys.sorted().map { wanted[$0]!.int("output") }, "\(name)")
        }
    }

    // MARK: - Qoder CLI

    static var cliExpected: [String: Any] { (expected["cli"] as? [String: Any]) ?? [:] }

    static var cliFixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["qoderCli"] as? [String: Any]) }
    }

    @Test func cliRootsFollowBothEditionsConfigFolders() throws {
        let cases = try #require(Self.cliExpected["automatic"] as? [[String: Any]])
        let posix = cases.filter { $0.string("platform") != "windows" }
        #expect(posix.count == 4)
        for item in posix {
            let environment = item["env"] as? [String: String] ?? [:]
            #expect(QoderPaths.cliRoots(environment: environment, home: try #require(item.string("home")))
                == item["roots"] as? [String], "\(environment)")
        }
        let locations = try #require(Self.cliExpected["locations"] as? [String: String])
        for (location, wanted) in locations {
            #expect(QoderPaths.cliProjectsFolder(URL(fileURLWithPath: location)).path == wanted, "\(location)")
            #expect(MonitorConfiguration.userRoot(location, family: .qoderCli).path == wanted, "\(location) as a user location")
        }
    }

    @Test func cliDiscoveryFindsTheTranscriptsUnderProjects() throws {
        let wanted = try #require(try Self.cliFixtures["discover"] as? [String])
        let root = QoderPaths.cliProjectsFolder(Fixtures.root.appendingPathComponent("qoder-cli", isDirectory: true))
        var index = DirectoryIndex(root: root.path, names: TranscriptFamily.qoderCli.names)
        let prefix = Fixtures.root.path + "/"
        #expect(index.scan().map { String($0.dropFirst(prefix.count)) }.sorted() == wanted)
    }

    @Test func cliFixturesMatchTheReference() throws {
        let (expected, results) = try ParserFixtureTests.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        let qoder = files.filter { $0.key.hasPrefix("qoder-cli/") }
        #expect(qoder.count == 5)
        for (relative, spec) in qoder {
            let summary = OmpTests.summary(try #require(results[relative]))
            for (field, wanted) in spec where !["parentFile", "effort", "speed", "responseModes"].contains(field) {
                let actual = summary[field] ?? NSNull()
                #expect((actual as AnyObject).isEqual(wanted), "\(relative) \(field): \(actual)")
            }
            let state = try #require(results[relative]).state
            #expect(state.modes.facts.isEmpty, "\(relative): no execution modes")
            #expect(try #require(results[relative]).responses.allSatisfy { $0.modes.facts.isEmpty && $0.source == .qoderCli })
        }
    }

    @Test func cliTracesMatchPhaseAfterEveryCheckpoint() throws {
        let traces = try #require(try Self.cliFixtures["traces"] as? [[String: Any]])
        #expect(traces.count == 12)
        for trace in traces {
            let file = try #require(trace.string("file"))
            let afterLine = trace.int("afterLine")
            var deduper = try OmpTests.deduper(before: file)
            var parser = ParserFixtureTests.parser(for: file, fileID: 999)
            try GeminiTests.feed(file, into: &parser, deduper: &deduper, lines: afterLine)
            #expect(parser.state.phase.rawValue == trace.string("phase"), "\(file) line \(afterLine) phase")
            #expect(parser.state.inFlightStartMs == trace.int64("inFlightStartMs"), "\(file) line \(afterLine) inFlightStartMs")
        }
    }

    /// `cli.history`: the UTC day totals of the Qoder CLI transcripts, through the history analysis.
    @Test func cliHistoryMatchesTheDayTotals() throws {
        let wanted = try #require(Self.cliExpected["history"] as? [String: [String: Any]])
        let (directory, copy) = try HistoryTests.fixtureCopy()
        var configuration = MonitorConfiguration(enabledSources: [.qoderCli])
        configuration.locations[.qoderCli] = [.path(copy.appendingPathComponent("qoder-cli").path)]
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        _ = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: HistoryTests.utc))
        let days = analyzer.days(sources: [.qoderCli])
        #expect(days.map(\.day) == wanted.keys.sorted())
        for day in days {
            let want = try #require(wanted[day.day])
            #expect(day.totals.requests == want.int("requests") && day.totals.input == want.int("input"), "\(day.day)")
            #expect(day.totals.cachedInput == want.int("cachedInput") && day.totals.cacheWriteInput == want.int("cacheWriteInput"))
            #expect(day.totals.output == want.int("output") && day.totals.reasoning == want.int("reasoning"))
            #expect(day.measuredOutput == want.int("measuredOutput") && day.measuredMs == want.int64("measuredMs"))
            #expect(day.agents == want.int("agents"))
            let models = try #require(want["models"] as? [String: [String: Any]])
            for model in day.models {
                let modelWant = try #require(models[model.model], "\(model.model)")
                #expect(model.requests == modelWant.int("requests") && model.output == modelWant.int("output"))
                #expect(model.measuredOutput == modelWant.int("measuredOutput") && model.measuredMs == modelWant.int64("measuredMs"))
            }
            #expect(day.models.count == models.count)
        }
        _ = directory
    }
}
