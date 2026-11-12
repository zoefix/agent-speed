import Foundation
import Testing
@testable import AgentSpeedCore

/// The facts a living character reacts to (CHARACTERS 5, Behavior, Inputs), on each agent of the list: whether output
/// came since its in-flight request started, the newest end of its responses, and a live failure right after a failure.
@Suite struct CharacterFactsTests {
    static let relative = WorkSignalSessionTests.relative

    /// A failing `Bash` run: the call, then its failed result a second later (lines carry whole seconds).
    static func failingRun(_ index: Int, at ms: Int64) -> [String] {
        let call = #"{"type":"tool_use","id":"t\#(index)","name":"Bash","input":{"command":"swift test"}}"#
        return [
            WorkSignalSessionTests.line(ms, "assistant", #"{"id":"m\#(index)","model":"claude-opus-5-5","stop_reason":"tool_use","content":[\#(call)],"usage":{"output_tokens":5}}"#),
            WorkSignalSessionTests.line(ms + 1000, "user", #"{"content":[{"type":"tool_result","tool_use_id":"t\#(index)","is_error":true,"content":"failed"}]}"#),
        ]
    }

    static func second(_ ms: Int64) -> Int64 { ms / 1000 * 1000 }

    @Test func liveFailuresInARowAndTheWaitForOutputReachTheAgent() throws {
        let directory = try TempDirectory()
        let now = Clock.milliseconds(Date())
        let today = SessionTests.startOfToday()
        _ = try directory.write(Self.relative, (0..<2).flatMap { Self.failingRun($0, at: now - 90_000 + Int64($0) * 3000) }
            .joined(separator: "\n") + "\n")
        let configuration = MonitorConfiguration(enabledSources: [.claudeCode], claudeRoot: directory.file("claude/projects"))
        let session = TranscriptSession(configuration: configuration)
        _ = session.seed(todayStartMs: today)
        let seeded = try #require(session.advance(nowMs: now, todayStartMs: today, emitsEvents: true).output.agents.first)
        #expect(seeded.repeatFailureMs == nil, "results read while seeding are not live")

        try directory.append(Self.relative, (2..<4).flatMap { Self.failingRun($0, at: now - 20_000 + Int64($0) * 3000) }
            .joined(separator: "\n") + "\n")
        #expect(session.rediscover(todayStartMs: today))
        let failing = try #require(session.advance(nowMs: now, todayStartMs: today, emitsEvents: true).output.agents.first)
        let newestResult = Self.second(now - 20_000 + 3 * 3000 + 1000)
        #expect(failing.repeatFailureMs == newestResult)
        // The failed result asks the model again: it waits for output since then.
        #expect(failing.phase == .generating)
        #expect(failing.agent.inFlightStartMs == newestResult)
        #expect(!failing.outputSinceInFlight)
        #expect(failing.lastEndMs.map { $0 < newestResult } == true)

        try directory.append(Self.relative, WorkSignalSessionTests.line(now - 3000, "assistant",
            #"{"id":"m9","model":"claude-opus-5-5","stop_reason":null,"content":[{"type":"text","text":"Let me look"}],"usage":{"output_tokens":4}}"#) + "\n")
        #expect(session.rediscover(todayStartMs: today))
        let answering = try #require(session.advance(nowMs: now, todayStartMs: today, emitsEvents: true).output.agents.first)
        #expect(answering.outputSinceInFlight)
        #expect(answering.lastEndMs.map { $0 >= newestResult } == true)
        _ = directory
    }

    @Test func aSuccessBetweenFailuresIsNoTrouble() {
        func action(_ result: Int64, failed: Bool, seeded: Bool = false) -> ToolAction {
            ToolAction(call: ToolCall(tool: "Bash", digest: .empty), callMs: result - 100, resultMs: result,
                       failed: failed, seeded: seeded)
        }
        #expect(SpeedEngine.repeatFailure([action(1000, failed: true), action(2000, failed: true)]) == 2000)
        #expect(SpeedEngine.repeatFailure([action(1000, failed: true), action(1500, failed: false),
                                           action(2000, failed: true)]) == nil)
        #expect(SpeedEngine.repeatFailure([action(1000, failed: true), action(2000, failed: true, seeded: true)]) == nil)
        // Results count in the order they were read, not in call order.
        #expect(SpeedEngine.repeatFailure([action(3000, failed: true), action(1000, failed: false),
                                           action(2000, failed: true)]) == 3000)
        #expect(SpeedEngine.repeatFailure([action(1000, failed: true)]) == nil)
    }
}
