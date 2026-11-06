import Foundation
import Testing
@testable import AgentSpeedCore

/// Pi (METRICS 4.13): the shared fixtures, root resolution and the line rules that differ from omp.
@Suite("Pi")
struct PiTests {
    static var piFixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["pi"] as? [String: Any]) }
    }

    // MARK: - Fixtures

    @Test func fixturesMatchTheReference() throws {
        let (expected, results) = try ParserFixtureTests.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        let pi = files.filter { $0.key.hasPrefix("pi/") }
        #expect(pi.count == 6)
        for (relative, spec) in pi {
            let summary = OmpTests.summary(try #require(results[relative]))
            for (field, wanted) in spec where !["parentFile", "effort", "speed", "responseModes"].contains(field) {
                let actual = summary[field] ?? NSNull()
                #expect((actual as AnyObject).isEqual(wanted), "\(relative) \(field): \(actual)")
            }
        }
    }

    @Test func tracesMatchPhaseAfterEveryCheckpoint() throws {
        let traces = try #require(try Self.piFixtures["traces"] as? [[String: Any]])
        #expect(traces.count >= 19)
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

    @Test func discoveryFindsSessionFilesInBothRoots() throws {
        let wanted = try #require(try Self.piFixtures["discover"] as? [String])
        let roots = ParserFixtureTests.piHome().roots.map(\.path)
        #expect(roots.map { String($0.dropFirst(Fixtures.root.path.count)) } == ["/pi/agent/sessions", "/pi/custom-sessions"])
        let prefix = Fixtures.root.path + "/"
        var found: [String] = []
        for root in roots {
            var index = DirectoryIndex(root: root, names: .piSession)
            found += index.scan().map { String($0.dropFirst(prefix.count)) }
        }
        #expect(found.sorted() == wanted)
    }

    // MARK: - Roots

    @Test func rootsFollowTheEnvironmentAndPiSettings() throws {
        let directory = try TempDirectory()
        let home = directory.url
        let agent = home.path + "/.pi/agent"
        let cases: [([String: String], String?, [String])] = [
            ([:], nil, [agent + "/sessions"]),
            ([:], #"{"sessionDir": "~/elsewhere"}"#, [agent + "/sessions", home.path + "/elsewhere"]),
            ([:], #"{"sessionDir": "   "}"#, [agent + "/sessions"]),
            ([:], #"{"sessionDir": 7}"#, [agent + "/sessions"]),
            ([:], #"{"sessionDir": "/srv/pi-a""#, [agent + "/sessions"]),
            (["PI_CODING_AGENT_DIR": "~/pi-agent"], nil, [home.path + "/pi-agent/sessions"]),
            (["PI_CODING_AGENT_SESSION_DIR": "/srv/pi"], #"{"sessionDir": "/srv/pi"}"#, [agent + "/sessions", "/srv/pi"]),
            (["PI_CODING_AGENT_SESSION_DIR": "/srv/env"], #"{"sessionDir": " /srv/file "}"#, [agent + "/sessions", "/srv/file", "/srv/env"]),
        ]
        for (environment, settings, wanted) in cases {
            let piHome = PiHome(environment: environment, home: home)
            let target = piHome.agentDirectory.appendingPathComponent("settings.json")
            try? FileManager.default.removeItem(at: target)
            if let settings { try directory.write(String(target.path.dropFirst(home.path.count + 1)), settings) }
            #expect(piHome.roots.map(\.path) == wanted, "\(environment) \(settings ?? "no settings")")
        }
    }

    @Test func aConfiguredFolderReplacesEveryAutomaticRoot() {
        var configuration = MonitorConfiguration(piRoot: URL(fileURLWithPath: "/data/pi/", isDirectory: true))
        configuration.piHome = PiHome(environment: ["PI_CODING_AGENT_SESSION_DIR": "/srv/env"], home: URL(fileURLWithPath: "/home/dev"))
        #expect(configuration.resolvedRoots(for: .pi) == ["/data/pi"])
        configuration.locations.reset(.pi)
        #expect(configuration.resolvedRoots(for: .pi) == ["/home/dev/.pi/agent/sessions", "/srv/env"])
    }

    @Test func ompRootsThatArePisAreNotReadAsOmp() {
        let home = "/Users/dev"
        let pi = [home + "/.pi/agent/sessions"]
        #expect(PiHome.contains(pi, home + "/.pi/agent/sessions"))
        #expect(PiHome.contains(pi, home + "/.pi/agent/sessions/"))
        #expect(PiHome.contains(pi, home + "/.pi/agent/sessions/x"))
        #expect(!PiHome.contains(pi, home + "/.omp/agent/sessions"))
        #expect(!PiHome.contains(pi, home + "/.pi/agent/sessions2"))
        #expect(!PiHome.contains(pi, home + "/.pi/agent"))

        var configuration = MonitorConfiguration(ompRoot: URL(fileURLWithPath: home + "/.pi/agent/sessions", isDirectory: true))
        configuration.piHome = PiHome(environment: [:], home: URL(fileURLWithPath: home))
        let roots = configuration.allRoots()
        #expect(roots.first { $0.family == .omp }?.readable == false)
        #expect(roots.first { $0.family == .pi }?.readable == true)
        #expect(!HistoryAnalyzer.roots(configuration).contains { $0.0 == .omp })
        configuration.enabledSources.remove(.pi)
        #expect(!HistoryAnalyzer.roots(configuration).contains { $0.0 == .omp }, "Pi's files stay Pi's while Pi is off")
        configuration.setLocation(URL(fileURLWithPath: home + "/.omp/agent/sessions", isDirectory: true), for: .omp)
        #expect(configuration.allRoots().first { $0.family == .omp }?.readable == true)
    }

    @Test func sourceOrderAndProcessRow() {
        #expect(SourceKind.allCases.firstIndex(of: .pi) == 13)
        #expect(SourceSet(.pi).rawValue == 1 << 13)
        #expect(TranscriptFamily(.pi) == .pi && TranscriptFamily.pi.sources == [.pi])
        let mac = AgentProcessRules(platform: TrafficPlatform(kind: .macos, home: "/Users/dev"))
        func source(_ executable: String, _ arguments: [String] = []) -> SourceKind? {
            mac.source(executable: executable, arguments: { arguments }, ancestorExecutables: [])
        }
        #expect(source("/Users/dev/.local/bin/pi") == .pi)
        #expect(source("/opt/homebrew/bin/node", ["node", "/opt/homebrew/lib/node_modules/@earendil-works/pi-coding-agent/dist/cli.js"]) == .pi)
        #expect(source("/usr/local/bin/node", ["node", "/usr/local/lib/node_modules/@mariozechner/pi-coding-agent/dist/cli.js"]) == .pi)
        #expect(source("/opt/homebrew/bin/node", ["node", "/Users/dev/projects/pi-calculator/index.js"]) == nil)
        #expect(source("/usr/local/bin/pip") == nil)
    }

    // MARK: - Lines

    static let mainPath = "/Users/dev/.pi/agent/sessions/--w-nova--/2026-10-20T12-00-00-000Z_s1.jsonl"
    static let t0: Int64 = 1_792_497_600_000

    static func header(_ ms: Int64 = t0) -> String {
        #"{"type":"session","version":3,"id":"s1","timestamp":"\#(OmpSessionTests.iso(ms))","cwd":"/w/nova"}"#
    }

    static func assistant(_ id: String, at ms: Int64, start: Int64, duration: String? = nil, stop: String = "stop",
                          usage: String = #"{"input":10,"output":20,"cacheRead":0,"cacheWrite":0}"#, extra: String = "") -> String {
        let timing = duration.map { #","durationMs":\#($0)"# } ?? ""
        return #"{"type":"message","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(ms))","message":{"role":"assistant","content":[],"api":"openai-responses","provider":"openai","model":"gpt-5.5","usage":\#(usage),"stopReason":"\#(stop)","timestamp":\#(start)\#(timing)\#(extra)}}"#
    }

    static func feed(_ lines: [String], path: String = mainPath, deduper: inout Deduper, fileID: FileID = 1)
        -> (state: AgentState, upserts: [ResponseUpsert]) {
        var parser = PiParser(agentId: path, fileID: fileID)
        var upserts: [ResponseUpsert] = []
        for line in lines {
            Data(line.utf8).withUnsafeBytes { parser.consume(PiLineExtractor.extract($0), deduper: &deduper) { upserts.append($0) } }
        }
        return (parser.state, upserts)
    }

    @Test func timingFollowsDurationMsAndFailedStopsCarryNoStart() {
        var deduper = Deduper()
        let (state, upserts) = Self.feed([
            Self.header(),
            Self.assistant("a1", at: Self.t0 + 3000, start: Self.t0 + 1000, duration: "1500.9"),
            Self.assistant("a2", at: Self.t0 + 6000, start: Self.t0 + 4000),
            Self.assistant("a3", at: Self.t0 + 8000, start: Self.t0 + 7000, duration: "-1"),
            Self.assistant("a4", at: Self.t0 + 9000, start: Self.t0 + 8500, duration: "1e999"),
            Self.assistant("a5", at: Self.t0 + 10_000, start: Self.t0 + 9500, duration: "300", stop: "pending"),
            Self.assistant("a6", at: Self.t0 + 11_000, start: Self.t0 + 10_500, duration: "300", stop: "aborted"),
            Self.assistant("a7", at: Self.t0 + 12_000, start: 0, duration: "300"),
            Self.assistant("a8", at: Self.t0 + 13_000, start: Self.t0 + 12_500,
                           usage: #"{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":5}"#),
        ], deduper: &deduper)
        let rows = upserts.map { [$0.record.key, $0.record.startMs.map(String.init) ?? "nil", String($0.record.endMs)] }
        #expect(rows == [
            ["pi:a1:\(Self.t0 + 1000)", "\(Self.t0 + 1000)", "\(Self.t0 + 2500)"],
            ["pi:a2:\(Self.t0 + 4000)", "\(Self.t0 + 4000)", "\(Self.t0 + 6000)"],
            ["pi:a3:\(Self.t0 + 7000)", "\(Self.t0 + 7000)", "\(Self.t0 + 8000)"],
            ["pi:a4:\(Self.t0 + 8500)", "\(Self.t0 + 8500)", "\(Self.t0 + 9000)"],
            ["pi:a5:\(Self.t0 + 9500)", "nil", "\(Self.t0 + 9800)"],
            ["pi:a6:\(Self.t0 + 10_500)", "nil", "\(Self.t0 + 10_800)"],
            ["pi:a7:\(Self.t0 + 12_000)", "nil", "\(Self.t0 + 12_000)"],
        ])
        #expect(upserts.allSatisfy { $0.isCompletion && $0.record.isFinal && $0.record.source == .pi })
        #expect(state.phase == .idle && state.model == "gpt-5.5")
    }

    @Test func toolRunsAndTheGuardFollowOmp() {
        var deduper = Deduper()
        let calls = #","content":[{"type":"toolCall","id":"c1"},{"type":"toolCall","id":"c2"},{"type":"text","text":"x"}]"#
        let user = #"{"type":"message","id":"u1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 100))","message":{"role":"user","content":"q"}}"#
        let result = { (id: String, call: String, ms: Int64, name: String) in
            #"{"type":"message","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(ms))","message":{"role":"toolResult","toolCallId":"\#(call)","toolName":"\#(name)","isError":false}}"#
        }
        var parser = PiParser(agentId: Self.mainPath, fileID: 1)
        var phases: [String] = []
        let lines = [
            Self.header(), user,
            Self.assistant("a1", at: Self.t0 + 2000, start: Self.t0 + 105, duration: "1800", stop: "toolUse", extra: calls)
                .replacingOccurrences(of: #""content":[],"#, with: ""),
            result("r1", "c1", Self.t0 + 2100, "yield"),
            result("r2", "c2", Self.t0 + 2200, "read"),
            #"{"type":"message","id":"a2","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 3000))","message":{"role":"assistant","provider":"openai","model":"m","usage":{"input":1,"output":1}}}"#,
            #"{"type":"session_info","id":"i1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 3100))","name":"x"}"#,
        ]
        for line in lines {
            Data(line.utf8).withUnsafeBytes { parser.consume(PiLineExtractor.extract($0), deduper: &deduper) { _ in } }
            phases.append(OmpTests.Phase(parser.state).description)
        }
        #expect(phases == [
            "idle nil", "generating \(Self.t0 + 100)", "tools nil", "tools nil", "generating \(Self.t0 + 2200)",
            "generating \(Self.t0 + 2200)", "generating \(Self.t0 + 2200)",
        ], "a yield result does not end a Pi run; a message without api fails the guard")
        #expect(parser.state.lastLineMs == Self.t0 + 3100)
    }

    @Test func sideUsageIsUntimedUnpricedAndDeduplicated() {
        var deduper = Deduper()
        let usage = #"{"input":100,"output":5,"cacheRead":50,"cacheWrite":20,"cacheWrite1h":30}"#
        let lines = [
            Self.header(),
            #"{"type":"usage","id":"w1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 1000))","kind":"cache_warm","provider":"anthropic","model":"claude-opus-5-5","usage":\#(usage)}"#,
            #"{"type":"usage","id":"w2","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 1100))","kind":"cache_warm","provider":7,"model":"claude-opus-5-5","usage":\#(usage)}"#,
            #"{"type":"usage","id":"w3","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 1200))","provider":"","model":"m","usage":\#(usage)}"#,
            #"{"type":"compaction","id":"k1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 2000))","usage":\#(usage)}"#,
            #"{"type":"branch_summary","id":"b1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 3000))","usage":"none"}"#,
            #"{"type":"message","id":"t1","timestamp":"\#(OmpSessionTests.iso(Self.t0 + 4000))","message":{"role":"toolResult","toolCallId":"x","usage":\#(usage)}}"#,
        ]
        let (state, upserts) = Self.feed(lines, deduper: &deduper)
        let records = upserts.map(\.record)
        #expect(records.map(\.key) == ["pi:w1:\(Self.t0 + 1000)", "pi:w3:\(Self.t0 + 1200)", "pi:k1:\(Self.t0 + 2000)",
                                       "pi:t1:\(Self.t0 + 4000)"])
        #expect(records.map(\.model) == ["claude-opus-5-5", "m", nil, nil])
        #expect(records.map(\.provider) == ["anthropic", nil, nil, nil])
        #expect(records.allSatisfy { $0.startMs == nil && $0.modes == .empty && $0.usage.input == 170 && $0.usage.cacheWrite1hInput == 20 })
        #expect(state.model == nil, "side usage never sets the agent's model")

        var copy = PiParser(agentId: "/Users/dev/.pi/agent/sessions/--w-nova--/copy.jsonl", fileID: 2)
        var copied: [ResponseUpsert] = []
        for line in lines {
            Data(line.utf8).withUnsafeBytes { copy.consume(PiLineExtractor.extract($0), deduper: &deduper) { copied.append($0) } }
        }
        #expect(copied.isEmpty, "a key first seen in another file stays that file's")
    }

    @Test func thinkingLevelsAreTheEffortContext() {
        var deduper = Deduper()
        let level = { (id: String, ms: Int64, value: String) in
            #"{"type":"thinking_level_change","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(ms))","thinkingLevel":"\#(value)"}"#
        }
        let (state, upserts) = Self.feed([
            Self.header(),
            level("l1", Self.t0 + 10, "medium"),
            Self.assistant("a1", at: Self.t0 + 2000, start: Self.t0 + 1000, extra: #","thinkingLevel":"high","providerThinkingLevel":"max""#),
            Self.assistant("a2", at: Self.t0 + 4000, start: Self.t0 + 3000),
            level("l2", Self.t0 + 5000, "inherit"),
            level("l3", Self.t0 + 5100, "off"),
            Self.assistant("a3", at: Self.t0 + 7000, start: Self.t0 + 6000),
        ], deduper: &deduper)
        let efforts = upserts.map { $0.record.modes.effortAndSpeed.effort.map { "\($0.mode) \($0.evidence)" } ?? "nil" }
        #expect(efforts == ["high recorded", "high recorded", "off selected"])
        #expect(state.modes.effortAndSpeed.effort?.mode == "off" && state.modes.effortAndSpeed.speed == nil)
    }

    @Test func subagentsAreFilesInAFolderNamedAfterASessionFile() throws {
        let directory = try TempDirectory()
        let main = try directory.write("sessions/--w--/2026-10-20T12-00-00-000Z_s1.jsonl", Self.header() + "\n")
        let child = try directory.write("sessions/--w--/2026-10-20T12-00-00-000Z_s1/reviewer.jsonl", Self.header() + "\n")
        let orphan = try directory.write("sessions/--w--/folder/helper.jsonl", Self.header() + "\n")
        let flat = try directory.write("sessions/2026-10-20T12-30-00-000Z_s2.jsonl", Self.header() + "\n")
        #expect(PiParser(agentId: main.path, fileID: 1).state.isSubagent == false)
        let parser = PiParser(agentId: child.path, fileID: 2)
        #expect(parser.state.isSubagent && parser.state.thread?.parentId == main.path)
        #expect(PiParser(agentId: orphan.path, fileID: 3).state.isSubagent == false)
        #expect(PiParser(agentId: flat.path, fileID: 4).state.isSubagent == false)
    }
}
