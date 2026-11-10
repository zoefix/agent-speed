import Foundation
import Testing
@testable import AgentSpeedCore

/// Qoder IDE's and Qoder CLI's rules one by one (METRICS 4.18, 4.19): what a row says, live reading, turn records,
/// read-only opening, several databases, and the location lists.
@Suite("Qoder")
struct QoderSourceTests {
    /// A `local.db` written the way Qoder's agent core does (the current schema of `spec/research/qoder.md`).
    final class IdeDatabase {
        let path: String
        private let connection: SQLiteConnection
        private var next = 0

        init(_ path: String, wal: Bool = false) throws {
            self.path = path
            try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                                    withIntermediateDirectories: true)
            connection = try SQLiteConnection(path: path, mode: .readWriteCreate)
            if wal { try connection.execute("PRAGMA journal_mode=WAL") }
            try connection.execute("""
                CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text, project_name text,
                    gmt_create INTEGER, gmt_modified INTEGER, mode text, parent_session_id varchar(64),
                    parent_tool_call_id varchar(64), session_type text);
                CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), request_id varchar(64),
                    role text, content text, summary text, summary_modified INTEGER, summary_trigger text,
                    tool_result text, token_info text, model_info text, extra text, gmt_create INTEGER);
                CREATE INDEX idx_chat_message_session ON chat_message (session_id);
                CREATE TABLE chat_record (request_id varchar(64) PRIMARY KEY, session_id varchar(64), question text,
                    answer text, extra text, gmt_create INTEGER, gmt_modified INTEGER);
                """)
        }

        func session(_ id: String, project: String? = "/w/aurora", parent: String? = nil) throws {
            let insert = try connection.prepare(
                "INSERT OR REPLACE INTO chat_session(session_id, project_uri, parent_session_id) VALUES (?1, ?2, ?3)")
            insert.bind(1, id)
            insert.bind(2, project)
            insert.bind(3, parent)
            try insert.run()
        }

        @discardableResult
        func row(_ session: String, _ role: String, at ms: Int64, request: String? = nil, tokens: String? = nil,
                 model: String? = nil, id: String? = nil) throws -> String {
            next += 1
            let messageId = id ?? "m-\(next)"
            let insert = try connection.prepare("""
                INSERT INTO chat_message(id, session_id, request_id, role, content, token_info, model_info, gmt_create)
                VALUES (?1, ?2, ?3, ?4, 'ZW5jcnlwdGVk', ?5, ?6, ?7)
                """)
            insert.bind(1, messageId)
            insert.bind(2, session)
            insert.bind(3, request)
            insert.bind(4, role)
            insert.bind(5, tokens)
            insert.bind(6, model.map { #"{"model_key":"\#($0)"}"# })
            insert.bind(7, ms)
            try insert.run()
            return messageId
        }

        @discardableResult
        func call(_ session: String, at ms: Int64, request: String? = "r1", prompt: Int = 1000, completion: Int = 40,
                  cached: Int = 600, model: String = "auto") throws -> String {
            try row(session, "assistant", at: ms, request: request,
                    tokens: #"{"prompt_tokens":\#(prompt),"completion_tokens":\#(completion),"cached_tokens":\#(cached),"max_input_tokens":200000}"#,
                    model: model)
        }

        func record(_ request: String, finishedAt: Int64) throws {
            let insert = try connection.prepare(
                "INSERT OR REPLACE INTO chat_record(request_id, session_id, extra) VALUES (?1, 's', ?2)")
            insert.bind(1, request)
            insert.bind(2, #"{"ai-coding/turn-info":{"startedAt":1,"finishedAt":\#(finishedAt),"status":"finished"},"modelConfig":{"key":"auto"}}"#)
            try insert.run()
        }

        func close() { connection.close() }

        /// Closes like Qoder's core does when the IDE quits: the last connection checkpoints and removes `-wal` and
        /// `-shm` (the system SQLite keeps them, so they are removed here).
        func closeCleanly() throws {
            try connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            connection.close()
            for suffix in ["-wal", "-shm"] { try? FileManager.default.removeItem(atPath: path + suffix) }
        }
    }

    static var todayStartMs: Int64 { MonitorCore.todayStartMs(for: Date()) }
    static var nowMs: Int64 { Clock.milliseconds(Date()) }

    static func created(in folder: URL) -> [String] {
        let names = FileManager.default.enumerator(atPath: folder.path)?.allObjects as? [String] ?? []
        return names.filter { $0.hasSuffix("-wal") || $0.hasSuffix("-shm") || $0.hasSuffix("-journal") }
    }

    // MARK: - Rows

    @Test func aRowGivesItsCallFromTheServersUsage() throws {
        func tokens(_ text: String) -> QoderTokens? { Array(text.utf8).withUnsafeBytes { QoderJSON.tokens($0) } }
        func model(_ text: String) -> String? { Array(text.utf8).withUnsafeBytes { QoderJSON.modelKey($0) } }
        func finished(_ text: String) -> Int64? { Array(text.utf8).withUnsafeBytes { QoderJSON.finishedAt($0) } }
        let usage = try #require(tokens(#"{"prompt_tokens":16846,"completion_tokens":481,"cached_tokens":20000}"#))
        let record = try #require(QoderCalls.record(tokens: usage, session: "s", messageId: "m1", rowId: 2, startMs: 1_000,
                                                    endMs: 6_820, model: "auto", agentId: "db#s"))
        #expect(record.key == "qoder:m1" && record.source == .qoder && record.model == "auto")
        #expect(record.usage == TokenUsage(input: 16_846, cachedInput: 16_846, cacheWriteInput: 0, output: 481),
                "the cache read is part of the prompt; never more than it")
        #expect(record.startMs == 1_000 && record.firstMs == nil && record.isFinal && !record.fast && record.provider == nil)
        #expect(tokens(#"{"prompt_tokens":12.9,"completion_tokens":-3,"cached_tokens":"x"}"#) == QoderTokens(prompt: 12))
        #expect(tokens(#"{"prompt_tokens": 10,"#) == nil && tokens("[10, 20]") == nil && tokens(#"{"a":NaN}"#) == nil)
        let empty = try #require(tokens(#"{"prompt_tokens":0,"completion_tokens":0}"#))
        #expect(QoderCalls.record(tokens: empty, session: "s", messageId: nil, rowId: 1, startMs: nil, endMs: 0, model: nil,
                                  agentId: "a") == nil, "no input and no output: no call")
        #expect(QoderCalls.key(session: "s", messageId: nil, rowId: 20) == "qoder:s:row:20")
        #expect(model(#"{"model_key":" efficient "}"#) == "efficient" && model(#"{"model_key":"   "}"#) == nil)
        #expect(model("not json") == nil && model(#"{"model_key":7}"#) == nil)
        #expect(finished(#"{"ai-coding/turn-info":{"finishedAt":1792490405900.7}}"#) == 1_792_490_405_900)
        #expect(finished(#"{"ai-coding/turn-info":{"finishedAt":"soon"}}"#) == nil && finished("{not json") == nil)
        #expect(finished(#"{"modelConfig":{"key":"performance"}}"#) == nil)
    }

    @Test func projectURIsNameTheWorkingFolder() {
        #expect(QoderPaths.projectPath(ofURI: "/Users/dev/projects/aurora") == "/Users/dev/projects/aurora")
        #expect(QoderPaths.projectPath(ofURI: "file:///Users/dev/projects/aurora%20labs") == "/Users/dev/projects/aurora labs")
        #expect(QoderPaths.projectPath(ofURI: "file:///c%3A/work/app") == "c:/work/app")
        #expect(QoderPaths.projectPath(ofURI: "FILE://host/share/app") == "/share/app")
        #expect(QoderPaths.projectPath(ofURI: "file://host") == nil && QoderPaths.projectPath(ofURI: "  ") == nil)
        #expect(QoderPaths.projectPath(ofURI: "file:///w/100%zz") == "/w/100%zz")
        #expect(QoderPaths.projectPath(ofURI: #"C:\Users\dev\work\ledger"#) == #"C:\Users\dev\work\ledger"#)
        #expect(DevinPaths.project(ofWorkingDirectory: #"C:\Users\dev\work\ledger"#) == "ledger")
    }

    // MARK: - Live

    /// The core keeps the database in WAL mode while it runs: changes arrive through the `-wal`.
    @Test func theStartupWindowThenNewRowsAsLiveCompletions() throws {
        let home = try TempDirectory()
        let database = try IdeDatabase(home.file("db/local.db").path, wal: true)
        defer { database.close() }
        let start = Self.todayStartMs
        try database.session("old-pond")
        try database.session("aurora", project: "file:///w/aurora")
        try database.row("old-pond", "user", at: start - 3 * 3_600_000)
        try database.call("old-pond", at: start - 3 * 3_600_000 + 5_000, request: "old")
        try database.row("aurora", "user", at: start - 20 * 60_000, request: "r1")
        try database.call("aurora", at: start + 60_000, request: "r1")
        let source = QoderSource(roots: [.location(home.file("db").path)])
        var deduper = Deduper()
        let seeded = source.seed(todayStartMs: start, deduper: &deduper)
        #expect(seeded.upserts.map(\.upsert.record.startMs) == [start - 20 * 60_000],
                "only the window's call, timed from its session's row below the window")
        let id = database.path + "#aurora"
        #expect(seeded.agents[id]?.phase == .tools && seeded.agents[id]?.project == "aurora" && seeded.agents[id]?.cwdPath == "/w/aurora")
        #expect(seeded.agents[database.path + "#old-pond"] == nil)
        #expect(source.activeSessionCount(todayStartMs: start) == 1)

        // The tool result, the next call; then old-pond resumes: its first new call starts at its row before.
        try database.row("aurora", "tool", at: start + 61_000, request: "r1")
        let tool = source.poll(deduper: &deduper)
        #expect(tool.upserts.isEmpty && tool.agents[id]?.phase == .generating && tool.agents[id]?.inFlightStartMs == start + 61_000)
        try database.call("aurora", at: start + 64_000, request: "r1", prompt: 1_200, completion: 90)
        try database.call("old-pond", at: start + 65_000, request: "late")
        let next = source.poll(deduper: &deduper)
        #expect(next.upserts.allSatisfy { !$0.seeded && $0.upsert.isCompletion })
        let starts = Dictionary(uniqueKeysWithValues: next.upserts.map { ($0.upsert.record.agentId, $0.upsert.record.startMs) })
        #expect(starts[id] == start + 61_000)
        #expect(starts[database.path + "#old-pond"] == start - 3 * 3_600_000 + 5_000, "the previous row, read below the cursor")
        #expect(source.activeSessionCount(todayStartMs: start) == 2)
        #expect(source.poll(deduper: &deduper).isEmpty)
    }

    @Test func aTurnRecordFinishesTheLatestCall() throws {
        let home = try TempDirectory()
        let database = try IdeDatabase(home.file("db/local.db").path, wal: true)
        defer { database.close() }
        let now = Self.nowMs - 60_000
        try database.session("aurora")
        try database.session("comet", project: nil)
        try database.row("aurora", "user", at: now, request: "r1")
        try database.call("aurora", at: now + 5_000, request: "r1")
        // comet's record finished before its call was written: not its finish.
        try database.row("comet", "user", at: now, request: "c1")
        try database.call("comet", at: now + 4_000, request: "c1")
        try database.record("c1", finishedAt: now + 3_000)
        let source = QoderSource(roots: [.location(home.file("db").path)])
        var deduper = Deduper()
        let seeded = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        let aurora = database.path + "#aurora", comet = database.path + "#comet"
        #expect(seeded.agents[aurora]?.phase == .tools && seeded.agents[comet]?.phase == .tools)
        #expect(seeded.agents[comet]?.project == "comet" && seeded.agents[comet]?.cwdPath == nil, "named by its id")

        try database.record("r1", finishedAt: now + 5_400)
        let finished = source.poll(deduper: &deduper)
        #expect(finished.upserts.isEmpty)
        #expect(finished.agents[aurora]?.phase == .idle && finished.agents[aurora]?.phaseSinceMs == now + 5_400)
        #expect(finished.agents[comet] == nil, "an earlier finish changes nothing")
        #expect(source.agentStates[comet]?.phase == .tools)

        // A record that is there when its row is read: idle at once.
        try database.row("aurora", "user", at: now + 10_000, request: "r2")
        try database.record("r2", finishedAt: now + 16_000)
        try database.call("aurora", at: now + 15_000, request: "r2")
        #expect(source.poll(deduper: &deduper).agents[aurora]?.phase == .idle)
        #expect(source.agentStates[aurora]?.lastLineMs == now + 15_000, "a turn record is no row")
    }

    @Test func rowsOutOfOrderAndMalformedRowsKeepTheRules() throws {
        let home = try TempDirectory()
        let database = try IdeDatabase(home.file("db/local.db").path)
        defer { database.close() }
        let now = Self.nowMs - 60_000
        try database.session("delta")
        try database.row("delta", "user", at: now, request: "r1")
        try database.call("delta", at: now + 4_000, request: "r1")
        try database.row("delta", "tool", at: now + 4_100, request: "r1")
        try database.call("delta", at: now + 3_000, request: "r1")
        try database.row("delta", "system", at: now + 4_200)
        let source = QoderSource(roots: [.location(home.file("db").path)])
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        let state = try #require(update.agents[database.path + "#delta"])
        #expect(state.phase == .generating && state.inFlightStartMs == now + 4_100, "an earlier row changes nothing")
        #expect(state.lastLineMs == now + 4_200 && state.malformedLines == 0, "another role only adds activity")
        #expect(update.upserts.map(\.upsert.record.startMs) == [now, now + 4_100])
    }

    @Test func readingNeverCreatesWalShmOrJournal() throws {
        let home = try TempDirectory()
        let walFolder = home.file("wal")
        let writer = try IdeDatabase(walFolder.appendingPathComponent("local.db").path, wal: true)
        try writer.session("aurora")
        try writer.row("aurora", "user", at: Self.nowMs - 9_000, request: "r1")
        try writer.call("aurora", at: Self.nowMs - 5_000, request: "r1")
        try writer.closeCleanly()
        #expect(Self.created(in: walFolder).isEmpty, "a clean close leaves no side file")
        #expect(OpencodeDatabase.headerSaysWAL(writer.path))
        let journal = try IdeDatabase(home.file("journal/local.db").path)
        try journal.session("lumen")
        try journal.call("lumen", at: Self.nowMs - 4_000, request: "l1")
        journal.close()
        let source = QoderSource(roots: [.location(walFolder.path), .location(home.file("journal").path)])
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        #expect(update.upserts.count == 2)
        _ = source.poll(deduper: &deduper)
        _ = source.rediscover(todayStartMs: Self.todayStartMs, deduper: &deduper)
        let planner = QoderHistoryPlanner()
        #expect(planner.plan(roots: source.roots) { _, _, _ in false }.count == 2)
        #expect(Self.created(in: home.url).isEmpty, "the reader never writes beside a database")
    }

    @Test func aDatabaseIsNeverALinkAndACopiedSessionIsSkipped() throws {
        let home = try TempDirectory()
        let first = try IdeDatabase(home.file("a/local.db").path)
        let second = try IdeDatabase(home.file("b/SharedClientCache/cache/db/local.db").path)
        defer { first.close(); second.close() }
        for database in [first, second] {
            try database.session("aurora")
            try database.call("aurora", at: Self.nowMs - 20_000, request: "r1")
        }
        try second.session("comet")
        try second.call("comet", at: Self.nowMs - 10_000, request: "r9")
        try FileManager.default.createDirectory(at: home.file("linked"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(atPath: home.file("linked/local.db").path, withDestinationPath: first.path)
        #expect(QoderRoot.location(home.file("linked").path).database == nil, "a linked database is not read")
        #expect(QoderRoot.database(home.file("linked/local.db").path).database == nil)
        let source = QoderSource(roots: [.location(home.file("a").path), .location(home.file("b").path)])
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        #expect(update.upserts.map(\.upsert.record.agentId).sorted() == [first.path + "#aurora", second.path + "#comet"])
        #expect(source.activeSessionCount(todayStartMs: Self.todayStartMs) == 2)
    }

    @Test func anOlderDatabaseWithoutItsColumnsIsUnsupported() throws {
        let home = try TempDirectory()
        let path = home.file("legacy/local.db").path
        try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        let connection = try SQLiteConnection(path: path, mode: .readWriteCreate)
        try connection.execute("""
            CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text);
            CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), role text, gmt_create INTEGER);
            INSERT INTO chat_session VALUES ('relic', '/w/relic');
            INSERT INTO chat_message VALUES ('m1', 'relic', 'assistant', \(Self.nowMs));
            """)
        connection.close()
        var configuration = MonitorConfiguration(enabledSources: [.qoder])
        configuration.locations[.qoder] = [.path(home.file("legacy").path)]
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: Self.todayStartMs)
        #expect(session.unsupportedSources == [.qoder])
        #expect(session.qoderLocation.available && session.sessionCounts(todayStartMs: Self.todayStartMs)[.qoder] == 0)
    }

    /// Through the session and engine: today's calls, the sub-agent as a child of its session, the source row.
    @Test func todayCountsEachCallOnceWithTheSubagentUnderItsParent() throws {
        let home = try TempDirectory()
        let database = try IdeDatabase(home.file("Qoder/SharedClientCache/cache/db/local.db").path)
        defer { database.close() }
        let now = Self.nowMs - 30_000
        try database.session("aurora")
        try database.session("scout", parent: "aurora")
        try database.row("aurora", "user", at: now, request: "r1")
        try database.call("aurora", at: now + 2_000, request: "r1", prompt: 1_000, completion: 40)
        try database.row("scout", "user", at: now + 2_100, request: "s1")
        try database.call("scout", at: now + 3_000, request: "s1", prompt: 500, completion: 10, cached: 0, model: "lite")
        var configuration = MonitorConfiguration(enabledSources: [.qoder])
        configuration.locations[.qoder] = [.automatic]
        configuration.qoderAutomaticRoots = [.database(database.path), .database(home.file("QoderCN/SharedClientCache/cache/db/local.db").path)]
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: Self.todayStartMs)
        func today() -> TokenTotals {
            session.advance(nowMs: Clock.milliseconds(Date()), todayStartMs: Self.todayStartMs, emitsEvents: false).output.today
        }
        #expect(today().requests == 2 && today().output == 50 && today().input == 1_500)
        #expect(session.sessionCounts(todayStartMs: Self.todayStartMs)[.qoder] == 2, "sub-agent sessions count too")
        #expect(session.qoderLocation.available
            && session.qoderLocation.path == home.file("Qoder/SharedClientCache/cache/db").path)
        let (agents, parents, _) = session.perfAgents()
        #expect(agents.map(\.id).sorted() == [database.path + "#aurora", database.path + "#scout"])
        #expect(parents[database.path + "#scout"] == database.path + "#aurora")
        try database.call("aurora", at: Self.nowMs, request: "r2", prompt: 10, completion: 5, cached: 0)
        _ = session.pollRecentFiles(nowMs: Clock.milliseconds(Date()))
        #expect(today().requests == 3 && today().output == 55)
    }

    // MARK: - Location lists

    @Test func sameLocationsAndTheAutomaticEntry() throws {
        let home = try TempDirectory()
        try home.write("Support/Qoder/SharedClientCache/cache/db/local.db", "")
        try FileManager.default.createDirectory(at: home.file("Support/QoderCN/SharedClientCache/cache/db"),
                                                withIntermediateDirectories: true)
        func key(_ relative: String) -> String { SourceLocations.locationKey(home.file(relative).path, folder: .qoder) }
        #expect(key("Support/Qoder") == key("Support/Qoder/SharedClientCache"))
        #expect(key("Support/Qoder") == key("Support/Qoder/SharedClientCache/cache/db"))
        #expect(key("Support/Qoder/SharedClientCache/cache") != key("Support/Qoder"))
        #expect(key("Support/QoderCN") == key("Support/QoderCN/SharedClientCache/cache/db"),
                "a data folder without its database yet names where the IDE will write it")
        var configuration = MonitorConfiguration()
        configuration.qoderAutomaticRoots = [.database(home.file("Support/Qoder/SharedClientCache/cache/db/local.db").path),
                                             .database(home.file("Support/QoderCN/SharedClientCache/cache/db/local.db").path)]
        let automatic = SourceLocations.automaticKeys(of: .qoder, configuration: configuration)
        #expect(automatic.keys.count == 2)
        var locations = SourceLocations()
        #expect(locations.add(home.file("Support/QoderCN").path, to: .qoder, automatic: automatic) == .alreadyAutomatic)
        #expect(locations.add(home.file("portable").path, to: .qoder, automatic: automatic) == .added)
        locations.remove(.automatic, from: .qoder)
        #expect(locations.add(home.file("Support/Qoder").path, to: .qoder, automatic: automatic) == .restoredAutomatic,
                "the default location brings the automatic entry back")
        #expect(locations[.qoder].first == .automatic)
        #expect(TranscriptFolder.qoder.sources == [.qoder] && !TranscriptFolder.qoder.automaticIsWiderThanDefault)
        configuration.locations[.qoder] = [.automatic, .path(home.file("Support/Qoder/SharedClientCache").path)]
        #expect(QoderPaths.databases(configuration.resolvedQoderRoots)
            == [home.file("Support/Qoder/SharedClientCache/cache/db/local.db").path], "a database named twice is read once")

        // Qoder CLI: a config folder and its projects folder are one location.
        func cliKey(_ relative: String) -> String { SourceLocations.locationKey(home.file(relative).path, folder: .qoderCli) }
        try FileManager.default.createDirectory(at: home.file(".qoder/projects"), withIntermediateDirectories: true)
        #expect(cliKey(".qoder") == cliKey(".qoder/projects"))
        #expect(TranscriptFolder.qoderCli.sources == [.qoderCli] && TranscriptFamily(folder: .qoderCli) == .qoderCli)
        var cli = MonitorConfiguration(enabledSources: [.qoderCli])
        cli.automaticRoots[.qoderCli] = [home.file(".qoder"), home.file(".qoder-cn/projects")]
        #expect(cli.resolvedRoots(for: .qoderCli) == [home.file(".qoder/projects").path, home.file(".qoder-cn/projects").path])
        cli.locations[.qoderCli] = [.automatic, .path(home.file(".qoder").path)]
        #expect(cli.resolvedRoots(for: .qoderCli).count == 2, "a root named twice is read once")
    }

    @Test func theAutomaticRootsFollowTheHomeAndTheEnvironment() {
        #expect(QoderPaths.automaticDatabases(home: "/Users/me") == [
            "/Users/me/Library/Application Support/Qoder/SharedClientCache/cache/db/local.db",
            "/Users/me/Library/Application Support/QoderCN/SharedClientCache/cache/db/local.db",
        ])
        #expect(QoderRoot.database("/a/Qoder/SharedClientCache/cache/db/local.db").displayFolder == "/a/Qoder/SharedClientCache/cache/db")
        #expect(QoderPaths.cliRoots(environment: [:], home: "/Users/me") == ["/Users/me/.qoder/projects", "/Users/me/.qoder-cn/projects"])
        #expect(QoderPaths.cliRoots(environment: ["QODER_CONFIG_DIR": "/srv/q", "QODERCN_CONFIG_DIR": ""], home: "/h")
            == ["/srv/q/projects", "/h/.qoder-cn/projects"])
        #expect(TranscriptFamily(.qoderCli) == .qoderCli && TranscriptFamily(.qoder) == nil)
        #expect(TranscriptFamily.qoderCli.sources == [.qoderCli] && !ToolEventExtractor.reads(.qoderCli))
    }

    // MARK: - Qoder CLI

    /// Qoder's own model keeps no usage: the session, its phase and model show, no tokens; a custom model's call counts.
    @Test func cliCallsWithoutUsageShowActivityButNoTokens() throws {
        let home = try TempDirectory()
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        func at(_ ms: Int64) -> String { formatter.string(from: Date(timeIntervalSince1970: Double(ms) / 1000)) }
        let now = Self.nowMs - 60_000
        let main = """
            {"type":"runtime-config","model":"auto","reasoningEffort":null,"timestamp":\(now - 100)}
            {"type":"user","cwd":"/w/atlas","sessionId":"s1","entrypoint":"cli","uuid":"u1","message":{"role":"user","content":"Fix it"},"timestamp":"\(at(now))"}
            {"type":"api-request","timestamp":"\(at(now + 500))"}
            {"type":"assistant","cwd":"/w/atlas","uuid":"u2","message":{"id":"m1","model":"auto","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]},"timestamp":"\(at(now + 3_000))"}
            {"type":"user","cwd":"/w/atlas","uuid":"u3","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"timestamp":"\(at(now + 3_200))"}
            {"type":"assistant","cwd":"/w/atlas","uuid":"u4","message":{"id":"m2","model":"claude-sonnet-4-5","stop_reason":"end_turn","usage":{"input_tokens":20341,"cache_read_input_tokens":20096,"output_tokens":92,"speed":"fast"}},"timestamp":"\(at(now + 6_000))"}

            """
        try home.write(".qoder/projects/-w-atlas/s1.jsonl", main)
        try home.write(".qoder/projects/-w-atlas/s1/subagents/agent-a.jsonl", """
            {"type":"user","cwd":"/w/atlas","isSidechain":true,"uuid":"v1","message":{"role":"user","content":"Look"},"timestamp":"\(at(now + 3_300))"}

            """)
        var configuration = MonitorConfiguration(enabledSources: [.qoderCli])
        configuration.automaticRoots[.qoderCli] = [home.file(".qoder"), home.file(".qoder-cn")]
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: Self.todayStartMs)
        let output = session.advance(nowMs: Clock.milliseconds(Date()), todayStartMs: Self.todayStartMs, emitsEvents: false).output
        #expect(output.today.requests == 1 && output.today.input == 20_341 && output.today.output == 92,
                "input_tokens includes the cache read")
        let response = try #require(session.responses.first)
        #expect(response.key == "qodercli:m2" && response.source == .qoderCli && !response.fast && response.firstMs == nil)
        #expect(response.startMs == now + 3_200, "the call without usage consumed the pending start")
        #expect(session.sessionCounts(todayStartMs: Self.todayStartMs)[.qoderCli] == 1, "main session files only")
        let (agents, parents, _) = session.perfAgents()
        let mainPath = home.file(".qoder/projects/-w-atlas/s1.jsonl").path
        let subPath = home.file(".qoder/projects/-w-atlas/s1/subagents/agent-a.jsonl").path
        #expect(Set(agents.map(\.id)) == [mainPath, subPath])
        #expect(parents[subPath] == mainPath)
        let mainAgent = try #require(agents.first { $0.id == mainPath })
        #expect(mainAgent.model == "claude-sonnet-4-5" && mainAgent.project == "atlas" && mainAgent.actions.isEmpty)
        #expect(session.root(for: .qoderCli).detected)
    }
}
