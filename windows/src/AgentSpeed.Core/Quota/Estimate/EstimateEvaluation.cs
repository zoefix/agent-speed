namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>A window instance with its cap class and interval (QUOTAS 10.5).</summary>
public sealed record EvaluatedInstance(
    string Account, string Window, string? Plan, string? Tier, CapClass Class, WindowInstance Instance, InstanceInterval? Bounds,
    string? Name, bool Closed);

/// <summary>
/// The estimate at one moment (QUOTAS 10.5–10.8): every instance's interval, every cap class's estimate and, per account
/// and window, the newest instance from which report entries and the shown values are computed. Immutable.
/// </summary>
public sealed partial class EstimateEvaluation
{
    private sealed record Current(EvaluatedInstance Item, QuotaObservation Last, IReadOnlyList<SourceKind> Sources, int[] NameRank);

    private readonly Dictionary<(string Account, string Window), Current> _current = [];

    private EstimateEvaluation(EstimateModel model, long nowMs)
    {
        Model = model;
        NowMs = nowMs;
    }

    public EstimateModel Model { get; }

    public long NowMs { get; }

    public IReadOnlyList<EvaluatedInstance> Instances { get; private set; } = [];

    public IReadOnlyDictionary<CapClass, CapEstimate> Estimates { get; private set; } = new Dictionary<CapClass, CapEstimate>();

    public static EstimateEvaluation Of(EstimateModel model, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(model);
        var evaluation = new EstimateEvaluation(model, nowMs);
        evaluation.Evaluate();
        return evaluation;
    }

    private void Evaluate()
    {
        var groups = new Dictionary<(string Account, string Window, string? Plan, string? Tier), List<QuotaObservation>>();
        var order = new List<(string, string, string?, string?)>();
        foreach (var o in Model.Observations)
        {
            if (o.T < NowMs - EstimateLimits.KeepMs)
            {
                continue;
            }

            var key = (o.Account, o.WindowId, o.Plan, o.Tier);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups.Add(key, list);
                order.Add(key);
            }

            list.Add(o);
        }

        var instances = new List<EvaluatedInstance>();
        foreach (var key in order)
        {
            var (account, window, plan, tier) = key;
            var observations = groups[key];
            var first = observations[0];
            var name = first.Kind == QuotaWindowKind.ModelWeek ? first.Name : null;
            var vendorPlan = plan is null ? null : EstimateNames.Vendor(first.Source) + "/" + plan;
            var cls = CapClass.Of(account, vendorPlan, tier, window);
            foreach (var instance in WindowInstance.Group(observations))
            {
                var bounds = InstanceSegments.Bounds(InstanceSegments.Of(instance, Model.Ledger, account, name, NowMs), instance.R);
                instances.Add(new EvaluatedInstance(account, window, plan, tier, cls, instance, bounds, name, NowMs >= instance.R));
            }
        }

        Instances = instances;
        var estimates = new Dictionary<CapClass, CapEstimate>();
        foreach (var (cls, items) in ClassItems())
        {
            estimates[cls] = CapVote.Estimate(items, NowMs, CapVote.HalfLifeMs(cls.Minutes));
        }

        Estimates = estimates;
        FindCurrent();
    }

    private List<(CapClass Class, IReadOnlyList<InstanceInterval> Items)> ClassItems()
    {
        var items = new Dictionary<CapClass, List<InstanceInterval>>();
        var order = new List<CapClass>();
        var have = new HashSet<(CapClass, string, long)>();
        void Add(CapClass cls, InstanceInterval interval)
        {
            if (!items.TryGetValue(cls, out var list))
            {
                list = [];
                items.Add(cls, list);
                order.Add(cls);
            }

            list.Add(interval);
        }

        foreach (var x in Instances)
        {
            if (x.Bounds is { } b)
            {
                Add(x.Class, b);
                have.Add((x.Class, x.Account, x.Instance.R));
            }
        }

        foreach (var s in Model.StoredInstances)
        {
            if (!have.Contains((s.Class, s.Account, s.R)))
            {
                Add(s.Class, new InstanceInterval(s.R, s.Lo, s.Hi ?? double.PositiveInfinity, s.PMax, s.TLast));
            }
        }

        return [.. order.Select(c => (c, (IReadOnlyList<InstanceInterval>)items[c].OrderBy(x => x.T).TakeLast(EstimateLimits.InstancesPerClass).ToList()))];
    }

    private void FindCurrent()
    {
        var current = new Dictionary<(string, string), (EvaluatedInstance Item, QuotaObservation Last)>();
        var order = new List<(string, string)>();
        foreach (var x in Instances)
        {
            var key = (x.Account, x.Window);
            var last = x.Instance.Observations[^1];
            if (!current.TryGetValue(key, out var c))
            {
                current.Add(key, (x, last));
                order.Add(key);
            }
            else if (last.T > c.Last.T || (last.T == c.Last.T && last.TLo > c.Last.TLo))
            {
                current[key] = (x, last);
            }
        }

        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var o in Model.Observations.OrderBy(o => o.T))
        {
            if (!string.IsNullOrEmpty(o.Name))
            {
                if (!names.TryGetValue(o.Account, out var list))
                {
                    list = [];
                    names.Add(o.Account, list);
                }

                if (!list.Contains(o.Name, StringComparer.Ordinal))
                {
                    list.Add(o.Name);
                }
            }
        }

        foreach (var key in order)
        {
            var (item, last) = current[key];
            var sources = Model.SourcesSince(item.Account, item.Instance.S).Append(last.Source).Distinct().Order().ToArray();
            var rank = WindowRank(last, names.GetValueOrDefault(item.Account));
            _current[key] = new Current(item, last, sources, rank);
        }
    }

    private static int[] WindowRank(QuotaObservation last, List<string>? names)
    {
        var minutes = last.Minutes;
        var kind = EstimateNames.KindRank(last.Kind);
        return string.IsNullOrEmpty(last.Name) ? [0, 0, minutes, kind] : [1, names?.IndexOf(last.Name) ?? 0, minutes, kind];
    }
}
