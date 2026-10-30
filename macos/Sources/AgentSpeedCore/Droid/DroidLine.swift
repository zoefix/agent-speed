import Foundation

/// An assistant message (METRICS 4.16 Calls).
struct DroidAssistant: Equatable, Sendable {
    /// The line's `id` when it is a non-empty string; without one the message is not a call.
    var id: String?
    /// `message.modelId` trimmed, when it is a string that is not blank.
    var modelId: String?
    /// `message.reasoningEffort` as a recorded request effort; nil when absent or no effort token.
    var effort: ExecutionModeFact?
    /// The tier an exact catalog `message.modelId` names (EXECUTION-MODES, Factory Droid).
    var tier: ExecutionModeFact?
    /// `message.content` is an array with a `tool_use` block.
    var asksForTools: Bool
}

enum DroidRecord: Equatable, Sendable {
    /// A user line sent to the model: the user's text, tool results, or the context droid adds to each turn.
    case input
    /// A `user_only` line (notices and hook records, never sent to the model).
    case notice
    case assistant(DroidAssistant)
    /// A `compaction_state` (a summary of the conversation so far).
    case compaction
    case other
}

/// One classified line of a Factory Droid transcript (METRICS 4.16 Transcript lines).
enum DroidLine: Equatable, Sendable {
    /// `session_start`, with `parent` and `callingSessionId` only when they are session ids.
    case sessionStart(cwd: String?, parent: String?, callingSessionId: String?)
    /// `agent_turn_outcome`, written without a timestamp.
    case turnOutcome
    /// Any other line with a parseable `timestamp`.
    case timed(timestampMs: Int64, record: DroidRecord)
    /// Any other line without one.
    case ignored
}

/// A transcript line with where it sits in the file, for the allocation weights.
struct DroidLineRead: Sendable {
    var extraction: LineExtraction<DroidLine>
    /// Byte offset of the line's first byte.
    var offset: Int64
    /// Length in bytes without the `\n` (a `\r` before it counts).
    var length: Int
}

/// Decodes Factory Droid transcript lines. Conversation content is skipped (only validated); repeated members resolve
/// like Python's `json.loads`: the last one wins.
enum DroidLineExtractor {
    private static let types: [StaticString] = ["session_start", "message", "compaction_state", "agent_turn_outcome"]
    private static let roles: [StaticString] = ["user", "assistant"]

    static func extract(_ bytes: UnsafeRawBufferPointer) -> LineExtraction<DroidLine> {
        if bytes.allSatisfy({ $0 == UInt8(ascii: " ") || (0x09...0x0D).contains($0) }) { return .blank }
        var scanner = JSONScanner(bytes)
        do {
            try scanner.beginObject()
            let top = try scanTopLevel(&scanner)
            try scanner.finish()
            return .line(top.classified())
        } catch {
            return .malformed
        }
    }

    private struct MessageMembers {
        var role: Int?
        var isUserOnly = false
        var modelId: String?
        var effort: String?
        var asksForTools = false
    }

    private struct TopLevel {
        var type: Int?
        var id: String?
        var timestampMs: Int64?
        var cwd: String?
        var parent: String?
        var callingSessionId: String?
        var message: MessageMembers?

        func classified() -> DroidLine {
            switch type {
            case 0:
                return .sessionStart(cwd: cwd, parent: parent.flatMap(Self.sessionId),
                                     callingSessionId: callingSessionId.flatMap(Self.sessionId))
            case 3:
                return .turnOutcome
            default:
                guard let timestampMs else { return .ignored }
                return .timed(timestampMs: timestampMs, record: record)
            }
        }

        private var record: DroidRecord {
            switch type {
            case 1:
                guard let message else { return .other }
                switch message.role {
                case 0:
                    return message.isUserOnly ? .notice : .input
                case 1:
                    let modelId = message.modelId.map(WorkBuddyLineExtractor.stripped).flatMap { $0.isEmpty ? nil : $0 }
                    return .assistant(DroidAssistant(
                        id: id.flatMap { $0.isEmpty ? nil : $0 }, modelId: modelId,
                        effort: ExecutionModeAdapters.effort(message.effort, evidence: .recorded, scope: .request),
                        tier: ExecutionModeAdapters.droidModel(modelId, evidence: .recorded, scope: .request),
                        asksForTools: message.asksForTools))
                default:
                    return .other
                }
            case 2:
                return .compaction
            default:
                return .other
            }
        }

        private static func sessionId(_ value: String) -> String? {
            DroidPaths.isSessionId(value) ? value : nil
        }
    }

    private static func scanTopLevel(_ scanner: inout JSONScanner) throws(JSONScanError) -> TopLevel {
        var top = TopLevel()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                top.type = try scanner.readStringMatch(types)
            } else if scanner.key(key, is: "id") {
                top.id = try scanner.readString()
            } else if scanner.key(key, is: "timestamp") {
                top.timestampMs = try scanner.readTimestamp()
            } else if scanner.key(key, is: "cwd") {
                top.cwd = try scanner.readString()
            } else if scanner.key(key, is: "parent") {
                top.parent = try scanner.readString()
            } else if scanner.key(key, is: "callingSessionId") {
                top.callingSessionId = try scanner.readString()
            } else if scanner.key(key, is: "message") {
                top.message = try scanMessage(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return top
    }

    private static func scanMessage(_ scanner: inout JSONScanner) throws(JSONScanError) -> MessageMembers? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var message = MessageMembers()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "role") {
                message.role = try scanner.readStringMatch(roles)
            } else if scanner.key(key, is: "visibility") {
                message.isUserOnly = try scanner.readStringMatch(["user_only"]) == 0
            } else if scanner.key(key, is: "modelId") {
                message.modelId = try scanner.readString()
            } else if scanner.key(key, is: "reasoningEffort") {
                message.effort = try scanner.readString()
            } else if scanner.key(key, is: "content") {
                message.asksForTools = try scanToolUse(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return message
    }

    /// Whether an array holds an object whose `type` is `tool_use`; anything else is skipped.
    private static func scanToolUse(_ scanner: inout JSONScanner) throws(JSONScanError) -> Bool {
        guard try scanner.beginArrayIfPresent() else {
            try scanner.skipValue()
            return false
        }
        var found = false
        while try scanner.nextElement() {
            guard try scanner.beginObjectIfPresent() else { continue }
            var isToolUse = false
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "type") {
                    isToolUse = try scanner.readStringMatch(["tool_use"]) == 0
                } else {
                    try scanner.skipValue()
                }
            }
            found = found || isToolUse
        }
        return found
    }
}
