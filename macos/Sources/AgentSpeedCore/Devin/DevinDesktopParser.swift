import Foundation

struct DevinDesktopParser: Sendable {
    private struct Aggregate: Sendable {
        var input = 0
        var cached = 0
        var write = 0
        var output = 0
        var model: String?
        var isEmpty: Bool { input + write == 0 && output == 0 }
    }

    let agentId: String
    private let stem: String
    private(set) var state: AgentState
    private(set) var title: String?
    private var aggregate = Aggregate()

    init(agentId: String) {
        self.agentId = agentId
        let name = (agentId as NSString).lastPathComponent
        stem = name.hasSuffix(".ndjson") ? String(name.dropLast(7)) : name
        state = AgentState(source: .devin)
    }

    mutating func reset() {
        let id = agentId
        self = DevinDesktopParser(agentId: id)
    }

    mutating func noteMalformedLine() {
        state.malformedLines += 1
    }

    mutating func consume(_ data: Data, lineNumber: Int) -> [ResponseRecord] {
        guard let object = try? JSONParser.parse(data), let notification = object["notification"], notification.object != nil else {
            state.malformedLines += 1
            return []
        }
        if notification["sessionUpdate"]?.string == "session_info_update" {
            if let candidate = notification["title"]?.string?.trimmingCharacters(in: .whitespacesAndNewlines), !candidate.isEmpty {
                title = candidate
            }
            return []
        }
        let stamp = Self.timestamp(notification)
        if let stamp { state.noteLine(at: stamp) }
        let model = Self.model(notification)
        if notification["sessionUpdate"]?.string == "usage_update", let meta = notification["_meta"], meta.object != nil,
           Self.hasCanonicalUsage(meta) {
            if let value = Self.count(meta["cognition.ai/inputTokens"]) { aggregate.input = value }
            if let value = Self.count(meta["cognition.ai/cachedReadTokens"]) { aggregate.cached = value }
            if let value = Self.count(meta["cognition.ai/cachedWriteTokens"]) { aggregate.write = value }
            if let value = Self.count(meta["cognition.ai/outputTokens"]) {
                aggregate.output = TokenCount.sum(aggregate.output, value)
            }
            aggregate.model = aggregate.model ?? model
            return aggregateResponse().map { [$0] } ?? []
        }
        guard let usage = Self.legacyUsage(notification) else { return [] }
        let tokens = Self.cliTokens(usage)
        guard tokens.input > 0 || tokens.output > 0, let stamp else { return [] }
        if let model { state.model = model }
        let key = "devin-desktop:" + stem + ":line:" + String(lineNumber)
        let start = Self.duration(usage).map { stamp - $0 }
        return [ResponseRecord(key: key, source: .devin, agentId: agentId, model: model, startMs: start, endMs: stamp,
                               usage: tokens)]
    }

    private mutating func aggregateResponse() -> ResponseRecord? {
        guard !aggregate.isEmpty, let end = state.lastLineMs else { return nil }
        state.model = aggregate.model
        return ResponseRecord(key: "devin-desktop:" + stem + ":usage", source: .devin, agentId: agentId,
                              model: aggregate.model, startMs: nil, endMs: end,
                              usage: TokenUsage(input: TokenCount.sum(aggregate.input, aggregate.write),
                                                cachedInput: aggregate.cached, cacheWriteInput: aggregate.write,
                                                output: aggregate.output))
    }

    static func cliTokens(_ usage: JSONValue) -> TokenUsage {
        let read = count(usage["cache_read_tokens"]) ?? 0
        let write = count(usage["cache_creation_tokens"]) ?? 0
        return TokenUsage(input: TokenCount.sum(count(usage["input_tokens"]) ?? 0, read, write), cachedInput: read,
                          cacheWriteInput: write, output: count(usage["output_tokens"]) ?? 0)
    }

    static func duration(_ usage: JSONValue) -> Int64? {
        guard let number = usage["total_time_ms"]?.literal, let value = number.double, value.isFinite, value > 0 else {
            return nil
        }
        if value >= Double(Int64.max) { return .max }
        return Int64(value.rounded(.towardZero))
    }

    private static func count(_ value: JSONValue?) -> Int? {
        guard let literal = value?.literal else { return nil }
        if let integer = literal.int64 { return TokenCount.clamp(integer) }
        guard let number = literal.double, number.isFinite else { return 0 }
        if number >= Double(TokenCount.ceiling) { return Int(TokenCount.ceiling) }
        if number <= 0 { return 0 }
        return Int(number.rounded(.towardZero))
    }

    private static func hasCanonicalUsage(_ meta: JSONValue) -> Bool {
        ["cognition.ai/inputTokens", "cognition.ai/cachedReadTokens", "cognition.ai/cachedWriteTokens",
         "cognition.ai/outputTokens"].contains { meta[$0] != nil }
    }

    private static func legacyUsage(_ notification: JSONValue) -> JSONValue? {
        let paths = [["content", "metadata", "metrics"], ["metadata", "metrics"], ["metrics"],
                     ["content", "metadata"], ["metadata"]]
        for path in paths {
            var value = notification
            var found = true
            for component in path {
                guard let next = value[component] else { found = false; break }
                value = next
            }
            if found, value.object != nil { return value }
        }
        return nil
    }

    private static func timestamp(_ notification: JSONValue) -> Int64? {
        let paths = [["content", "metadata", "created_at"], ["metadata", "created_at"], ["created_at"], ["timestamp"]]
        for path in paths {
            var value = notification
            var found = true
            for component in path {
                guard let next = value[component] else { found = false; break }
                value = next
            }
            if found, let text = value.string, let stamp = Timestamp.parse(text) { return stamp }
        }
        return nil
    }

    private static func model(_ notification: JSONValue) -> String? {
        let paths = [["content", "metadata", "generation_model"], ["metadata", "generation_model"],
                     ["_meta", "cognition.ai/model"]]
        for path in paths {
            var value = notification
            var found = true
            for component in path {
                guard let next = value[component] else { found = false; break }
                value = next
            }
            if found, let model = value.string, !model.isEmpty, model.lowercased() != "adaptive" { return model }
        }
        return nil
    }
}
