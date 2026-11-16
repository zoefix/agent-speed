import Foundation
import Testing
@testable import AgentSpeedCore

/// CodeBuddy Code (METRICS 4.20): the WorkBuddy format in the standalone CLI's own folder, with the measured attempts
/// of CLI 2.184.0 (METRICS 4.9 Timing model).
@Suite("CodeBuddy Code")
struct CodeBuddyTests {
    static var fixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["codebuddyCode"] as? [String: Any]) }
    }

    /// The fixture folder `codebuddy` plays the config folder `~/.codebuddy`.
    static var fixtureRoot: String {
        TranscriptLocations.workbuddyProjects(Fixtures.root.appendingPathComponent("codebuddy", isDirectory: true)).path
    }

    // MARK: - Fixtures

    @Test func fixturesMatchTheReference() throws {
        let (expected, results) = try ParserFixtureTests.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        let codebuddy = files.filter { $0.key.hasPrefix("codebuddy/") }
        #expect(codebuddy.count == 3)
        for (relative, spec) in codebuddy {
            let result = try #require(results[relative])
            #expect(result.state.source == .codebuddyCode)
            let summary = OmpTests.summary(result)
            for (field, wanted) in spec where !["parentFile", "effort", "speed", "responseModes"].contains(field) {
                let actual = summary[field] ?? NSNull()
                #expect((actual as AnyObject).isEqual(wanted), "\(relative) \(field): \(actual)")
            }
        }
    }

    @Test func tracesMatchPhaseAfterEveryCheckpoint() throws {
        let traces = try #require(try Self.fixtures["traces"] as? [[String: Any]])
        #expect(traces.count >= 20)
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

    @Test func discoveryFindsOnlySessionFiles() throws {
        let wanted = try #require(try Self.fixtures["discover"] as? [String])
        let prefix = Fixtures.root.path + "/"
        #expect(TranscriptFamily.codebuddyCode.names == .workbuddySession)
        var index = DirectoryIndex(root: Self.fixtureRoot, names: TranscriptFamily.codebuddyCode.names)
        #expect(index.scan().map { String($0.dropFirst(prefix.count)) }.sorted() == wanted)
    }

    // MARK: - Roots

    @Test func theConfigFolderComesFromTheVariableWhenItIsNotBlank() {
        let home = URL(fileURLWithPath: "/home/dev", isDirectory: true)
        func folder(_ environment: [String: String]) -> String {
            TranscriptLocations.codebuddyCodeConfigFolder(environment: environment, home: home).path
        }
        #expect(folder([:]) == "/home/dev/.codebuddy")
        #expect(folder(["CODEBUDDY_CONFIG_DIR": "/data/cb"]) == "/data/cb")
        #expect(folder(["CODEBUDDY_CONFIG_DIR": ""]) == "/home/dev/.codebuddy")
        #expect(folder(["CODEBUDDY_CONFIG_DIR": " \t\u{3000}"]) == "/home/dev/.codebuddy")
    }

    @Test func rootsAreTheConfigFoldersProjects() {
        let defaults = MonitorConfiguration()
        let automatic = TranscriptLocations.defaultCodebuddyCodeConfigFolder.appendingPathComponent("projects").path
        #expect(defaults.resolvedRoots(for: .codebuddyCode) == [automatic])
        let chosen = MonitorConfiguration(codebuddyCodeRoot: URL(fileURLWithPath: "/data/cb/", isDirectory: true))
        #expect(chosen.resolvedRoots(for: .codebuddyCode) == ["/data/cb/projects"])
        let projects = MonitorConfiguration(codebuddyCodeRoot: URL(fileURLWithPath: "/data/cb/projects", isDirectory: true))
        #expect(projects.resolvedRoots(for: .codebuddyCode) == ["/data/cb/projects"])
        #expect(TranscriptFamily(.codebuddyCode) == .codebuddyCode)
        #expect(SourceKind.codebuddyCode.transcriptFolder == .codebuddyCode)
        #expect(TranscriptFamily.codebuddyCode.writesWorkBuddyFormat && !TranscriptFamily.codebuddyCode.isWorkBuddy)
    }

    // MARK: - Measured attempts

    static func attempts(_ json: String) -> String {
        WorkBuddyTests.usage(1000, 40) + #","rawUsage":{"prompt_tokens":1000,"completion_tokens":40,"model_calls":\#(json)}"#
    }

    static func run(_ lines: [String]) -> WorkBuddyTests.Run {
        WorkBuddyTests.run(lines, source: .codebuddyCode)
    }

    @Test func theLastAttemptGivesTheStartAndTheFirstToken() {
        let t0 = WorkBuddyTests.t0
        let run = Self.run([
            WorkBuddyTests.user(t0),
            WorkBuddyTests.say(t0 + 5000, "m1", Self.attempts(
                #"[{"attempt_no":1,"duration_ms":3000,"result":"failed"},{"attempt_no":2,"duration_ms":2400.9,"time_to_first_token_ms":600.7}]"#)),
        ])
        let response = run.responses[0]
        #expect(response.source == .codebuddyCode)
        #expect(response.startMs == t0 + 2600 && response.endMs == t0 + 5000 && response.firstMs == t0 + 3200)
    }

    @Test func attemptsThatGiveNoStartKeepTheInferredOne() {
        let t0 = WorkBuddyTests.t0
        for json in [#"{"duration_ms":1000}"#, "[]", #"[{"duration_ms":900},"(text)"]"#, #"[{"duration_ms":"2500"}]"#,
                     #"[{"duration_ms":0.9}]"#, #"[{"duration_ms":1e999}]"#, #"[{"duration_ms":true}]"#,
                     #"[{"duration_ms":-5}]"#, #"[{"duration_ms":2000,"duration_ms":null}]"#, "null"] {
            let run = Self.run([WorkBuddyTests.user(t0), WorkBuddyTests.say(t0 + 3000, "m1", Self.attempts(json))])
            #expect(run.responses.first?.startMs == t0 && run.responses.first?.firstMs == nil, "\(json)")
        }
    }

    @Test func aFirstTokenOutsideTheDurationIsDropped() {
        let t0 = WorkBuddyTests.t0
        for (ttft, first) in [("3000", nil), ("-5", nil), ("0.5", nil), ("1e999", nil), (#""9""#, nil), ("2000", t0 + 3000)] as [(String, Int64?)] {
            let run = Self.run([WorkBuddyTests.user(t0), WorkBuddyTests.say(t0 + 3000, "m1", Self.attempts(
                #"[{"duration_ms":2000,"time_to_first_token_ms":\#(ttft)}]"#))])
            #expect(run.responses.first?.startMs == t0 + 1000 && run.responses.first?.firstMs == first, "\(ttft)")
        }
    }

    /// A call whose usage line lacks raw usage starts at the input line until the end-of-run `model-usage` line brings
    /// its attempts; the first measured start is kept.
    @Test func aLaterLineWithAttemptsMovesTheStartOnce() {
        let t0 = WorkBuddyTests.t0
        let modelUsage = { (ms: Int64, duration: Int) in
            #"{"type":"model-usage","id":"u\#(ms)","timestamp":\#(ms),"providerData":{"messageId":"m1","model":"glm-5.3",\#(Self.attempts("[{\"duration_ms\":\(duration),\"time_to_first_token_ms\":100}]"))}}"#
        }
        let run = Self.run([
            WorkBuddyTests.user(t0),
            WorkBuddyTests.say(t0 + 5000, "m1", WorkBuddyTests.usage(1000, 40)),
            modelUsage(t0 + 4990, 2000),
            modelUsage(t0 + 6000, 500),
        ])
        #expect(run.responses.count == 1)
        let response = run.responses[0]
        #expect(response.startMs == t0 + 2990 && response.firstMs == t0 + 3090 && response.endMs == t0 + 6000)
        #expect(run.completions == ["m1"])
    }

    /// A start before Int64.min saturates (METRICS 3.6): year 0000 minus a duration that truncates to 2^63 − 1.
    @Test func aMeasuredStartSaturates() {
        let line = #"{"id":"a","timestamp":"0000-01-01T00:00:00Z","type":"message","role":"assistant","status":"completed","providerData":{"messageId":"m1",\#(Self.attempts(#"[{"duration_ms":1e19,"time_to_first_token_ms":5}]"#))},"cwd":"/w/x"}"#
        let run = Self.run([line])
        #expect(run.responses.first?.startMs == Int64.min && run.responses.first?.firstMs == Int64.min + 5)
    }
}
