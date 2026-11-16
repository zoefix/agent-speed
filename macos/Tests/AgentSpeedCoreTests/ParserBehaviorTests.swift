import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Parser behavior")
struct ParserBehaviorTests {
    static func run(_ family: TranscriptFamily, _ lines: [String], path: String = "/tmp/agent.jsonl", deduper: inout Deduper, fileID: FileID = 0) -> (TranscriptParser, [ResponseUpsert]) {
        var parser = TranscriptParser(family: family, agentId: path, fileID: fileID)
        var upserts: [ResponseUpsert] = []
        for line in lines {
            var copy = line
            copy.withUTF8 { buffer in
                parser.consume(rawLine: UnsafeRawBufferPointer(buffer), deduper: &deduper) { upserts.append($0) }
            }
        }
        return (parser, upserts)
    }

    static func claudeUser(_ ts: String, content: String) -> String {
        #"{"type":"user","entrypoint":"cli","cwd":"/w/p","message":{"role":"user","content":\#(content)},"timestamp":"\#(ts)"}"#
    }

    static func claudeAssistant(_ ts: String, id: String, output: Int, stop: String = "null") -> String {
        #"{"type":"assistant","entrypoint":"cli","cwd":"/w/p","message":{"id":"\#(id)","model":"m","stop_reason":\#(stop),"usage":{"input_tokens":1,"output_tokens":\#(output)}},"timestamp":"\#(ts)"}"#
    }

    @Test func claudeInterruptEndsTheTurn() {
        var deduper = Deduper()
        let (parser, _) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            Self.claudeUser("2026-10-20T10:00:05Z", content: #"[{"type":"text","text":"[Request interrupted by user for tool use]"}]"#),
            Self.claudeAssistant("2026-10-20T10:00:09Z", id: "late", output: 5),
        ], deduper: &deduper)
        // The interrupt cleared the pending start, so the late response has no start.
        #expect(parser.state.phase == .generating)
        let (stringInterrupt, _) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            Self.claudeUser("2026-10-20T10:00:05Z", content: #""[Request interrupted by user]""#),
        ], deduper: &deduper)
        #expect(stringInterrupt.state.phase == .idle)
        #expect(stringInterrupt.state.inFlightStartMs == nil)
    }

    @Test func claudeResponseAfterInterruptHasUnknownStart() {
        var deduper = Deduper()
        let (_, upserts) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            Self.claudeUser("2026-10-20T10:00:05Z", content: #"[{"type":"tool_result","content":"x"},{"type":"text","text":"[Request interrupted by user]"}]"#),
            Self.claudeAssistant("2026-10-20T10:00:09Z", id: "late", output: 5, stop: #""end_turn""#),
        ], deduper: &deduper)
        #expect(upserts.count == 1)
        #expect(upserts.first?.record.startMs == nil)
    }

    @Test func claudeToolResultThatEndsTheRunSetsIdle() {
        var deduper = Deduper()
        let result = #"{"type":"user","toolEndsTurn":true,"entrypoint":"cli","cwd":"/w/p","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]},"timestamp":"2026-10-20T10:00:03Z"}"#
        let (ended, _) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""count""#),
            Self.claudeAssistant("2026-10-20T10:00:02Z", id: "m1", output: 10, stop: #""tool_use""#),
            result,
        ], deduper: &deduper)
        #expect(ended.state.phase == .idle)
        #expect(ended.state.inFlightStartMs == nil)
        let (continued, _) = Self.run(.claude, [
            result.replacingOccurrences(of: #""toolEndsTurn":true"#, with: #""toolEndsTurn":false"#),
        ], deduper: &deduper)
        #expect(continued.state.phase == .generating)
    }

    @Test func claudeApiErrorMessageEndsTheTurnAndOtherSyntheticLinesDoNot() {
        var deduper = Deduper()
        let synthetic = #"{"type":"assistant","entrypoint":"cli","cwd":"/w/p","uuid":"s1","isApiErrorMessage":true,"error":"rate_limit","message":{"id":"x1","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","content":[{"type":"text","text":"limit"}],"usage":{"input_tokens":0,"output_tokens":0}},"timestamp":"2026-10-20T10:00:05Z"}"#
        let (failed, upserts) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            synthetic,
        ], deduper: &deduper)
        #expect(upserts.isEmpty)
        #expect(failed.state.phase == .idle)
        #expect(failed.state.inFlightStartMs == nil)
        let (other, _) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:01:00Z", content: #""go""#),
            synthetic.replacingOccurrences(of: #""isApiErrorMessage":true,"#, with: ""),
        ], deduper: &deduper)
        #expect(other.state.phase == .generating)
        #expect(other.state.inFlightStartMs == 1_792_490_460_000)
    }

    @Test func claudeRetryWaitStopsTheInFlightEstimateUntilTheNextAttempt() {
        var deduper = Deduper()
        let retry = #"{"type":"system","subtype":"api_error","level":"error","entrypoint":"cli","cwd":"/w/p","error":{"status":529},"retryInMs":8000,"retryAttempt":1,"maxRetries":10,"timestamp":"2026-10-20T10:00:02Z"}"#
        let request = #"{"type":"api-request","entrypoint":"cli","cwd":"/w/p","timestamp":"2026-10-20T10:00:10Z"}"#
        let (waiting, _) = Self.run(.claude, [Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#), retry],
                                    deduper: &deduper)
        #expect(waiting.state.phase == .generating)
        #expect(waiting.state.inFlightStartMs == nil)
        #expect(waiting.state.phaseSinceMs == 1_792_490_400_000)
        let (retried, upserts) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#), retry, request,
            Self.claudeAssistant("2026-10-20T10:00:14Z", id: "r1", output: 40, stop: #""end_turn""#),
        ], deduper: &deduper)
        #expect(upserts.first?.record.startMs == 1_792_490_410_000)
        #expect(retried.state.phase == .idle)
        let (idle, _) = Self.run(.claude, [retry], deduper: &deduper)
        #expect(idle.state.phase == .idle)
    }

    @Test func claudeMergesRepeatedLinesAndMarksOnlyTheFirstAsCompletion() {
        var deduper = Deduper()
        let (parser, upserts) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            Self.claudeAssistant("2026-10-20T10:00:02Z", id: "m1", output: 10),
            Self.claudeAssistant("2026-10-20T10:00:04Z", id: "m1", output: 30, stop: #""tool_use""#),
        ], deduper: &deduper)
        #expect(upserts.map(\.isCompletion) == [true, false])
        #expect(upserts.last?.record.usage.output == 30)
        #expect(upserts.last?.record.endMs == 1_792_490_404_000)
        #expect(upserts.last?.record.startMs == 1_792_490_400_000)
        #expect(parser.state.phase == .tools)
    }

    @Test func claudeIgnoresKeysOwnedByAnotherFile() {
        var deduper = Deduper()
        _ = Self.run(.claude, [Self.claudeAssistant("2026-10-20T10:00:02Z", id: "shared", output: 10)], path: "/a.jsonl", deduper: &deduper, fileID: 1)
        let (parser, upserts) = Self.run(.claude, [
            Self.claudeUser("2026-10-20T10:00:00Z", content: #""go""#),
            Self.claudeAssistant("2026-10-20T10:00:02Z", id: "shared", output: 10, stop: #""end_turn""#),
            Self.claudeAssistant("2026-10-20T10:00:06Z", id: "own", output: 7, stop: #""end_turn""#),
        ], path: "/b.jsonl", deduper: &deduper, fileID: 2)
        #expect(upserts.map(\.record.key) == ["own"])
        // The ignored line did not consume the pending start.
        #expect(upserts.first?.record.startMs == 1_792_490_400_000)
        #expect(parser.state.phase == .idle)
    }

    @Test func countsMalformedLinesButNotBlankOnes() {
        var deduper = Deduper()
        let (parser, _) = Self.run(.codex, [
            "", "   ", "not json", "[1,2]", "{\"timestamp\":", "{\"timestamp\":\"2026-10-20T10:00:00Z\",\"type\":\"event_msg\"}\r",
        ], deduper: &deduper)
        #expect(parser.state.malformedLines == 3)
        #expect(parser.state.lastLineMs == 1_792_490_400_000)
    }

    @Test func codexUsesOnlyTheFirstSessionMeta() {
        var deduper = Deduper()
        let (parser, upserts) = Self.run(.codex, [
            #"{"timestamp":"2026-10-20T10:00:00Z","type":"session_meta","payload":{"id":"child","cwd":"/w/child","thread_source":"subagent","agent_path":"/root/worker"}}"#,
            #"{"timestamp":"2026-10-20T10:00:00Z","type":"session_meta","payload":{"id":"parent","cwd":"/w/parent","thread_source":"user"}}"#,
            #"{"timestamp":"2026-10-20T10:00:01Z","type":"response_item","payload":{"type":"agent_message","author":"/root","recipient":"/root/worker","content":[]}}"#,
            #"{"timestamp":"2026-10-20T10:00:03Z","type":"token_usage_record","payload":{"usage":{"input_tokens":5,"output_tokens":9}}}"#,
        ], deduper: &deduper)
        #expect(parser.state.isSubagent)
        #expect(parser.state.project == "child")
        #expect(upserts.first?.record.key == "codex:child:1792490403000")
        #expect(upserts.first?.record.startMs == 1_792_490_401_000)
    }

    @Test func codexMailboxInputMarksStartWithoutChangingPhase() {
        var deduper = Deduper()
        let (parser, _) = Self.run(.codex, [
            #"{"timestamp":"2026-10-20T10:00:00Z","type":"session_meta","payload":{"id":"root"}}"#,
            #"{"timestamp":"2026-10-20T10:00:01Z","type":"event_msg","payload":{"type":"task_complete"}}"#,
            #"{"timestamp":"2026-10-20T10:00:05Z","type":"response_item","payload":{"type":"agent_message","author":"/root/w","recipient":"/root","content":[]}}"#,
        ], deduper: &deduper)
        #expect(parser.state.phase == .idle)
        #expect(parser.state.inFlightStartMs == nil)
    }

    @Test func codexTokenCountWhileStreamingEndsTheResponse() {
        var deduper = Deduper()
        let (parser, upserts) = Self.run(.codex, [
            #"{"timestamp":"2026-10-20T10:00:00Z","type":"session_meta","payload":{"id":"root"}}"#,
            #"{"timestamp":"2026-10-20T10:00:01Z","type":"event_msg","payload":{"type":"user_message"}}"#,
            #"{"timestamp":"2026-10-20T10:00:02Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"output_tokens":9}}}"#,
            #"{"timestamp":"2026-10-20T10:00:03Z","type":"response_item","payload":{"type":"reasoning"}}"#,
            #"{"timestamp":"2026-10-20T10:00:04Z","type":"event_msg","payload":{"type":"token_count","info":null}}"#,
            #"{"timestamp":"2026-10-20T10:13:00Z","type":"response_item","payload":{"type":"agent_message","author":"/root/w","recipient":"/root","content":[]}}"#,
            #"{"timestamp":"2026-10-20T10:13:02Z","type":"response_item","payload":{"type":"reasoning"}}"#,
            #"{"timestamp":"2026-10-20T10:13:05Z","type":"token_usage_record","payload":{"response_id":"r2","usage":{"output_tokens":30}}}"#,
        ], deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["r1", "r2"])
        #expect(upserts.last?.record.startMs == Timestamp.parse("2026-10-20T10:13:00Z"))
        #expect(parser.state.phase == .tools)
    }

    @Test func codexIgnoresCopiedHistory() {
        var deduper = Deduper()
        let (parser, upserts) = Self.run(.codex, [
            #"{"timestamp":"2026-10-20T10:00:10Z","type":"session_meta","payload":{"id":"t"}}"#,
            #"{"timestamp":"2026-10-20T10:00:07Z","type":"token_usage_record","payload":{"response_id":"old","usage":{"output_tokens":9}}}"#,
            #"{"timestamp":"2026-10-20T10:00:08.500Z","type":"token_usage_record","payload":{"response_id":"recent","usage":{"output_tokens":9}}}"#,
        ], deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["recent"])
        #expect(parser.state.lastLineMs == 1_792_490_410_000)
    }

    /// A paginated sub-agent whose first `session_meta` names its start ordinal: `start` is the raw JSON value.
    static func pagedSubagent(start: String, prefixOrdinals: [String] = ["1", "2", "3", "4", "5"]) -> [String] {
        let o = prefixOrdinals
        return [
            #"{"timestamp":"2026-10-20T10:00:00Z","ordinal":0,"type":"session_meta","payload":{"id":"child","cwd":"/w/child","thread_source":"subagent","agent_path":"/root/scout","history_mode":"paginated","subagent_history_start_ordinal":\#(start)}}"#,
            #"{"timestamp":"2026-10-20T10:00:00.010Z","ordinal":\#(o[0]),"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":50,"output_tokens":7,"total_tokens":57},"last_token_usage":{"input_tokens":50,"output_tokens":7,"total_tokens":57}}}}"#,
            #"{"timestamp":"2026-10-20T10:00:00.020Z","ordinal":\#(o[1]),"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}"#,
            #"{"timestamp":"2026-10-20T10:00:00.030Z","ordinal":\#(o[2]),"type":"turn_context","payload":{"cwd":"/w/parent","model":"parent-model","effort":"xhigh"}}"#,
            #"{"timestamp":"2026-10-20T10:00:00.040Z","ordinal":\#(o[3]),"type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":"ls","call_id":"copied"}}"#,
            #"{"timestamp":"2026-10-20T10:00:00.050Z","ordinal":\#(o[4]),"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":90,"output_tokens":9,"total_tokens":99},"last_token_usage":{"input_tokens":40,"output_tokens":2,"total_tokens":42}}}}"#,
            #"{"timestamp":"2026-10-20T10:00:01Z","ordinal":6,"type":"event_msg","payload":{"type":"task_started"}}"#,
            #"{"timestamp":"2026-10-20T10:00:01.100Z","ordinal":7,"type":"turn_context","payload":{"model":"child-model","effort":"low"}}"#,
            #"{"timestamp":"2026-10-20T10:00:03Z","ordinal":8,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":20,"output_tokens":4,"total_tokens":24},"last_token_usage":{"input_tokens":20,"output_tokens":4,"total_tokens":24}}}}"#,
        ]
    }

    @Test func codexSkipsTheCopiedPrefixOfAPaginatedSubagent() {
        var deduper = Deduper()
        let (parser, upserts) = Self.run(.codex, Self.pagedSubagent(start: "6"), deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["codex-legacy:child:24"])
        #expect(upserts.first?.record.model == "child-model")
        #expect(upserts.first?.record.startMs == Timestamp.parse("2026-10-20T10:00:01Z"))
        #expect(upserts.first?.record.usage.output == 4)
        #expect(upserts.first?.record.modes.effortAndSpeed.speed == nil)
        #expect(parser.state.project == "child")
        #expect(parser.state.actions.actions.isEmpty)
        #expect(parser.state.lastLineMs == Timestamp.parse("2026-10-20T10:00:03Z"))
    }

    @Test func codexWithoutAValidStartOrdinalKeepsEveryLine() {
        for start in ["null", "-1", "6.0", "6e0", #""6""#, "true", "9223372036854775808"] {
            var deduper = Deduper()
            let (_, upserts) = Self.run(.codex, Self.pagedSubagent(start: start), deduper: &deduper)
            #expect(upserts.map(\.record.key) == ["codex-legacy:child:57", "codex-legacy:child:99", "codex-legacy:child:24"],
                    "start ordinal \(start)")
            #expect(upserts.first?.record.model == nil)
        }
        var deduper = Deduper()
        let missing = Self.pagedSubagent(start: "6").map {
            $0.replacingOccurrences(of: #","subagent_history_start_ordinal":6"#, with: "")
        }
        #expect(Self.run(.codex, missing, deduper: &deduper).1.count == 3)
    }

    @Test func codexStartOrdinalUsesEachLinesOwnOrdinal() {
        var deduper = Deduper()
        // An invalid line ordinal is no copy; a malformed line in the prefix does not shift the boundary.
        var lines = Self.pagedSubagent(start: "6", prefixOrdinals: ["1", "2", "3", "4", "-5"])
        lines.insert(#"{"timestamp":"2026-10-20T10:00:00.045Z","ordinal":4,"type":"resp"#, at: 5)
        let (parser, upserts) = Self.run(.codex, lines, deduper: &deduper)
        #expect(upserts.map(\.record.key) == ["codex-legacy:child:99", "codex-legacy:child:24"])
        #expect(upserts.first?.record.model == nil)
        #expect(parser.state.malformedLines == 1)
        var fresh = Deduper()
        let maxStart = Self.pagedSubagent(start: "9223372036854775807")
        #expect(Self.run(.codex, maxStart, deduper: &fresh).1.isEmpty)
    }

    @Test func codexSurvivesHostileNumbers() {
        var deduper = Deduper()
        let (_, upserts) = Self.run(.codex, [
            #"{"timestamp":"2026-10-20T10:00:10Z","type":"token_usage_record","payload":{"response_id":"r","usage":{"input_tokens":1e400,"output_tokens":-7,"cached_input_tokens":"12","reasoning_output_tokens":99999999999999999999999}}}"#,
        ], deduper: &deduper)
        let usage = upserts.first?.record.usage
        #expect(usage?.input == Int(TokenCount.ceiling))
        #expect(usage?.output == 0)
        #expect(usage?.cachedInput == 0)
        #expect(usage?.reasoning == Int(TokenCount.ceiling))
    }

    @Test func summedTokenFieldsSaturateAtTheCap() {
        var deduper = Deduper()
        let (_, upserts) = Self.run(.claude, [
            #"{"type":"assistant","entrypoint":"cli","cwd":"/w/p","message":{"id":"big","model":"m","stop_reason":"end_turn","usage":{"input_tokens":900000000000,"cache_creation_input_tokens":900000000000,"cache_read_input_tokens":1e999,"output_tokens":5}},"timestamp":"2026-10-20T10:00:10Z"}"#,
        ], deduper: &deduper)
        let usage = upserts.first?.record.usage
        #expect(usage?.input == Int(TokenCount.ceiling), "input + cache writes + cache reads saturates (METRICS 3.2)")
        #expect(usage?.cachedInput == Int(TokenCount.ceiling))
        #expect(usage?.cacheWriteInput == 900_000_000_000)
        #expect(TokenCount.sum(Int(TokenCount.ceiling), Int(TokenCount.ceiling), 7) == Int(TokenCount.ceiling))
    }

    @Test func dedupeEvictsOldestKeysFirst() {
        var deduper = Deduper(capacity: 3)
        #expect(deduper.claim("a", for: 1) == .new)
        #expect(deduper.claim("b", for: 1) == .new)
        #expect(deduper.claim("c", for: 2) == .new)
        #expect(deduper.claim("a", for: 2) == .otherFile)
        #expect(deduper.claim("d", for: 2) == .new)
        #expect(!deduper.contains("a"))
        #expect(deduper.claim("b", for: 1) == .sameFile)
        #expect(deduper.count == 3)
    }
}
