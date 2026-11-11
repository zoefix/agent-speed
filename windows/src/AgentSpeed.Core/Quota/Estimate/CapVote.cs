namespace AgentSpeed.Core.Quota.Estimate;

public enum EstimateConfidence
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>An instance interval in the vote with its weight; <see cref="Id"/> names its window (instances with one R count once).</summary>
public readonly record struct VoteItem(double Lo, double Hi, double W, long Id);

/// <summary>The winning region <c>[Low, High]</c>, the weight of the instances holding it and how many of them are narrow.</summary>
public sealed record VoteResult(double Low, double High, double Inside, int Narrow, double Loss);

/// <summary>The estimate of a cap class (QUOTAS 10.6). <see cref="Cap"/> is null with only a lower bound (<see cref="Low"/> is then "at least").</summary>
public sealed record CapEstimate(EstimateConfidence Confidence, double? Cap, double? Low, double? High, int Windows)
{
    public static CapEstimate None { get; } = new(EstimateConfidence.None, null, null, null, 0);

    public static string Name(EstimateConfidence confidence) => confidence switch
    {
        EstimateConfidence.High => "high",
        EstimateConfidence.Medium => "medium",
        EstimateConfidence.Low => "low",
        _ => "none",
    };
}

/// <summary>Weights, the interval vote, confidence and the lowered-cap rule of QUOTAS 10.6.</summary>
public static class CapVote
{
    public static long HalfLifeMs(int? minutes) => minutes is >= 10_000 ? 7 * EstimateLimits.Day : 2 * EstimateLimits.Day;

    public static double Weight(double dp, long t, long nowMs, long halfLifeMs)
    {
        var age = Math.Max(0, nowMs - t);
        return Math.Min(1.0, dp / EstimateLimits.FullStep) * Math.Pow(0.5, (double)age / halfLifeMs);
    }

    public static VoteResult? Vote(IReadOnlyList<VoteItem> weighted)
    {
        ArgumentNullException.ThrowIfNull(weighted);
        var items = weighted.Where(x => (x.Lo > 0 || (double.IsFinite(x.Hi) && x.Hi > 0)) && x.W >= EstimateLimits.MinWeight).ToList();
        if (!items.Any(x => x.Lo > 0))
        {
            return null;
        }

        var floor = items.Where(x => x.Lo > 0).Min(x => x.Lo);
        var points = new SortedSet<double>();
        foreach (var x in items)
        {
            if (double.IsFinite(x.Lo) && x.Lo >= floor)
            {
                points.Add(x.Lo);
            }

            if (double.IsFinite(x.Hi) && x.Hi >= floor)
            {
                points.Add(x.Hi);
            }
        }

        var p = points.ToArray();
        var cells = new List<(double A, double B)>(p.Length * 2);
        for (var i = 0; i < p.Length; i++)
        {
            cells.Add((p[i], p[i]));
            cells.Add((p[i], i + 1 < p.Length ? p[i + 1] : double.PositiveInfinity));
        }

        var los = items.Select(x => (x.Lo, x.W)).OrderBy(x => x.Lo).ThenBy(x => x.W).ToArray();
        var his = items.Where(x => double.IsFinite(x.Hi)).Select(x => (x.Hi, x.W)).OrderBy(x => x.Hi).ThenBy(x => x.W).ToArray();
        var loKeys = los.Select(x => x.Lo).ToArray();
        var hiKeys = his.Select(x => x.Hi).ToArray();
        var loSuffix = new double[los.Length + 1];
        for (var i = los.Length - 1; i >= 0; i--)
        {
            loSuffix[i] = loSuffix[i + 1] + los[i].W;
        }

        var hiPrefix = new double[his.Length + 1];
        for (var i = 0; i < his.Length; i++)
        {
            hiPrefix[i + 1] = hiPrefix[i] + his[i].W;
        }

        var scored = new List<(double A, double B, double Loss)>(cells.Count);
        foreach (var (a, b) in cells)
        {
            var probe = a == b ? a : double.IsFinite(b) ? Math.Sqrt(a * b) : a * 2;
            var loss = loSuffix[EstimateMath.BisectRight(loKeys, probe)] + EstimateLimits.HiPenalty * hiPrefix[EstimateMath.BisectLeft(hiKeys, probe)];
            scored.Add((a, b, EstimateMath.Round9(loss)));
        }

        var least = scored.Min(c => c.Loss);
        var regions = new List<double[]>();
        foreach (var (a, b, loss) in scored)
        {
            if (loss != least)
            {
                continue;
            }

            if (regions.Count > 0 && regions[^1][1] >= a)
            {
                regions[^1][1] = Math.Max(regions[^1][1], b);
            }
            else
            {
                regions.Add([a, b]);
            }
        }

        List<VoteItem> Holders(double[] region) =>
            items.Where(x => x.Lo <= region[0] * (1 + 1e-12) && x.Hi >= region[1] * (1 - 1e-12)).ToList();

        static double SumW(List<VoteItem> held)
        {
            var sum = 0.0;
            foreach (var h in held)
            {
                sum += h.W;
            }

            return sum;
        }

        var best = regions.OrderBy(r => -EstimateMath.Round9(SumW(Holders(r)))).ThenBy(r => r[0]).First();
        var held = Holders(best);
        var narrow = held.Where(x => x.Lo > 0 && double.IsFinite(x.Hi) && x.Hi / x.Lo - 1 <= EstimateLimits.HighWidth).Select(x => x.Id).Distinct().Count();
        return new VoteResult(best[0], best[1], SumW(held), narrow, least);
    }

    /// <param name="above">The weight of lower bounds above the region: a region below a lower bound is never high.</param>
    public static EstimateConfidence Confidence(double low, double high, double inside, int narrow, double above)
    {
        if (!double.IsFinite(high))
        {
            return EstimateConfidence.Low;
        }

        var width = high / low - 1;
        if (width <= EstimateLimits.HighWidth && inside >= 1.5 && narrow >= 2 && above == 0)
        {
            return EstimateConfidence.High;
        }

        return width <= 0.25 && inside >= 0.5 ? EstimateConfidence.Medium : EstimateConfidence.Low;
    }

    /// <summary>
    /// A lowered cap: when the two newest narrow instances agree, older instances whose lower bound lies above both are
    /// dropped (unseen usage never raises a lower bound, a cut cap does).
    /// </summary>
    public static IReadOnlyList<InstanceInterval> AfterCut(IReadOnlyList<InstanceInterval> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var narrow = items.Where(IsNarrow).OrderBy(x => x.T).ToList();
        if (narrow.Count < 2)
        {
            return items;
        }

        var a = narrow[^2];
        var b = narrow[^1];
        if (Math.Max(a.Lo, b.Lo) > Math.Min(a.Hi, b.Hi))
        {
            return items;
        }

        var top = Math.Max(a.Hi, b.Hi);
        return items.Where(x => !(x.T < a.T && x.Lo > top)).ToList();
    }

    /// <summary>The estimate of one cap class from its instance intervals.</summary>
    public static CapEstimate Estimate(IReadOnlyList<InstanceInterval> items, long nowMs, long halfLifeMs)
    {
        ArgumentNullException.ThrowIfNull(items);
        var kept = AfterCut(items);
        var weighted = kept.Select(s => new VoteItem(s.Lo, s.Hi, Weight(s.Dp, s.T, nowMs, halfLifeMs), s.R)).ToList();
        var used = weighted.Where(x => x.W >= EstimateLimits.MinWeight).ToList();
        if (Vote(weighted) is not { } result)
        {
            return CapEstimate.None;
        }

        var above = 0.0;
        foreach (var x in used)
        {
            if (x.Lo > result.Low * (1 + 1e-12))
            {
                above += x.W;
            }
        }

        double? cap = double.IsFinite(result.High) ? Math.Sqrt(result.Low * result.High) : null;
        double low = result.Low;
        double? high = double.IsFinite(result.High) ? result.High : null;
        if (cap is { } c)
        {
            low = Math.Min(low, c / (1 + EstimateLimits.PrecisionFloor));
            high = Math.Max(high!.Value, c * (1 + EstimateLimits.PrecisionFloor));
        }

        return new CapEstimate(Confidence(result.Low, result.High, result.Inside, result.Narrow, above), cap, low, high,
            used.Select(x => x.Id).Distinct().Count());
    }

    private static bool IsNarrow(InstanceInterval x) => x.Lo > 0 && double.IsFinite(x.Hi) && x.Hi / x.Lo - 1 <= EstimateLimits.HighWidth;
}
