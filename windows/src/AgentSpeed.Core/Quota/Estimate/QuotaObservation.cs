namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// One vendor value of a window (QUOTAS 10.4): <paramref name="T"/> when the vendor computed it, <paramref name="TLo"/> the
/// earliest moment it may describe, <paramref name="R"/> its reset.
/// </summary>
public sealed record QuotaObservation(
    string Account, SourceKind Source, QuotaWindowKind Kind, string? Name, int Minutes, double P, long T, long TLo, long R,
    string? Plan, string? Tier)
{
    public string WindowId { get; } = EstimateNames.WindowId(Kind, Name, Minutes);

    /// <summary>A value's own resolution: 1 for integers (<c>23.0</c> included), 0.1 for one decimal, else 0.01.</summary>
    public static double Resolution(double p)
    {
        if (Math.Abs(p - Math.Round(p)) < 1e-9)
        {
            return 1.0;
        }

        var scaled = p * 10;
        return Math.Abs(scaled - Math.Round(scaled)) < 1e-9 ? 0.1 : 0.01;
    }
}

/// <summary>Observations of one account and window whose resets lie within 2 minutes of the first (QUOTAS 10.4).</summary>
/// <param name="S">The start for lower-bound sums: <c>max R − minutes</c>.</param>
/// <param name="SHi">The start for upper-bound sums: <c>min R − minutes</c>, an hour earlier for a 5-hour reset on a whole hour.</param>
public sealed record WindowInstance(long R, long S, long SHi, IReadOnlyList<QuotaObservation> Observations)
{
    public static IReadOnlyList<WindowInstance> Group(IEnumerable<QuotaObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var groups = new List<(long FirstR, List<QuotaObservation> Items)>();
        foreach (var o in observations.OrderBy(o => o.R).ThenBy(o => o.T))
        {
            if (groups.Count > 0 && o.R - groups[^1].FirstR <= EstimateLimits.InstanceJoinMs)
            {
                groups[^1].Items.Add(o);
            }
            else
            {
                groups.Add((o.R, [o]));
            }
        }

        var result = new List<WindowInstance>(groups.Count);
        foreach (var (_, items) in groups)
        {
            long max = long.MinValue, min = long.MaxValue;
            foreach (var o in items)
            {
                max = Math.Max(max, o.R);
                min = Math.Min(min, o.R);
            }

            var length = items[0].Minutes * EstimateLimits.Minute;
            var slack = items[0].Kind == QuotaWindowKind.FiveHour && min % EstimateLimits.Hour == 0 ? EstimateLimits.HourRoundedSlackMs : 0;
            var s = max - length;
            var sHi = min - length - slack;
            var kept = items.Where(o => o.T >= sHi - EstimateLimits.Minute && o.T <= max + EstimateLimits.Minute)
                .OrderBy(o => o.T).ThenBy(o => o.TLo).ToArray();
            if (kept.Length > 0)
            {
                result.Add(new WindowInstance(max, s, sHi, kept));
            }
        }

        return result;
    }
}
