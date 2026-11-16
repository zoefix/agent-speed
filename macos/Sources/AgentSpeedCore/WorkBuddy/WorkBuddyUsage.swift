import Foundation

/// agents-core's `Usage` (`providerData.usage`): the members METRICS 4.9 reads.
struct WorkBuddyAgentsUsage: Equatable, Sendable {
    var inputTokens: JSONNumber?
    var outputTokens: JSONNumber?
    /// Σ `inputTokensDetails[].cached_tokens`, already clamped.
    var cachedTokens = 0
    /// Σ `outputTokensDetails[].reasoning_tokens`, already clamped.
    var reasoningTokens = 0
}

/// The service's OpenAI-style usage of the same call (`providerData.rawUsage`).
struct WorkBuddyRawUsage: Equatable, Sendable {
    var promptTokens: JSONNumber?
    var completionTokens: JSONNumber?
    var cacheReadInputTokens: JSONNumber?
    var cacheCreationInputTokens: JSONNumber?
    var promptCacheHitTokens: JSONNumber?
    var promptCacheWriteTokens: JSONNumber?
    /// `prompt_tokens_details.cached_tokens`
    var detailCachedTokens: JSONNumber?
    /// `completion_tokens_details.reasoning_tokens`
    var detailReasoningTokens: JSONNumber?
    /// The last element of `model_calls` when it is an object (CLI 2.184.0+); nil for anything else.
    var lastAttempt: WorkBuddyAttemptNumbers?
}

/// `duration_ms` and `time_to_first_token_ms` of one element of `rawUsage.model_calls`.
struct WorkBuddyAttemptNumbers: Equatable, Sendable {
    var durationMs: JSONNumber?
    var firstTokenMs: JSONNumber?
}

/// The measured attempt that answered a call (METRICS 4.9 Timing model), in truncated milliseconds.
struct WorkBuddyAttempt: Equatable, Sendable {
    /// Above 0.
    var durationMs: Int64
    /// Above 0 and at most `durationMs`; nil otherwise.
    var firstTokenMs: Int64?

    init?(_ numbers: WorkBuddyAttemptNumbers?) {
        guard let duration = numbers?.durationMs, duration.isFinite, duration.truncated > 0 else { return nil }
        durationMs = duration.truncated
        if let first = numbers?.firstTokenMs, first.isFinite, first.truncated > 0, first.truncated <= durationMs {
            firstTokenMs = first.truncated
        }
    }

    /// The call's start for a line written at `timestamp`, saturating.
    func start(endingAt timestamp: Int64) -> Int64 {
        let (start, overflow) = timestamp.subtractingReportingOverflow(durationMs)
        return overflow ? .min : start
    }
}

/// Normalized tokens of one WorkBuddy model call (METRICS 4.9 Tokens): per field the larger of the two records.
enum WorkBuddyTokens {
    static func normalize(_ usage: WorkBuddyAgentsUsage?, _ raw: WorkBuddyRawUsage?) -> TokenUsage {
        let usage = usage ?? WorkBuddyAgentsUsage()
        let raw = raw ?? WorkBuddyRawUsage()
        let cached = max(usage.cachedTokens,
                         firstPositive(raw.cacheReadInputTokens, raw.detailCachedTokens, raw.promptCacheHitTokens))
        // DeepSeek's `prompt_cache_miss_tokens` are uncached input, never cache writes.
        let write = firstPositive(raw.cacheCreationInputTokens, raw.promptCacheWriteTokens)
        return TokenUsage(
            input: max(count(usage.inputTokens), count(raw.promptTokens), add(cached, write)),
            cachedInput: cached,
            cacheWriteInput: write,
            output: max(count(usage.outputTokens), count(raw.completionTokens)),
            reasoning: max(usage.reasoningTokens, count(raw.detailReasoningTokens))
        )
    }

    /// A number truncated and clamped; anything else is 0.
    static func count(_ number: JSONNumber?) -> Int {
        TokenCount.clamp(number?.truncated)
    }

    /// The first value that is a finite number above 0, truncated (so 0.5 gives 0); 0 when none is.
    static func firstPositive(_ numbers: JSONNumber?...) -> Int {
        for case let number? in numbers where number.isFinite && number.isPositive {
            return count(number)
        }
        return 0
    }

    /// Adds clamped token counts without leaving the per-field ceiling.
    static func add(_ lhs: Int, _ rhs: Int) -> Int {
        Int(min(TokenCount.ceiling, Int64(lhs) + Int64(rhs)))
    }
}
