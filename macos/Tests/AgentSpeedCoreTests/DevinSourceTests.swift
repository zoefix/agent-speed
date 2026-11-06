import Foundation
import Testing
@testable import AgentSpeedCore

/// Devin CLI's rules one by one (METRICS 4.17): what a row says, live reading, several databases, and the location
/// list.
@Suite("Devin")
struct DevinSourceTests {
    /// A CLI database written the way Devin does (tables of the public readers, rows appended by `row_id`).
    final class CliDatabase {
        let path: String
        private let connection: SQLiteConnection

        init(_ path: String) throws {
            self.path = path
            try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                                    withIntermediateDirectories: true)
            connection = try SQLiteConnection(path: path, mode: .readWriteCreate)
            try connection.execute("""
                CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT NOT NULL, model TEXT NOT NULL,
                    title TEXT, hidden INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL DEFAULT 0,
                    last_activity_at INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE message_nodes (row_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                    node_id INTEGER NOT NULL DEFAULT 0, parent_node_id INTEGER, chat_message TEXT NOT NULL,
                    created_at INTEGER NOT NULL, metadata TEXT);
                """)
        }

        func session(_ id: String, model: String = "swe-1-6", directory: String = "/w/aurora") throws {
            let insert = try connection.prepare("INSERT OR REPLACE INTO sessions(id, working_directory, model) VALUES (?1, ?2, ?3)")
            insert.bind(1, id)
            insert.bind(2, directory)
            insert.bind(3, model)
            try insert.run()
        }

        func row(_ session: String, _ message: String, at seconds: Int64) throws {
            let insert = try connection.prepare("INSERT INTO message_nodes(session_id, chat_message, created_at) VALUES (?1, ?2, ?3)")
            insert.bind(1, session)
            insert.bind(2, message)
            insert.bind(3, seconds)
            try insert.run()
        }

        func call(_ session: String, request: String?, input: Int = 100, output: Int = 40, at seconds: Int64,
                  tools: Bool = false) throws {
            let id = request.map { #","request_id":"\#($0)""# } ?? ""
            let toolCalls = tools ? #","tool_calls":[{"id":"c1"}]"# : ""
            try row(session, #"{"role":"assistant"\#(toolCalls),"metadata":{"generation_model":"swe-1-6"\#(id),"metrics":{"input_tokens":\#(input),"output_tokens":\#(output),"cache_read_tokens":900,"total_time_ms":2000,"ttft_ms":500}}}"#,
                    at: seconds)
        }

        func close() { connection.close() }
    }

    static var todayStartMs: Int64 { MonitorCore.todayStartMs(for: Date()) }
    static var nowSeconds: Int64 { Int64(Date().timeIntervalSince1970) }

    // MARK: - Rows and lines

    @Test func aRowGivesItsCallFromTheMetricsAlone() throws {
        func parse(_ text: String) -> DevinMessage? { Array(text.utf8).withUnsafeBytes { DevinMessage.parse($0) } }
        let message = try #require(parse(#"{"role":"assistant","content":"x","tool_calls":[{}],"metadata":{"request_id":" r1 ","generation_model":"glm-5-2","finish_reason":"stop","metrics":{"input_tokens":67,"output_tokens":103,"cache_read_tokens":32256,"cache_creation_tokens":-4,"total_time_ms":13666,"ttft_ms":13394}}}"#))
        #expect(message.role == .assistant && message.phase == .tools && message.requestId == "r1")
        let record = try #require(DevinCalls.record(of: message, session: "s", rowId: 7, endMs: 20_000, sessionModel: nil,
                                                    agentId: "db#s"))
        #expect(record.key == "devin:s:request:r1")
        #expect(record.usage == TokenUsage(input: 32_323, cachedInput: 32_256, cacheWriteInput: 0, output: 103))
        #expect(record.startMs == 20_000 - 13_666 && record.firstMs == 20_000 - 13_666 + 13_394)
        #expect(record.model == "glm-5-2" && record.provider == nil && record.isFinal)
        // No request id: the row is the call; a session model of `adaptive` is no model.
        let plain = try #require(parse(#"{"role":"assistant","metadata":{"metrics":{"output_tokens":1,"total_time_ms":0.5}}}"#))
        let row = try #require(DevinCalls.record(of: plain, session: "s", rowId: 9, endMs: 1_000,
                                                 sessionModel: DevinCalls.sessionModel(" Adaptive "), agentId: "db#s"))
        #expect(row.key == "devin:s:row:9" && row.model == nil && row.startMs == nil && row.firstMs == nil)
        #expect(DevinCalls.sessionModel(" kimi-k2-7 ") == "kimi-k2-7")
        #expect(parse(#"{"role":"tool","content":"…"}"#)?.phase == .generating)
        #expect(parse(#"{"role":"assistant","metadata":{"finish_reason":"tool_calls"}}"#)?.phase == .tools)
        #expect(parse(#"{"role":"system"}"#)?.phase == nil)
        #expect(parse(#"{"role":"assistant","metadata":{"metrics":{"input_tokens":NaN}}}"#) == nil)
        #expect(parse("[1]") == nil && parse(#"{"role":"user"} x"#) == nil)
        // Zero tokens: no call (the row still sets the phase).
        let empty = try #require(parse(#"{"role":"assistant","metadata":{"metrics":{"input_tokens":0}}}"#))
        #expect(DevinCalls.record(of: empty, session: "s", rowId: 1, endMs: 0, sessionModel: nil, agentId: "a") == nil)
    }

    // MARK: - Live

    @Test func theStartupWindowThenNewRowsAsLiveCompletions() throws {
        let home = try TempDirectory()
        let database = try CliDatabase(home.file("cli/sessions.db").path)
        defer { database.close() }
        try database.session("old-pond")
        try database.session("aurora")
        let start = Self.todayStartMs / 1000
        try database.call("old-pond", request: "old", at: start - 3 * 3600)
        try database.row("aurora", #"{"role":"user","content":"go"}"#, at: start + 60)
        try database.call("aurora", request: "r1", at: start + 65, tools: true)
        let source = DevinSource(roots: [.location(home.file("cli").path)])
        var deduper = Deduper()
        let seeded = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        #expect(seeded.upserts.map(\.upsert.record.key) == ["devin:aurora:request:r1"], "only rows of the window")
        let id = database.path + "#aurora"
        #expect(seeded.agents[id]?.phase == .tools && seeded.agents[id]?.project == "aurora")
        #expect(seeded.agents[database.path + "#old-pond"] == nil)
        #expect(source.activeSessionCount(todayStartMs: Self.todayStartMs) == 1)

        // Devin stores the call twice, then the tool result and the next call.
        try database.call("aurora", request: "r1", at: start + 65, tools: true)
        try database.row("aurora", #"{"role":"tool","tool_call_id":"c1","content":"…"}"#, at: start + 70)
        let tool = source.poll(deduper: &deduper)
        #expect(tool.upserts.isEmpty, "a stored copy of a call is no new request")
        #expect(tool.agents[id]?.phase == .generating && tool.agents[id]?.inFlightStartMs == (start + 70) * 1000)
        try database.call("aurora", request: "r2", input: 10, output: 5, at: start + 80)
        let next = source.poll(deduper: &deduper)
        #expect(next.upserts.map(\.upsert.record.key) == ["devin:aurora:request:r2"])
        #expect(next.upserts.allSatisfy { !$0.seeded && $0.upsert.isCompletion })
        #expect(next.agents[id]?.phase == .idle)
        #expect(source.poll(deduper: &deduper).isEmpty)
    }

    @Test func aDatabaseIsNeverALinkAndASessionWithoutAFolderIsNamedByItsId() throws {
        let home = try TempDirectory()
        let database = try CliDatabase(home.file("real/sessions.db").path)
        defer { database.close() }
        try database.session("night-owl", directory: "")
        try database.call("night-owl", request: "r1", at: Self.nowSeconds - 20)
        try FileManager.default.createDirectory(at: home.file("linked"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(atPath: home.file("linked/sessions.db").path,
                                                   withDestinationPath: database.path)
        #expect(DevinRoot.location(home.file("linked").path).database == nil, "a linked database is not read")
        #expect(DevinRoot.database(home.file("linked/sessions.db").path).database == nil)
        let source = DevinSource(roots: [.location(home.file("real").path)])
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        let state = try #require(update.agents[database.path + "#night-owl"])
        #expect(state.project == "night-owl" && state.cwdPath == nil, "never the database's folder")
        #expect(update.upserts.map(\.project) == ["night-owl"])
    }

    @Test func aCopiedSessionInALaterDatabaseIsSkipped() throws {
        let home = try TempDirectory()
        let first = try CliDatabase(home.file("a/sessions.db").path)
        let second = try CliDatabase(home.file("b/sessions.db").path)
        defer { first.close(); second.close() }
        for database in [first, second] {
            try database.session("aurora")
            try database.call("aurora", request: "r1", at: Self.nowSeconds - 20)
        }
        try second.session("comet")
        try second.call("comet", request: "r9", at: Self.nowSeconds - 10)
        let source = DevinSource(roots: [.location(home.file("a").path), .location(home.file("b").path)])
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.todayStartMs, deduper: &deduper)
        #expect(update.upserts.map(\.upsert.record.agentId).sorted() == [first.path + "#aurora", second.path + "#comet"])
        #expect(source.activeSessionCount(todayStartMs: Self.todayStartMs) == 2)
    }

    /// Through the session and engine: today's calls, each stored copy once; a Devin Desktop folder in the list adds
    /// nothing.
    @Test func todayCountsEachCallOnce() throws {
        let home = try TempDirectory()
        let database = try CliDatabase(home.file("cli/sessions.db").path)
        defer { database.close() }
        try database.session("aurora")
        for _ in 0..<2 { try database.call("aurora", request: "r1", input: 100, output: 40, at: Self.nowSeconds - 30) }
        try home.write("Devin/User/acp-events/d.ndjson",
                       #"{"notification":{"sessionUpdate":"usage_update","used":9,"size":9,"_meta":{"cognition.ai/inputTokens":300,"cognition.ai/outputTokens":9}}}"# + "\n")
        var configuration = MonitorConfiguration(enabledSources: [.devin])
        configuration.locations[.devin] = [.path(home.file("cli").path), .path(home.file("Devin").path)]
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: Self.todayStartMs)
        func today() -> TokenTotals {
            session.advance(nowMs: Clock.milliseconds(Date()), todayStartMs: Self.todayStartMs, emitsEvents: false).output.today
        }
        #expect(today().requests == 1 && today().output == 40 && today().input == 1_000)
        #expect(session.sessionCounts(todayStartMs: Self.todayStartMs)[.devin] == 1)
        try database.call("aurora", request: "r2", input: 10, output: 5, at: Self.nowSeconds)
        _ = session.pollRecentFiles(nowMs: Clock.milliseconds(Date()))
        #expect(today().requests == 2 && today().output == 45)
        #expect(session.devinLocation.available && session.devinLocation.path == home.file("cli").path)
    }

    // MARK: - Location list

    @Test func sameLocationsAndTheAutomaticEntry() throws {
        let home = try TempDirectory()
        try home.write("share/devin/cli/sessions.db", "")
        func key(_ relative: String) -> String { SourceLocations.locationKey(home.file(relative).path, folder: .devin) }
        #expect(key("share/devin") == key("share/devin/cli"))
        #expect(key("share") != key("share/devin"))
        var configuration = MonitorConfiguration()
        configuration.devinAutomaticRoots = [.database(home.file("share/devin/cli/sessions.db").path)]
        let automatic = SourceLocations.automaticKeys(of: .devin, configuration: configuration)
        var locations = SourceLocations()
        #expect(locations.add(home.file("share/devin").path, to: .devin, automatic: automatic) == .alreadyAutomatic)
        #expect(locations.add(home.file("work").path, to: .devin, automatic: automatic) == .added)
        locations.remove(.automatic, from: .devin)
        #expect(locations.add(home.file("share/devin/cli").path, to: .devin, automatic: automatic) == .restoredAutomatic,
                "the default location brings the automatic entry back")
        #expect(locations[.devin].first == .automatic)
        #expect(TranscriptFolder.devin.sources == [.devin] && !TranscriptFolder.devin.automaticIsWiderThanDefault)
        configuration.locations[.devin] = [.automatic, .path(home.file("share/devin").path)]
        #expect(DevinPaths.databases(configuration.resolvedDevinRoots) == [home.file("share/devin/cli/sessions.db").path],
                "a database named twice is read once")
        #expect(DevinRoot.location(home.file("Support/Devin/User/acp-events").path).database == nil)
    }

    @Test func theAutomaticRootsFollowTheEnvironment() {
        #expect(DevinPaths.automaticDatabase(environment: [:], home: "/Users/me") == "/Users/me/.local/share/devin/cli/sessions.db")
        #expect(DevinPaths.automaticDatabase(environment: ["XDG_DATA_HOME": "/x"], home: "/Users/me") == "/x/devin/cli/sessions.db")
        #expect(DevinPaths.automaticDatabase(environment: ["XDG_DATA_HOME": "/x", "CHISEL_SESSION_DB": "/y/s.db"], home: "/h") == "/y/s.db")
        #expect(DevinPaths.automaticRoots(environment: [:], home: "/h") == [.database("/h/.local/share/devin/cli/sessions.db")])
        #expect(DevinPaths.project(ofWorkingDirectory: #"C:\Users\dev\ledger\"#) == "ledger")
    }
}
