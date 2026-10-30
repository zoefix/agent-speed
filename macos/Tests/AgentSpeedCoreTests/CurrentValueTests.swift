import Foundation
import Testing
@testable import AgentSpeedCore

/// What an agent shows beyond its own facts (EXECUTION-MODES, Current values): the config files, the session-current
/// stores, Brotli rows, and that none of them reaches a response or a report.
@Suite("Current values")
struct CurrentValueTests {
    private static func decode(_ base64: String, size: Int) throws -> [UInt8]? {
        try #require(Data(base64Encoded: base64)).withUnsafeBytes { Brotli.decode($0, size: size) }
    }

    private static let row = "ExAIAMBSpVW1aCqQ7LtNAIS2pkFABXzx3creJH4SO792Dp2iPD1waVGreWCt5RjQdznoL/8WXv0gcrshGUbpvnBqRzMJ+hkmXQSMsmoOs/z6ZvJPQyt2hUOVtP6o1VQyuHPOxdcIBg1d6OO9eiLq/IMQ+Wci8tSmA1EHh1o2CwqGbbmlReHQrquC4/adFm0SpQkcWgWjp/7Jd+R7+urumYjp4UTU/a3Q/+A+lj+RdFprKnkvsA3f9AqGjJobL5Ly4HCwLbHCtuw7OPwW+6mrBH1JBsb5u6rVc9TL+fQ6OesUDif1eccklrVW8H42Wp7D4bCUqDN4x2rlkqIaGJLbZGVNAQ5ZFgUjzLJF9WXdqn/0j3CQNckv+kVrlVErHAap7aNEBQ8yV3VoU8o/73rRGYwpjRMcTKWWnPLI1ZRhwOEQSh7SCN5hGiU0jeAduTQF4xanG+nf12bSUsl3N6ThOI4D"

    @Test func aBrotliRowDecodesToExactlyItsSize() throws {
        let text = try #require(try Self.decode(Self.row, size: 2065))
        #expect(text.count == 2065)
        let modes = text.withUnsafeBytes(OpenClawTraceExtractor.modes)
        #expect(modes?.attribution(for: .effort).mode == "high")
        #expect(modes?.attribution(for: .speed).isUnknown == true, "fastMode false says nothing")
    }

    @Test func damagedTruncatedAndOversizedStreamsGiveNothing() throws {
        #expect(try Self.decode("G/8gbm90IGEgYnJvdGxpIHN0cmVhbQ==", size: 64) == nil, "damaged")
        #expect(try Self.decode("E/QHAADKbamOyyJ9rT1uURBZUMSSxA==", size: 2030) == nil, "truncated")
        #expect(try Self.decode("Ez4AAMBcW+qXH+kL2VKGf6C490UORcZKo40iiYT+mzDo8Y3LoLy7HA1siTE7A5bQflznypcBQ8wPVQU=", size: 9) == nil,
                "longer than declared")
        let bomb = "y///PwAkAOKxQHLv//j//weABEAcFoDu/R////8AkACIwwLQvf/j//8fABIAcVgAuvd//P//A0ACIA4LQPf+j///fwBIAMRhAeje//H//w8ACYA4LADd+z/+//8BIAEQhwWge/83"
        #expect(try Self.decode(bomb, size: 2065) == nil, "a small stream that expands past its size")
        let stream = try #require(Data(base64Encoded: Self.row))
        #expect(stream.dropLast().withUnsafeBytes { Brotli.decode($0, size: 2065) } == nil, "cut short")
        #expect(stream.withUnsafeBytes { Brotli.decode($0, size: 2064) } == nil, "shorter than decoded")
        #expect(stream.withUnsafeBytes { Brotli.decode($0, size: 2066) } == nil, "longer than decoded")
    }

    @Test func traceRowsKeepOnlyThinkLevelAndFastMode() {
        func modes(_ text: String) -> ExecutionModes? { Data(text.utf8).withUnsafeBytes(OpenClawTraceExtractor.modes) }
        let full = modes(#"{"type":"trace.metadata","data":{"config":{"x":1},"model":{"thinkLevel":"adaptive","fastMode":true,"reasoningLevel":"on"}}}"#)
        #expect(full?.attribution(for: .effort).mode == "adaptive")
        #expect(full?.attribution(for: .speed) == ExecutionModeAttribution(kind: .speed, mode: "fast", evidence: "selected"))
        #expect(modes(#"{"type":"trace.metadata","data":{"model":{"fastMode":false}}}"#)?.facts.isEmpty == true, "false says nothing")
        #expect(modes(#"{"type":"trace.metadata","data":{"trimmed":true}}"#)?.facts.isEmpty == true, "kept, without facts")
        #expect(modes(#"{"type":"model.completed","data":{"model":{"thinkLevel":"high"}}}"#) == nil)
        #expect(modes("[1]") == nil)
        #expect(modes("{\"type\":\"trace.metadata\"") == nil)
    }

    @Test func aLateTraceRowUpdatesItsResponsesAndATrimmedRowEndsTheReach() throws {
        var deduper = Deduper()
        var parser = OpenClawParser(agentId: OpenClawTests.mainPath, fileID: 1, window: OpenClawTests.window())
        var upserts: [ResponseUpsert] = []
        let t0 = OpenClawTests.t0
        func effort(_ token: String) -> ExecutionModes {
            ExecutionModes([ExecutionModeAdapters.effort(token, evidence: .recorded, scope: .turn)].compactMap { $0 })
        }
        parser.addTraces([OpenClawTrace(createdMs: t0, modes: effort("low"))]) { upserts.append($0) }
        for line in [OpenClawTests.header(), OpenClawTests.assistant("a", at: t0 + 2000, start: t0 + 1000),
                     OpenClawTests.assistant("b", at: t0 + 4000, start: t0 + 3000)] {
            Data(line.utf8).withUnsafeBytes { parser.consume(OpenClawLineExtractor.extract($0), deduper: &deduper) { upserts.append($0) } }
        }
        #expect(upserts.map { $0.record.modes.effortAndSpeed.effort?.mode } == ["low", "low"])
        upserts.removeAll()
        parser.addTraces([OpenClawTrace(createdMs: t0 + 2500, modes: effort("max"))]) { upserts.append($0) }
        #expect(upserts.count == 1 && upserts[0].isCompletion == false)
        #expect(upserts[0].record.key.hasPrefix("openclaw:b:") && upserts[0].record.modes.effortAndSpeed.effort?.mode == "max")
        #expect(parser.state.modes.effortAndSpeed.effort?.mode == "max")
        upserts.removeAll()
        parser.addTraces([OpenClawTrace(createdMs: t0 + 2800, modes: .empty)]) { upserts.append($0) }
        #expect(upserts.first?.record.modes.facts.isEmpty == true, "a trimmed row leaves its attempt unknown")
        #expect(parser.state.modes.facts.isEmpty)
    }

    @Test func aTraceRowWrittenAfterItsTranscriptReachesTheLiveWindow() throws {
        let directory = try TempDirectory()
        let relative = "agents/main/agent/openclaw-agent.sqlite"
        let path = directory.file(relative).path
        try FileManager.default.createDirectory(at: directory.file("agents/main/agent"), withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent("openclaw/.openclaw/" + relative), to: directory.file(relative))
        let window = "0f1c2d3e-0001-4a00-8000-00000000c001"
        let source = OpenClawSource(roots: [directory.path])
        var deduper = Deduper()
        let seeded = source.seed(todayStartMs: 0, deduper: &deduper)
        let agentId = OpenClawSource.agentId(path, window)
        let last = try #require(seeded.upserts.map(\.upsert.record).filter { $0.agentId == agentId && $0.startMs != nil }.last)
        let before = last.modes
        let writer = try SQLiteConnection(path: path, mode: .readWriteCreate)
        let insert = try writer.prepare("""
            INSERT INTO trajectory_runtime_events (session_id, seq, event_json, created_at)
            VALUES (?1, 99, '{"type":"trace.metadata","data":{"model":{"thinkLevel":"ultra"}}}', ?2)
            """)
        insert.bind(1, window)
        insert.bind(2, try #require(last.startMs))
        try insert.run()
        writer.close()
        let update = source.refreshIfChanged(deduper: &deduper)
        #expect(update.agents[agentId]?.modes.effortAndSpeed.effort?.mode == "ultra")
        let changed = update.upserts.map(\.upsert).filter { $0.record.key == last.key }
        #expect(before.attribution(for: .effort).mode != "ultra")
        #expect(changed.count == 1 && changed.first?.isCompletion == false)
        #expect(changed.first?.record.modes.effortAndSpeed.effort?.mode == "ultra", "a late row updates a response like any upsert")
    }

    // MARK: - Config files

    @Test func geminiOverridesCountOnlyForTheAgentsModel() throws {
        let directory = try TempDirectory()
        let path = directory.file("settings.json").path
        try #"""
        {"modelConfigs":{
          "overrides":[
            {"match":{"model":"gemini-a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"LOW"}}}},
            {"match":{"model":"gemini-b","overrideScope":"core"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}},
            {"match":{"model":"gemini-c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":0}}}},
            {"match":{"model":"gemini-c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":8192}}}}
          ],
          "customOverrides":[
            {"match":{"model":"gemini-a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}}
          ]}}
        """#.write(toFile: path, atomically: true, encoding: .utf8)
        let efforts = ConfiguredModes.gemini(path: path)
        func current(_ model: String) -> ExecutionModeAttribution? {
            EffortAndSpeed(facts: .empty, configured: efforts.modes(model: model)).effort
        }
        #expect(current("gemini-a") == ExecutionModeAttribution(kind: .effort, mode: "high", evidence: "configured"))
        #expect(current("gemini-b") == nil, "a match with another member is not this model's")
        #expect(current("gemini-c")?.mode == "off", "a budget that names no level is passed over")
        #expect(efforts.modes(model: "gemini-a").attribution(for: .effort).isUnknown, "configured never counts as attribution")
    }

    @Test func configFilesAreRegularBoundedUTF8AndKeepOnlyTheNamedKey() throws {
        let directory = try TempDirectory()
        let path = directory.file("settings.json").path
        try Data([0xEF, 0xBB, 0xBF] + Array(#"{"reasoningEffort":"xhigh","apiKey":"secret"}"#.utf8)).write(to: directory.file("settings.json"))
        let modes = ConfiguredModes.workBuddy(path: path)
        #expect(modes.facts.map(\.value) == ["xhigh"] && modes.facts.first?.evidence == .configured)
        let oversized = directory.file("big.json")
        try Data(#"{"reasoningEffort":"high"}"#.utf8 + Array(repeating: 0x20, count: 1 << 20)).write(to: oversized)
        #expect(ConfiguredModes.workBuddy(path: oversized.path).facts.isEmpty)
        let invalid = directory.file("invalid.json")
        try Data(#"{"reasoningEffort":""#.utf8 + [0xFF] + #""}"#.utf8).write(to: invalid)
        #expect(ConfiguredModes.workBuddy(path: invalid.path).facts.isEmpty)
        let list = directory.file("list.json")
        try #"[{"reasoningEffort":"high"}]"#.write(to: list, atomically: true, encoding: .utf8)
        #expect(ConfiguredModes.workBuddy(path: list.path).facts.isEmpty)
        let link = directory.file("link.json")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: URL(fileURLWithPath: path))
        #expect(ConfiguredModes.workBuddy(path: link.path).facts.isEmpty)
    }

    @Test func antigravityTierGoesThroughTheGoogleAdapter() throws {
        let directory = try TempDirectory()
        let path = directory.file("config.json").path
        func tier(_ value: String) throws -> ExecutionModeAttribution? {
            try #"{"userSettings":{"vertexServiceTier":"\#(value)"}}"#.write(toFile: path, atomically: true, encoding: .utf8)
            return EffortAndSpeed(facts: .empty, configured: ConfiguredModes.antigravity(path: path)).speed
        }
        #expect(try tier("priority") == ExecutionModeAttribution(kind: .speed, mode: "fast", evidence: "configured", provider: "google"))
        #expect(try tier("flex")?.mode == "flex")
        #expect(try tier("unspecified") == nil)
    }

    @Test func antigravityConfigSitsBesideTheStore() {
        #expect(TranscriptLocations.antigravityConfig(ofRoot: "/Users/a/.gemini/antigravity-cli/conversations")
            == "/Users/a/.gemini/config/config.json")
    }

    @Test func geminiSettingsFollowASetRoot() {
        var configuration = MonitorConfiguration()
        configuration.automaticRoots[.gemini] = [URL(fileURLWithPath: "/fixtures/gemini/tmp")]
        #expect(configuration.resolvedGeminiSettings == "/fixtures/gemini/settings.json")
    }

    // MARK: - Reading

    private static func writeWorkBuddy(_ folder: URL, wal: Bool, level: String?) throws {
        let connection = try SQLiteConnection(path: folder.appendingPathComponent("workbuddy.db").path, mode: .readWriteCreate)
        if wal { try connection.execute("PRAGMA journal_mode=WAL") }
        try connection.execute("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, title TEXT, thought_level TEXT)")
        try connection.execute("INSERT OR REPLACE INTO sessions VALUES('s-1','private title',\(level.map { "'\($0)'" } ?? "NULL"))")
        if wal { try connection.execute("PRAGMA wal_checkpoint(TRUNCATE)") }
        connection.close()
        // A cleanly closed WAL database: the macOS library keeps these files, the apps that own such a database do not.
        for suffix in ["-wal", "-shm"] {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent("workbuddy.db" + suffix))
        }
    }

    @Test func sessionStoresCreateNoFilesAndGiveNothingForAMissingRow() throws {
        let directory = try TempDirectory()
        try Self.writeWorkBuddy(directory.url, wal: true, level: "disabled")
        let path = directory.file("workbuddy.db").path
        let before = try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
        #expect(!before.contains("workbuddy.db-wal") && !before.contains("workbuddy.db-shm"))
        let values = SessionModes.workBuddy(databasePath: path, sessions: ["s-1", "s-2"])
        #expect(values["s-1"]?.facts.first?.value == "off" && values["s-1"]?.facts.first?.evidence == .selected)
        #expect(values["s-2"] == nil)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() == before)
        #expect(SessionModes.workBuddy(databasePath: directory.file("none.db").path, sessions: ["s-1"]).isEmpty)
        #expect(SessionModes.hermes(databasePath: path, sessions: ["s-1"]).isEmpty, "a table without the column gives nothing")
    }

    @Test func hermesReasoningConfigTakesOnlyItsEffort() throws {
        let directory = try TempDirectory()
        let path = directory.file("state.db").path
        let connection = try SQLiteConnection(path: path, mode: .readWriteCreate)
        try connection.execute("CREATE TABLE sessions(id TEXT PRIMARY KEY, model_config TEXT, system_prompt TEXT)")
        for (id, config) in [("on", #"{"reasoning_config":{"enabled":true,"effort":"high"},"service_tier":"fast"}"#),
                             ("off", #"{"reasoning_config":{"enabled":false,"effort":"high"}}"#),
                             ("bare", #"{"model":"m"}"#), ("broken", "not json"),
                             ("huge", #"{"reasoning_config":{"effort":"high"},"x":""# + String(repeating: "x", count: 70_000) + #""}"#)] {
            let insert = try connection.prepare("INSERT INTO sessions VALUES(?1,?2,'prompt')")
            insert.bind(1, id)
            insert.bind(2, config)
            try insert.run()
        }
        connection.close()
        let values = SessionModes.hermes(databasePath: path, sessions: ["on", "off", "bare", "broken", "huge", "none"])
        #expect(values["on"]?.facts.first?.value == "high")
        #expect(values["off"]?.facts.first?.value == "off")
        #expect(values["bare"]?.facts.isEmpty == true && values["broken"]?.facts.isEmpty == true)
        #expect(values["huge"] == nil && values["none"] == nil)
    }

    private static func workBuddySession(_ directory: TempDirectory, enabled: Set<SourceKind> = [.workbuddy, .codex]) throws
        -> (session: TranscriptSession, transcript: String) {
        let data = directory.file("data")
        let folder = data.appendingPathComponent("projects/slug")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let line = #"{"id":"m1","type":"message","role":"user","timestamp":1792497600000,"cwd":"/w/p","content":[{"type":"input_text","text":"x"}]}"#
        try (line + "\n").write(to: folder.appendingPathComponent("s-1.jsonl"), atomically: true, encoding: .utf8)
        let configuration = MonitorConfiguration(enabledSources: enabled, workbuddyRoot: data)
        return (TranscriptSession(configuration: configuration, clock: { 1_792_497_700_000 }), folder.appendingPathComponent("s-1.jsonl").path)
    }

    @Test func workBuddyAgentsTakeTheStoresAtRediscoveryOnly() throws {
        let directory = try TempDirectory()
        let (session, transcript) = try Self.workBuddySession(directory)
        let data = directory.file("data")
        try #"{"reasoningEffort":"low"}"#.write(to: data.appendingPathComponent("settings.json"), atomically: true, encoding: .utf8)
        try Self.writeWorkBuddy(data, wal: false, level: "high")
        _ = session.seed(todayStartMs: 1_792_454_400_000)
        func current() -> EffortAndSpeed? {
            let output = session.advance(nowMs: 1_792_497_700_000, todayStartMs: 1_792_454_400_000, emitsEvents: false).output
            return output.agents.first { $0.agent.id == transcript }.map {
                EffortAndSpeed(facts: $0.agent.modes, session: $0.agent.sessionModes, configured: $0.agent.configuredModes)
            }
        }
        #expect(current()?.effort == ExecutionModeAttribution(kind: .effort, mode: "high", evidence: "selected"))
        try Self.writeWorkBuddy(data, wal: false, level: nil)
        #expect(current()?.effort?.mode == "high", "read again only at rediscovery")
        #expect(session.rediscover(todayStartMs: 1_792_454_400_000))
        #expect(current()?.effort == ExecutionModeAttribution(kind: .effort, mode: "low", evidence: "configured"))
        #expect(!session.rediscover(todayStartMs: 1_792_454_400_000), "nothing changed")
    }

    @Test func aDisabledSourcesFilesAreNotRead() throws {
        let directory = try TempDirectory()
        let (session, _) = try Self.workBuddySession(directory, enabled: [.codex])
        try #"{"reasoningEffort":"low"}"#.write(to: directory.file("data/settings.json"), atomically: true, encoding: .utf8)
        _ = session.seed(todayStartMs: 1_792_454_400_000)
        #expect(session.configuredModes(of: .workbuddy).facts.isEmpty)
    }

    // MARK: - Ledger

    @Test func aLiveGrokResponseFreezesTheSummarysEffort() throws {
        let directory = try TempDirectory()
        let wire = "sessions/work/one/updates.jsonl"
        try directory.write("sessions/work/one/summary.json",
                            #"{"info":{"id":"one","cwd":"/projects/nova"},"current_model_id":"grok-4.5","reasoning_effort":"high"}"#)
        try directory.write(wire, "")
        let configuration = MonitorConfiguration(enabledSources: [.grokBuild], grokBuildRoot: directory.file("sessions"))
        let base = HarnessEdgeTests.base
        let session = TranscriptSession(configuration: configuration, clock: { base + 5000 })
        _ = session.seed(todayStartMs: base - 3_600_000)
        try directory.append(wire, HarnessEdgeTests.grokTurn())
        #expect(session.handleChanges(atCanonicalPaths: [try WorkBuddySessionTests.canonical(directory.file(wire).path)]))
        let report = session.advance(nowMs: base + 5000, todayStartMs: base - 3_600_000, emitsEvents: true).output.report
        let efforts = report.counters.byMode.keys.map(\.attribution).filter { $0.kind == .effort }
        #expect(efforts == [ExecutionModeAttribution(kind: .effort, mode: "high", evidence: "selected")])
        // The response itself and the history stay unknown: a session-current value is not a response's.
        #expect(session.responses.first?.modes.facts.isEmpty == true)
        let history = try HistoryScan.run(configuration: configuration, storePath: directory.file("history.sqlite").path)
        #expect(history.days.first?.modeUsage.allSatisfy { $0.attribution.kind != .effort || $0.attribution.isUnknown } == true)
    }

    @Test func aLiveResponseFreezesTheSessionCurrentEffortAndNeverAConfiguredOne() throws {
        func facts(_ effort: String?, configured: Bool) -> [ReportModeKey: ReportUsage] {
            var engine = SpeedEngine()
            let record = ResponseRecord(key: "k", source: .grokBuild, agentId: "a", model: "m", startMs: 1_792_497_600_000,
                                        endMs: 1_792_497_601_000, usage: TokenUsage(output: 10))
            let current = effort.flatMap { ExecutionModeAdapters.effort($0, evidence: configured ? .configured : .selected,
                                                                        scope: configured ? .configuration : .session) }
            let view = EffortAndSpeed(facts: .empty, session: configured ? .empty : ExecutionModes([current].compactMap { $0 }),
                                      configured: configured ? ExecutionModes([current].compactMap { $0 }) : .empty)
            engine.upsert(record, project: "p", isCompletion: true, emitsEvents: true, nowMs: 1_792_497_601_500,
                          currentEffort: view.effort)
            _ = engine.advance(nowMs: 1_792_497_601_500, todayStartMs: 1_792_454_400_000, emitsEvents: true)
            return engine.advance(nowMs: 1_792_497_601_500, todayStartMs: 1_792_454_400_000, emitsEvents: true).report.counters.byMode
        }
        let selected = facts("high", configured: false).keys.map(\.attribution)
        #expect(selected.contains(ExecutionModeAttribution(kind: .effort, mode: "high", evidence: "selected")))
        #expect(facts("high", configured: true).keys.allSatisfy { $0.attribution.kind != .effort })
        #expect(facts(nil, configured: false).isEmpty)
    }
}
