import Foundation
import Testing
@testable import AgentSpeedCore

/// OpenClaw (METRICS 4.14): the shared fixtures (windows, traces, discovery, the Codex homes next to the agent
/// databases), state folders, the user setting, the process row and the entry rules that differ from Pi.
@Suite("OpenClaw")
struct OpenClawTests {
    static var fixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["openclaw"] as? [String: Any]) }
    }

    /// The fixture folder `openclaw` plays the home folder: its state folders are `.openclaw` and `.openclaw-work`.
    static func home(fixtures root: URL = Fixtures.root) -> OpenClawHome {
        OpenClawHome(environment: [:], home: root.appendingPathComponent("openclaw", isDirectory: true))
    }

    struct WindowResult {
        var agentId: String
        var window: OpenClawWindow
        var state: AgentState
        var responses: [ResponseRecord]
        var parentId: String?
    }

    /// Every window of one database in `created_at` order, one shared dedupe set; nil for a database without session
    /// tables. `stopAt` ends the read after that window's row (phase traces).
    static func parse(
        _ path: String,
        deduper: inout Deduper,
        nextFileID: inout FileID,
        stopAt: (window: String, seq: Int64)? = nil
    ) throws -> [WindowResult]? {
        let database: OpenClawDatabase
        do {
            database = try OpenClawDatabase(path: path)
        } catch .unsupported {
            return nil
        }
        var results: [WindowResult] = []
        try database.read {
            let current = try database.currentSessions()
            for window in try database.windows() {
                let agentId = OpenClawSource.agentId(path, window.sessionId)
                var parser = OpenClawParser(agentId: agentId, fileID: nextFileID, window: window)
                nextFileID += 1
                var latest: [String: ResponseRecord] = [:]
                var order: [String] = []
                var stopped = false
                try database.rows(session: window.sessionId, after: .min, maxLineBytes: FileTailer.defaultMaxLineBytes,
                                  tools: .hashed) { row in
                    guard !stopped, let entry = row.entry else { return }
                    parser.consume(entry, tools: row.tools, deduper: &deduper) { upsert in
                        if latest[upsert.record.key] == nil { order.append(upsert.record.key) }
                        latest[upsert.record.key] = upsert.record
                    }
                    if stopAt?.window == window.sessionId, stopAt?.seq == row.seq { stopped = true }
                }
                let parent = window.spawnedBy.flatMap { current[$0] }.map { OpenClawSource.agentId(path, $0) }
                results.append(WindowResult(agentId: agentId, window: window, state: parser.state,
                                            responses: order.compactMap { latest[$0] }, parentId: parent))
                if stopAt?.window == window.sessionId { break }
            }
        }
        let tracked = Set(results.map(\.agentId))
        for index in results.indices where results[index].parentId.map({ !tracked.contains($0) }) == true {
            results[index].parentId = nil
        }
        return results
    }

    static func relative(_ path: String) -> String {
        String(path.dropFirst(Fixtures.root.path.count + 1))
    }

    // MARK: - Fixtures

    @Test func everyWindowMatchesTheReference() throws {
        let order = try #require(try Self.fixtures["dbOrder"] as? [String])
        let databases = try #require(try Self.fixtures["databases"] as? [String: [String: Any]])
        var deduper = Deduper()
        var nextFileID = OpenClawSource.firstFileID
        var windows = 0
        for databaseRelative in order {
            let wanted = try #require(databases[databaseRelative])
            let path = Fixtures.root.appendingPathComponent(databaseRelative).path
            guard let results = try Self.parse(path, deduper: &deduper, nextFileID: &nextFileID) else {
                #expect(wanted["unsupported"] as? Bool == true, "\(databaseRelative) unsupported")
                continue
            }
            #expect(Set(results.map { Self.relative($0.agentId) }) == Set(wanted.keys), "\(databaseRelative) windows")
            for result in results {
                let id = Self.relative(result.agentId)
                let spec = try #require(wanted[id] as? [String: Any])
                windows += 1
                var summary = OmpTests.summary(ParserFixtureTests.FileResult(state: result.state, responses: result.responses))
                summary["parentId"] = result.parentId.map(Self.relative) ?? NSNull()
                for (field, value) in spec where !["effort", "speed", "responseModes"].contains(field) {
                    let actual = summary[field] ?? NSNull()
                    #expect((actual as AnyObject).isEqual(value), "\(id) \(field): \(actual)")
                }
                expectModeFields(result.state.modes, result.responses, spec, id)
            }
        }
        #expect(windows == 9)
    }

    @Test func tracesMatchPhaseAfterEveryCheckpoint() throws {
        let order = try #require(try Self.fixtures["dbOrder"] as? [String])
        let traces = try #require(try Self.fixtures["traces"] as? [[String: Any]])
        #expect(traces.count >= 18)
        for trace in traces {
            let target = try #require(trace.string("database"))
            let window = try #require(trace.string("window"))
            let seq = Int64(trace.int("afterSeq"))
            var deduper = Deduper()
            var nextFileID = OpenClawSource.firstFileID
            for databaseRelative in order {
                let path = Fixtures.root.appendingPathComponent(databaseRelative).path
                guard databaseRelative == target else {
                    _ = try Self.parse(path, deduper: &deduper, nextFileID: &nextFileID)
                    continue
                }
                let last = try #require(try Self.parse(path, deduper: &deduper, nextFileID: &nextFileID, stopAt: (window, seq))?.last)
                #expect(last.window.sessionId == window)
                #expect(last.state.phase.rawValue == trace.string("phase"), "\(window) row \(seq) phase")
                #expect(last.state.inFlightStartMs == trace.int64("inFlightStartMs"), "\(window) row \(seq) inFlightStartMs")
                break
            }
        }
    }

    @Test func discoveryFindsTheAgentDatabasesAndTheirCodexHomes() throws {
        let home = Self.home()
        let folders = home.stateFolders.map { Self.relative($0.path) }
        #expect(folders == ["openclaw/.openclaw", "openclaw/.openclaw-work"])
        let databases = home.stateFolders.flatMap { OpenClawHome.databases(in: $0.path) }
        #expect(databases.map(Self.relative).sorted() == (try #require(try Self.fixtures["discover"] as? [String])))

        var configuration = MonitorConfiguration(enabledSources: [.codex, .openclaw])
        configuration.openclawHome = home
        configuration.automaticRoots = [.codex: [Fixtures.root.appendingPathComponent("codex/sessions")]]
        let codexRoots = configuration.resolvedRoots(for: .codex)
        // Discovery order: state folders, then agent folders in ordinal order (as reference.py's startup_files).
        #expect(codexRoots.map(Self.relative) == ["codex/sessions", "openclaw/.openclaw/agents/legacy/agent/codex-home/sessions",
                                                   "openclaw/.openclaw/agents/main/agent/codex-home/sessions",
                                                   "openclaw/.openclaw-work/agents/ops/agent/codex-home/sessions"])
        var rollouts: [String] = []
        for root in codexRoots.dropFirst() {
            var index = DirectoryIndex(root: root, names: .codexSession)
            rollouts += index.scan().map(Self.relative)
        }
        #expect(rollouts == (try #require(try Self.fixtures["codexDiscover"] as? [String])))
        #expect(HistoryAnalyzer.roots(configuration).contains { $0.0 == .codex && $0.1 == codexRoots[2] })

        configuration.enabledSources = [.codex]
        #expect(configuration.resolvedRoots(for: .codex) == codexRoots, "Codex homes stay Codex roots while OpenClaw is off")
        configuration.codexRoot = URL(fileURLWithPath: "/data/codex/sessions")
        #expect(configuration.resolvedRoots(for: .codex) == ["/data/codex/sessions"], "a Codex setting replaces every root")
    }

    @Test func aSettingNamesAStateFolderAnAgentFolderOrADatabase() throws {
        let main = Fixtures.root.appendingPathComponent(try #require(try Self.fixtures["dbOrder"] as? [String])[0]).path
        #expect(OpenClawHome.databases(in: (main as NSString).deletingLastPathComponent) == [main])
        #expect(OpenClawHome.databases(in: main) == [main])
        let state = Fixtures.root.appendingPathComponent("openclaw/.openclaw").path
        #expect(OpenClawHome.databases(in: state).map(Self.relative) == [
            "openclaw/.openclaw/agents/legacy/agent/openclaw-agent.sqlite",
            "openclaw/.openclaw/agents/main/agent/openclaw-agent.sqlite",
        ])
        let decoy = Fixtures.root.appendingPathComponent("openclaw/.openclaw/agents/main/agent/incognito-openclaw-agent.sqlite").path
        #expect(OpenClawHome.databases(in: decoy).isEmpty)

        var configuration = MonitorConfiguration(openclawRoot: URL(fileURLWithPath: main))
        configuration.openclawHome = OpenClawHome(environment: ["OPENCLAW_STATE_DIR": "/srv/elsewhere"], home: URL(fileURLWithPath: "/u"))
        #expect(configuration.resolvedOpenclawRoots == [main])
        #expect(configuration.openclawCodexRoots == [((main as NSString).deletingLastPathComponent as NSString).appendingPathComponent("codex-home/sessions")])
        configuration.openclawRoot = nil
        #expect(configuration.resolvedOpenclawRoots == ["/srv/elsewhere"])
        #expect(configuration.requiresRescan(comparedTo: MonitorConfiguration(openclawRoot: URL(fileURLWithPath: main))))
    }

    @Test func readingCreatesNoFile() throws {
        let tree = Fixtures.root.appendingPathComponent("openclaw")
        func files() -> [String] {
            (FileManager.default.enumerator(atPath: tree.path)?.allObjects as? [String] ?? []).sorted()
        }
        let before = files()
        var deduper = Deduper()
        var nextFileID = OpenClawSource.firstFileID
        for relative in try #require(try Self.fixtures["dbOrder"] as? [String]) {
            _ = try Self.parse(Fixtures.root.appendingPathComponent(relative).path, deduper: &deduper, nextFileID: &nextFileID)
        }
        let source = OpenClawSource(roots: Self.home().stateFolders.map(\.path))
        _ = source.seed(todayStartMs: 0, deduper: &deduper)
        _ = source.rediscover(todayStartMs: 0, deduper: &deduper)
        #expect(files() == before)
        #expect(source.hasUnsupportedDatabase, "the 2026.7 database has no session tables")
        #expect(source.activeSessionCount(todayStartMs: 0) == 8, "sub-agent windows are not sessions")
    }

    // MARK: - Roots

    @Test func stateFoldersFollowTheEnvironmentAndProfiles() throws {
        let directory = try TempDirectory()
        let home = directory.url
        for name in [".openclaw", ".openclaw-work", ".openclaw-Default", ".openclaw-x.y", ".openclaw-_z", ".openclaw-2nd",
                     "openclaw-play"] {
            try FileManager.default.createDirectory(at: home.appendingPathComponent(name), withIntermediateDirectories: true)
        }
        try directory.write(".openclaw-file", "")
        let path = { (relative: String) in home.appendingPathComponent(relative).path }
        let cases: [([String: String], [String])] = [
            ([:], [path(".openclaw"), path(".openclaw-2nd"), path(".openclaw-work")]),
            (["OPENCLAW_STATE_DIR": "~/state"], [path("state")]),
            (["OPENCLAW_STATE_DIR": "  ", "OPENCLAW_PROFILE": "work"], [path(".openclaw"), path(".openclaw-2nd"), path(".openclaw-work")]),
            (["OPENCLAW_HOME": "/srv/oc"], ["/srv/oc/.openclaw"]),
            (["OPENCLAW_HOME": "~/.openclaw-work"], [path(".openclaw-work/.openclaw")]),
            (["OPENCLAW_STATE_DIR": "/srv/state/"], ["/srv/state"]),
        ]
        for (environment, wanted) in cases {
            #expect(OpenClawHome(environment: environment, home: home).stateFolders.map(\.path) == wanted, "\(environment)")
        }
        #expect(OpenClawHome.isProfileFolder(".openclaw-" + String(repeating: "a", count: 64)))
        #expect(!OpenClawHome.isProfileFolder(".openclaw-" + String(repeating: "a", count: 65)))
        #expect(!OpenClawHome.isProfileFolder(".openclaw-") && !OpenClawHome.isProfileFolder(".openclaw-DEFAULT"))
        #expect(!OpenClawHome.isProfileFolder(".openclaw-é"))
    }

    @Test func sourceOrderAndProcessRow() {
        #expect(SourceKind.allCases.firstIndex(of: .openclaw) == 14)
        #expect(SourceSet(.openclaw).rawValue == 1 << 14)
        #expect(TranscriptFamily(.openclaw) == nil)
        let mac = AgentProcessRules(platform: TrafficPlatform(kind: .macos, home: "/Users/dev"))
        func source(_ executable: String, _ arguments: [String] = []) -> SourceKind? {
            mac.source(executable: executable, arguments: { arguments }, ancestorExecutables: [])
        }
        #expect(source("/opt/homebrew/bin/node", ["node", "/opt/homebrew/lib/node_modules/openclaw/openclaw.mjs", "gateway", "run"]) == .openclaw)
        #expect(source("/Users/dev/.openclaw/bin/openclaw") == .openclaw)
        #expect(source("/Users/dev/.openclaw/tools/bun-1.3.0/bun", ["bun", "/Users/dev/.openclaw/lib/node_modules/openclaw/dist/entry.js", "gateway"]) == .openclaw)
        #expect(source("/opt/homebrew/bin/node", ["node", "/Users/dev/projects/openclaw-skills/index.js"]) == nil)
        #expect(source("/Applications/OpenClaw.app/Contents/MacOS/OpenClaw") == nil)
        #expect(source("/usr/local/bin/openclaw-helper") == nil)
    }

    @Test func agentIdsNameTheirDatabase() {
        let id = "/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite#0f1c-0001"
        #expect(OpenClawSource.databasePath(ofAgent: id) == "/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite")
        #expect(OpenClawSource.databasePath(ofAgent: "/w/session.jsonl") == nil)
        #expect(EngineAgent.fallbackProject(for: id) == "main")
        let status = AgentStatus(id: id, source: .openclaw, project: "p", model: nil, phase: .idle, isSubagent: false,
                                 live: .zero, recentSpeed: nil, lastActivity: Date())
        #expect(TrafficAgent(status).transcript == "/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite")
    }

    // MARK: - Entries

    static let mainPath = "/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite#w1"
    static let t0: Int64 = 1_792_497_600_000

    static func header(_ ms: Int64 = t0) -> String {
        #"{"type":"session","version":4,"id":"w1","timestamp":"\#(OmpSessionTests.iso(ms))","cwd":"/w/claw"}"#
    }

    static func assistant(_ id: String, at ms: Int64, start: Int64, api: String = "anthropic-messages",
                          provider: String = "anthropic", stop: String = "stop",
                          usage: String = #"{"input":10,"output":20,"cacheRead":5,"cacheWrite":0}"#, extra: String = "") -> String {
        #"{"type":"message","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(ms))","message":{"role":"assistant","content":[],"api":"\#(api)","provider":"\#(provider)","model":"claude-opus-5-5","usage":\#(usage),"stopReason":"\#(stop)","timestamp":\#(start)\#(extra)}}"#
    }

    static func window(runtime: String = "openclaw", acp: Bool = false, spawnedBy: String? = nil) -> OpenClawWindow {
        OpenClawWindow(sessionId: "w1", createdMs: t0, transcriptUpdatedMs: t0, spawnedBy: spawnedBy, runtime: runtime, acpOwned: acp)
    }

    static func feed(_ lines: [String], window: OpenClawWindow = window(), deduper: inout Deduper)
        -> (state: AgentState, upserts: [ResponseUpsert]) {
        var parser = OpenClawParser(agentId: mainPath, fileID: 1, window: window)
        var upserts: [ResponseUpsert] = []
        for line in lines {
            Data(line.utf8).withUnsafeBytes { parser.consume(OpenClawLineExtractor.extract($0), deduper: &deduper) { upserts.append($0) } }
        }
        return (parser.state, upserts)
    }

    @Test func timingRunsFromTheMessageToItsAppend() {
        var deduper = Deduper()
        let (state, upserts) = Self.feed([
            Self.header(),
            Self.assistant("a1", at: Self.t0 + 4000, start: Self.t0 + 1000,
                           usage: #"{"input":3,"output":40,"cacheRead":100,"cacheWrite":50,"cacheWrite1h":80,"reasoningTokens":7}"#),
            Self.assistant("a2", at: Self.t0 + 9000, start: Self.t0 + 5000, stop: "error"),
            Self.assistant("a3", at: Self.t0 + 12000, start: Self.t0 + 10000, stop: "pending"),
        ], deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["openclaw:a1:\(Self.t0 + 1000)", "openclaw:a2:\(Self.t0 + 5000)", "openclaw:a3:\(Self.t0 + 10000)"])
        let first = upserts[0].record
        #expect(first.startMs == Self.t0 + 1000 && first.endMs == Self.t0 + 4000 && first.source == .openclaw)
        #expect(first.usage == TokenUsage(input: 153, cachedInput: 100, cacheWriteInput: 50, cacheWrite1hInput: 50, output: 40, reasoning: 7))
        #expect(first.provider == "anthropic" && first.modes == .empty)
        #expect(upserts[1].record.startMs == nil, "a failed request counts in totals only")
        #expect(upserts[2].record.startMs == nil, "an unknown stop reason counts as aborted")
        #expect(state.phase == .idle && state.project == "claw" && state.cwdPath == "/w/claw")
    }

    @Test func mirrorsCountNothingAndCliTurnsAreUntimed() {
        var deduper = Deduper()
        let (state, upserts) = Self.feed([
            Self.header(),
            Self.assistant("c1", at: Self.t0 + 1000, start: Self.t0 + 900, api: "cli", provider: "claude-cli"),
            Self.assistant("c2", at: Self.t0 + 2000, start: Self.t0 + 1900, provider: "acp-opencode"),
            Self.assistant("c3", at: Self.t0 + 3000, start: Self.t0 + 2900, extra: #","__openclaw":{"mirrorOrigin":"codex-app-server"}"#),
            Self.assistant("c4", at: Self.t0 + 4000, start: Self.t0 + 3900, extra: #","__openclaw":{"mirrorOrigin":""}"#),
            Self.assistant("c5", at: Self.t0 + 5000, start: Self.t0 + 4900, api: "cli", provider: "google-gemini-cli"),
            Self.assistant("c6", at: Self.t0 + 6000, start: Self.t0 + 5900,
                           usage: #"{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"usageReport":{"state":"unavailable"}}"#),
            Self.assistant("c7", at: Self.t0 + 7000, start: Self.t0 + 6900, usage: #"{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}"#),
        ], deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["openclaw:c4:\(Self.t0 + 3900)", "openclaw:c5:\(Self.t0 + 4900)"])
        #expect(upserts[0].record.startMs == Self.t0 + 3900, "an empty mirror origin is no mirror")
        #expect(upserts[1].record.startMs == nil && upserts[1].record.provider == "google-gemini-cli")
        #expect(state.lastLineMs == Self.t0 + 7000)
    }

    @Test func delegatedWindowsStayIdleAndCountUntimed() {
        let user = #"{"type":"message","id":"u1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 500))","message":{"role":"user","content":"x","timestamp":\#(Self.t0 + 500)}}"#
        for (window, counted) in [(Self.window(runtime: "copilot"), true), (Self.window(runtime: "muse"), true),
                                  (Self.window(runtime: "codex"), false), (Self.window(runtime: "claude-cli"), false),
                                  (Self.window(runtime: "acpx"), false), (Self.window(runtime: "", acp: true), false)] {
            var deduper = Deduper()
            let (state, upserts) = Self.feed([Self.header(), user, Self.assistant("a1", at: Self.t0 + 3000, start: Self.t0 + 1000)],
                                             window: window, deduper: &deduper)
            #expect(state.phase == .idle && state.inFlightStartMs == nil, "\(window.runtime) stays idle")
            #expect(upserts.count == (counted ? 1 : 0), "\(window.runtime)")
            #expect(upserts.allSatisfy { $0.record.startMs == nil })
        }
        for runtime in ["openclaw", "pi", "auto", "default", ""] {
            #expect(!Self.window(runtime: runtime).isDelegated, "\(runtime) is embedded")
        }
        var deduper = Deduper()
        let (state, _) = Self.feed([Self.header(), user], deduper: &deduper)
        #expect(state.phase == .generating && state.inFlightStartMs == Self.t0 + 500)
    }

    @Test func copiedHistoryAndDuplicateKeysCountOnce() {
        var deduper = Deduper()
        let line = Self.assistant("a1", at: Self.t0 + 4000, start: Self.t0 + 1000)
        let first = Self.feed([Self.header(), line], deduper: &deduper)
        #expect(first.upserts.count == 1)
        var parser = OpenClawParser(agentId: Self.mainPath + "2", fileID: 2, window: Self.window())
        var upserts: [ResponseUpsert] = []
        for text in [Self.header(Self.t0 + 1000), line, Self.assistant("old", at: Self.t0 - 2000, start: Self.t0 - 3000)] {
            Data(text.utf8).withUnsafeBytes { parser.consume(OpenClawLineExtractor.extract($0), deduper: &deduper) { upserts.append($0) } }
        }
        #expect(upserts.isEmpty, "another window's key and copied history count nothing")
        #expect(parser.state.lastLineMs == Self.t0 + 4000)
    }
}
