namespace AgentSpeed.Core.Quota.Estimate;

public enum SegmentType
{
    Cumulative,
    Increment,
}

/// <summary>One interval of the cap from a run or a pair of runs (QUOTAS 10.5); <see cref="Hi"/> may be ∞.</summary>
public readonly record struct EstimateSegment(SegmentType Type, double Lo, double Hi, double Dp, long T);

/// <summary>An instance's interval <c>[L, H]</c> with its largest step and newest time (QUOTAS 10.5).</summary>
/// <param name="Offset">A cumulative lower bound lay above an increment's upper bound: the increments alone were kept.</param>
public sealed record InstanceInterval(long R, double Lo, double Hi, double Dp, long T, bool Offset = false);

/// <summary>The cumulative and increment segments of a window instance and its interval (QUOTAS 10.5).</summary>
public static class InstanceSegments
{
    private sealed class Run(QuotaObservation first, double q)
    {
        public double P { get; } = first.P;

        public QuotaObservation First { get; } = first;

        public QuotaObservation Last { get; set; } = first;

        public long TLoMax { get; set; } = first.TLo;

        public double Q { get; set; } = q;
    }

    /// <summary>The finest resolution per source among the instance's observations.</summary>
    public static Dictionary<SourceKind, double> Resolutions(IEnumerable<QuotaObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var q = new Dictionary<SourceKind, double>();
        foreach (var o in observations)
        {
            q[o.Source] = Math.Min(q.TryGetValue(o.Source, out var v) ? v : 1.0, QuotaObservation.Resolution(o.P));
        }

        return q;
    }

    /// <summary>The segments of one instance; spend after <paramref name="nowMs"/> does not exist yet.</summary>
    public static IReadOnlyList<EstimateSegment> Of(WindowInstance instance, SpendLedger ledger, string account, string? name, long? nowMs = null, long lag = EstimateLimits.LagMs)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(ledger);
        var s = instance.S;
        var sHi = instance.SHi;
        long Upto(long t) => nowMs is { } now ? Math.Min(t, now) : t;
        var qOf = Resolutions(instance.Observations);
        var runs = new List<Run>();
        foreach (var o in instance.Observations)
        {
            var q = qOf[o.Source];
            if (runs.Count > 0 && Math.Abs(runs[^1].P - o.P) < 1e-9)
            {
                var r = runs[^1];
                r.Last = o;
                r.TLoMax = Math.Max(r.TLoMax, o.TLo);
                r.Q = Math.Max(r.Q, q);
            }
            else
            {
                runs.Add(new Run(o, q));
            }
        }

        var segments = new List<EstimateSegment>();
        foreach (var r in runs)
        {
            var p = r.P;
            var q = r.Q;
            if (p <= 0)
            {
                continue;
            }

            var cLo = ledger.Inner(account, name, s, r.TLoMax - lag).Cost;
            var cHi = ledger.OuterHigh(account, name, sHi, Upto(r.First.T + lag));
            var lo = p >= 100 ? 0.0 : 100.0 * cLo / (p + q);
            var pTop = Math.Min(p, 100.0);
            var hi = pTop - q <= 0 ? double.PositiveInfinity : 100.0 * cHi / (pTop - q);
            segments.Add(new EstimateSegment(SegmentType.Cumulative, lo, hi, p, r.Last.T));
        }

        var anchors = new List<Run>();
        foreach (var r in runs)
        {
            if (r.P <= 0)
            {
                continue;
            }

            if (anchors.Count == 0 || r.P >= anchors[^1].P + Math.Max(3 * Math.Max(r.Q, anchors[^1].Q), EstimateLimits.IncrementStep) - 1e-9)
            {
                anchors.Add(r);
            }
        }

        var pairs = new List<(Run A, Run B)>();
        for (var i = 0; i < anchors.Count; i++)
        {
            for (var j = i + 1; j < anchors.Count; j++)
            {
                pairs.Add((anchors[i], anchors[j]));
            }
        }

        var chosen = pairs.OrderBy(ab => -(ab.B.P - ab.A.P)).ThenBy(ab => ab.A.P).Take(EstimateLimits.SegmentsPerInstance / 2)
            .OrderBy(ab => ab.A.P).ThenBy(ab => ab.B.P);
        foreach (var (a, b) in chosen)
        {
            var q2 = a.Q + b.Q;
            var dp = b.P - a.P;
            var cLo = Math.Max(0, ledger.Inner(account, name, s, b.TLoMax - lag).Cost - ledger.Outer(account, name, s, Upto(a.First.T + lag)).Cost);
            var hiEnd = ledger.OuterHigh(account, name, sHi, Upto(b.First.T + lag));
            var hiStart = ledger.Inner(account, name, sHi, a.TLoMax - lag).Cost;
            var lo = b.P >= 100 ? 0.0 : 100.0 * cLo / (dp + q2);
            var dpTop = Math.Min(b.P, 100.0) - a.P;
            var hi = dpTop - q2 <= 0 ? double.PositiveInfinity : 100.0 * Math.Max(0, hiEnd - hiStart) / (dpTop - q2);
            segments.Add(new EstimateSegment(SegmentType.Increment, lo, hi, dp, b.Last.T));
        }

        segments.RemoveAll(x => !(x.Lo > 0 || (double.IsFinite(x.Hi) && x.Hi > 0)));
        if (segments.Count > EstimateLimits.SegmentsPerInstance)
        {
            var cumulative = segments.Where(x => x.Type == SegmentType.Cumulative).ToList();
            var increments = segments.Where(x => x.Type == SegmentType.Increment).ToList();
            var room = EstimateLimits.SegmentsPerInstance - increments.Count;
            var stride = (double)cumulative.Count / room;
            var kept = new EstimateSegment[room];
            for (var i = 0; i < room; i++)
            {
                kept[room - 1 - i] = cumulative[Math.Min(cumulative.Count - 1, (int)(cumulative.Count - 1 - i * stride))];
            }

            segments = [.. kept, .. increments];
        }

        return segments;
    }

    /// <summary>
    /// The instance's interval: the largest lower bound and the smallest upper bound not below it. When a cumulative lower
    /// bound lies above an increment's upper bound, the increments alone are kept. Null without a lower bound.
    /// </summary>
    public static InstanceInterval? Bounds(IReadOnlyList<EstimateSegment> segments, long r)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var increments = segments.Where(x => x.Type == SegmentType.Increment).ToList();
        var cumLo = 0.0;
        var incHi = double.PositiveInfinity;
        var anyCumulative = false;
        foreach (var x in segments)
        {
            if (x.Type == SegmentType.Cumulative)
            {
                cumLo = anyCumulative ? Math.Max(cumLo, x.Lo) : x.Lo;
                anyCumulative = true;
            }
            else
            {
                incHi = Math.Min(incHi, x.Hi);
            }
        }

        var offset = cumLo > incHi;
        var b = offset && BoundsOf(increments) is { } fromIncrements ? fromIncrements : BoundsOf(segments);
        if (b is not { } bounds)
        {
            return null;
        }

        return new InstanceInterval(r, bounds.Lo, bounds.Hi, segments.Max(x => x.Dp), segments.Max(x => x.T), offset);
    }

    private static (double Lo, double Hi)? BoundsOf(IReadOnlyList<EstimateSegment> group)
    {
        double? low = null;
        foreach (var x in group)
        {
            if (x.Lo > 0)
            {
                low = low is { } l ? Math.Max(l, x.Lo) : x.Lo;
            }
        }

        if (low is not { } lo)
        {
            return null;
        }

        var hi = double.PositiveInfinity;
        foreach (var x in group)
        {
            if (double.IsFinite(x.Hi) && x.Hi >= lo)
            {
                hi = Math.Min(hi, x.Hi);
            }
        }

        return (lo, hi);
    }
}
