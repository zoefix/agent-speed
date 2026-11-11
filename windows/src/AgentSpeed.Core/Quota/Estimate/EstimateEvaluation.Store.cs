using System.Globalization;

namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateEvaluation
{
    /// <summary>
    /// The store of QUOTAS 10.4: the last 8 days of observations thinned per instance, the closed instances' intervals (64
    /// per class, newest kept) and the key timelines.
    /// </summary>
    public EstimateStoreData Store()
    {
        var groups = new Dictionary<string, List<QuotaObservation>>(StringComparer.Ordinal);
        foreach (var o in Model.Observations)
        {
            if (o.T < NowMs - EstimateLimits.KeepMs)
            {
                continue;
            }

            var key = GroupText(o);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups.Add(key, list);
            }

            list.Add(o);
        }

        var observations = new List<QuotaObservation>();
        foreach (var key in groups.Keys.Order(StringComparer.Ordinal))
        {
            observations.AddRange(Thin(groups[key]));
        }

        var closed = new Dictionary<CapClass, List<StoredInstance>>();
        void Add(StoredInstance s)
        {
            if (!closed.TryGetValue(s.Class, out var list))
            {
                list = [];
                closed.Add(s.Class, list);
            }

            list.Add(s);
        }

        foreach (var x in Instances)
        {
            if (x.Closed && x.Bounds is { } b)
            {
                Add(new StoredInstance(x.Class, x.Account, x.Instance.R, b.Lo, double.IsFinite(b.Hi) ? b.Hi : null, b.Dp, b.T));
            }
        }

        var have = closed.Values.SelectMany(v => v).Select(s => (s.Class, s.Account, s.R)).ToHashSet();
        foreach (var s in Model.StoredInstances)
        {
            if (!have.Contains((s.Class, s.Account, s.R)))
            {
                Add(s);
            }
        }

        var instances = new List<StoredInstance>();
        foreach (var cls in closed.Keys.OrderBy(c => c.SortText, StringComparer.Ordinal))
        {
            instances.AddRange(closed[cls].OrderBy(s => s.TLast).TakeLast(EstimateLimits.InstancesPerClass));
        }

        var keys = new List<StoredKey>();
        foreach (var (source, entries) in Model.StoredTimelines.OrderBy(t => EstimateNames.Source(t.Source), StringComparer.Ordinal))
        {
            foreach (var e in entries)
            {
                keys.Add(new StoredKey(source, e.At, e.Key, e.Plan, e.Tier, source == SourceKind.ClaudeCode ? Model.OrgKeyOf(e) : null));
            }
        }

        return new EstimateStoreData(EstimateLimits.StoreVersion, observations, instances, keys);
    }

    /// <summary>
    /// Runs of equal values keep their first and last observation; above 256, each run keeps its last; still above, 256
    /// runs evenly spaced, the first and last included.
    /// </summary>
    public static IReadOnlyList<QuotaObservation> Thin(IEnumerable<QuotaObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var runs = new List<List<QuotaObservation>>();
        foreach (var o in observations.OrderBy(o => o.T).ThenBy(o => o.TLo))
        {
            if (runs.Count > 0 && runs[^1][^1].P == o.P)
            {
                runs[^1].Add(o);
            }
            else
            {
                runs.Add([o]);
            }
        }

        var kept = runs.SelectMany(r => r.Count > 1 ? new[] { r[0], r[^1] } : [r[0]]).ToList();
        if (kept.Count <= EstimateLimits.ObservationsPerInstance)
        {
            return kept;
        }

        var lasts = runs.Select(r => r[^1]).ToList();
        if (lasts.Count <= EstimateLimits.ObservationsPerInstance)
        {
            return lasts;
        }

        const int n = EstimateLimits.ObservationsPerInstance;
        return [.. Enumerable.Range(0, n).Select(i => lasts[(int)Math.Round(i * (lasts.Count - 1) / (double)(n - 1), MidpointRounding.ToEven)])];
    }

    /// <summary>The reference's group key as Python prints the tuple (account, kind, name, minutes, R bucket, plan, tier).</summary>
    private static string GroupText(QuotaObservation o)
    {
        static string Text(string? s) => s is null ? "None" : "'" + s + "'";
        var bucket = (long)Math.Round(o.R / (double)EstimateLimits.InstanceJoinMs, MidpointRounding.ToEven);
        return "(" + string.Join(", ",
            Text(o.Account), Text(EstimateNames.Kind(o.Kind)), Text(o.Name), o.Minutes.ToString(CultureInfo.InvariantCulture),
            bucket.ToString(CultureInfo.InvariantCulture), Text(o.Plan), Text(o.Tier)) + ")";
    }
}
