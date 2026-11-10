import Foundation

enum ClaudeLineType: Sendable {
    case assistant
    case user
    case apiRequest
    case attachment
    case other
}

/// `attachment.type` markers of ultracode (EXECUTION-MODES, Claude Code).
enum ClaudeAttachment: Equatable, Sendable {
    case ultraEffortEnter
    case ultraEffortExit
    case workflowKeywordRequest
}

enum ClaudeStopReason: Equatable, Sendable {
    /// `stop_reason` is null or missing.
    case none
    case pauseTurn
    case toolUse
    case other
}

struct ClaudeLine: Equatable, Sendable {
    var timestampMs: Int64?
    var type: ClaudeLineType = .other
    /// Source implied by `entrypoint`, nil when the line has none.
    var entrypointSource: SourceKind?
    var cwd: String?
    var isSidechain = false
    var messageId: String?
    var requestId: String?
    var uuid: String?
    var model: String?
    var stopReason: ClaudeStopReason = .none
    var usage: TokenUsage = .zero
    /// `message.usage.input_tokens` alone, as a token value (Qoder CLI's input rule, METRICS 4.19).
    var inputTokens = 0
    /// `message.usage.speed == "fast"` (METRICS 3.2).
    var fast = false
    var modes: ExecutionModes = .empty
    /// The recorded effort of an assistant line: `perTurnEffort` when it is a string, else top-level `effort`.
    var effort: ExecutionModeFact?
    var attachment: ClaudeAttachment?
    /// A user line a person typed (or an interrupt): not `isMeta`, string content or a `text` block.
    var isPrompt = false
    /// A user line whose content starts with `[Request interrupted by user`.
    var isInterrupt = false
    /// A user line with `toolEndsTurn == true`: the tool result ends the agent's run.
    var toolEndsTurn = false
    /// `message.content` contains a block of type `tool_use`.
    var hasToolUse = false
}

/// Decodes the fields of a Claude transcript line that METRICS 4.1 needs. Conversation text is never captured.
enum ClaudeLineExtractor {
    private static let lineTypes: [StaticString] = ["assistant", "user", "api-request", "attachment"]
    private static let stopReasons: [StaticString] = ["pause_turn", "tool_use"]
    private static let attachments: [StaticString] = ["ultra_effort_enter", "ultra_effort_exit", "workflow_keyword_request"]
    private static let interruptPrefix: StaticString = "[Request interrupted by user"

    static func extract(_ bytes: UnsafeRawBufferPointer) -> LineExtraction<ClaudeLine> {
        var scanner = JSONScanner(bytes)
        if scanner.isBlank { return .blank }
        do {
            var line = ClaudeLine()
            var isMeta = false
            var hasPromptContent = false
            // `.some(nil)`: a string too long to keep, which still wins over `effort` and gives no fact.
            var perTurnEffort: String??
            var effort: String??
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "type") {
                    switch try scanner.readStringMatch(lineTypes) {
                    case 0: line.type = .assistant
                    case 1: line.type = .user
                    case 2: line.type = .apiRequest
                    case 3: line.type = .attachment
                    default: line.type = .other
                    }
                } else if scanner.key(key, is: "timestamp") {
                    line.timestampMs = try scanner.readTimestamp()
                } else if scanner.key(key, is: "entrypoint") {
                    switch try scanner.readStringHasPrefix("claude-desktop") {
                    case true?: line.entrypointSource = .claudeDesktop
                    case false?: line.entrypointSource = .claudeCode
                    case nil: line.entrypointSource = nil
                    }
                } else if scanner.key(key, is: "cwd") {
                    line.cwd = try scanner.readNonEmptyString()
                } else if scanner.key(key, is: "isSidechain") {
                    line.isSidechain = try scanner.readBool() ?? false
                } else if scanner.key(key, is: "toolEndsTurn") {
                    line.toolEndsTurn = try scanner.readBool() ?? false
                } else if scanner.key(key, is: "isMeta") {
                    isMeta = try scanner.readBool() ?? false
                } else if scanner.key(key, is: "perTurnEffort") {
                    perTurnEffort = try scanner.readStringValue()
                } else if scanner.key(key, is: "effort") {
                    effort = try scanner.readStringValue()
                } else if scanner.key(key, is: "attachment") {
                    line.attachment = try scanAttachment(&scanner)
                } else if scanner.key(key, is: "requestId") {
                    line.requestId = try scanner.readNonEmptyString()
                } else if scanner.key(key, is: "uuid") {
                    line.uuid = try scanner.readNonEmptyString()
                } else if scanner.key(key, is: "message") {
                    hasPromptContent = try scanMessage(&scanner, into: &line)
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
            if line.type != .assistant {
                line.messageId = nil
                line.requestId = nil
                line.uuid = nil
                line.model = nil
            }
            if line.type != .user {
                line.isInterrupt = false
                line.toolEndsTurn = false
            }
            if line.type != .assistant { line.hasToolUse = false }
            if line.type != .attachment { line.attachment = nil }
            line.isPrompt = line.type == .user && !isMeta && hasPromptContent
            if line.type == .assistant, let raw = perTurnEffort ?? effort {
                line.effort = ExecutionModeAdapters.effort(raw, evidence: .recorded, scope: .request)
            }
            return .line(line)
        } catch {
            return .malformed
        }
    }

    /// Returns whether `content` is a string or has a `text` block.
    private static func scanMessage(_ scanner: inout JSONScanner, into line: inout ClaudeLine) throws(JSONScanError) -> Bool {
        guard try scanner.beginObjectIfPresent() else { return false }
        var hasText = false
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "id") {
                line.messageId = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "model") {
                line.model = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "stop_reason") {
                if try scanner.readNull() {
                    line.stopReason = .none
                } else {
                    switch try scanner.readStringMatch(stopReasons) {
                    case 0: line.stopReason = .pauseTurn
                    case 1: line.stopReason = .toolUse
                    default: line.stopReason = .other
                    }
                }
            } else if scanner.key(key, is: "usage") {
                (line.usage, line.inputTokens, line.fast, line.modes) = try scanUsage(&scanner)
            } else if scanner.key(key, is: "content") {
                (line.isInterrupt, line.hasToolUse, hasText) = try scanContent(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return hasText
    }

    private static func scanAttachment(_ scanner: inout JSONScanner) throws(JSONScanError) -> ClaudeAttachment? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var marker: ClaudeAttachment?
        while let key = try scanner.nextMember() {
            guard scanner.key(key, is: "type") else {
                try scanner.skipValue()
                continue
            }
            switch try scanner.readStringMatch(attachments) {
            case 0: marker = .ultraEffortEnter
            case 1: marker = .ultraEffortExit
            case 2: marker = .workflowKeywordRequest
            default: marker = nil
            }
        }
        return marker
    }

    private static func scanUsage(_ scanner: inout JSONScanner) throws(JSONScanError)
        -> (TokenUsage, inputTokens: Int, fast: Bool, modes: ExecutionModes) {
        guard try scanner.beginObjectIfPresent() else { return (.zero, 0, false, .empty) }
        var input = 0
        var cacheCreation = 0
        var cacheCreation1h = 0
        var cacheRead = 0
        var output = 0
        var thinking = 0
        var fast = false
        var speed: String?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "input_tokens") {
                input = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "cache_creation_input_tokens") {
                cacheCreation = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "cache_read_input_tokens") {
                cacheRead = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "output_tokens") {
                output = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "output_tokens_details") {
                thinking = try scanNestedCount(&scanner, "thinking_tokens")
            } else if scanner.key(key, is: "cache_creation") {
                cacheCreation1h = try scanNestedCount(&scanner, "ephemeral_1h_input_tokens")
            } else if scanner.key(key, is: "speed") {
                speed = try scanner.readString()
                fast = speed == "fast"
            } else {
                try scanner.skipValue()
            }
        }
        let usage = TokenUsage(
            input: TokenCount.sum(input, cacheCreation, cacheRead),
            cachedInput: cacheRead,
            cacheWriteInput: cacheCreation,
            cacheWrite1hInput: min(cacheCreation1h, cacheCreation),
            output: output,
            reasoning: thinking
        )
        return (usage, input, fast, ExecutionModeAdapters.claude(speed))
    }

    /// One integer member of a nested object (`output_tokens_details.thinking_tokens`,
    /// `cache_creation.ephemeral_1h_input_tokens`); 0 when absent.
    private static func scanNestedCount(_ scanner: inout JSONScanner, _ name: StaticString) throws(JSONScanError) -> Int {
        guard try scanner.beginObjectIfPresent() else { return 0 }
        var count = 0
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: name) {
                count = TokenCount.clamp(try scanner.readInteger())
            } else {
                try scanner.skipValue()
            }
        }
        return count
    }

    /// Whether string content or any top-level `text` block starts with the interrupt marker, whether any
    /// top-level block is a `tool_use`, and whether the content is a string or has a `text` block.
    private static func scanContent(_ scanner: inout JSONScanner) throws(JSONScanError) -> (interrupt: Bool, toolUse: Bool, text: Bool) {
        if scanner.nextIsString {
            return (try scanner.readStringHasPrefix(interruptPrefix) ?? false, false, true)
        }
        guard try scanner.beginArrayIfPresent() else {
            try scanner.skipValue()
            return (false, false, false)
        }
        var interrupted = false
        var toolUse = false
        var text = false
        while try scanner.nextElement() {
            guard try scanner.beginObjectIfPresent() else { continue }
            var blockType: Int?
            var hasPrefix = false
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "type") {
                    blockType = try scanner.readStringMatch(["text", "tool_use"])
                } else if scanner.key(key, is: "text") {
                    hasPrefix = try scanner.readStringHasPrefix(interruptPrefix) ?? false
                } else {
                    try scanner.skipValue()
                }
            }
            if blockType == 0 { text = true }
            if blockType == 0 && hasPrefix { interrupted = true }
            if blockType == 1 { toolUse = true }
        }
        return (interrupted, toolUse, text)
    }
}
