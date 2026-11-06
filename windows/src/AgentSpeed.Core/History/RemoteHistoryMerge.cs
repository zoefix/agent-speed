using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.History;

/// <summary>Joins the remote hosts' rows to the local history (REMOTE 8.3). Without remote rows, the local values pass unchanged.</summary>
internal static class RemoteHistoryMerge
{
    /// <summary>The evidence values a stored contribution keeps for its modes.</summary>
    private static readonly HashSet<string> StoredEvidence = new(["served", "recorded", "selected"], StringComparer.Ordinal);

    /// <summary>
    /// The aggregate as the local analyzer holds one stored contribution: the same totals, models, costs (null when either
    /// part is null), mode usage and unpriced facts (after METRICS 9.10 <i>Reading</i>).
    /// </summary>
    public static DayAggregate DayAggregate(RemoteDayAggregate remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var aggregate = new DayAggregate
        {
            Requests = remote.Requests,
            Input = remote.Input,
            CachedInput = remote.CachedInput,
            CacheWriteInput = remote.CacheWriteInput,
            Output = remote.Output,
            Reasoning = remote.Reasoning,
            MeasuredOutput = remote.MeasuredOutput,
            MeasuredMs = remote.MeasuredMs,
            Unfinalized = remote.Unfinalized,
            Agents = (int)Math.Min(remote.Agents, int.MaxValue),
            Cost = Cost(remote.CostMicroUsd, remote.UnpricedRequests),
        };
        foreach (var (model, tally) in remote.Models)
        {
            aggregate.Models[model] = new ModelTotals(tally.Requests, tally.Output, tally.MeasuredOutput, tally.MeasuredMs,
                Cost(tally.CostMicroUsd, tally.UnpricedRequests));
        }

        foreach (var mode in remote.Modes)
        {
            if (!StoredEvidence.Contains(mode.Evidence))
            {
                continue;
            }

            var key = new ExecutionModeKey(mode.Model, mode.Kind, mode.Value, mode.Evidence, mode.Provider ?? string.Empty);
            aggregate.Modes[key] = aggregate.Modes.GetValueOrDefault(key)
                + new ExecutionModeTotals(mode.Requests, mode.Output, mode.MeasuredOutput, mode.MeasuredMs);
        }

        aggregate.SetUnpriced(Reading(remote, aggregate));
        return aggregate;
    }

    /// <summary>
    /// One host's row of the daily history. It has no price version: the relay priced it with the table it was given, whose
    /// version the line does not carry.
    /// </summary>
    public static DailyUsage Usage(RemoteDayAggregate remote, string day, SourceKind source, string host, double? peak = null) =>
        DayAggregate(remote).ToUsage(new DayKey(day, source), peak, priceVersion: null) with { Host = host };

    /// <summary>Local and remote days sorted by day, source, then host (this computer first, then by id).</summary>
    public static IReadOnlyList<DailyUsage> Days(IReadOnlyList<DailyUsage> local, IReadOnlyList<DailyUsage> remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Count == 0)
        {
            return local;
        }

        return [.. local.Concat(remote)
            .OrderBy(d => d.Day, StringComparer.Ordinal)
            .ThenBy(d => SourceNames.Of(d.Source), StringComparer.Ordinal)
            .ThenBy(d => d.IsLocal ? 0 : 1)
            .ThenBy(d => d.Host, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every host's records pooled per day, source and model (METRICS 6.11: the baseline compares a model with itself across
    /// the user's machines); local records are added first. The given sums are never changed.
    /// </summary>
    public static IReadOnlyList<PerfRecord> Perf(IReadOnlyList<PerfRecord> local, IReadOnlyList<PerfRecord> remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Count == 0)
        {
            return local;
        }

        var sums = new Dictionary<PerfRecordKey, PerfSums>();
        foreach (var record in local.Concat(remote))
        {
            var key = new PerfRecordKey(record.Day, record.Source, record.Model);
            if (!sums.TryGetValue(key, out var total))
            {
                sums[key] = total = new PerfSums();
            }

            total.Add(record.Sums);
        }

        var pooled = sums.Select(s => new PerfRecord(s.Key.Day, s.Key.Source, s.Key.Model, s.Value)).ToList();
        pooled.Sort(PerfRecord.Compare);
        return pooled.AsReadOnly();
    }

    private static CostTally? Cost(long? micro, long? unpriced) =>
        micro is { } m && unpriced is { } u ? new CostTally(m, u) : null;

    /// <summary>
    /// METRICS 9.10 <i>Reading</i> of the aggregate: no cost data counts every request; the line's facts stand when they add up to
    /// its unpriced requests, else they are derived from the per-model counts.
    /// </summary>
    private static UnpricedReading Reading(RemoteDayAggregate remote, DayAggregate aggregate)
    {
        if (aggregate.Cost is not { } cost)
        {
            return new UnpricedReading(aggregate.Requests, 0, []);
        }

        if (cost.UnpricedRequests <= 0)
        {
            return UnpricedReading.None;
        }

        var facts = new Dictionary<UnpricedFact, long>();
        long sum = 0;
        foreach (var fact in remote.UnpricedFacts)
        {
            if (fact.Count > 0)
            {
                var provider = string.IsNullOrEmpty(fact.Provider) ? null : fact.Provider;
                UnpricedFacts.Add(facts, new UnpricedFact(fact.Model, provider, fact.Fast, fact.NoDate), fact.Count);
                sum += fact.Count;
            }
        }

        return sum == cost.UnpricedRequests
            ? new UnpricedReading(0, 0, UnpricedFacts.Sorted(facts))
            : UnpricedReading.Read(0, aggregate.Requests, cost.UnpricedRequests,
                aggregate.Models.Select(m => KeyValuePair.Create(m.Key, m.Value.Cost?.UnpricedRequests)), []);
    }
}
