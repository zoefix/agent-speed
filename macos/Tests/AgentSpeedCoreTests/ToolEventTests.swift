import Foundation
import Testing
@testable import AgentSpeedCore

/// Edges of the tool-call reading of METRICS 6.10 that the fixtures leave out.
@Suite("Tool events")
struct ToolEventTests {
    static func codexResult(_ output: String) -> [ToolEvent] {
        let line = #"{"type":"response_item","payload":{"type":"function_call_output","call_id":"c","output":\#(output)}}"#
        return Array(line.utf8).withUnsafeBytes { ToolEventExtractor.events(.codex, $0) }
    }

    @Test(arguments: [
        (#""  {\"exit_code\":2} and more""#, true),
        (#""{\"exit_code\":0}""#, false),
        (#""{\"exit_code\":true}""#, nil),
        (#""{\"exit_code\":1.0}""#, nil),
        (#""exit_code: 1""#, nil),
        (#"[{"type":"input_image"},{"type":"input_text","text":"{\"exit_code\":7}"}]"#, true),
        (#"[{"type":"input_text","text":7},{"type":"input_text","text":"{\"exit_code\":7}"}]"#, nil),
        (#""[{\"type\":\"input_text\",\"text\":\"{\\\"exit_code\\\":3}\"}]""#, true),
        (#""[not json""#, nil),
        (#"{"exit_code":1}"#, nil),
    ] as [(String, Bool?)])
    func codexExitCodes(_ output: String, _ failed: Bool?) {
        #expect(Self.codexResult(output) == [.result(id: "c", failed: failed)])
    }

    @Test func callsWithoutAnIdOrNameAreNotActions() {
        let line = #"{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}},{"type":"tool_use","id":"t"}]}}"#
        #expect(Array(line.utf8).withUnsafeBytes { ToolEventExtractor.events(.claude, $0) }.isEmpty)
    }

    @Test func aMissingOrNullInputHashesAsEmptyText() {
        let line = #"{"type":"message","message":{"role":"assistant","content":[{"type":"toolCall","id":"p","name":"ls","arguments":null}]}}"#
        let events = Array(line.utf8).withUnsafeBytes { ToolEventExtractor.events(.omp, $0) }
        #expect(events == [.call(id: "p", ToolCall(tool: "ls", digest: .empty))])
        #expect(ToolDigest.empty.hex == "e3b0c44298fc1c14")
    }

    @Test func theLogKeepsTheNewestActions() {
        var log = ActionLog()
        for index in 0..<(ActionLog.capacity + 6) {
            log.record([.call(id: "c\(index)", ToolCall(tool: "t", digest: .empty))], at: Int64(index), model: "m")
        }
        log.record([.result(id: "c0", failed: true), .result(id: "c69", failed: true)], at: 100, model: nil)
        #expect(log.actions.count == ActionLog.capacity)
        #expect(log.actions.first?.callMs == 6)
        #expect(log.actions.last?.failed == true && log.actions.last?.resultMs == 100)
        #expect(!log.actions.dropLast().contains { $0.failed != nil })
        let completions = log.takeCompletions()
        #expect(completions.map(\.callMs) == [0, 69], "a call that left the list still completes")
        #expect(completions.allSatisfy { $0.model == "m" && !$0.seeded })
        #expect(log.takeCompletions().isEmpty, "taken once")
    }

    /// METRICS 6.10: an id the file already had adds nothing, only the first result counts, and a result whose id
    /// names no call is ignored.
    @Test func onlyTheFirstResultOfACallCounts() {
        var log = ActionLog()
        let call = ToolCall(tool: "Bash", digest: .empty)
        log.record([.call(id: "a", call)], at: 10, model: "m1")
        log.record([.call(id: "a", call)], at: 11, model: "m2")
        log.record([.result(id: "a", failed: false)], at: 20, model: nil)
        log.record([.result(id: "a", failed: true)], at: 30, model: nil)
        log.record([.result(id: "nobody", failed: true)], at: 40, model: nil)
        #expect(log.actions.count == 1)
        #expect(log.actions.first?.resultMs == 20 && log.actions.first?.failed == false)
        #expect(log.actions.first?.model == "m1")
        #expect(log.takeCompletions().count == 1)
    }

    /// Lines read while seeding add seeded actions; a result read live completes as live.
    @Test func seededActionsAreMarked() {
        var log = ActionLog()
        log.marksSeeded = true
        log.record([.call(id: "a", ToolCall(tool: "Read", digest: .empty))], at: 10, model: nil)
        log.record([.call(id: "b", ToolCall(tool: "Read", digest: .empty))], at: 11, model: nil)
        log.record([.result(id: "a", failed: nil)], at: 12, model: nil)
        log.marksSeeded = false
        log.record([.result(id: "b", failed: nil)], at: 13, model: nil)
        #expect(log.actions.allSatisfy { $0.seeded })
        #expect(log.takeCompletions().map(\.seeded) == [true, false])
        #expect(WorkSignals(log.actions, nowMs: 20) == .none, "the signals ignore seeded actions")
    }

    /// The history analysis keeps no stored list, only the completions.
    @Test func completionsOnly() {
        var log = ActionLog()
        log.storesActions = false
        log.record([.call(id: "a", ToolCall(tool: "Read", digest: .empty))], at: 10, model: "m")
        log.record([.result(id: "a", failed: true)], at: 15, model: nil)
        #expect(log.actions.isEmpty)
        #expect(log.takeCompletions() == [ToolCompletion(callMs: 10, resultMs: 15, failed: true, model: "m", seeded: false)])
    }
}
