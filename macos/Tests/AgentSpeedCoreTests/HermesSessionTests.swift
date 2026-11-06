import Foundation
import Testing
@testable import AgentSpeedCore

/// Hermes Agent inside the live session and the daily history (METRICS 4.15, 5, 8): a log that grows, rotates, shrinks
/// and loses its oldest file while it is read, older files read from their end, the Codex runtime switch, a first run
/// and a new profile while the app runs, the user setting, session counts and the history cached by file identity.
@Suite("Hermes Agent session")
struct HermesSessionTests {
    /// 2026-10-20 12:00:00 UTC; the tests' wall times are UTC.
    static let base: Int64 = 1_792_497_600_000
    static let utc = TimeZone(identifier: "UTC")!

    static func stamp(_ ms: Int64) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = utc
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss,SSS"
        return formatter.string(from: Date(timeIntervalSince1970: TimeInterval(ms) / 1000))
    }

    static func turn(_ session: String, _ ms: Int64, platform: String = "cli", model: String = "gpt-5.5") -> String {
        "\(stamp(ms)) INFO [\(session)] agent.turn_context: conversation turn: session=\(session) model=\(model) "
            + "provider=openai platform=\(platform) history=0 msg='hello'\n"
    }

    static func call(_ session: String, _ number: Int, _ ms: Int64, output: Int = 100, latency: String = "2.0") -> String {
        "\(stamp(ms)) INFO [\(session)] agent.conversation_loop: API call #\(number): model=gpt-5.5 provider=openai "
            + "in=1000 out=\(output) total=\(1000 + output) latency=\(latency)s cache=800/1000 (80%)\n"
    }

    static func end(_ session: String, _ ms: Int64) -> String {
        "\(stamp(ms)) INFO [\(session)] agent.conversation_loop: Turn ended: reason=text_response(finish_reason=stop) "
            + "model=gpt-5.5 api_calls=1/90 budget=1/90 tool_turns=0 last_msg_role=assistant response_len=42 session=\(session)\n"
    }

    static func tool(_ session: String, _ ms: Int64) -> String {
        "\(stamp(ms)) INFO [\(session)] agent.tool_executor: tool terminal completed (0.10s, 12 chars)\n"
    }

    /// A home folder whose `.hermes` is the automatic Hermes home.
    struct Home {
        let directory = try! TempDirectory()

        var hermes: URL { directory.file(".hermes") }

        var configuration: MonitorConfiguration {
            var configuration = MonitorConfiguration(enabledSources: [.hermes])
            configuration.hermesHome = HermesHome(environment: [:], home: directory.url)
            configuration.hermesTimeZone = HermesSessionTests.utc
            return configuration
        }

        func path(_ relative: String) -> String { hermes.appendingPathComponent(relative).path }

        func write(_ relative: String, _ text: String, modified: Date? = nil) throws {
            try directory.write(".hermes/" + relative, text, modified: modified)
        }

        func append(_ relative: String, _ text: String) throws {
            try directory.append(".hermes/" + relative, text)
        }

        func rename(_ from: String, _ to: String) throws {
            try FileManager.default.moveItem(atPath: path(from), toPath: path(to))
        }

        /// The path the file watcher reports.
        func canonical(_ relative: String) -> String {
            let path = path(relative)
            let folder = (path as NSString).deletingLastPathComponent
            guard let resolved = realpath(folder, nil) else { return path }
            defer { free(resolved) }
            return (String(cString: resolved) as NSString).appendingPathComponent((path as NSString).lastPathComponent)
        }

        func agentId(_ session: String, folder: String = "logs") -> String {
            HermesLogSet.agentId(folder: path(folder), sessionId: session)
        }
    }

    static func agents(_ session: TranscriptSession, at ms: Int64 = base + 60_000) -> [String: EngineAgentStatus] {
        let output = session.advance(nowMs: ms, todayStartMs: base - 3_600_000, emitsEvents: false).output
        return Dictionary(uniqueKeysWithValues: output.agents.map { ($0.agent.id, $0) })
    }

    static var nowMs: Int64 { Clock.milliseconds(Date()) }

    // MARK: - Live reading

    @Test func aGrowingRotatingLogIsFollowedByIdentity() throws {
        let home = Home()
        try home.write("logs/agent.log", Self.turn("A", Self.base) + Self.tool("A", Self.base + 500)
            + Self.call("A", 1, Self.base + 3000))
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 30_000 })
        let summary = session.seed(todayStartMs: Self.base - 3_600_000)
        #expect(summary.filesDiscovered == 1 && summary.filesRead == 1)
        #expect(session.responses.map(\.key) == ["hermes:A:1:2026-10-20 12:00:03,000"])
        let first = try #require(session.responses.first)
        #expect(first.agentId == home.agentId("A") && first.startMs == Self.base + 1000 && first.endMs == Self.base + 3000)
        #expect(first.usage == TokenUsage(input: 1000, cachedInput: 800, output: 100) && first.provider == "openai")
        var agent = try #require(Self.agents(session)[home.agentId("A")])
        #expect(agent.phase == .generating && agent.agent.inFlightStartMs == Self.base + 3000)
        #expect(agent.agent.project == ".hermes" && agent.agent.model == "gpt-5.5" && agent.parentId == nil)
        #expect(session.watchedPaths == [(home.canonical("logs/agent.log") as NSString).deletingLastPathComponent])
        #expect(session.sessionCounts(todayStartMs: Self.base - 3_600_000)[.hermes] == 1)
        let context = SnapshotContext(session: session, todayStartMs: Self.base - 3_600_000)
        #expect(context.sources[.hermes]?.available == true && context.sources[.hermes]?.rootPath == home.hermes.path)

        // The watcher reports the log: the turn ends, the next one starts.
        try home.append("logs/agent.log", Self.end("A", Self.base + 3010) + Self.turn("A", Self.base + 20_000))
        #expect(session.handleChanges(atCanonicalPaths: [home.canonical("logs/agent.log")]))
        agent = try #require(Self.agents(session)[home.agentId("A")])
        #expect(agent.phase == .generating && agent.agent.inFlightStartMs == Self.base + 20_000)

        // Rotation: the live log becomes agent.log.1 and a new agent.log starts; the 1-second fallback notices that the
        // path names another file, follows the renamed one and reads the new one from its start.
        try home.rename("logs/agent.log", "logs/agent.log.1")
        try home.write("logs/agent.log", Self.call("A", 1, Self.base + 24_000, output: 50))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        let (output, events) = session.advance(nowMs: Self.base + 30_000, todayStartMs: Self.base - 3_600_000, emitsEvents: true)
        let completions = events.filter { if case .responseCompleted = $0 { true } else { false } }
        #expect(completions.count == 1, "only the new call is a live completion")
        #expect(session.responses.count == 2 && output.todayBySource[.hermes]?.output == 150)

        // A process that had not reopened yet still writes to the renamed file: those lines are read normally.
        try home.append("logs/agent.log.1", Self.call("B", 1, Self.base + 25_000, output: 7) + Self.end("A", Self.base + 25_100))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        #expect(session.responses.count == 3)
        #expect(Self.agents(session)[home.agentId("A")]?.phase == .idle)

        // A copy of an earlier line (a failed tool quoting the log) only counts as the agent's last line.
        try home.append("logs/agent.log", Self.call("A", 1, Self.base + 3000))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        #expect(session.responses.count == 3)

        // A shrunk file is read again from its start into the same state; the dedupe set keeps the counts.
        let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: home.path("logs/agent.log")))
        try handle.truncate(atOffset: 0)
        try handle.write(contentsOf: Data((Self.call("A", 1, Self.base + 24_000, output: 50) + Self.call("C", 1, Self.base + 28_000, output: 7)).utf8))
        try handle.close()
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        #expect(session.responses.map(\.key).sorted() == ["hermes:A:1:2026-10-20 12:00:03,000", "hermes:A:1:2026-10-20 12:00:24,000",
                                                           "hermes:B:1:2026-10-20 12:00:25,000", "hermes:C:1:2026-10-20 12:00:28,000"])

        // Rotation deletes the oldest file: its agents and responses stay.
        try FileManager.default.removeItem(atPath: home.path("logs/agent.log.1"))
        _ = session.rediscover(todayStartMs: Self.base - 3_600_000)
        #expect(session.responses.count == 4 && Set(Self.agents(session).keys).isSuperset(of: [home.agentId("A"), home.agentId("B")]))
        #expect(session.malformedLines == 0 && session.unreadableFileCount == 0)
    }

    @Test func olderFilesStartAtTheirEndAndPartialLinesWait() throws {
        let home = Home()
        let yesterday = Date(timeIntervalSince1970: TimeInterval(Self.base) / 1000 - 86_400)
        try home.write("logs/agent.log.1", Self.turn("O", Self.base - 86_400_000) + Self.call("O", 1, Self.base - 86_397_000),
                       modified: yesterday)
        try home.write("logs/agent.log", Self.turn("N", Self.base) + Self.call("N", 1, Self.base + 2000))
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 30_000 })
        let summary = session.seed(todayStartMs: Self.base - 3_600_000)
        #expect(summary.filesDiscovered == 2 && summary.filesRead == 1)
        #expect(session.responses.map(\.key) == ["hermes:N:1:2026-10-20 12:00:02,000"])
        #expect(Self.agents(session)[home.agentId("O")] == nil)

        // The older file's new lines are read; a line without its newline waits for it.
        try home.append("logs/agent.log.1", Self.call("O", 2, Self.base + 5000) + String(Self.end("O", Self.base + 6000).dropLast()))
        #expect(session.handleChanges(atCanonicalPaths: [home.canonical("logs/agent.log.1")]))
        #expect(session.responses.count == 2)
        #expect(Self.agents(session)[home.agentId("O")]?.phase == .idle, "no turn start was read for O")
        try home.append("logs/agent.log.1", "\n" + Self.turn("O", Self.base + 7000))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs), "now modified within the last two minutes")
        #expect(Self.agents(session)[home.agentId("O")]?.agent.lastLineMs == Self.base + 7000)
        #expect(Self.agents(session)[home.agentId("O")]?.phase == .generating)
    }

    @Test func theCodexRuntimeSwitchIdlesTurnsUntilItIsTurnedOff() throws {
        let home = Home()
        try home.write("config.yaml", "model:\n  default: gpt-5.5\n  openai_runtime: codex_app_server\n")
        try home.write("logs/agent.log", Self.turn("A", Self.base))
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 30_000 })
        _ = session.seed(todayStartMs: Self.base - 3_600_000)
        var agent = try #require(Self.agents(session)[home.agentId("A")])
        #expect(agent.phase == .idle && agent.agent.inFlightStartMs == nil, "checked before the backlog")

        // Hermes still runs a fallback turn itself: its responses count, without a generating phase.
        try home.append("logs/agent.log", Self.turn("B", Self.base + 1000) + Self.call("B", 1, Self.base + 3000))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        #expect(session.responses.count == 1 && Self.agents(session)[home.agentId("B")]?.phase == .idle)

        // Turned off: nothing changes until the next turn start, which is generating again.
        try home.write("config.yaml", "model:\n  openai_runtime: auto\n")
        _ = session.rediscover(todayStartMs: Self.base - 3_600_000)
        #expect(Self.agents(session)[home.agentId("B")]?.phase == .idle)
        try home.append("logs/agent.log", Self.turn("B", Self.base + 10_000))
        #expect(session.pollRecentFiles(nowMs: Self.nowMs))
        agent = try #require(Self.agents(session)[home.agentId("B")])
        #expect(agent.phase == .generating && agent.agent.inFlightStartMs == Self.base + 10_000)

        // Turned on again (a later modification time): the rediscovery check idles the generating agent at once.
        try home.write("config.yaml", "model:\n  openai_runtime: 'Codex_App_Server'  # /codex-runtime\n",
                       modified: Date().addingTimeInterval(5))
        #expect(session.rediscover(todayStartMs: Self.base - 3_600_000))
        #expect(Self.agents(session)[home.agentId("B")]?.phase == .idle)
    }

    @MainActor
    @Test func theLiveMonitorFollowsAppendsAndRotation() async throws {
        let home = Home()
        let start = Self.nowMs - 10_000
        try home.write("logs/agent.log", Self.turn("L", start) + Self.call("L", 1, start + 2000))
        let store = SpeedStore()
        let monitor = LiveMonitor(store: store, configuration: home.configuration)
        monitor.start()
        defer { monitor.stop() }
        func requests() -> Int { store.snapshot.sources.first { $0.kind == .hermes }?.today.requests ?? -1 }
        #expect(await LiveMonitorTests.waitUntil { store.snapshot.state == .running && requests() == 1 })
        #expect(store.snapshot.agents.first { $0.source == .hermes }?.phase == .generating)

        try home.append("logs/agent.log", Self.call("L", 2, start + 6000) + Self.end("L", start + 6010))
        #expect(await LiveMonitorTests.waitUntil { requests() == 2 })
        #expect(await LiveMonitorTests.waitUntil { store.snapshot.agents.first { $0.source == .hermes }?.phase == .idle })

        try home.rename("logs/agent.log", "logs/agent.log.1")
        try home.write("logs/agent.log", Self.turn("L", start + 8000) + Self.call("L", 1, start + 9000))
        #expect(await LiveMonitorTests.waitUntil { requests() == 3 })
        #expect(store.snapshot.sources.first { $0.kind == .hermes }?.sessionCount == 1)
    }

    // MARK: - Roots

    @Test func aFirstRunAndANewProfileWhileRunning() throws {
        let home = Home()
        try FileManager.default.createDirectory(at: home.hermes, withIntermediateDirectories: true)
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 30_000 })
        #expect(session.hasAvailableRoot, "a home without logs is detected")
        _ = session.seed(todayStartMs: Self.base - 3_600_000)
        var context = SnapshotContext(session: session, todayStartMs: Self.base - 3_600_000)
        #expect(context.sources[.hermes]?.available == true && context.sources[.hermes]?.sessionCount == 0)
        #expect(session.watchedPaths.isEmpty)

        // Hermes runs for the first time: the new log folder is read from its start at the next rediscovery.
        try home.write("logs/agent.log", Self.turn("A", Self.base) + Self.call("A", 1, Self.base + 2000))
        #expect(session.rediscover(todayStartMs: Self.base - 3_600_000))
        #expect(session.responses.count == 1)

        // A new profile, whose sub-agent session is not counted as a session.
        try home.write("profiles/work/logs/agent.log", Self.turn("W", Self.base + 4000) + Self.call("W", 1, Self.base + 6000)
            + Self.turn("S", Self.base + 7000, platform: "subagent") + Self.call("S", 1, Self.base + 9000))
        try home.write("profiles/.trash/logs/agent.log", Self.turn("T", Self.base) + Self.call("T", 1, Self.base + 1000))
        #expect(session.rediscover(todayStartMs: Self.base - 3_600_000))
        #expect(session.responses.count == 3)
        let agents = Self.agents(session)
        #expect(agents[home.agentId("W", folder: "profiles/work/logs")]?.agent.project == "work")
        #expect(agents[home.agentId("S", folder: "profiles/work/logs")]?.agent.isSubagent == true)
        #expect(agents[home.agentId("S", folder: "profiles/work/logs")]?.parentId == nil)
        #expect(session.watchedPaths.count == 2)
        context = SnapshotContext(session: session, todayStartMs: Self.base - 3_600_000)
        #expect(context.sources[.hermes]?.sessionCount == 2, "A and W; S is a sub-agent")
        #expect(SnapshotContext(session: session, todayStartMs: Self.base + 5000).sources[.hermes]?.sessionCount == 0,
                "a session counts on the day of a turn start")

        // The profile is removed: its agents leave, its responses stay.
        try FileManager.default.removeItem(at: home.hermes.appendingPathComponent("profiles/work"))
        #expect(session.rediscover(todayStartMs: Self.base - 3_600_000))
        #expect(Self.agents(session)[home.agentId("W", folder: "profiles/work/logs")] == nil)
        #expect(session.responses.count == 3)
    }

    @Test func theSettingNamesAHomeOrOneLogsFolder() throws {
        let home = Home()
        try home.write("logs/agent.log", Self.turn("A", Self.base) + Self.call("A", 1, Self.base + 2000))
        try home.write("profiles/work/logs/agent.log", Self.turn("W", Self.base) + Self.call("W", 1, Self.base + 3000))
        try home.write("profiles/work/config.yaml", "model:\n  openai_runtime: codex_app_server\n")
        func keys(_ root: URL) -> [String] {
            var configuration = home.configuration
            configuration.setLocation(root, for: .hermes)
            let session = TranscriptSession(configuration: configuration, clock: { Self.base + 30_000 })
            _ = session.seed(todayStartMs: Self.base - 3_600_000)
            return session.responses.map(\.key).sorted()
        }
        #expect(keys(home.hermes) == ["hermes:A:1:2026-10-20 12:00:02,000", "hermes:W:1:2026-10-20 12:00:03,000"])
        #expect(keys(home.hermes.appendingPathComponent("profiles/work/logs")) == ["hermes:W:1:2026-10-20 12:00:03,000"])
        #expect(keys(home.directory.file("nowhere")).isEmpty)

        var configuration = home.configuration
        configuration.setLocation(home.hermes.appendingPathComponent("profiles/work/logs"), for: .hermes)
        #expect(configuration.requiresRescan(comparedTo: home.configuration))
        let session = TranscriptSession(configuration: configuration, clock: { Self.base + 30_000 })
        _ = session.seed(todayStartMs: Self.base - 3_600_000)
        #expect(Self.agents(session)[home.agentId("W", folder: "profiles/work/logs")]?.phase == .idle,
                "the profile's config.yaml applies to its logs folder")
        #expect(SnapshotContext(session: session, todayStartMs: 0).sources[.hermes]?.rootPath
                == home.hermes.appendingPathComponent("profiles/work/logs").path)
    }

    // MARK: - History

    @Test func historyKeepsRotatedLogsByIdentity() throws {
        let home = Home()
        let old = Date().addingTimeInterval(-600)
        try home.write("logs/agent.log.1", Self.turn("A", Self.base) + Self.call("A", 1, Self.base + 2000, output: 10), modified: old)
        try home.write("logs/agent.log", Self.call("A", 2, Self.base + 5000, output: 20) + Self.call("A", 2, Self.base + 5000, output: 20)
            + Self.call("B", 1, Self.base + 86_400_000, output: 40))
        let store = home.directory.file("history.sqlite").path
        let configuration = home.configuration

        var report = try HistoryScan.run(configuration: configuration, timeZone: Self.utc, storePath: store)
        #expect(report.filesAnalyzed == 2)
        var day = try #require(report.days.first { $0.source == .hermes && $0.day == "2026-10-20" })
        #expect(day.totals.requests == 2 && day.totals.output == 30 && day.agents == 1, "one session over two files")
        #expect(report.days.first { $0.source == .hermes && $0.day == "2026-10-21" }?.agents == 1)

        // Rotation renames both files and starts a new one: only the new file is analysed.
        try home.rename("logs/agent.log.1", "logs/agent.log.2")
        try home.rename("logs/agent.log", "logs/agent.log.1")
        try home.write("logs/agent.log", Self.call("A", 3, Self.base + 9000, output: 30))
        report = try HistoryScan.run(configuration: configuration, timeZone: Self.utc, storePath: store)
        #expect(report.filesAnalyzed == 1, "renamed logs keep their contributions")
        day = try #require(report.days.first { $0.source == .hermes && $0.day == "2026-10-20" })
        #expect(day.totals.requests == 3 && day.totals.output == 60 && day.agents == 1)

        // The oldest is deleted: its contributions stay.
        try FileManager.default.removeItem(atPath: home.path("logs/agent.log.2"))
        report = try HistoryScan.run(configuration: configuration, timeZone: Self.utc, storePath: store)
        #expect(report.filesAnalyzed == 0)
        day = try #require(report.days.first { $0.source == .hermes && $0.day == "2026-10-20" })
        #expect(day.totals.requests == 3 && day.totals.output == 60 && day.agents == 1)

        // A version bump analyses the remaining files again with the deleted file's keys and agent days still owned.
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase(path: store))
        var options = HistoryAnalyzer.Options(timeZone: Self.utc)
        options.analysisVersion = HistoryAnalyzer.analysisVersion + 1
        let bumped = try analyzer.run(configuration: configuration, options: options)
        #expect(bumped.filesAnalyzed == 2)
        let recomputed = analyzer.days(sources: [.hermes])
        #expect(recomputed.first { $0.day == "2026-10-20" }?.totals.output == 60)
        #expect(recomputed.first { $0.day == "2026-10-20" }?.agents == 1)
    }
}
