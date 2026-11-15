import Foundation

/// Explicit native counters. Cache reads and writes are already part of input, and reasoning is part of output.
struct QuotaTokenCounts: Sendable, Hashable {
    static let limit: Int64 = 9_007_199_254_740_991

    var input: Int64
    var output: Int64
    var cachedInput: Int64?
    var cacheWriteInput: Int64?
    var reasoning: Int64?

    var isValid: Bool {
        guard (0...Self.limit).contains(input), (0...(Self.limit - input)).contains(output),
              (0...input).contains(cachedInput ?? 0),
              (0...(input - (cachedInput ?? 0))).contains(cacheWriteInput ?? 0),
              (0...output).contains(reasoning ?? 0) else { return false }
        return true
    }

    func adding(_ other: Self) -> Self? {
        guard isValid, other.isValid else { return nil }
        func sum(_ a: Int64?, _ b: Int64?) -> Int64? {
            guard let a, let b else { return nil }
            return a + b
        }
        let total = Self(input: input + other.input, output: output + other.output,
                         cachedInput: sum(cachedInput, other.cachedInput),
                         cacheWriteInput: sum(cacheWriteInput, other.cacheWriteInput),
                         reasoning: sum(reasoning, other.reasoning))
        return total.isValid ? total : nil
    }
}

/// One contribution to the measured spend. The model is a public pricing-table ID or nil for unknown.
struct QuotaTokenContribution: Sendable, Equatable {
    var counts: QuotaTokenCounts
    var model: String?
    var earliestEndMs: Int64
    var latestEndMs: Int64

    func adding(_ other: Self) -> Self? {
        guard let counts = counts.adding(other.counts) else { return nil }
        return Self(counts: counts, model: model == other.model ? model : nil,
                    earliestEndMs: min(earliestEndMs, other.earliestEndMs),
                    latestEndMs: max(latestEndMs, other.latestEndMs))
    }
}

struct QuotaTokenUsage: Sendable, Equatable {
    struct Model: Sendable, Equatable {
        var id: String?
        var input: Int64
        var output: Int64
    }

    var counts: QuotaTokenCounts
    var models: [Model]

    var json: ReportJSON {
        .object([
            "inputTokens": .int(counts.input),
            "outputTokens": .int(counts.output),
            "cachedInputTokens": .optionalInt(counts.cachedInput),
            "cacheWriteInputTokens": .optionalInt(counts.cacheWriteInput),
            "reasoningTokens": .optionalInt(counts.reasoning),
            "models": .array(models.map {
                .object(["model": $0.id.map(ReportJSON.string) ?? .null,
                         "inputTokens": .int($0.input), "outputTokens": .int($0.output)])
            }),
        ])
    }
}
