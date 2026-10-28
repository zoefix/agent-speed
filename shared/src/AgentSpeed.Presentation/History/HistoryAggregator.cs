using AgentSpeed.Core;

namespace AgentSpeed.Presentation.History;

/// <summary>Turns stored per-source days (<see cref="DailyUsage"/>) into what the Usage History window shows.</summary>
public static class HistoryAggregator
{
    /// <summary>
    /// Days with data inside <paramref name="range"/> for one source (or every source when null), oldest first.
    /// Records with an unreadable day or without any usage are ignored. The costs of <paramref name="excluded"/> sources add
    /// nothing (METRICS 9.4); their tokens count as usual.
    /// </summary>
    public static IReadOnlyList<DaySummary> Daily(IEnumerable<DailyUsage> usage, SourceKind? source, DayRange? range = null,
        SourceSet excluded = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var groups = new SortedDictionary<DateOnly, List<DailyUsage>>();
        foreach (var record in usage)
        {
            if (record is null || (source is { } only && record.Source != only) || !HasUsage(record)
                || !HistoryRanges.TryParseDay(record.Day, out var day) || (range is { } r && !r.Contains(day)))
            {
                continue;
            }

            if (!groups.TryGetValue(day, out var list))
            {
                list = [];
                groups[day] = list;
            }

            list.Add(record);
        }

        var result = new List<DaySummary>(groups.Count);
        foreach (var (day, records) in groups)
        {
            result.Add(Summarize(day, records, excluded));
        }

        return result;
    }

    public static HistoryTotals Totals(IEnumerable<DaySummary> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        long requests = 0, input = 0, cached = 0, output = 0, measuredOutput = 0, measuredMs = 0;
        var active = 0;
        var costDays = 0;
        var cost = HistoryCost.None;
        DaySummary? busiest = null;
        foreach (var day in days)
        {
            cost = cost.Add(day.Cost);
            if (day.Cost.Tally is not null)
            {
                costDays++;
            }

            requests += day.Requests;
            input += day.Input;
            cached += day.CachedInput;
            output += day.Output;
            measuredOutput += day.MeasuredOutput;
            measuredMs += day.MeasuredMs;
            // A day listed only for its traffic is not an active day (DESIGN 10).
            if (!day.HasUsage)
            {
                continue;
            }

            active++;
            if (busiest is null || day.Output > busiest.Output || (day.Output == busiest.Output && day.Day > busiest.Day))
            {
                busiest = day;
            }
        }

        return active == 0
            ? HistoryTotals.Empty
            : new HistoryTotals(requests, input, cached, output, measuredOutput, measuredMs, active, busiest) { Cost = cost, CostDays = costDays };
    }

    /// <summary>Sources with at least one day of usage, in <see cref="SourceKind"/> order.</summary>
    public static IReadOnlyList<SourceKind> SourcesWithHistory(IEnumerable<DailyUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var found = new HashSet<SourceKind>();
        foreach (var record in usage)
        {
            if (record is not null && HasUsage(record))
            {
                found.Add(record.Source);
            }
        }

        return Enum.GetValues<SourceKind>().Where(found.Contains).ToArray();
    }

    /// <summary>First and last day with usage, or null when there is none.</summary>
    public static DayRange? Bounds(IEnumerable<DailyUsage> usage, SourceKind? source = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        DateOnly? first = null, last = null;
        foreach (var record in usage)
        {
            if (record is null || (source is { } only && record.Source != only) || !HasUsage(record)
                || !HistoryRanges.TryParseDay(record.Day, out var day))
            {
                continue;
            }

            if (first is null || day < first)
            {
                first = day;
            }

            if (last is null || day > last)
            {
                last = day;
            }
        }

        return first is { } f && last is { } l ? new DayRange(f, l) : null;
    }

    /// <summary>
    /// One value per day of <paramref name="range"/>: <paramref name="value"/> for days with data,
    /// <paramref name="missing"/> for the others.
    /// </summary>
    public static double?[] Series(IReadOnlyList<DaySummary> days, DayRange range, Func<DaySummary, double?> value, double? missing = 0)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(value);
        var series = new double?[range.DayCount];
        Array.Fill(series, missing);
        foreach (var day in days)
        {
            if (range.Contains(day.Day))
            {
                series[range.IndexOf(day.Day)] = value(day);
            }
        }

        return series;
    }

    /// <summary>Relative change from <paramref name="previous"/>; null when there is nothing to compare with.</summary>
    public static double? Change(double? current, double? previous)
    {
        if (current is not { } now || previous is not { } before || !double.IsFinite(now) || !double.IsFinite(before) || before <= 0)
        {
            return null;
        }

        return (now - before) / before;
    }

    /// <summary>One model's figures added up across sources or records; negative counts are ignored.</summary>
    internal static ModelUsage Add(ModelUsage total, ModelUsage part) => total with
    {
        Requests = total.Requests + Math.Max(0, part.Requests),
        Output = total.Output + Math.Max(0, part.Output),
        MeasuredOutput = total.MeasuredOutput + Math.Max(0, part.MeasuredOutput),
        MeasuredMs = total.MeasuredMs + Math.Max(0, part.MeasuredMs),
    };

    /// <summary>
    /// Models with responses of unknown price in <paramref name="range"/> over the counted sources, most unpriced requests
    /// first (<c>history.unpricedNotice</c>). Contributions stored before costs existed are not unknown prices and are left
    /// out; unpriced requests no named model accounts for are listed under an empty name.
    /// </summary>
    public static IReadOnlyList<UnpricedModel> UnpricedModels(IEnumerable<DailyUsage> usage, SourceKind? source, DayRange range,
        SourceSet excluded = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var counts = new Dictionary<(string Model, SourceKind Source), long>();
        foreach (var record in usage)
        {
            if (record is null || (source is { } only && record.Source != only) || excluded.Contains(record.Source)
                || record.Cost is not { } cost || !HistoryRanges.TryParseDay(record.Day, out var day) || !range.Contains(day))
            {
                continue;
            }

            long named = 0;
            foreach (var model in record.Models ?? [])
            {
                if (model.Cost is not { UnpricedRequests: > 0 } modelCost)
                {
                    continue;
                }

                var key = ((model.Model ?? string.Empty).Trim(), record.Source);
                counts[key] = counts.GetValueOrDefault(key) + modelCost.UnpricedRequests;
                named += modelCost.UnpricedRequests;
            }

            if (cost.UnpricedRequests - named is > 0 and var rest)
            {
                var key = (string.Empty, record.Source);
                counts[key] = counts.GetValueOrDefault(key) + rest;
            }
        }

        return counts
            .Select(c => new UnpricedModel(c.Key.Model, c.Key.Source, c.Value))
            .OrderByDescending(m => m.Requests)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ThenBy(m => m.Source)
            .ToArray();
    }

    /// <summary>The cost of one stored record as counted: nothing (but the mark) for an excluded source.</summary>
    public static HistoryCost CostOf(DailyUsage record, SourceSet excluded = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return excluded.Contains(record.Source)
            ? new HistoryCost(null, 0, SourceSet.Of(record.Source))
            : new HistoryCost(record.Cost, Math.Max(0, record.Totals.Requests));
    }

    internal static bool HasUsage(DailyUsage record) =>
        record.Totals.Requests > 0 || record.Totals.Output > 0 || record.Totals.Input > 0;

    private static DaySummary Summarize(DateOnly day, List<DailyUsage> records, SourceSet excluded)
    {
        long requests = 0, input = 0, cached = 0, cacheWrite = 0, output = 0, reasoning = 0, measuredOutput = 0, measuredMs = 0, unfinalized = 0;
        var agents = 0;
        double? peak = null;
        var models = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        var sources = new Dictionary<SourceKind, SourceOutput>();
        var dayCost = HistoryCost.None;
        CostTally? totalCost = null;
        long totalRequests = 0;
        int? priceVersion = null;
        var modelCosts = new Dictionary<string, ModelCost>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var counted = CostOf(record, excluded);
            var recordRequests = Math.Max(0, record.Totals.Requests);
            dayCost = dayCost.Add(counted);
            totalCost = HistoryCostMath.Merged(totalCost, totalRequests, record.Cost, recordRequests);
            totalRequests += recordRequests;
            if (record.PriceVersion is { } version)
            {
                priceVersion = Math.Max(priceVersion ?? version, version);
            }

            var previous = sources.GetValueOrDefault(record.Source, new SourceOutput(record.Source, 0));
            sources[record.Source] = previous with
            {
                Output = previous.Output + Math.Max(0, record.Totals.Output),
                Requests = previous.Requests + recordRequests,
                Cost = previous.Cost.Add(counted),
            };
            if (!excluded.Contains(record.Source))
            {
                foreach (var model in record.Models ?? [])
                {
                    if (string.IsNullOrWhiteSpace(model.Model))
                    {
                        continue;
                    }

                    var current = modelCosts.GetValueOrDefault(model.Model);
                    var modelRequests = Math.Max(0, model.Requests);
                    modelCosts[model.Model] = new ModelCost(
                        HistoryCostMath.Merged(current.Cost, current.Requests, model.Cost, modelRequests), current.Requests + modelRequests);
                }
            }

            var totals = record.Totals;
            requests += Math.Max(0, totals.Requests);
            input += Math.Max(0, totals.Input);
            cached += Math.Max(0, totals.CachedInput);
            cacheWrite += Math.Max(0, totals.CacheWriteInput);
            output += Math.Max(0, totals.Output);
            reasoning += Math.Max(0, totals.Reasoning);
            measuredOutput += Math.Max(0, record.MeasuredOutput);
            measuredMs += Math.Max(0, record.MeasuredMs);
            unfinalized += Math.Max(0, record.Unfinalized);
            agents += Math.Max(0, record.Agents);
            if (record.Peak is { } p && double.IsFinite(p) && p > 0 && (peak is null || p > peak))
            {
                peak = p;
            }

            foreach (var model in record.Models ?? [])
            {
                if (string.IsNullOrWhiteSpace(model.Model))
                {
                    continue;
                }

                models[model.Model] = Add(models.GetValueOrDefault(model.Model) ?? new ModelUsage(model.Model, 0, 0), model);
            }
        }

        var modelList = models.Values
            .OrderByDescending(m => m.Output)
            .ThenByDescending(m => m.Requests)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToArray();
        var sourceList = sources.Values
            .Where(s => s.Output > 0 || s.Requests > 0)
            .OrderBy(s => s.Source)
            .ToArray();
        // A model only subscription sources used has no counted requests (its cost reads cost.subscription.tag).
        foreach (var model in modelList)
        {
            modelCosts.TryAdd(model.Model, new ModelCost(null, 0));
        }

        return new DaySummary(day, requests, input, cached, cacheWrite, output, reasoning, measuredOutput, measuredMs, unfinalized,
            agents, peak, modelList, sourceList)
        {
            Cost = dayCost,
            TotalCost = totalCost,
            PriceVersion = totalCost is null ? null : priceVersion,
            ModelCosts = modelCosts,
            Modes = ExecutionModeUsage.Combine(records.SelectMany(r => r.Modes)),
        };
    }
}
