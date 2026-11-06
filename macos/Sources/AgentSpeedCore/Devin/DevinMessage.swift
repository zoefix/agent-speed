import Foundation

/// The parts of one CLI `message_nodes.chat_message` that METRICS 4.17 reads: never its content.
struct DevinMessage: Equatable, Sendable {
    enum Role: Equatable, Sendable {
        case user
        case tool
        case assistant
        case other
    }

    /// `metadata.metrics`, as token values (METRICS 3.2) and finite, truncated times.
    struct Metrics: Equatable, Sendable {
        var input = 0
        var output = 0
        var cacheRead = 0
        var cacheCreation = 0
        var totalMs: Int64?
        var ttftMs: Int64?
    }

    var role: Role = .other
    var hasToolCalls = false
    var finishedWithToolCalls = false
    var requestId: String?
    var model: String?
    var metrics: Metrics?

    /// The phase a row sets (nil: none).
    var phase: AgentPhase? {
        switch role {
        case .user, .tool: .generating
        case .assistant: hasToolCalls || finishedWithToolCalls ? .tools : .idle
        case .other: nil
        }
    }

    /// Nil when the text is not a JSON object (a malformed row).
    static func parse(_ bytes: UnsafeRawBufferPointer) -> DevinMessage? {
        var scanner = JSONScanner(bytes)
        var message = DevinMessage()
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "role") {
                    message.role = switch try scanner.readStringMatch(["user", "tool", "assistant"]) {
                    case 0: .user
                    case 1: .tool
                    case 2: .assistant
                    default: .other
                    }
                } else if scanner.key(key, is: "tool_calls") {
                    message.hasToolCalls = try nonEmptyArray(&scanner)
                } else if scanner.key(key, is: "metadata") {
                    try readMetadata(&scanner, into: &message)
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
        } catch {
            return nil
        }
        return message
    }

    private static func nonEmptyArray(_ scanner: inout JSONScanner) throws(JSONScanError) -> Bool {
        guard try scanner.beginArrayIfPresent() else {
            try scanner.skipValue()
            return false
        }
        var count = 0
        while try scanner.nextElement() {
            count += 1
            try scanner.skipValue()
        }
        return count > 0
    }

    private static func readMetadata(_ scanner: inout JSONScanner, into message: inout DevinMessage) throws(JSONScanError) {
        message.requestId = nil
        message.model = nil
        message.finishedWithToolCalls = false
        message.metrics = nil
        guard scanner.nextIsObject else {
            try scanner.skipValue()
            return
        }
        try scanner.beginObject()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "metrics") {
                message.metrics = try readMetrics(&scanner)
            } else if scanner.key(key, is: "request_id") {
                message.requestId = trimmed(try scanner.readString())
            } else if scanner.key(key, is: "generation_model") {
                message.model = trimmed(try scanner.readString())
            } else if scanner.key(key, is: "finish_reason") {
                message.finishedWithToolCalls = try scanner.readStringMatch(["tool_calls"]) == 0
            } else {
                try scanner.skipValue()
            }
        }
    }

    private static func readMetrics(_ scanner: inout JSONScanner) throws(JSONScanError) -> Metrics? {
        guard scanner.nextIsObject else {
            try scanner.skipValue()
            return nil
        }
        var metrics = Metrics()
        try scanner.beginObject()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "input_tokens") {
                metrics.input = TokenCount.clamp(try scanner.readNumber()?.truncated)
            } else if scanner.key(key, is: "output_tokens") {
                metrics.output = TokenCount.clamp(try scanner.readNumber()?.truncated)
            } else if scanner.key(key, is: "cache_read_tokens") {
                metrics.cacheRead = TokenCount.clamp(try scanner.readNumber()?.truncated)
            } else if scanner.key(key, is: "cache_creation_tokens") {
                metrics.cacheCreation = TokenCount.clamp(try scanner.readNumber()?.truncated)
            } else if scanner.key(key, is: "total_time_ms") {
                metrics.totalMs = finite(try scanner.readNumber())
            } else if scanner.key(key, is: "ttft_ms") {
                metrics.ttftMs = finite(try scanner.readNumber())
            } else {
                try scanner.skipValue()
            }
        }
        return metrics
    }

    private static func finite(_ number: JSONNumber?) -> Int64? {
        guard let number, number.isFinite else { return nil }
        return number.truncated
    }

    static func trimmed(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { return nil }
        return value
    }
}

/// One CLI call as METRICS 4.17 *Calls* defines it.
enum DevinCalls {
    static func key(session: String, requestId: String?, rowId: Int64) -> String {
        if let requestId { return "devin:" + session + ":request:" + requestId }
        return "devin:" + session + ":row:" + String(rowId)
    }

    /// The session model: `sessions.model` unless blank or `adaptive` (a routing mode, not a model).
    static func sessionModel(_ raw: String?) -> String? {
        guard let model = DevinMessage.trimmed(raw), model.lowercased() != "adaptive" else { return nil }
        return model
    }

    /// The response of an assistant row with metrics; nil when it has none or no tokens.
    static func record(of message: DevinMessage, session: String, rowId: Int64, endMs: Int64, sessionModel: String?,
                       agentId: String) -> ResponseRecord? {
        guard message.role == .assistant, let metrics = message.metrics else { return nil }
        let input = TokenCount.sum(metrics.input, metrics.cacheRead, metrics.cacheCreation)
        guard input > 0 || metrics.output > 0 else { return nil }
        var start: Int64?
        if let total = metrics.totalMs, total > 0 {
            let (value, overflow) = endMs.subtractingReportingOverflow(total)
            start = overflow ? .min : value
        }
        var first: Int64?
        if let start, let total = metrics.totalMs, let ttft = metrics.ttftMs, ttft > 0, ttft <= total {
            first = start + ttft
        }
        let usage = TokenUsage(input: input, cachedInput: metrics.cacheRead, cacheWriteInput: metrics.cacheCreation,
                               output: metrics.output)
        return ResponseRecord(key: key(session: session, requestId: message.requestId, rowId: rowId), source: .devin,
                              agentId: agentId, model: message.model ?? sessionModel, startMs: start, endMs: endMs,
                              usage: usage, firstMs: first)
    }
}
