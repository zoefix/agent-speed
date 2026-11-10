import Foundation

/// `chat_message.token_info` as METRICS 4.18 reads it: token values (METRICS 3.2) of the server's usage.
struct QoderTokens: Equatable, Sendable {
    var prompt = 0
    var completion = 0
    var cached = 0
}

/// The JSON columns of Qoder IDE's rows (METRICS 4.18): each is read only when it is a JSON object; content is never
/// read.
enum QoderJSON {
    /// `token_info`; nil when it is not a JSON object.
    static func tokens(_ bytes: UnsafeRawBufferPointer) -> QoderTokens? {
        var scanner = JSONScanner(bytes)
        var tokens = QoderTokens()
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "prompt_tokens") {
                    tokens.prompt = TokenCount.clamp(try scanner.readNumber()?.truncated)
                } else if scanner.key(key, is: "completion_tokens") {
                    tokens.completion = TokenCount.clamp(try scanner.readNumber()?.truncated)
                } else if scanner.key(key, is: "cached_tokens") {
                    tokens.cached = TokenCount.clamp(try scanner.readNumber()?.truncated)
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
        } catch {
            return nil
        }
        return tokens
    }

    /// `model_info.model_key` when it is a string that is not blank (trimmed); nil also when the text is no JSON object.
    static func modelKey(_ bytes: UnsafeRawBufferPointer) -> String? {
        var scanner = JSONScanner(bytes)
        var model: String?
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "model_key") {
                    model = DevinMessage.trimmed(try scanner.readString())
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
        } catch {
            return nil
        }
        return model
    }

    /// `chat_record.extra["ai-coding/turn-info"].finishedAt` (Unix ms, finite, truncated); nil when absent.
    static func finishedAt(_ bytes: UnsafeRawBufferPointer) -> Int64? {
        var scanner = JSONScanner(bytes)
        var finished: Int64?
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                guard scanner.key(key, is: "ai-coding/turn-info") else {
                    try scanner.skipValue()
                    continue
                }
                finished = nil
                guard scanner.nextIsObject else {
                    try scanner.skipValue()
                    continue
                }
                try scanner.beginObject()
                while let member = try scanner.nextMember() {
                    if scanner.key(member, is: "finishedAt") {
                        finished = try scanner.readNumber().flatMap { $0.isFinite ? $0.truncated : nil }
                    } else {
                        try scanner.skipValue()
                    }
                }
            }
            try scanner.finish()
        } catch {
            return nil
        }
        return finished
    }
}

/// One model call as METRICS 4.18 *Calls* defines it.
enum QoderCalls {
    /// `"qoder:" + chat_message.id`, else `"qoder:" + session id + ":row:" + rowid`.
    static func key(session: String, messageId: String?, rowId: Int64) -> String {
        if let messageId { return "qoder:" + messageId }
        return "qoder:" + session + ":row:" + String(rowId)
    }

    /// The response of an assistant row whose `token_info` is an object; nil when its input and output are both 0.
    /// `startMs` is the time of the session's previous row.
    static func record(tokens: QoderTokens, session: String, messageId: String?, rowId: Int64, startMs: Int64?,
                       endMs: Int64, model: String?, agentId: String) -> ResponseRecord? {
        guard tokens.prompt > 0 || tokens.completion > 0 else { return nil }
        let usage = TokenUsage(input: tokens.prompt, cachedInput: min(tokens.cached, tokens.prompt), cacheWriteInput: 0,
                               output: tokens.completion)
        return ResponseRecord(key: key(session: session, messageId: messageId, rowId: rowId), source: .qoder,
                              agentId: agentId, model: model, startMs: startMs, endMs: endMs, usage: usage)
    }

    /// The calls of one session's rows in `rowid` order, each timed from the row before it (history).
    static func records(of rows: [QoderDatabase.Row], session: String, agentId: String) -> [ResponseRecord] {
        var previous: Int64?
        var records: [ResponseRecord] = []
        for row in rows {
            let start = previous
            previous = row.timeMs
            guard let time = row.timeMs, row.role == .assistant, let tokens = row.tokens,
                  let record = record(tokens: tokens, session: session, messageId: row.messageId, rowId: row.rowId,
                                      startMs: start, endMs: time, model: row.model, agentId: agentId) else { continue }
            records.append(record)
        }
        return records
    }
}
