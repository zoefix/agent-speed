using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Presentation.History;

public enum BreakdownKind
{
    /// <summary>One bar per source (all sources selected).</summary>
    Sources,

    /// <summary>One bar per model of the selected source.</summary>
    Models,
}

/// <summary>One bar of a day's breakdown.</summary>
/// <param name="Model">The model name; null on a source bar and on <paramref name="OtherModels"/>.</param>
/// <param name="Share">Output (or cost, for the cost metric) as a share (0..1) of the day's; the bars' shares add up to 1.</param>
/// <param name="Fraction">The metric relative to the largest bar (0..1), the bar's length.</param>
/// <param name="Speed">Σ measured output ÷ Σ measured time (per source, or per model, METRICS 8); null when nothing was measured.</param>
/// <param name="OtherModels">Output no named model accounts for (responses without a model, days stored before per-model time).</param>
public sealed record BreakdownRow(
    SourceKind Source,
    string? Model,
    long Output,
    long Requests,
    double Share,
    double Fraction,
    double? Speed,
    bool OtherModels = false)
{
    /// <summary>The bar's cost as counted (an excluded subscription source adds nothing, METRICS 9.4).</summary>
    public HistoryCost Cost { get; init; }

    /// <summary>The source's traffic of the day as displayed (DESIGN 10); null on model bars and before measuring began.</summary>
    public TrafficBytes? Traffic { get; init; }

    /// <summary>Stable identity of the bar while its values change.</summary>
    public string Key => OtherModels ? "\0other" : Model ?? Source.ToString();
}

/// <summary>
/// What a single-day range shows instead of the daily chart (DESIGN 4b, toolbar): the day's output by source, or by
/// model for one source, sorted and sized by the chosen metric (DESIGN 9).
/// </summary>
/// <param name="Output">Σ output of the bars.</param>
public sealed record DayBreakdown(BreakdownKind Kind, DateOnly Day, long Output, IReadOnlyList<BreakdownRow> Rows)
{
    /// <summary>The measure that sorts and sizes the bars.</summary>
    public HistoryChartMetric Metric { get; init; }

    public string TitleKey => Kind == BreakdownKind.Sources ? "history.chart.bySource" : "history.chart.byModel";

    /// <summary>The breakdown a range shows: by source with every source, by model with one; null for several days.</summary>
    /// <param name="excluded">Sources whose costs add nothing (subscriptions); a source selected directly is never excluded.</param>
    /// <param name="traffic">The day's traffic (by source): with the traffic metric the source bars are sized by ↓ + ↑.</param>
    public static DayBreakdown? For(IEnumerable<DailyUsage> usage, DayRange range, SourceKind? source,
        HistoryChartMetric metric = HistoryChartMetric.Output, SourceSet excluded = default, DayTraffic? traffic = null)
    {
        if (!range.IsSingleDay)
        {
            return null;
        }

        if (source is not { } only)
        {
            return BySource(usage, range.Start, metric, excluded, traffic);
        }

        // A day of one source with traffic but no token usage has no models: it shows the source's one bar (DESIGN 10).
        return metric == HistoryChartMetric.Traffic && traffic?.BySource.ContainsKey(only) == true && !OfDay(usage, range.Start, only).Any()
            ? BySource([], range.Start, metric, excluded, traffic)
            : ByModel(usage, range.Start, only, metric);
    }

    /// <summary>Every source with usage on <paramref name="day"/> (and, with traffic, every source with traffic).</summary>
    public static DayBreakdown BySource(IEnumerable<DailyUsage> usage, DateOnly day, HistoryChartMetric metric = HistoryChartMetric.Output,
        SourceSet excluded = default, DayTraffic? traffic = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var tallies = new Dictionary<SourceKind, Tally>();
        foreach (var record in OfDay(usage, day, source: null))
        {
            tallies[record.Source] = tallies.GetValueOrDefault(record.Source).Add(new Tally(record.Totals.Output, record.Totals.Requests,
                record.MeasuredOutput, record.MeasuredMs, HistoryAggregator.CostOf(record, excluded)));
        }

        foreach (var (kind, bytes) in traffic?.BySource ?? new Dictionary<SourceKind, TrafficBytes>())
        {
            tallies[kind] = tallies.GetValueOrDefault(kind) with { Traffic = bytes };
        }

        var bars = tallies.Select(t => new Bar(t.Key, null, false, t.Value)).ToList();
        return Build(BreakdownKind.Sources, day, bars, metric);
    }

    /// <summary>
    /// The models of one source on <paramref name="day"/>, each with its own measured output, time and cost; what no named
    /// model accounts for is gathered in one last bar. One source is selected directly, so nothing is excluded.
    /// </summary>
    public static DayBreakdown ByModel(IEnumerable<DailyUsage> usage, DateOnly day, SourceKind source,
        HistoryChartMetric metric = HistoryChartMetric.Output)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var models = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var other = new Tally();
        var hasOther = false;
        foreach (var record in OfDay(usage, day, source))
        {
            var named = new Tally();
            foreach (var model in record.Models ?? [])
            {
                var name = model.Model?.Trim();
                if (string.IsNullOrEmpty(name) || (model.Output <= 0 && model.Requests <= 0))
                {
                    continue;
                }

                // A contribution stored before costs existed has no model costs: they stay unavailable.
                var part = new Tally(model.Output, model.Requests, model.MeasuredOutput, model.MeasuredMs,
                    new HistoryCost(model.Cost, Math.Max(0, model.Requests)));
                models[name] = models.GetValueOrDefault(name).Add(part);
                named = named.Add(part);
            }

            var restRequests = Math.Max(0, record.Totals.Requests - named.Requests);
            CostTally? restCost = record.Cost is { } recorded
                ? new CostTally(
                    Math.Max(0, recorded.CostMicroUsd - named.Cost.MicroUsd),
                    Math.Max(0, recorded.UnpricedRequests - named.Cost.Unpriced))
                : null;
            var rest = new Tally(
                Math.Max(0, record.Totals.Output - named.Output),
                restRequests,
                Math.Max(0, record.MeasuredOutput - named.MeasuredOutput),
                Math.Max(0, record.MeasuredMs - named.MeasuredMs),
                new HistoryCost(restCost, restRequests));
            if (rest.Output > 0 || (metric == HistoryChartMetric.Cost && rest.Cost.MicroUsd > 0))
            {
                other = other.Add(rest);
                hasOther = true;
            }
        }

        var bars = models.Select(m => new Bar(source, m.Key, false, m.Value)).ToList();
        if (hasOther)
        {
            bars.Add(new Bar(source, null, true, other));
        }

        return Build(BreakdownKind.Models, day, bars, metric);
    }

    private static IEnumerable<DailyUsage> OfDay(IEnumerable<DailyUsage> usage, DateOnly day, SourceKind? source)
    {
        var text = HistoryRanges.FormatDay(day);
        return usage.Where(r => r is not null && (source is null || r.Source == source) && HistoryAggregator.HasUsage(r)
            && string.Equals(r.Day, text, StringComparison.Ordinal));
    }

    private static DayBreakdown Build(BreakdownKind kind, DateOnly day, List<Bar> bars, HistoryChartMetric metric)
    {
        // Models have no traffic of their own: a by-model breakdown keeps sizing by output.
        var byTraffic = metric == HistoryChartMetric.Traffic && kind == BreakdownKind.Sources;
        bars.Sort((a, b) => Precedes(a, b, metric, byTraffic));
        double Value(Bar bar) => byTraffic ? bar.Tally.Traffic?.Sum ?? 0
            : metric == HistoryChartMetric.Cost ? bar.Tally.Cost.MicroUsd : bar.Tally.Output;
        var total = bars.Sum(Value);
        var largest = bars.Count > 0 ? bars.Max(Value) : 0;
        var rows = bars
            .Select(b => new BreakdownRow(b.Source, b.Model, b.Tally.Output, b.Tally.Requests,
                total > 0 ? Value(b) / total : 0,
                largest > 0 ? Value(b) / largest : 0,
                b.Tally.Speed,
                b.Other) { Cost = b.Tally.Cost, Traffic = b.Tally.Traffic })
            .ToArray();
        return new DayBreakdown(kind, day, bars.Sum(b => b.Tally.Output), rows) { Metric = metric };
    }

    /// <summary>
    /// Most of the metric first (cost, then output for the cost metric; traffic, sources with traffic first, for the traffic
    /// metric), then most requests, then source order or model name; other models always last.
    /// </summary>
    private static int Precedes(Bar a, Bar b, HistoryChartMetric metric, bool byTraffic)
    {
        if (a.Other != b.Other)
        {
            return a.Other ? 1 : -1;
        }

        if (byTraffic && (a.Tally.Traffic is null) != (b.Tally.Traffic is null))
        {
            return a.Tally.Traffic is null ? 1 : -1;
        }

        var order = byTraffic ? (b.Tally.Traffic?.Sum ?? 0).CompareTo(a.Tally.Traffic?.Sum ?? 0)
            : metric == HistoryChartMetric.Cost ? b.Tally.Cost.MicroUsd.CompareTo(a.Tally.Cost.MicroUsd) : 0;
        if (order == 0)
        {
            order = b.Tally.Output.CompareTo(a.Tally.Output);
        }

        if (order == 0)
        {
            order = b.Tally.Requests.CompareTo(a.Tally.Requests);
        }

        if (order == 0)
        {
            order = a.Model is not null && b.Model is not null
                ? StringComparer.OrdinalIgnoreCase.Compare(a.Model, b.Model)
                : a.Source.CompareTo(b.Source);
        }

        return order;
    }

    private readonly record struct Bar(SourceKind Source, string? Model, bool Other, Tally Tally);

    /// <summary>Counts of one bar; negative inputs count as zero.</summary>
    private readonly record struct Tally(long Output, long Requests, long MeasuredOutput, long MeasuredMs, HistoryCost Cost = default)
    {
        public TrafficBytes? Traffic { get; init; }

        public double? Speed => MeasuredMs > 0 && MeasuredOutput > 0 ? MeasuredOutput / (MeasuredMs / 1000.0) : null;

        public Tally Add(Tally part) => new(
            Output + Math.Max(0, part.Output),
            Requests + Math.Max(0, part.Requests),
            MeasuredOutput + Math.Max(0, part.MeasuredOutput),
            MeasuredMs + Math.Max(0, part.MeasuredMs),
            Cost.Add(part.Cost))
        {
            Traffic = Traffic,
        };
    }
}
