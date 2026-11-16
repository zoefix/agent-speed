import Foundation

/// The token counts the ledger keeps per response and row (QUOTAS 10.3 *Token counts*): `input` holds cache reads and
/// writes once, `output` reasoning once; a detail is nil where the source's logs do not report it.
public struct QuotaTokenCounts: Equatable, Sendable {
    public var input: Int64
    public var output: Int64
    public var cachedInput: Int64?
    public var cacheWriteInput: Int64?
    public var reasoning: Int64?

    public init(input: Int64, output: Int64, cachedInput: Int64?, cacheWriteInput: Int64?, reasoning: Int64?) {
        self.input = input
        self.output = output
        self.cachedInput = cachedInput
        self.cacheWriteInput = cacheWriteInput
        self.reasoning = reasoning
    }

    static let zero = QuotaTokenCounts(input: 0, output: 0, cachedInput: 0, cacheWriteInput: 0, reasoning: 0)

    /// The details each source's logs report; the parsers write 0 for an absent field, so the others are missing.
    static let details: [String: (cachedInput: Bool, cacheWriteInput: Bool, reasoning: Bool)] = [
        "codex": (true, false, true),
        "claudeCode": (true, true, false),
        "claudeDesktop": (true, true, false),
        "gemini": (true, false, true),
        "antigravity": (true, false, true),
    ]

    /// A response's counts: the details clamped (cache reads at most the input, cache writes at most the rest of it,
    /// reasoning at most the output) and those the source does not report missing.
    public init(source: String, usage: TokenUsage) {
        let input = Int64(max(0, usage.input)), output = Int64(max(0, usage.output))
        let cached = min(Int64(max(0, usage.cachedInput)), input)
        let kept = Self.details[source] ?? (false, false, false)
        self.init(input: input, output: output, cachedInput: kept.cachedInput ? cached : nil,
                  cacheWriteInput: kept.cacheWriteInput ? min(Int64(max(0, usage.cacheWriteInput)), input - cached) : nil,
                  reasoning: kept.reasoning ? min(Int64(max(0, usage.reasoning)), output) : nil)
    }

    /// Both contributions of one row; a detail either misses stays missing. Sums saturate (a stored row is read back
    /// from disk), so an absurd total ends as an unsafe one instead of trapping.
    func adding(_ other: QuotaTokenCounts) -> QuotaTokenCounts {
        func sum(_ a: Int64?, _ b: Int64?) -> Int64? { a.flatMap { a in b.map { Self.add(a, $0) } } }
        return QuotaTokenCounts(input: Self.add(input, other.input), output: Self.add(output, other.output),
                                cachedInput: sum(cachedInput, other.cachedInput),
                                cacheWriteInput: sum(cacheWriteInput, other.cacheWriteInput),
                                reasoning: sum(reasoning, other.reasoning))
    }

    static func add(_ a: Int64, _ b: Int64) -> Int64 {
        let (sum, overflow) = a.addingReportingOverflow(b)
        return overflow ? .max : sum
    }

    /// Every value is a count: none negative.
    var isValid: Bool {
        [input, output, cachedInput ?? 0, cacheWriteInput ?? 0, reasoning ?? 0].allSatisfy { $0 >= 0 }
    }

    /// Unmeasured (nil) when either is.
    static func merge(_ a: QuotaTokenCounts?, _ b: QuotaTokenCounts?) -> QuotaTokenCounts? {
        a.flatMap { a in b.map { a.adding($0) } }
    }
}

/// `tokenUsage` of a report entry (REPORTING-EXTENSIONS 12): the counts of the rows its `usedMicroUsd` sums, split by
/// price-table model with one nil row for every other model.
public struct QuotaTokenUsage: Equatable, Sendable {
    public struct Model: Equatable, Sendable {
        /// A price-table id; nil for the models outside the table.
        public var model: String?
        public var inputTokens: Int64
        public var outputTokens: Int64
    }

    public var counts: QuotaTokenCounts
    public var models: [Model]

    static let maxModels = 16
    static let maxSafe: Int64 = 9_007_199_254_740_991

    /// Nil past 16 models or an unsafe total; rows with no tokens are left out, the rest by id with the nil row last.
    init?(counts: QuotaTokenCounts, models byModel: [String?: (input: Int64, output: Int64)]) {
        let rows = byModel.filter { $0.value.input != 0 || $0.value.output != 0 }
            .map { Model(model: $0.key, inputTokens: $0.value.input, outputTokens: $0.value.output) }
            .sorted { a, b in
                switch (a.model, b.model) {
                case let (x?, y?): Array(x.utf8).lexicographicallyPrecedes(y.utf8)
                case (_?, nil): true
                default: false
                }
            }
        guard rows.count <= Self.maxModels, counts.input <= Self.maxSafe, counts.output <= Self.maxSafe else { return nil }
        self.counts = counts
        models = rows
    }

    public var json: ReportJSON {
        .object([
            "inputTokens": .int(counts.input),
            "outputTokens": .int(counts.output),
            "cachedInputTokens": .optionalInt(counts.cachedInput),
            "cacheWriteInputTokens": .optionalInt(counts.cacheWriteInput),
            "reasoningTokens": .optionalInt(counts.reasoning),
            "models": .array(models.map {
                .object(["model": $0.model.map(ReportJSON.string) ?? .null, "inputTokens": .int($0.inputTokens),
                         "outputTokens": .int($0.outputTokens)])
            }),
        ])
    }
}
