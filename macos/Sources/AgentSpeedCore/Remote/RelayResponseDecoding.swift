import Foundation

extension RelayLineDecoder {
    /// A `response` line: the record of REMOTE 5.4 without a cost (the app prices it, METRICS 9.3).
    func response(_ line: RemoteFields) throws(RemoteLineError) -> RemoteResponse {
        let f = try line.object("r")
        let key = try f.string("key", maxBytes: RemoteLimits.keyBytes)
        guard !key.isEmpty else { throw RemoteLineError(.outOfRange, "r.key") }
        let requestCount = try f.integer("requestCount")
        guard (1...100_000).contains(requestCount) else { throw RemoteLineError(.outOfRange, "r.requestCount") }
        let cacheWrite = try f.tokens("cacheWriteInput")
        let usage = TokenUsage(
            input: try f.tokens("input"),
            cachedInput: try f.tokens("cachedInput"),
            cacheWriteInput: cacheWrite,
            cacheWrite1hInput: min(cacheWrite, try f.tokens("cacheWrite1hInput")),
            output: try f.tokens("output"),
            reasoning: try f.tokens("reasoning")
        )
        let record = ResponseRecord(
            key: key,
            source: try f.source("source"),
            agentId: try agentId(f, "agent"),
            model: try f.modelString("model", maxBytes: RemoteLimits.modelBytes),
            startMs: try f.optionalTime("startMs"),
            endMs: try f.time("endMs"),
            usage: usage,
            isFinal: try f.bool("final"),
            provider: try f.modelString("provider", maxBytes: RemoteLimits.providerBytes),
            fast: try f.bool("fast"),
            requestCount: Int(requestCount),
            modes: try Self.modes(f, "modes"),
            firstMs: try f.optionalTime("firstMs"),
            host: hostId
        )
        return RemoteResponse(
            hostId: hostId,
            record: record,
            isCompletion: try line.bool("completion"),
            project: try line.string("project", maxBytes: RemoteLimits.projectBytes)
        )
    }

    /// A `tool` line (METRICS 11.10 *Counting*).
    func tool(_ f: RemoteFields) throws(RemoteLineError) -> RemoteToolCompletion {
        RemoteToolCompletion(
            hostId: hostId,
            agentId: try agentId(f, "agent"),
            source: try f.source("source"),
            completion: ToolCompletion(
                callMs: try f.time("callMs"),
                resultMs: try f.time("resultMs"),
                failed: try f.optionalBool("failed"),
                model: try f.modelString("model", maxBytes: RemoteLimits.modelBytes),
                seeded: try f.bool("seeded")
            )
        )
    }
}
