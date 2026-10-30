import Foundation
import Testing
@testable import AgentSpeedCore

/// Hermes Agent (METRICS 4.15): the shared fixtures (discovery, log sets, phase traces, wall times and their time-zone
/// rules, homes, a scripted rotation, config.yaml texts and a scripted Codex runtime switch), the line grammar and the
/// process row.
@Suite("Hermes Agent")
struct HermesTests {
    static var fixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["hermes"] as? [String: Any]) }
    }

    static let losAngeles = TimeZone(identifier: "America/Los_Angeles")!

    /// The fixture's zone (`hermes.timeZone`).
    static var zone: TimeZone {
        get throws { try timeZone(try fixtures) }
    }

    /// The zone a fixture object names in `timeZone`.
    static func timeZone(_ object: [String: Any]) throws -> TimeZone {
        let identifier = try #require(object.string("timeZone"))
        return try #require(TimeZone(identifier: identifier))
    }

    /// One parser state per log folder, fed the files of `order` (the last one up to `afterLine` physical lines), and
    /// the responses each session emitted in order.
    struct Sets {
        var sets: [String: HermesLogSet] = [:]
        var responses: [String: [ResponseRecord]] = [:]
        var deduper = Deduper()

        mutating func feed(_ relative: String, zone: TimeZone, afterLine: Int? = nil) throws {
            let path = Fixtures.root.appendingPathComponent(relative).path
            let folder = (path as NSString).deletingLastPathComponent
            var set = sets[folder] ?? HermesLogSet(folder: folder, timeZone: zone, fileID: HermesSource.firstFileID + FileID(sets.count))
            var count = 0
            forEachLine(in: try Data(contentsOf: URL(fileURLWithPath: path))) { line in
                count += 1
                guard afterLine.map({ count <= $0 }) ?? true else { return }
                set.consume(line, deduper: &deduper) { responses[$0.record.agentId, default: []].append($0.record) }
            }
            sets[folder] = set
        }
    }

    static func relative(_ path: String) -> String {
        String(path.dropFirst(Fixtures.root.path.count + 1))
    }

    // MARK: - Fixtures

    @Test func discoveryFindsTheLogsOfEveryProfile() throws {
        let home = Fixtures.root.appendingPathComponent(try #require(try Self.fixtures.string("home"))).path
        let found = HermesHome.logFolders(ofHome: home).flatMap(HermesHome.logFiles(in:)).map(Self.relative).sorted()
        #expect(found == (try Self.fixtures["discover"] as? [String]))
        #expect(HermesHome.logFolders(ofHome: home).map(Self.relative) == ["hermes/home/logs", "hermes/home/profiles/work/logs"],
                "the dot profile and the profile without logs are skipped")
        #expect(HermesHome.settingFolders(home) == HermesHome.logFolders(ofHome: home), "a home as the setting")
        let logs = home + "/logs"
        #expect(HermesHome.settingFolders(logs) == [logs], "one logs folder as the setting")
        #expect(HermesHome.settingFolders(home + "/missing").isEmpty)
        #expect(HermesHome.configPath(ofLogFolder: logs) == home + "/config.yaml")
        #expect(HermesHome.configPath(ofLogFolder: home + "/profiles/work/logs/") == home + "/profiles/work/config.yaml")
        #expect(HermesHome.configPath(ofLogFolder: home + "/profiles/work/Logs") == nil, "case-sensitive on macOS")
    }

    @Test func everyLogSetMatchesTheReference() throws {
        let fixtures = try Self.fixtures
        let order = try #require(fixtures["order"] as? [String])
        let wanted = try #require(fixtures["sets"] as? [String: [String: Any]])
        var sets = Sets()
        for relative in order { try sets.feed(relative, zone: try Self.zone) }
        #expect(Set(sets.sets.keys.map(Self.relative)) == Set(wanted.keys))
        var agents = 0
        for (folderRelative, expected) in wanted {
            let set = try #require(sets.sets[Fixtures.root.appendingPathComponent(folderRelative).path])
            #expect(set.malformedLines == expected.int("malformedLines"), "\(folderRelative) malformed")
            let specs = try #require(expected["agents"] as? [String: [String: Any]])
            #expect(Set(set.sessionOrder) == Set(specs.keys), "\(folderRelative) agents")
            for (sessionId, spec) in specs {
                agents += 1
                let state = try #require(set.agents[sessionId])
                let label = "\(folderRelative)#\(sessionId)"
                let responses = sets.responses[set.agentId(sessionId)] ?? []
                var summary = OmpTests.summary(ParserFixtureTests.FileResult(state: state, responses: responses))
                summary["parentId"] = NSNull()
                for (field, value) in spec where !["responses", "effort", "speed", "responseModes"].contains(field) {
                    let actual = summary[field] ?? NSNull()
                    #expect((actual as AnyObject).isEqual(value), "\(label) \(field): \(actual)")
                }
                // The profile's `config.yaml` and `state.db` beside the log folder play what a live read would find.
                let config = Self.profileConfig(ofFolder: folderRelative)
                let session = Self.sessionModes(ofFolder: folderRelative, session: sessionId)
                expectModeFields(EffortAndSpeed(facts: state.modes, session: session, configured: config), responses, spec, label)
                let wantedResponses = try #require(spec["responses"] as? [[String: Any]])
                #expect(responses.map(\.key) == wantedResponses.compactMap { $0.string("key") }, "\(label) keys")
                for (record, want) in zip(responses, wantedResponses) {
                    #expect(record.agentId == set.agentId(sessionId))
                    #expect(record.startMs == want.int64("startMs") && record.endMs == want.int64("endMs"), "\(record.key) times")
                    #expect(record.usage.input == want.int("input") && record.usage.output == want.int("output"), "\(record.key)")
                    #expect(record.usage.cachedInput == want.int("cachedInput"), "\(record.key) cache read")
                    #expect(record.usage.cacheWriteInput == want.int("cacheWriteInput"), "\(record.key) cache write")
                    #expect(record.usage.reasoning == want.int("reasoning") && record.isFinal == (want["final"] as? Bool))
                    #expect(record.model == want.string("model") && record.source == .hermes, "\(record.key) model")
                    expectPricingFields(record, want, record.key)
                }
            }
        }
        #expect(agents == 6)
    }

    private static func profileConfig(ofFolder relative: String) -> ExecutionModes {
        let folder = Fixtures.root.appendingPathComponent(relative).path
        guard let path = HermesHome.configPath(ofLogFolder: folder),
              let data = FileManager.default.contents(atPath: path) else { return .empty }
        return HermesCodexRuntime.configuredModes(data)
    }

    private static func sessionModes(ofFolder relative: String, session: String) -> ExecutionModes {
        let folder = Fixtures.root.appendingPathComponent(relative).path
        guard let path = HermesHome.configPath(ofLogFolder: folder) else { return .empty }
        return SessionModes.hermes(databasePath: HermesHome.join(HermesHome.directory(of: path), "state.db"),
                                   sessions: [session])[session] ?? .empty
    }

    @Test func agentScalarsFollowTheLineRules() throws {
        for item in try #require(try Self.fixtures["agentConfigs"] as? [[String: Any]]) {
            let text = try #require(item.string("text"))
            let got = HermesCodexRuntime.agentScalars(Array(text.utf8))
            let name = item.string("name") ?? ""
            #expect(Set(got.keys) == Set(item.keys).subtracting(["name", "text"]), "\(name)")
            for (key, value) in got { #expect(value == item.string(key), "\(name) \(key)") }
        }
    }

    @Test func tracesMatchPhaseAfterEveryLine() throws {
        let fixtures = try Self.fixtures
        let order = try #require(fixtures["order"] as? [String])
        let traces = try #require(fixtures["traces"] as? [[String: Any]])
        #expect(traces.count >= 20)
        for trace in traces {
            let target = try #require(trace.string("file"))
            let sessionId = try #require(trace.string("session"))
            var sets = Sets()
            for relative in order {
                try sets.feed(relative, zone: try Self.zone, afterLine: relative == target ? trace.int("afterLine") : nil)
                if relative == target { break }
            }
            let folder = (Fixtures.root.appendingPathComponent(target).path as NSString).deletingLastPathComponent
            let state = sets.sets[folder]?.agents[sessionId]
            let label = "\(target):\(trace.int("afterLine")) \(sessionId)"
            #expect(state?.phase.rawValue == trace.string("phase"), "\(label) phase")
            #expect(state?.inFlightStartMs == trace.int64("inFlightStartMs"), "\(label) in flight")
        }
    }

    @Test func wallTimesFollowTheZoneRules() throws {
        let cases = try #require(try Self.fixtures["wallTimes"] as? [[String: Any]])
        #expect(cases.count >= 16)
        for testCase in cases {
            let zone = try Self.timeZone(testCase)
            let line = try #require(testCase.string("line"))
            let got = Array(line.utf8).withUnsafeBytes { HermesLine.parse($0, timeZone: zone)?.timeMs }
            #expect(got == testCase.int64("ms"), "\(line.prefix(23)) in \(zone.identifier)")
        }
    }

    @Test func wallTimesDoNotDependOnThisMacsZone() throws {
        // The repeated hour of 2026-11-01 in Los Angeles is the earlier (PDT) instant, the skipped hour of 2026-03-08 is
        // read with the offset before the change (PST), whatever the zone of the computer running the test.
        func ms(_ text: String, _ zone: TimeZone) -> Int64? {
            Array(text.utf8).withUnsafeBytes { WallClock($0.bindMemory(to: UInt8.self))?.unixMs(in: zone) }
        }
        #expect(ms("2026-11-01 01:59:59,999", Self.losAngeles) == 1_793_523_599_999)
        #expect(ms("2026-11-01 02:00:00,000", Self.losAngeles) == 1_793_527_200_000)
        #expect(ms("2026-03-08 02:00:00,000", Self.losAngeles) == 1_772_964_000_000)
        #expect(ms("2026-03-08 02:59:59,999", Self.losAngeles) == 1_772_967_599_999)
        #expect(ms("1970-01-01 00:00:00,000", TimeZone(identifier: "Asia/Tokyo")!) == -32_400_000)
        #expect(ms("9999-12-31 23:59:59,999", TimeZone(identifier: "UTC")!) == 253_402_300_799_999)
        #expect(ms("2024-02-29 12:00:00,000", TimeZone(identifier: "UTC")!) == 1_709_208_000_000, "a leap day")
        #expect(ms("2100-02-29 12:00:00,000", TimeZone(identifier: "UTC")!) == nil, "2100 is not a leap year")
        #expect(ms("2026-10-20 23:59:60,000", TimeZone(identifier: "UTC")!) == nil, "no leap second")
        #expect(ms("2026-1o-20 12:00:00,000", TimeZone(identifier: "UTC")!) == nil)
    }

    @Test func homesResolveLikeHermes() throws {
        let cases = try #require(try Self.fixtures["homes"] as? [[String: Any]])
        var checked = 0
        for testCase in cases where testCase.string("platform") == "darwin" {
            let environment = testCase["env"] as? [String: String] ?? [:]
            let folder = try #require(testCase.string("home"))
            let home = HermesHome(environment: environment, home: URL(fileURLWithPath: folder))
            #expect(home.folder == testCase.string("expected"), "\(environment)")
            checked += 1
        }
        #expect(checked == 7)
        #expect(HermesHome.resolve(hermesHome: "~", home: "/Users/dev") == "/Users/dev", "the home folder itself")
        #expect(HermesHome.resolve(hermesHome: "/srv/x/../hermes/./profiles/a", home: "/Users/dev") == "/srv/hermes")
        #expect(HermesHome.resolve(hermesHome: "/Users/dev/.hermes-two", home: "/Users/dev") == "/Users/dev/.hermes-two")
        #expect(HermesHome.normalize("//a/b/") == "//a/b" && HermesHome.normalize("///a") == "/a")
        #expect(HermesHome.normalize("/a/../..") == "/" && HermesHome.normalize("a/../../b") == "../b")
    }

    @Test func runtimeConfigsMatchTheReference() throws {
        let cases = try #require(try Self.fixtures["runtimeConfigs"] as? [[String: Any]])
        #expect(cases.count >= 30)
        for testCase in cases {
            let text = try #require(testCase.string("text"))
            #expect(HermesCodexRuntime.isOn(Array(text.utf8)) == (testCase["codexRuntime"] as? Bool),
                    "\(testCase.string("name") ?? "?")")
        }
    }

    // MARK: - Scripted folders

    /// Applies one `append`, `write`, `remove`, `rename` or `delete` step of a scripted fixture under `base`.
    static func apply(_ step: [String: Any], under base: URL) throws {
        let manager = FileManager.default
        if let target = step.string("append") {
            let url = base.appendingPathComponent(target)
            let text = step.string("text") ?? (step["lines"] as? [String] ?? []).map { $0 + "\n" }.joined()
            if !manager.fileExists(atPath: url.path) { manager.createFile(atPath: url.path, contents: nil) }
            let handle = try FileHandle(forWritingTo: url)
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(text.utf8))
            try handle.close()
            try setModified(url, step.int64("mtime"))
        } else if let target = step.string("write") {
            let url = base.appendingPathComponent(target)
            var data = Data(try #require(step.string("text")).utf8)
            if let size = step["size"] as? Int, size > data.count { data.append(Data(repeating: UInt8(ascii: "#"), count: size - data.count)) }
            try data.write(to: url)
            try setModified(url, step.int64("mtime"))
        } else if let target = step.string("remove") ?? step.string("delete") {
            try manager.removeItem(at: base.appendingPathComponent(target))
        } else if let names = step["rename"] as? [String] {
            try manager.moveItem(at: base.appendingPathComponent(names[0]), to: base.appendingPathComponent(names[1]))
        }
    }

    static func setModified(_ url: URL, _ ms: Int64?) throws {
        guard let ms else { return }
        var times = [timespec(tv_sec: Int(ms / 1000), tv_nsec: Int(ms % 1000) * 1_000_000),
                     timespec(tv_sec: Int(ms / 1000), tv_nsec: Int(ms % 1000) * 1_000_000)]
        #expect(utimensat(AT_FDCWD, url.path, &times, 0) == 0)
    }

    @Test func rotationFollowsFilesAcrossRenames() throws {
        let scenario = try #require(try Self.fixtures["rotation"] as? [String: Any])
        let steps = try #require(scenario["steps"] as? [[String: Any]])
        let zone = try Self.timeZone(scenario)
        let directory = try TempDirectory()
        let folder = directory.file("logs")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let source = HermesSource(root: folder.path, isSetting: true, timeZone: zone)
        var deduper = Deduper()
        var keys: [String] = []
        var reads = 0
        for (number, step) in steps.enumerated() {
            guard step["read"] as? Bool == true else {
                try Self.apply(step, under: folder)
                continue
            }
            reads += 1
            let update = source.rediscover(deduper: &deduper)
            keys += update.upserts.map(\.upsert.record.key)
            #expect(keys.sorted() == (step["responseKeys"] as? [String] ?? []).sorted(), "step \(number + 1) keys")
            for (sessionId, want) in step["agents"] as? [String: [String: Any]] ?? [:] {
                let state = source.agentStates[HermesLogSet.agentId(folder: folder.path, sessionId: sessionId)]
                #expect(state?.phase.rawValue == want.string("phase"), "step \(number + 1) \(sessionId) phase")
                #expect(state?.inFlightStartMs == want.int64("inFlightStartMs"), "step \(number + 1) \(sessionId) in flight")
                #expect(state?.lastLineMs == want.int64("lastLineMs"), "step \(number + 1) \(sessionId) last line")
            }
        }
        #expect(reads == 5)
        #expect(source.codexRuntime(ofFolder: folder.path) == false, "no config.yaml next to the folder")
    }

    @Test func codexRuntimeSwitchFollowsTheProfileConfig() throws {
        let scenario = try #require(try Self.fixtures["codexRuntime"] as? [String: Any])
        let steps = try #require(scenario["steps"] as? [[String: Any]])
        let zone = try Self.timeZone(scenario)
        let directory = try TempDirectory()
        let home = directory.file("home")
        for folder in try #require(scenario["logFolders"] as? [String]) {
            try FileManager.default.createDirectory(at: home.appendingPathComponent(folder), withIntermediateDirectories: true)
        }
        let source = HermesSource(root: home.path, isSetting: false, timeZone: zone)
        var deduper = Deduper()
        var keys: [String] = []
        var reads = 0
        for (number, step) in steps.enumerated() {
            guard step["read"] as? Bool == true else {
                try Self.apply(step, under: home)
                continue
            }
            reads += 1
            keys += source.rediscover(deduper: &deduper).upserts.map(\.upsert.record.key)
            #expect(keys.sorted() == (step["responseKeys"] as? [String] ?? []).sorted(), "step \(number + 1) keys")
            for (folderRelative, want) in step["sets"] as? [String: [String: Any]] ?? [:] {
                let folder = home.appendingPathComponent(folderRelative).path
                #expect(source.codexRuntime(ofFolder: folder) == (want["codexRuntime"] as? Bool), "step \(number + 1) \(folderRelative)")
                for (sessionId, agent) in want["agents"] as? [String: [String: Any]] ?? [:] {
                    let state = source.agentStates[HermesLogSet.agentId(folder: folder, sessionId: sessionId)]
                    let label = "step \(number + 1) \(folderRelative)#\(sessionId)"
                    #expect(state?.phase.rawValue == agent.string("phase"), "\(label) phase")
                    #expect(state?.inFlightStartMs == agent.int64("inFlightStartMs"), "\(label) in flight")
                    #expect(state?.lastLineMs == agent.int64("lastLineMs"), "\(label) last line")
                    if let model = agent.string("model") { #expect(state?.model == model, "\(label) model") }
                }
            }
        }
        #expect(reads >= 12)
    }

    // MARK: - Grammar

    static func parse(_ line: String, zone: TimeZone = losAngeles) -> HermesLine? {
        Array(line.utf8).withUnsafeBytes { HermesLine.parse($0, timeZone: zone) }
    }

    static func call(_ message: String) -> HermesCall? {
        guard case .call(let call) = parse("2026-10-20 05:00:00,000 INFO [s] agent.conversation_loop: " + message)?.kind else {
            Issue.record("not an API-call line: \(message)")
            return nil
        }
        return call
    }

    @Test func apiCallGrammarBacktracksLikeTheReference() throws {
        let plain = try #require(Self.call("API call #7: model=m provider=p in=10 out=2 total=12 latency=1.25s"))
        #expect(plain == HermesCall(number: "7", model: "m", provider: "p", input: "10", output: "2", latency: "1.25"))
        // The lazy model group ends at the first ` provider=` after which the rest matches.
        let spaced = try #require(Self.call("API call #1: model=a provider=b c provider=d in=1 out=1 total=2 latency=0.1s"))
        #expect(spaced.model == "a provider=b c" && spaced.provider == "d")
        #expect(Self.call("API call #01: model= provider= in=? out=? total=? latency=2s")
            == HermesCall(number: "01", model: "", provider: "", input: nil, output: nil, latency: "2"))
        // Cache and write count only when a space or the end follows them; ` .*` takes the rest.
        let both = try #require(Self.call("API call #2: model=m provider=p in=5 out=1 total=6 latency=1s cache=3/5 (60%) write=2 id=x y"))
        #expect(both.cacheRead == "3" && both.cacheWrite == "2")
        let glued = try #require(Self.call("API call #2: model=m provider=p in=5 out=1 total=6 latency=1s cache=3/5 (60%)x write=2"))
        #expect(glued.cacheRead == nil && glued.cacheWrite == nil)
        let writeOnly = try #require(Self.call("API call #2: model=m provider=p in=5 out=1 total=6 latency=1s write=-4"))
        #expect(writeOnly.cacheRead == nil && writeOnly.cacheWrite == "-4")
        #expect(try #require(Self.call("API call #2: model=m provider=p in=5 out=1 total=6 latency=1s write=4x")).cacheWrite == nil)
        for malformed in ["API call #x: model=m provider=p in=1 out=1 total=2 latency=1s",
                          "API call #2: model=m provider=p in=1 out=1 total=2 latency=1.s",
                          "API call #2: model=m provider=p in=1 out=1 total=2 latency=1sx",
                          "API call #2: model=m provider=p in=-? out=1 total=2 latency=1s",
                          "API call #2: model=m provider=p in=1 out=1 latency=1s",
                          "API call #2 model=m provider=p in=1 out=1 total=2 latency=1s"] {
            #expect(Self.call(malformed) == nil, "\(malformed)")
        }
    }

    @Test func responsesFollowTheTokenAndTimingRules() throws {
        func response(_ tail: String, provider: String = "p") -> ResponseRecord? {
            guard let call = Self.call("API call #3: model=m provider=\(provider) \(tail)") else { return nil }
            return HermesLogSet.response(call, key: "k", agentId: "/l#s", endMs: 10_000)
        }
        let negative = try #require(response("in=-5 out=1000000000000000000000 total=0 latency=0.0009s cache=-1/0 (0%)"))
        #expect(negative.usage == TokenUsage(input: 0, cachedInput: 0, output: 1_000_000_000_000))
        #expect(negative.startMs == 10_000, "latency truncated to whole milliseconds")
        #expect(response("in=0 out=0 total=0 latency=1s") == nil, "zero usage")
        #expect(response("in=? out=4 total=? latency=1s") == nil, "no usage")
        #expect(response("in=1 out=0 total=1 latency=-2.5s")?.startMs == nil, "a wall-clock step")
        #expect(response("in=1 out=1 total=2 latency=99999999999999999999.5s")?.startMs == 10_000 - Int64.max, "saturated")
        let precise = try #require(response("in=1 out=1 total=2 latency=1.23456s"))
        #expect(precise.startMs == 8766, "1.234 s, the rest of the digits dropped")
        #expect(response("in=1 out=1 total=2 latency=1s", provider: "unknown")?.provider == nil)
        #expect(response("in=1 out=1 total=2 latency=1s", provider: "")?.provider == nil)
    }

    @Test func linesBelongToTheFirstLoggerThatSplits() throws {
        // A tagged id is the text up to the first `] <logger>: `; agent.turn_context is tried first.
        let turn = try #require(Self.parse("2026-10-20 05:00:00,000 INFO [a] b] agent.turn_context: conversation turn: "
            + "session=x model=m provider=p platform=cli history=0 msg='hi'"))
        #expect(turn.sessionId == "a] b" && turn.kind == .turn(model: "m", platform: "cli"))
        #expect(Self.parse("2026-10-20 05:00:00,000 INFO [] agent.conversation_loop: Turn ended: x") == nil, "an empty tag")
        #expect(Self.parse("2026-10-20 05:00:00,000 WARNING agent.turn_context: conversation turn: session=x model=m "
            + "provider=p platform=cli history=0 msg=''") == nil, "turn starts are INFO")
        #expect(Self.parse("2026-10-20 05:00:00,000 ERROR [s] agent.conversation_loop: [subagent-1]   API call failed after 3 retries")?
            .kind == .end)
        #expect(Self.parse("2026-10-20 05:00:00,000 ERROR [s] agent.conversation_loop: oops API call failed after 3") == nil)
        #expect(Self.parse("2026-10-20 05:00:00,000 WARNING [s] agent.conversation_loop: API call #1: model=m provider=p "
            + "in=1 out=1 total=2 latency=1s") == nil, "API calls are INFO")
        #expect(Self.parse("2026-10-20 05:00:00,000 INFO [s] agent.tool_executor: Turn ended: x") == nil)
        #expect(Self.parse("2026-10-20 05:00:00,000 INFO") == nil && Self.parse("2026-10-20 05:00:00,000 INFOX [s] agent.x: y") == nil)
        let crlf = try #require(Self.parse("2026-10-20 05:00:00,000 INFO agent.conversation_loop: Turn ended: x\r"))
        #expect(crlf.sessionId == "" && crlf.kind == .end && crlf.stamp == "2026-10-20 05:00:00,000")
        let invalid = try #require(Self.parse("2026-10-20 05:00:00,000 INFO [s\u{FFFD}] agent.conversation_loop: Turn ended: x"))
        #expect(invalid.sessionId == "s\u{FFFD}")
    }

    @Test func fileNames() {
        for name in ["agent.log", "agent.log.1", "agent.log.12"] { #expect(HermesHome.isLogFileName(name), "\(name)") }
        for name in ["agent.log.", "agent.log.x", "agent.log.1.gz", "agent.log.bak", "errors.log", "agent.logs", "agent.log.١"] {
            #expect(!HermesHome.isLogFileName(name), "\(name)")
        }
    }

    // MARK: - Source

    @Test func sourceOrderAndProcessRow() {
        #expect(SourceKind.allCases.firstIndex(of: .hermes) == SourceKind.allCases.firstIndex(of: .openclaw)! + 1)
        #expect(SourceSet(.hermes).rawValue == 1 << 15)
        #expect(TranscriptFamily(.hermes) == nil)
        let mac = AgentProcessRules(platform: TrafficPlatform(kind: .macos, home: "/Users/dev"))
        func source(_ executable: String, _ arguments: [String] = []) -> SourceKind? {
            mac.source(executable: executable, arguments: { arguments }, ancestorExecutables: [])
        }
        let framework = "/Library/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python"
        #expect(source(framework, ["Python", "-m", "hermes_cli.main", "gateway"]) == .hermes)
        #expect(source("/Users/dev/.hermes/venv/bin/python3.12", ["python3.12", "-I", "-c", "import hermes_bootstrap"]) == .hermes)
        #expect(source("/usr/bin/python3", ["python3", "/Users/dev/.local/bin/hermes", "chat"]) == .hermes)
        #expect(source("/usr/bin/pythonw", ["pythonw", "-m", "acp_adapter"]) == .hermes)
        #expect(source("/usr/bin/python3", ["/Users/dev/hermes_cli/main.py"]) == nil, "only arguments after the first")
        #expect(source("/usr/bin/python3", ["python3", "/Users/dev/bin/Hermes"]) == nil, "names keep their case on macOS")
        #expect(source("/usr/bin/python3", ["python3", "-m", "http.server"]) == nil)
        #expect(source("/usr/local/bin/hermes", ["hermes", "chat"]) == nil, "a launcher sends nothing")
        #expect(source("/usr/bin/python3x", ["python3x", "-m", "hermes_cli.main"]) == nil)
        #expect(mac.isPythonHost("/usr/bin/python") && mac.isPythonHost("/usr/bin/python3.14") && !mac.isPythonHost("/usr/bin/python.3"))
        #expect(mac.needsArguments(executable: framework, bundleIdentifier: { nil }))
        let status = AgentStatus(id: "/Users/dev/.hermes/logs#s1", source: .hermes, project: ".hermes", model: nil, phase: .idle,
                                 isSubagent: false, live: .zero, recentSpeed: nil, lastActivity: Date())
        #expect(TrafficAgent(status).transcript == nil, "the log is shared by every session of its folder")
        #expect(HermesSource.project(ofFolder: "/Users/dev/.hermes/profiles/work/logs") == "work")
        #expect(HermesSource.project(ofFolder: "/Users/dev/.hermes/logs/") == ".hermes")
    }
}
