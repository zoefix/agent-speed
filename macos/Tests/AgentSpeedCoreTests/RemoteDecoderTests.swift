import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote protocol decoding")
struct RemoteDecoderTests {
    static let hostId = "8C0D6B0A-1111-4222-8333-944455556666"
    static let prefix = "ssh://\(hostId)/"
    let decoder = RelayLineDecoder(hostId: Self.hostId)

    func decode(_ line: String) -> RelayLineDecoder.Outcome {
        decoder.decode(Data(line.utf8))
    }

    func message(_ line: String, sourceLocation: SourceLocation = #_sourceLocation) -> RelayMessage? {
        guard case .message(let message) = decode(line) else {
            Issue.record("not a message: \(decode(line))", sourceLocation: sourceLocation)
            return nil
        }
        return message
    }

    func invalid(_ line: String) -> RemoteLineError? {
        if case .invalid(let error) = decode(line) { return error }
        return nil
    }

    func agentLine(_ change: (inout [String: Any]) -> Void) -> String {
        var object = RelayLines.agentObject()
        change(&object)
        return RelayLines.agent(object)
    }

    func responseLine(_ change: (inout [String: Any]) -> Void) -> String {
        var object = RelayLines.responseObject()
        change(&object)
        return RelayLines.response(object)
    }

    // MARK: Each message type

    @Test func hello() {
        #expect(message(RelayLines.hello()) == .hello(RelayHello(protocolVersion: 1, version: "1.7.0", arch: "x86_64",
                                                                 nowMs: RelayLines.baseMs)))
        #expect(message(#"{"t":"hello","protocol":2}"#) == .hello(RelayHello(protocolVersion: 2, version: "", arch: "", nowMs: 0)))
        #expect(invalid(#"{"t":"hello","version":"1"}"#)?.field == "protocol")
    }

    @Test func agentDecodesEveryField() throws {
        guard case .agent(let agent) = message(RelayLines.agent()) else { return }
        #expect(agent.hostId == Self.hostId)
        #expect(agent.id == Self.prefix + "/home/u/.codex/sessions/a.jsonl")
        #expect(agent.id.contains("//home"))
        #expect(agent.relayId == "/home/u/.codex/sessions/a.jsonl")
        #expect(agent.source == .codex)
        #expect(agent.project == "proj")
        #expect(agent.cwdPath == "/home/u/proj")
        #expect(agent.model == "gpt-5.5")
        #expect(!agent.isSubagent)
        #expect(agent.phase == .generating)
        #expect(agent.inFlightStartMs == RelayLines.baseMs)
        #expect(agent.lastLineMs == RelayLines.baseMs + 500)
        let fact = try #require(agent.modes.facts.first)
        #expect(fact.kind == .serviceTier && fact.value == "fast" && fact.rawValue == "priority" && fact.provider == "openai")
        #expect(agent.actions.count == 1)
        #expect(agent.actions[0].tool == "shell")
        #expect(agent.actions[0].call.digest == ToolDigest(hex: "0123456789abcdef"))
        #expect(agent.actions[0].resultMs == RelayLines.baseMs + 400)
        #expect(agent.actions[0].failed == false)
        #expect(agent.actions[0].seeded)

        let engine = agent.engineAgent(parentId: Self.prefix + "/p.jsonl")
        #expect(engine.id == agent.id)
        #expect(engine.project == "proj")
        #expect(engine.parentId == Self.prefix + "/p.jsonl")
        #expect(engine.actions == agent.actions)
        #expect(engine.modes == agent.modes)
    }

    @Test func agentWithNullsAndProjectFallback() {
        let line = agentLine {
            $0["project"] = NSNull()
            $0["cwd"] = NSNull()
            $0["model"] = NSNull()
            $0["inFlightStartMs"] = NSNull()
            $0.removeValue(forKey: "lastLineMs")
            $0["phase"] = "idle"
            $0["actions"] = [RelayLines.action(resultMs: NSNull())]
        }
        guard case .agent(let agent) = message(line) else { return }
        #expect(agent.project == nil && agent.cwdPath == nil && agent.model == nil)
        #expect(agent.inFlightStartMs == nil && agent.lastLineMs == nil)
        #expect(agent.actions[0].resultMs == nil)
        #expect(agent.engineAgent(parentId: nil).project == "sessions")
    }

    @Test func responseIsARecordWithThePrefixedAgent() throws {
        guard case .response(let response) = message(RelayLines.response(completion: false)) else { return }
        #expect(response.hostId == Self.hostId)
        #expect(!response.isCompletion)
        #expect(response.project == "proj")
        let record = response.record
        #expect(record.key == "resp_1")
        #expect(record.agentId == Self.prefix + "/home/u/.codex/sessions/a.jsonl")
        #expect(record.source == .codex)
        #expect(record.startMs == RelayLines.baseMs && record.endMs == RelayLines.baseMs + 2000)
        #expect(record.firstMs == RelayLines.baseMs + 300)
        #expect(record.usage == TokenUsage(input: 1200, cachedInput: 1000, output: 340, reasoning: 120))
        #expect(record.isFinal && !record.fast && record.provider == "openai" && record.requestCount == 1)
        #expect(record.costMicroUsd == nil)
    }

    @Test func otherMessages() {
        #expect(message(#"{"t":"progress","value":0.42}"#) == .progress(0.42))
        #expect(message(#"{"t":"progress","value":1.2}"#) == .progress(1))
        #expect(message(#"{"t":"agentRemoved","id":"/a"}"#) == .agentRemoved(id: Self.prefix + "/a"))
        #expect(message(#"{"t":"parents","links":{"/c":"/p"}}"#) == .parents([Self.prefix + "/c": Self.prefix + "/p"]))
        #expect(message(#"{"t":"ready"}"#) == .ready)
        #expect(message(#"{"t":"clock","id":7,"now":1780000000000}"#) == .clock(id: 7, nowMs: 1_780_000_000_000))
        #expect(message(#"{"t":"historyDone","days":3,"analyzed":12}"#) == .historyDone(days: 3, analyzed: 12))
        #expect(message(#"{"t":"historyProgress","value":0.5}"#) == .historyProgress(0.5))
        #expect(message(#"{"t":"error","code":"historyFailed","detail":"disk full"}"#)
            == .error(RelayError(code: .historyFailed, detail: "disk full")))
        #expect(message(#"{"t":"error","code":"newCode","detail":""}"#) == .error(RelayError(code: .other("newCode"), detail: "")))
        let tool = #"{"t":"tool","agent":"/a","source":"claudeCode","callMs":1780000000000,"resultMs":1780000001000,"#
            + #""failed":null,"model":null,"seeded":false}"#
        #expect(message(tool) == .tool(RemoteToolCompletion(
            hostId: Self.hostId, agentId: Self.prefix + "/a", source: .claudeCode,
            completion: ToolCompletion(callMs: 1_780_000_000_000, resultMs: 1_780_000_001_000, failed: nil, model: nil,
                                       seeded: false))))
    }

    @Test func statusSkipsUnknownSources() throws {
        let line = #"{"t":"status","sources":[{"source":"codex","available":true,"sessions":3,"unsupported":false,"#
            + #""configuredModes":[]},{"source":"futureAgent","available":true,"sessions":1,"unsupported":false,"#
            + #""configuredModes":[]}],"malformed":2,"unreadable":1,"partialHistory":false}"#
        guard case .status(let report) = message(line) else { return }
        #expect(report.sources == [RemoteSourceState(source: .codex, available: true, sessions: 3, unsupported: false,
                                                     configuredModes: .empty)])
        #expect(report.malformed == 2 && report.unreadable == 1 && !report.partialHistory)
    }

    @Test func historyDayKeepsTheExactAggregateText() throws {
        let aggregate = #"{"requests":3,"input":1200,"cachedInput":0,"cacheWriteInput":0,"output":340,"reasoning":0,"#
            + #""measuredOutput":300,"measuredMs":2000,"unfinalized":0,"agents":1,"costMicroUsd":1234,"unpricedRequests":0,"#
            + #""models":{"gpt-5.5":[3,340,300,2000,1234,0]},"unpriced":[["mystery",null,false,false,1]],"#
            + #""modes":[["gpt-5.5","serviceTier","fast","recorded","openai",1,100,90,500],["gpt-5.5","future","x","y",null,1,1,1,1]]}"#
        let line = #"{"t":"historyDay","day":"2026-11-03","source":"codex","aggregate":"# + aggregate
            + #","digest":"0123456789abcdef"}"#
        guard case .historyDay(let day) = message(line) else { return }
        #expect(day.aggregateJSON == aggregate)
        #expect(day.digestKey == "2026-11-03/codex")
        #expect(day.aggregate.requests == 3 && day.aggregate.costMicroUsd == 1234)
        #expect(day.aggregate.models["gpt-5.5"] == RemoteDayAggregate.Model(
            requests: 3, output: 340, measuredOutput: 300, measuredMs: 2000, costMicroUsd: 1234, unpricedRequests: 0))
        #expect(day.aggregate.unpriced == [RemoteDayAggregate.Unpriced(model: "mystery", provider: nil, fast: false,
                                                                       noDate: false, count: 1)])
        #expect(day.aggregate.modes.count == 1)
        #expect(day.aggregate.modes[0].kind == .serviceTier && day.aggregate.modes[0].provider == "openai")
    }

    @Test func historyPerfAndDigest() throws {
        let bins = "[" + (0..<64).map { $0 == 3 ? "2" : "0" }.joined(separator: ",") + "]"
        let record = #"{"n":2,"outputSum":340,"durationMsSum":2000,"latencyBins":"# + bins
            + #","reasonN":1,"reasonOutput":100,"reasonDurationMsSum":900,"cacheN":2,"cacheInput":2400,"cacheCached":2000,"#
            + #""toolN":3,"toolLnSum":17.25,"toolFailed":1,"activeMs":60000}"#
        let line = #"{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"gpt-5.5","record":"# + record + "}"
        guard case .historyPerf(let perf) = message(line) else { return }
        #expect(perf.recordJSON == record)
        #expect(perf.record.n == 2 && perf.record.latencyBins[3] == 2 && perf.record.toolLnSum == 17.25)
        #expect(perf.key == PerfRecordKey(day: "2026-11-03", source: .codex, model: "gpt-5.5"))

        let digest = RemoteHistoryDigest.digest(aggregateJSON: "{}", perf: [(model: "b", json: "[2]"), (model: "a", json: "[1]")])
        #expect(digest == String(FileDigest.sha256(of: Data("{}[1][2]".utf8)).prefix(16)))
    }

    @Test func unknownTypesAndFieldsAreIgnored() {
        #expect(decode(#"{"t":"futureThing","x":1}"#) == .ignored)
        #expect(message(#"{"t":"ready","extra":{"nested":[1,2]}}"#) == .ready)
        let line = agentLine { $0["newField"] = "x" }
        guard case .agent = message(line) else { return }
    }

    // MARK: Validation (REMOTE 5.6)

    @Test func structuralViolations() {
        #expect(invalid("not json")?.kind == .notJSON)
        #expect(invalid("")?.kind == .notJSON)
        #expect(invalid("[1,2]")?.kind == .notObject)
        #expect(invalid(#"{"t":1}"#)?.kind == .wrongType)
        #expect(invalid(#"{"x":"ready"}"#)?.kind == .missing)
        #expect(invalid(#"{"t":"progress","value":NaN}"#)?.kind == .notJSON)
        #expect(invalid(#"{"t":"progress","value":"0.5"}"#)?.kind == .wrongType)
        #expect(invalid(#"{"t":"agent","a":[]}"#)?.kind == .wrongType)
        let huge = #"{"t":"ready","pad":""# + String(repeating: "x", count: RemoteLineFramer.maxLineBytes) + #""}"#
        #expect(invalid(huge)?.kind == .oversized)
    }

    @Test func missingAndMistypedFields() {
        #expect(invalid(responseLine { $0.removeValue(forKey: "endMs") })?.kind == .missing)
        #expect(invalid(responseLine { $0.removeValue(forKey: "output") })?.kind == .missing)
        #expect(invalid(responseLine { $0["final"] = "yes" })?.kind == .wrongType)
        #expect(invalid(RelayLines.response().replacingOccurrences(of: #""endMs":1780000002000"#,
                                                                   with: #""endMs":1.780000002e12"#))?.kind == .wrongType)
        #expect(invalid(responseLine { $0["output"] = "340" })?.kind == .wrongType)
        #expect(invalid(agentLine { $0.removeValue(forKey: "subagent") })?.kind == .missing)
        #expect(invalid(agentLine { $0.removeValue(forKey: "modes") })?.kind == .missing)
        #expect(invalid(agentLine { $0["phase"] = "thinking" })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["source"] = "unknownAgent" })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["id"] = "" })?.kind == .outOfRange)
    }

    @Test func stringBounds() {
        #expect(invalid(agentLine { $0["id"] = "/" + String(repeating: "a", count: 4096) })?.kind == .tooLong)
        #expect(message(agentLine { $0["id"] = "/" + String(repeating: "a", count: 4095) }) != nil)
        #expect(invalid(agentLine { $0["project"] = String(repeating: "p", count: 257) })?.kind == .tooLong)
        #expect(invalid(agentLine { $0["project"] = String(repeating: "é", count: 129) })?.kind == .tooLong)
        #expect(invalid(agentLine { $0["cwd"] = "/" + String(repeating: "c", count: 4096) })?.kind == .tooLong)
        #expect(invalid(agentLine { $0["model"] = String(repeating: "m", count: 257) })?.kind == .tooLong)
        #expect(invalid(responseLine { $0["key"] = String(repeating: "k", count: 513) })?.kind == .tooLong)
        #expect(invalid(responseLine { $0["provider"] = String(repeating: "p", count: 65) })?.kind == .tooLong)
        var longTool = RelayLines.action()
        longTool["tool"] = String(repeating: "t", count: 129)
        #expect(invalid(agentLine { $0["actions"] = [longTool] })?.kind == .tooLong)
        let detail = #"{"t":"error","code":"internal","detail":""# + String(repeating: "x", count: 201) + #""}"#
        #expect(invalid(detail)?.kind == .tooLong)
    }

    @Test func controlCharactersInIdsKeysAndProjects() {
        for bad in ["a\u{0}b", "a\nb", "a\u{1F}b", "a\u{7F}b"] {
            #expect(invalid(agentLine { $0["id"] = bad })?.kind == .badCharacter, "\(bad.debugDescription)")
            #expect(invalid(agentLine { $0["project"] = bad })?.kind == .badCharacter)
            #expect(invalid(responseLine { $0["key"] = bad })?.kind == .badCharacter)
            #expect(invalid(responseLine { $0["agent"] = bad })?.kind == .badCharacter)
            #expect(invalid(RelayLines.response(project: bad))?.kind == .badCharacter)
        }
        #expect(invalid(#"{"t":"agentRemoved","id":"a\u0000"}"#)?.kind == .badCharacter)
        #expect(invalid(#"{"t":"parents","links":{"a\n":"/p"}}"#)?.kind == .badCharacter)
        #expect(message(agentLine { $0["project"] = "專案 ü" }) != nil)
    }

    @Test func modelAndProviderCharacters() {
        for good in ["claude-opus-5-5[1m]", "Gemini 3.1 Pro (High)", "custom:GPT-5.5-[OpenAI-BYOK]-0", "anthropic/claude-sonnet-4.5",
                     "us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-opus-5-5@default"] {
            #expect(message(responseLine { $0["model"] = good }) != nil, "\(good)")
        }
        for bad in ["gpt\u{0}", "gpt\u{85}", "gpt\u{202E}", "gpt\u{2066}", "gpt\u{2028}", "gpt\u{200F}"] {
            #expect(invalid(responseLine { $0["model"] = bad })?.kind == .badCharacter, "\(bad.debugDescription)")
            #expect(invalid(responseLine { $0["provider"] = bad })?.kind == .badCharacter)
            #expect(invalid(agentLine { $0["model"] = bad })?.kind == .badCharacter)
        }
        guard case .response(let response) = message(responseLine { $0["provider"] = ""; $0["model"] = "" }) else { return }
        #expect(response.record.provider == nil && response.record.model == nil)
    }

    @Test func integersBeyond2To53AreInvalid() {
        #expect(message(#"{"t":"clock","id":9007199254740992,"now":1780000000000}"#) != nil)
        #expect(invalid(#"{"t":"clock","id":9007199254740993,"now":1780000000000}"#)?.kind == .outOfRange)
        #expect(invalid(#"{"t":"clock","id":-9007199254740993,"now":1780000000000}"#)?.kind == .outOfRange)
        #expect(invalid(#"{"t":"clock","id":123456789012345678901234567890,"now":1780000000000}"#)?.kind == .outOfRange)
        #expect(invalid(#"{"t":"historyDone","days":-1,"analyzed":0}"#)?.kind == .outOfRange)
        #expect(invalid(responseLine { $0["requestCount"] = 0 })?.kind == .outOfRange)
        #expect(invalid(responseLine { $0["requestCount"] = 100_001 })?.kind == .outOfRange)
    }

    @Test func tokenValuesAreClamped() throws {
        let line = RelayLines.response().replacingOccurrences(of: #""output":340"#, with: #""output":1e20"#)
            .replacingOccurrences(of: #""input":1200"#, with: #""input":123456789012345678901234567890"#)
            .replacingOccurrences(of: #""reasoning":120"#, with: #""reasoning":12.9"#)
            .replacingOccurrences(of: #""cachedInput":1000"#, with: #""cachedInput":-7"#)
            .replacingOccurrences(of: #""cacheWriteInput":0"#, with: #""cacheWriteInput":5"#)
            .replacingOccurrences(of: #""cacheWrite1hInput":0"#, with: #""cacheWrite1hInput":1e999"#)
        guard case .response(let response) = message(line) else { return }
        #expect(response.record.usage == TokenUsage(input: 1_000_000_000_000, cachedInput: 0, cacheWriteInput: 5,
                                                    cacheWrite1hInput: 5, output: 1_000_000_000_000, reasoning: 12))
    }

    @Test func timesOutsideTheYears2000To2100AreInvalid() {
        #expect(message(responseLine { $0["endMs"] = RemoteFields.earliestMs; $0["startMs"] = NSNull(); $0["firstMs"] = NSNull() }) != nil)
        #expect(message(responseLine { $0["endMs"] = RemoteFields.latestMs }) != nil)
        #expect(invalid(responseLine { $0["endMs"] = RemoteFields.earliestMs - 1 })?.kind == .badTime)
        #expect(invalid(responseLine { $0["endMs"] = RemoteFields.latestMs + 1 })?.kind == .badTime)
        #expect(invalid(responseLine { $0["startMs"] = 0 })?.kind == .badTime)
        #expect(invalid(agentLine { $0["lastLineMs"] = 1_000 })?.kind == .badTime)
        #expect(invalid(agentLine { $0["actions"] = [RelayLines.action(callMs: 5)] })?.kind == .badTime)
        #expect(invalid(#"{"t":"hello","protocol":1,"version":"1","arch":"x86_64","now":99}"#)?.kind == .badTime)
    }

    @Test func modeFactsFollowExecutionModes() {
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(value: "fa st")] })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(raw: "-x")] })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(kind: "speed")] })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(evidence: "guessed")] })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(provider: "https://x")] })?.kind == .outOfRange)
        #expect(invalid(agentLine { $0["modes"] = [RelayLines.fact(provider: String(repeating: "p", count: 33))] })?.kind == .tooLong)
        #expect(invalid(agentLine { $0["modes"] = (0..<21).map { _ in RelayLines.fact() } })?.kind == .tooMany)
        #expect(invalid(responseLine { $0["modes"] = [["kind": "effort"]] })?.kind == .missing)
        guard case .agent(let agent) = message(agentLine {
            $0["configuredModes"] = [RelayLines.fact(kind: "effort", value: "high", raw: "high", evidence: "configured",
                                                     provider: NSNull(), scope: "configuration")]
        }) else { return }
        #expect(agent.configuredModes.facts.first?.provider == nil)
        #expect(agent.configuredModes.facts.first?.evidence == .configured)
    }

    @Test func actionLimitsAndDigests() {
        #expect(message(agentLine { $0["actions"] = (0..<64).map { _ in RelayLines.action() } }) != nil)
        #expect(invalid(agentLine { $0["actions"] = (0..<65).map { _ in RelayLines.action() } })?.kind == .tooMany)
        var upper = RelayLines.action()
        upper["digest"] = "0123456789ABCDEF"
        #expect(invalid(agentLine { $0["actions"] = [upper] })?.kind == .outOfRange)
        var short = RelayLines.action()
        short["digest"] = "0123"
        #expect(invalid(agentLine { $0["actions"] = [short] })?.kind == .outOfRange)
    }

    @Test func historyViolations() {
        let base = #"{"t":"historyDay","day":"2026-11-03","source":"codex","digest":"0123456789abcdef","aggregate":"#
        let good = #"{"requests":1,"input":1,"cachedInput":0,"cacheWriteInput":0,"output":1,"reasoning":0,"measuredOutput":0,"#
            + #""measuredMs":0,"unfinalized":0,"agents":1,"costMicroUsd":null,"unpricedRequests":null,"models":{},"unpriced":[],"modes":[]}"#
        #expect(message(base + good + "}") != nil)
        #expect(invalid(base.replacingOccurrences(of: "2026-11-03", with: "2026-1-3") + good + "}")?.kind == .outOfRange)
        #expect(invalid(base.replacingOccurrences(of: "0123456789abcdef", with: "XYZ") + good + "}")?.kind == .outOfRange)
        #expect(invalid(base + good.replacingOccurrences(of: #""models":{}"#, with: #""models":{"m":[1,2,3]}"#) + "}")?.kind == .outOfRange)
        #expect(invalid(base + good.replacingOccurrences(of: #""requests":1"#, with: #""requests":-1"#) + "}")?.kind == .outOfRange)
        let perf = #"{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"m","record":{"n":1,"outputSum":1,"#
            + #""durationMsSum":1,"latencyBins":[1,2],"reasonN":0,"reasonOutput":0,"reasonDurationMsSum":0,"cacheN":0,"#
            + #""cacheInput":0,"cacheCached":0,"toolN":0,"toolLnSum":0,"toolFailed":0,"activeMs":0}}"#
        #expect(invalid(perf)?.kind == .outOfRange)
    }

    @Test func historyDaysStayInsideTheYears2000To2100() {
        let base = #"{"t":"historyDay","day":"2026-11-03","source":"codex","digest":"0123456789abcdef","aggregate":"#
            + #"{"requests":1,"input":1,"cachedInput":0,"cacheWriteInput":0,"output":1,"reasoning":0,"measuredOutput":0,"#
            + #""measuredMs":0,"unfinalized":0,"agents":1,"costMicroUsd":null,"unpricedRequests":null,"models":{},"#
            + #""unpriced":[],"modes":[]}}"#
        for day in ["2000-01-01", "2100-12-31", "2024-02-29", "2000-02-29"] {
            #expect(message(base.replacingOccurrences(of: "2026-11-03", with: day)) != nil, "\(day)")
        }
        for day in ["1999-12-31", "2101-01-01", "2026-00-10", "2026-13-01", "2026-02-29", "2100-02-29", "2026-04-31",
                    "2026-11-00"] {
            #expect(invalid(base.replacingOccurrences(of: "2026-11-03", with: day))?.kind == .outOfRange, "\(day)")
        }
        #expect(RemoteFields.isDay("2026-11-03") && !RemoteFields.isDay("2026/11/03"))
    }

    @Test func framerCapsLinesAt1MiB() {
        var framer = RemoteLineFramer()
        let big = Data(repeating: UInt8(ascii: "x"), count: RemoteLineFramer.maxLineBytes + 1)
        var (lines, oversized) = framer.consume(Data("{\"t\":\"ready\"}\n{\"t\":".utf8))
        #expect(lines == [Data("{\"t\":\"ready\"}".utf8)])
        #expect(oversized == 0)
        (lines, oversized) = framer.consume(Data("\"ready\"}\n".utf8) + big + Data("\n{}\n".utf8))
        #expect(lines == [Data("{\"t\":\"ready\"}".utf8), Data("{}".utf8)])
        #expect(oversized == 1)
    }
}
