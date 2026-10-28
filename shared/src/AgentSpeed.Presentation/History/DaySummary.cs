using AgentSpeed.Core;

namespace AgentSpeed.Presentation.History;

/// <summary>One source's output and cost on one day, for stacking the chart.</summary>
/// <param name="Cost">The source's cost as counted: an excluded subscription source adds nothing (METRICS 9.4).</param>
public readonly record struct SourceOutput(SourceKind Source, long Output, long Requests = 0, HistoryCost Cost = default);

/// <summary>
/// One calendar day of usage for the selected source, or summed over every source (METRICS 8). Speeds are always
/// Σ measured output ÷ Σ measured time, never an average of averages.
/// </summary>
/// <param name="Peak">Highest stored live peak of the day; only days observed live have one.</param>
/// <param name="Models">Every model of the day, most output first.</param>
/// <param name="Sources">Output and cost per source (non-zero output or requests only), in source order, for stacking.</param>
public sealed record DaySummary(
    DateOnly Day,
    long Requests,
    long Input,
    long CachedInput,
    long CacheWriteInput,
    long Output,
    long Reasoning,
    long MeasuredOutput,
    long MeasuredMs,
    long Unfinalized,
    int Agents,
    double? Peak,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyList<SourceOutput> Sources)
{
    public IReadOnlyList<ExecutionModeUsage> Modes { get; init; } = [];

    /// <summary>The day's cost over the counted sources (DESIGN 9: subscriptions left out unless included).</summary>
    public HistoryCost Cost { get; init; }

    /// <summary>Every source's cost, for the CSV (subscription exclusion does not apply there); null without cost data.</summary>
    public CostTally? TotalCost { get; init; }

    /// <summary>The price table version bound to the day (METRICS 9.7); null without cost data.</summary>
    public int? PriceVersion { get; init; }

    /// <summary>Per model (by name) over the counted sources.</summary>
    public IReadOnlyDictionary<string, ModelCost> ModelCosts { get; init; } = EmptyModelCosts;

    /// <summary>The day's observed traffic (METRICS 10.9); null before measuring began.</summary>
    public DayTraffic? Traffic { get; init; }

    private static IReadOnlyDictionary<string, ModelCost> EmptyModelCosts { get; } = new Dictionary<string, ModelCost>(StringComparer.Ordinal);

    /// <summary>The day has token usage (as a stored record with usage); a day listed only for its traffic has none.</summary>
    public bool HasUsage => Requests > 0 || Output > 0 || Input > 0;

    /// <summary>A day with agent traffic but no token usage (DESIGN 10): listed, every token figure 0.</summary>
    public static DaySummary TrafficOnly(DateOnly day) => new(day, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, [], []);

    /// <summary>Output tok/s over the day's measured responses.</summary>
    public double? AverageSpeed => MeasuredMs > 0 ? MeasuredOutput / (MeasuredMs / 1000.0) : null;

    /// <summary>Share of input served from the cache (cachedInput ÷ input).</summary>
    public double? CacheHitRatio => Input > 0 ? Math.Clamp((double)CachedInput / Input, 0, 1) : null;

    public long OutputOf(SourceKind source)
    {
        foreach (var entry in Sources)
        {
            if (entry.Source == source)
            {
                return entry.Output;
            }
        }

        return 0;
    }

    /// <summary>The counted, known cost of <paramref name="source"/> (0 when excluded or unpriced).</summary>
    public long CostMicroUsdOf(SourceKind source)
    {
        foreach (var entry in Sources)
        {
            if (entry.Source == source)
            {
                return entry.Cost.MicroUsd;
            }
        }

        return 0;
    }

    /// <summary>The day's value of a chart metric: output tokens, or known micro-USD.</summary>
    public double Value(HistoryChartMetric metric) => metric == HistoryChartMetric.Cost ? Cost.MicroUsd : Output;

    /// <summary>A model's cost as counted; null for a model the day does not have.</summary>
    public ModelCost? CostOf(string model) => ModelCosts.TryGetValue(model, out var cost) ? cost : null;
}

/// <summary>Totals of a range of <see cref="DaySummary"/> rows.</summary>
public sealed record HistoryTotals(
    long Requests,
    long Input,
    long CachedInput,
    long Output,
    long MeasuredOutput,
    long MeasuredMs,
    int ActiveDays,
    DaySummary? BusiestDay)
{
    public static HistoryTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>Σ of the days' costs (METRICS 9.8: a range total is the sum of its days).</summary>
    public HistoryCost Cost { get; init; }

    /// <summary>Days that have cost data (<c>history.summary.costPerDay</c> divides by them).</summary>
    public int CostDays { get; init; }

    /// <summary>Σ measured output ÷ Σ measured time over the whole range.</summary>
    public double? AverageSpeed => MeasuredMs > 0 ? MeasuredOutput / (MeasuredMs / 1000.0) : null;

    public bool HasData => ActiveDays > 0;
}
