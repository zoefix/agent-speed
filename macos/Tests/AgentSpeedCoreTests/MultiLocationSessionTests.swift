import Foundation
import Testing
@testable import AgentSpeedCore

/// METRICS 2.1 in the live session and the daily history: several locations of one source are read together, a file
/// or database reachable twice counts once, and every database keeps its own identity (issue #45).
@Suite("Several locations per source")
struct MultiLocationSessionTests {
    static let utc = TimeZone(identifier: "UTC")!
    static let fixtureDay = Clock.milliseconds(Calendar.current.startOfDay(for: Date(timeIntervalSince1970: 1_792_490_400)))
    static let codexDay = "codex/sessions/2026/10/20/"

    /// Copies Codex fixture rollouts into `folder/2026/10/20/`, dated just before now so they are read at startup.
    private static func copyCodex(_ names: [String], to folder: URL) throws {
        let day = folder.appendingPathComponent("2026/10/20", isDirectory: true)
        try FileManager.default.createDirectory(at: day, withIntermediateDirectories: true)
        for (index, name) in names.enumerated() {
            let target = day.appendingPathComponent(name)
            try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent(codexDay + name), to: target)
            try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-60 + Double(index))],
                                                  ofItemAtPath: target.path)
        }
    }

    private static func codex(_ list: [DataLocation]) -> MonitorConfiguration {
        var configuration = MonitorConfiguration(enabledSources: [.codex])
        configuration.locations[.codex] = list
        configuration.openclawHome = OpenClawHome(environment: ["OPENCLAW_STATE_DIR": "/nonexistent/claw"], home: URL(fileURLWithPath: "/u"))
        return configuration
    }

    private static let modern = "rollout-2026-10-20T10-00-00-modern.jsonl"
    private static let legacy = "rollout-2026-10-20T10-01-00-legacy.jsonl"

    // MARK: - Files

    @Test func twoCodexFoldersAreReadTogether() throws {
        let directory = try TempDirectory()
        let official = directory.file("official/sessions"), company = directory.file("company/sessions")
        try Self.copyCodex([Self.modern], to: official)
        try Self.copyCodex([Self.legacy], to: company)
        let session = TranscriptSession(configuration: Self.codex([.path(official.path), .path(company.path)]))
        let summary = session.seed(todayStartMs: Self.fixtureDay)
        #expect(summary.filesDiscovered == 2)
        let keys = Set(session.responses.map(\.key))
        #expect(keys.isSuperset(of: ["resp-1", "codex-legacy:thread-legacy:20213"]), "both folders' responses")
        #expect(session.sessionCounts(todayStartMs: Self.fixtureDay)[.codex] == 2)
        let context = SnapshotContext(session: session, todayStartMs: Self.fixtureDay)
        #expect(context.sources[.codex]?.available == true)
        #expect(context.sources[.codex]?.rootPath == official.path, "the row's path is the first detected root")
    }

    @Test func theFirstDetectedRootNamesTheSourceAndAnyRootDetectsIt() throws {
        let directory = try TempDirectory()
        let company = directory.file("company/sessions")
        try Self.copyCodex([Self.legacy], to: company)
        let session = TranscriptSession(configuration: Self.codex([.path(directory.file("missing").path), .path(company.path)]))
        _ = session.seed(todayStartMs: Self.fixtureDay)
        let context = SnapshotContext(session: session, todayStartMs: Self.fixtureDay)
        #expect(context.sources[.codex]?.available == true)
        #expect(context.sources[.codex]?.rootPath == company.path)
        #expect(context.sources[.codex]?.sessionCount == 1)
    }

    @Test func aFileUnderTwoNestedRootsIsReadOnce() throws {
        let directory = try TempDirectory()
        let root = directory.file("codex/sessions")
        try Self.copyCodex([Self.modern, Self.legacy], to: root)
        let nested = root.appendingPathComponent("2026/10")
        let session = TranscriptSession(configuration: Self.codex([.path(root.path), .path(nested.path)]))
        let summary = session.seed(todayStartMs: Self.fixtureDay)
        #expect(summary.filesDiscovered == 2)
        #expect(session.sessionCounts(todayStartMs: Self.fixtureDay)[.codex] == 2)
        #expect(Set(session.responses.map(\.agentId)).allSatisfy { $0.hasPrefix(root.path + "/") })
        #expect(!session.rediscover(todayStartMs: Self.fixtureDay), "the nested root finds nothing new")
    }

    @Test func aRootThatResolvesToAnEarlierRootIsReadOnce() throws {
        let directory = try TempDirectory()
        let root = directory.file("codex/sessions")
        try Self.copyCodex([Self.modern], to: root)
        try FileManager.default.createSymbolicLink(at: directory.file("link"), withDestinationURL: root)
        let session = TranscriptSession(configuration: Self.codex([.path(root.path), .path(directory.file("link").path)]))
        let summary = session.seed(todayStartMs: Self.fixtureDay)
        #expect(summary.filesDiscovered == 1)
        #expect(session.sessionCounts(todayStartMs: Self.fixtureDay)[.codex] == 1)
        #expect(session.watchedPaths.count == 1)
    }

    // MARK: - Databases

    /// A copy of the OpenCode fixture database whose ids carry `suffix`, so it holds other sessions and rows.
    private static func opencodeCopy(in folder: URL, suffix: String? = nil) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent("opencode.db")
        try FileManager.default.copyItem(at: OpencodeTests.fixtureRoot.appendingPathComponent("opencode.db"), to: target)
        guard let suffix else { return }
        let writer = try SQLiteConnection(path: target.path, mode: .readWriteCreate)
        try writer.execute("UPDATE session_v2 SET id = id || '\(suffix)', parent_id = parent_id || '\(suffix)'")
        try writer.execute("UPDATE session_message SET id = id || '\(suffix)', session_id = session_id || '\(suffix)'")
        writer.close()
    }

    @Test func everyOpencodeDatabaseIsRead() throws {
        let directory = try TempDirectory()
        try Self.opencodeCopy(in: directory.file("a"))
        try Self.opencodeCopy(in: directory.file("b"), suffix: "_b")
        var configuration = MonitorConfiguration(enabledSources: [.opencode])
        configuration.locations[.opencode] = [.path(directory.file("a").path), .path(directory.file("b/opencode.db").path)]
        let session = TranscriptSession(configuration: configuration)
        let summary = session.seed(todayStartMs: 0)
        #expect(summary.filesDiscovered == 2)
        #expect(Set(session.responses.map(\.key)) == ["msg_oc1", "msg_oc2", "cmp_1", "msg_oc3",
                                                    "msg_oc1_b", "msg_oc2_b", "cmp_1_b", "msg_oc3_b"])
        let (output, _) = session.advance(nowMs: 1_792_490_750_000, todayStartMs: 0, emitsEvents: false)
        let agents = SnapshotBuilder.running(output, context: SnapshotContext(session: session, todayStartMs: 0)).agents
        #expect(agents.first { $0.id == "opencode:ses_sub_b" }?.parentId == "opencode:ses_main_b")
        #expect(SnapshotContext(session: session, todayStartMs: 0).sources[.opencode]?.sessionCount == 4)
        #expect(session.watchedPaths.count == 2)
    }

    @Test func aSessionAnotherOpencodeDatabaseProvidesIsSkipped() throws {
        let directory = try TempDirectory()
        try Self.opencodeCopy(in: directory.file("live"))
        try Self.opencodeCopy(in: directory.file("backup"))
        var configuration = MonitorConfiguration(enabledSources: [.opencode])
        configuration.locations[.opencode] = [.path(directory.file("live").path), .path(directory.file("backup").path)]
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: 0)
        #expect(session.responses.count == 4)
        #expect(SnapshotContext(session: session, todayStartMs: 0).sources[.opencode]?.sessionCount == 2)
        #expect(session.opencode?.sources[1].activeSessionCount(todayStartMs: 0) == 0, "the copy's sessions stay with the first")
        _ = session.rediscover(todayStartMs: 0)
        #expect(SnapshotContext(session: session, todayStartMs: 0).sources[.opencode]?.sessionCount == 2)
    }

    @Test func aSessionAnotherZcodeDatabaseProvidesIsSkipped() throws {
        let directory = try TempDirectory()
        let fixture = Fixtures.root.appendingPathComponent("zcode/cli/db/db.sqlite")
        for name in ["a", "b"] {
            try FileManager.default.createDirectory(at: directory.file(name), withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: fixture, to: directory.file("\(name)/db.sqlite"))
        }
        func session(_ names: [String]) -> TranscriptSession {
            var configuration = MonitorConfiguration(enabledSources: [.zcode])
            configuration.locations[.zcode] = names.map { .path(directory.file($0).path) }
            let session = TranscriptSession(configuration: configuration)
            _ = session.seed(todayStartMs: HarnessFixtureTests.dayStart)
            return session
        }
        let one = session(["a"]), two = session(["a", "b"])
        let count = SnapshotContext(session: one, todayStartMs: HarnessFixtureTests.dayStart).sources[.zcode]?.sessionCount
        #expect(count != nil && count! > 0)
        #expect(SnapshotContext(session: two, todayStartMs: HarnessFixtureTests.dayStart).sources[.zcode]?.sessionCount == count)
        #expect(Set(two.responses.map(\.key)) == Set(one.responses.map(\.key)))
        #expect(two.zcode?.sources[1].activeSessionCount(todayStartMs: HarnessFixtureTests.dayStart) == 0)
        #expect(two.zcode?.sources.map { $0.status } == [.ready, .ready])
    }

    // MARK: - Hermes Agent

    @Test func hermesReadsTheHomeAndAnAddedLogsFolder() throws {
        let home = HermesSessionTests.Home()
        let base = HermesSessionTests.base
        try home.write("logs/agent.log", HermesSessionTests.turn("A", base) + HermesSessionTests.call("A", 1, base + 2000))
        let other = try TempDirectory()
        try other.write("logs/agent.log", HermesSessionTests.turn("B", base) + HermesSessionTests.call("B", 1, base + 3000))
        var configuration = home.configuration
        configuration.locations[.hermes] = [.automatic, .path(other.file("logs").path)]
        let session = TranscriptSession(configuration: configuration, clock: { base + 30_000 })
        _ = session.seed(todayStartMs: base - 3_600_000)
        #expect(session.responses.map(\.key).sorted() == ["hermes:A:1:2026-10-20 12:00:02,000", "hermes:B:1:2026-10-20 12:00:03,000"])
        #expect(session.hermes?.logFolders == [home.path("logs"), other.file("logs").path])
        let context = SnapshotContext(session: session, todayStartMs: base - 3_600_000)
        #expect(context.sources[.hermes]?.rootPath == home.hermes.path)
        #expect(context.sources[.hermes]?.sessionCount == 2)
        _ = other
    }

    // MARK: - Gemini CLI configured effort

    @Test func eachGeminiRootTakesItsOwnSettings() throws {
        let directory = try TempDirectory()
        let settings = #"{"modelConfigs":{"overrides":[{"match":{"model":"gemini-3-pro"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"%@"}}}}]}}"#
        try directory.write("home/.gemini/settings.json", settings.replacingOccurrences(of: "%@", with: "HIGH"))
        try directory.write("work/.gemini/settings.json", settings.replacingOccurrences(of: "%@", with: "LOW"))
        try FileManager.default.createDirectory(at: directory.file("home/.gemini/tmp"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: directory.file("work/.gemini/tmp"), withIntermediateDirectories: true)
        var configuration = MonitorConfiguration(enabledSources: [.gemini])
        configuration.automaticRoots[.gemini] = [directory.file("home/.gemini/tmp")]
        configuration.locations[.gemini] = [.automatic, .path(directory.file("work/.gemini/tmp").path)]
        let roots = configuration.resolvedRoots(for: .gemini)
        let context = AgentModeContext(roots: roots.map { (family: TranscriptFamily.gemini, path: $0) }, geminiSettings: configuration.resolvedGeminiSettings)
        _ = context.refresh()
        func effort(_ root: String) -> String? {
            context.configured(family: .gemini, root: root, model: "gemini-3-pro").facts.first?.value
        }
        #expect(effort(roots[0]) != nil)
        #expect(effort(roots[0]) != effort(roots[1]), "the home's file and the added folder's file differ")
    }

    // MARK: - History

    @Test func historyReadsEveryLocationAndEachDatabaseOnce() throws {
        let directory = try TempDirectory()
        let official = directory.file("official/sessions"), company = directory.file("company/sessions")
        try Self.copyCodex([Self.modern], to: official)
        try Self.copyCodex([Self.legacy], to: company)
        try Self.opencodeCopy(in: directory.file("oc-live"))
        try Self.opencodeCopy(in: directory.file("oc-copy"))
        try Self.opencodeCopy(in: directory.file("oc-other"), suffix: "_b")
        func run(_ codex: [DataLocation], _ opencode: [DataLocation], store: HistoryDatabase) throws -> (HistoryAnalyzer, HistoryAnalyzer.Report) {
            var configuration = Self.codex(codex)
            configuration.enabledSources = [.codex, .opencode]
            configuration.locations[.opencode] = opencode
            let analyzer = try HistoryAnalyzer(database: store)
            let report = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: Self.utc))
            return (analyzer, report)
        }
        func requests(_ analyzer: HistoryAnalyzer, _ source: SourceKind) -> Int {
            analyzer.days(sources: [source]).reduce(0) { $0 + $1.totals.requests }
        }
        let single = try run([.path(official.path)], [.path(directory.file("oc-live").path)], store: try HistoryDatabase.inMemory()).0
        let other = try run([.path(company.path)], [.path(directory.file("oc-other").path)], store: try HistoryDatabase.inMemory()).0
        let store = try HistoryDatabase.inMemory()
        let lists: ([DataLocation], [DataLocation]) = (
            [.path(official.path), .path(company.path)],
            [.path(directory.file("oc-live").path), .path(directory.file("oc-copy").path), .path(directory.file("oc-other").path)]
        )
        let (both, first) = try run(lists.0, lists.1, store: store)
        #expect(first.filesDiscovered == 2 + 3)
        #expect(requests(both, .codex) == requests(single, .codex) + requests(other, .codex))
        #expect(requests(both, .opencode) == requests(single, .opencode) + requests(other, .opencode),
                "the copied database adds nothing")
        let again = try run(lists.0, lists.1, store: store).1
        #expect(again.filesAnalyzed == 0 && again.sessionsAnalyzed == 0, "each database keeps its own identity")
    }
}
