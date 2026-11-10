import Foundation

/// Raw Codex usage object (`usage`, `total_token_usage`, `last_token_usage`).
struct CodexRawUsage: Equatable, Sendable {
    var input = 0
    var cached = 0
    var cacheWrite = 0
    var output = 0
    var reasoning = 0
    var total = 0

    static let zero = CodexRawUsage()

    var normalized: TokenUsage {
        TokenUsage(input: input, cachedInput: cached, cacheWriteInput: cacheWrite, output: output, reasoning: reasoning)
    }

    /// Field-wise difference and whether any field went negative.
    func delta(since previous: CodexRawUsage) -> (usage: CodexRawUsage, hasNegative: Bool) {
        let usage = CodexRawUsage(
            input: input - previous.input,
            cached: cached - previous.cached,
            cacheWrite: cacheWrite - previous.cacheWrite,
            output: output - previous.output,
            reasoning: reasoning - previous.reasoning,
            total: total - previous.total
        )
        let hasNegative = usage.input < 0 || usage.cached < 0 || usage.cacheWrite < 0 || usage.output < 0
            || usage.reasoning < 0 || usage.total < 0
        return (usage, hasNegative)
    }
}

struct CodexSessionMeta: Equatable, Sendable {
    var id: String?
    var cwd: String?
    var agentPath: String?
    var isSubagent: Bool
    /// The spawning thread (METRICS 6.7).
    var parentThreadId: String?
    /// `payload.model_provider` (METRICS 3.2).
    var modelProvider: String?
    /// `payload.subagent_history_start_ordinal`: lines with a lower `ordinal` are copied history (METRICS 3.5).
    var subagentStartOrdinal: Int64?
}

struct CodexLegacyInfo: Equatable, Sendable {
    var total: CodexRawUsage?
    var last: CodexRawUsage?
}

enum CodexLineKind: Equatable, Sendable {
    case sessionMeta(CodexSessionMeta)
    case turnContext(model: String?, cwd: String?)
    case input
    case output
    case agentMessage(recipient: String?)
    indirect case modernUsage(responseId: String?, threadId: String?, usage: CodexRawUsage)
    /// `event_msg` / `token_count`; `info` is nil when it is null or missing.
    indirect case legacyUsage(threadId: String?, info: CodexLegacyInfo?)
    case turnEnded
    case other
}

/// How a line changes a rollout's mode context (EXECUTION-MODES, Codex).
enum CodexModeUpdate: Equatable, Sendable {
    case none
    /// `turn_context`: every turn fact is replaced by these.
    case turn(ExecutionModes)
    /// `thread_settings_applied`: each setting replaces (or, without a fact, clears) the selected session fact of its kind.
    case session([CodexThreadSetting])
    /// `token_usage_record`: facts of that response only, never of the context.
    case request(ExecutionModes)
}

struct CodexThreadSetting: Equatable, Sendable {
    var kind: ExecutionModeKind
    var provider: String?
    var fact: ExecutionModeFact?
}

struct CodexLine: Equatable, Sendable {
    var timestampMs: Int64?
    /// The top-level `ordinal` of a paginated rollout line, when it is an ordinal (METRICS 3.5).
    var ordinal: Int64?
    var kind: CodexLineKind
    var modeUpdate: CodexModeUpdate = .none
    /// `payload.rate_limits` of an `event_msg` / `token_count` line, when it is an object (QUOTAS 3).
    var rateLimits: CodexRateLimits?
}

/// Decodes the fields of a Codex envelope line that METRICS 4.2 needs. Conversation text is never captured.
enum CodexLineExtractor {
    private static let envelopeTypes: [StaticString] = [
        "session_meta", "turn_context", "token_usage_record", "event_msg", "response_item",
    ]

    private struct Payload {
        var type: String?
        var role: String?
        var recipient: String?
        var id: String?
        var cwd: String?
        var agentPath: String?
        var threadSource: String?
        var sourceHasSubagent = false
        var parentThreadId: String?
        var spawnParentThreadId: String?
        var model: String?
        var modelProvider: String?
        var subagentStartOrdinal: Int64?
        var serviceTier: String?
        /// `.some(nil)`: a string too long to keep, which still wins over the collaboration mode and gives no fact.
        var effort: String??
        var collaborationEffort: String??
        var threadSettings: ThreadSettings?
        var responseId: String?
        var threadId: String?
        var usage: CodexRawUsage?
        var infoPresent = false
        var info: CodexLegacyInfo?
        var rateLimits: CodexRateLimits?
    }

    /// Outer nil: the field is absent; `.some(nil)`: present without a usable string, which clears the setting.
    private struct ThreadSettings {
        var serviceTier: String??
        var reasoningEffort: String??
    }

    static func extract(_ bytes: UnsafeRawBufferPointer) -> LineExtraction<CodexLine> {
        var scanner = JSONScanner(bytes)
        if scanner.isBlank { return .blank }
        do {
            var timestamp: Int64?
            var ordinal: Int64?
            var envelope: Int?
            var payload: Payload?
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "timestamp") {
                    timestamp = try scanner.readTimestamp()
                } else if scanner.key(key, is: "ordinal") {
                    ordinal = try scanner.readOrdinal()
                } else if scanner.key(key, is: "type") {
                    envelope = try scanner.readStringMatch(envelopeTypes)
                } else if scanner.key(key, is: "payload") {
                    payload = try scanPayload(&scanner)
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
            var line = CodexLine(timestampMs: timestamp, ordinal: ordinal, kind: classify(envelope: envelope, payload: payload))
            line.modeUpdate = modeUpdate(envelope: envelope, payload: payload)
            if envelope == 3, payload?.type == "token_count" { line.rateLimits = payload?.rateLimits }
            return .line(line)
        } catch {
            return .malformed
        }
    }

    private static func modeUpdate(envelope: Int?, payload: Payload?) -> CodexModeUpdate {
        switch envelope {
        case 1:
            var facts = ExecutionModeAdapters.openAI(payload?.serviceTier, evidence: .selected, scope: .turn)
            let raw = payload?.effort ?? payload?.collaborationEffort
            if let raw, let effort = ExecutionModeAdapters.effort(raw, evidence: .recorded, scope: .turn) {
                facts.insert(effort)
            }
            return .turn(facts)
        case 2:
            return .request(ExecutionModeAdapters.openAI(payload?.serviceTier, evidence: .recorded, scope: .request))
        case 3:
            guard payload?.type == "thread_settings_applied", let settings = payload?.threadSettings else { return .none }
            var changes: [CodexThreadSetting] = []
            if let tier = settings.serviceTier {
                let fact = ExecutionModeAdapters.openAI(tier, evidence: .selected, scope: .session).facts.first
                changes.append(CodexThreadSetting(kind: .serviceTier, provider: "openai", fact: fact))
            }
            if let effort = settings.reasoningEffort {
                let fact = ExecutionModeAdapters.effort(effort, evidence: .selected, scope: .session)
                changes.append(CodexThreadSetting(kind: .effort, provider: nil, fact: fact))
            }
            return .session(changes)
        default:
            return .none
        }
    }

    private static func classify(envelope: Int?, payload: Payload?) -> CodexLineKind {
        switch envelope {
        case 0:
            let payload = payload ?? Payload()
            return .sessionMeta(CodexSessionMeta(
                id: payload.id,
                cwd: payload.cwd,
                agentPath: payload.agentPath,
                isSubagent: payload.threadSource == "subagent" || payload.sourceHasSubagent,
                parentThreadId: payload.parentThreadId ?? payload.spawnParentThreadId,
                modelProvider: payload.modelProvider,
                subagentStartOrdinal: payload.subagentStartOrdinal
            ))
        case 1:
            return .turnContext(model: payload?.model, cwd: payload?.cwd)
        case 2:
            guard let payload else { return .modernUsage(responseId: nil, threadId: nil, usage: .zero) }
            return .modernUsage(responseId: payload.responseId, threadId: payload.threadId, usage: payload.usage ?? .zero)
        case 3:
            guard let payload, let type = payload.type else { return .other }
            switch type {
            case "token_count":
                return .legacyUsage(threadId: payload.threadId, info: payload.info)
            case "task_started", "user_message":
                return .input
            case "task_complete", "turn_aborted":
                return .turnEnded
            default:
                return .other
            }
        case 4:
            guard let payload, let type = payload.type else { return .other }
            if type.hasSuffix("_output") { return .input }
            switch type {
            case "message":
                switch payload.role {
                case "user", "developer", "system": return .input
                case "assistant": return .output
                default: return .other
                }
            case "agent_message":
                return .agentMessage(recipient: payload.recipient)
            case "reasoning":
                return .output
            default:
                return type.hasSuffix("_call") ? .output : .other
            }
        default:
            return .other
        }
    }

    private static func scanPayload(_ scanner: inout JSONScanner) throws(JSONScanError) -> Payload? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var payload = Payload()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                payload.type = try scanner.readString()
            } else if scanner.key(key, is: "role") {
                payload.role = try scanner.readString()
            } else if scanner.key(key, is: "recipient") {
                payload.recipient = try scanner.readString()
            } else if scanner.key(key, is: "id") {
                payload.id = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "cwd") {
                payload.cwd = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "agent_path") {
                payload.agentPath = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "thread_source") {
                payload.threadSource = try scanner.readString()
            } else if scanner.key(key, is: "parent_thread_id") {
                payload.parentThreadId = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "source") {
                (payload.sourceHasSubagent, payload.spawnParentThreadId) = try scanSource(&scanner)
            } else if scanner.key(key, is: "model") {
                payload.model = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "service_tier") || scanner.key(key, is: "serviceTier") {
                payload.serviceTier = try scanner.readString()
            } else if scanner.key(key, is: "effort") {
                payload.effort = try scanner.readStringValue()
            } else if scanner.key(key, is: "collaboration_mode") {
                payload.collaborationEffort = try scanCollaborationEffort(&scanner)
            } else if scanner.key(key, is: "thread_settings") {
                payload.threadSettings = try scanThreadSettings(&scanner)
            } else if scanner.key(key, is: "model_provider") {
                payload.modelProvider = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "subagent_history_start_ordinal") {
                payload.subagentStartOrdinal = try scanner.readOrdinal()
            } else if scanner.key(key, is: "response_id") {
                payload.responseId = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "thread_id") {
                payload.threadId = try scanner.readNonEmptyString()
            } else if scanner.key(key, is: "usage") {
                payload.usage = try scanUsage(&scanner)
            } else if scanner.key(key, is: "info") {
                payload.info = try scanInfo(&scanner)
            } else if scanner.key(key, is: "rate_limits") {
                payload.rateLimits = try CodexRateLimits.scan(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return payload
    }

    /// `collaboration_mode.settings.reasoning_effort` when it is a string.
    private static func scanCollaborationEffort(_ scanner: inout JSONScanner) throws(JSONScanError) -> String?? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var effort: String??
        while let key = try scanner.nextMember() {
            guard scanner.key(key, is: "settings") else {
                try scanner.skipValue()
                continue
            }
            effort = nil
            guard try scanner.beginObjectIfPresent() else { continue }
            while let inner = try scanner.nextMember() {
                if scanner.key(inner, is: "reasoning_effort") {
                    effort = try scanner.readStringValue()
                } else {
                    try scanner.skipValue()
                }
            }
        }
        return effort
    }

    /// `thread_settings`: a present field changes its setting, a string value sets it and any other value clears it.
    private static func scanThreadSettings(_ scanner: inout JSONScanner) throws(JSONScanError) -> ThreadSettings? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var settings = ThreadSettings()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "service_tier") {
                settings.serviceTier = .some(try scanner.readString())
            } else if scanner.key(key, is: "reasoning_effort") {
                settings.reasoningEffort = .some(try scanner.readString())
            } else {
                try scanner.skipValue()
            }
        }
        return settings
    }

    /// `payload.source`: whether it names a sub-agent, and `subagent.thread_spawn.parent_thread_id`.
    private static func scanSource(_ scanner: inout JSONScanner) throws(JSONScanError) -> (Bool, String?) {
        guard try scanner.beginObjectIfPresent() else { return (false, nil) }
        var found = false
        var parent: String?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "subagent") {
                found = try !scanner.readNull()
                parent = found ? try scanSpawnParent(&scanner) : nil
            } else {
                try scanner.skipValue()
            }
        }
        return (found, parent)
    }

    private static func scanSpawnParent(_ scanner: inout JSONScanner) throws(JSONScanError) -> String? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var parent: String?
        while let key = try scanner.nextMember() {
            guard scanner.key(key, is: "thread_spawn") else {
                try scanner.skipValue()
                continue
            }
            parent = nil
            guard try scanner.beginObjectIfPresent() else { continue }
            while let inner = try scanner.nextMember() {
                if scanner.key(inner, is: "parent_thread_id") {
                    parent = try scanner.readNonEmptyString()
                } else {
                    try scanner.skipValue()
                }
            }
        }
        return parent
    }

    private static func scanInfo(_ scanner: inout JSONScanner) throws(JSONScanError) -> CodexLegacyInfo? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var info = CodexLegacyInfo()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "total_token_usage") {
                info.total = try scanUsage(&scanner)
            } else if scanner.key(key, is: "last_token_usage") {
                info.last = try scanUsage(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return info
    }

    static func scanUsage(_ scanner: inout JSONScanner) throws(JSONScanError) -> CodexRawUsage? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var usage = CodexRawUsage()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "input_tokens") {
                usage.input = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "cached_input_tokens") {
                usage.cached = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "cache_write_input_tokens") {
                usage.cacheWrite = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "output_tokens") {
                usage.output = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "reasoning_output_tokens") {
                usage.reasoning = TokenCount.clamp(try scanner.readInteger())
            } else if scanner.key(key, is: "total_tokens") {
                usage.total = TokenCount.clamp(try scanner.readInteger())
            } else {
                try scanner.skipValue()
            }
        }
        return usage
    }
}
