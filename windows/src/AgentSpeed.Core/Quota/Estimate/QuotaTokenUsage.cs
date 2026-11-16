using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The token counts the ledger keeps per response and row (QUOTAS 10.3 <i>Token counts</i>): <see cref="Input"/> holds cache
/// reads and writes once, <see cref="Output"/> reasoning once; a detail is null where the source's logs do not report it.
/// </summary>
public readonly record struct QuotaTokenCounts(long Input, long Output, long? CachedInput, long? CacheWriteInput, long? Reasoning)
{
    public static QuotaTokenCounts Zero { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Every value is a count: none negative.</summary>
    public bool IsValid => Input >= 0 && Output >= 0 && CachedInput is null or >= 0 && CacheWriteInput is null or >= 0 && Reasoning is null or >= 0;

    /// <summary>
    /// A response's counts: the details clamped (cache reads at most the input, cache writes at most the rest of it, reasoning
    /// at most the output) and those the source's logs lack missing; the parsers write 0 for an absent field.
    /// </summary>
    internal static QuotaTokenCounts Of(SourceKind source, TokenCounts usage)
    {
        var input = Math.Max(0, usage.Input);
        var output = Math.Max(0, usage.Output);
        var cached = Math.Min(Math.Max(0, usage.CachedInput), input);
        var (keepsCached, keepsWrites, keepsReasoning) = source switch
        {
            SourceKind.Codex => (true, false, true),
            SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => (true, true, false),
            SourceKind.Gemini or SourceKind.Antigravity => (true, false, true),
            _ => (false, false, false),
        };
        return new QuotaTokenCounts(input, output, keepsCached ? cached : null,
            keepsWrites ? Math.Min(Math.Max(0, usage.CacheWriteInput), input - cached) : null,
            keepsReasoning ? Math.Min(Math.Max(0, usage.Reasoning), output) : null);
    }

    /// <summary>Both contributions of one row; a detail either misses stays missing. Sums saturate, so an absurd stored total ends unsafe.</summary>
    public QuotaTokenCounts Adding(QuotaTokenCounts other) => new(
        Add(Input, other.Input), Add(Output, other.Output), Sum(CachedInput, other.CachedInput), Sum(CacheWriteInput, other.CacheWriteInput),
        Sum(Reasoning, other.Reasoning));

    /// <summary>Unmeasured (null) when either is.</summary>
    public static QuotaTokenCounts? Merge(QuotaTokenCounts? a, QuotaTokenCounts? b) => a is { } x && b is { } y ? x.Adding(y) : null;

    /// <summary>A saturating sum: <see cref="long.MaxValue"/> instead of an overflow.</summary>
    public static long Add(long a, long b)
    {
        var sum = unchecked(a + b);
        return ((a ^ sum) & (b ^ sum)) < 0 ? long.MaxValue : sum;
    }

    private static long? Sum(long? a, long? b) => a is { } x && b is { } y ? Add(x, y) : null;
}

/// <summary>One row of <see cref="QuotaTokenUsage.Models"/>: a price-table id, or null for every model outside the table.</summary>
public sealed record QuotaTokenModel(string? Model, long InputTokens, long OutputTokens);

/// <summary>
/// <c>tokenUsage</c> of a report entry (REPORTING-EXTENSIONS 12): the counts of the rows its <c>usedMicroUsd</c> sums, split
/// by price-table model with one null row for every other model, adding up to the totals exactly.
/// </summary>
public sealed record QuotaTokenUsage(QuotaTokenCounts Counts, IReadOnlyList<QuotaTokenModel> Models)
{
    public const int MaxModels = 16;

    public const long MaxSafe = 9_007_199_254_740_991;

    /// <summary>
    /// Null past 16 models or an unsafe total; models with no tokens are left out, the rest by id (ordinal, as UTF-8 bytes for
    /// these ASCII ids) with the null row last.
    /// </summary>
    internal static QuotaTokenUsage? Of(QuotaTokenCounts counts, IReadOnlyDictionary<string, (long Input, long Output)> models, (long Input, long Output) others)
    {
        var rows = models.Where(m => m.Value.Input != 0 || m.Value.Output != 0)
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new QuotaTokenModel(m.Key, m.Value.Input, m.Value.Output))
            .ToList();
        if (others.Input != 0 || others.Output != 0)
        {
            rows.Add(new QuotaTokenModel(null, others.Input, others.Output));
        }

        return rows.Count > MaxModels || counts.Input > MaxSafe || counts.Output > MaxSafe ? null : new QuotaTokenUsage(counts, rows);
    }
}
