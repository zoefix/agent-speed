namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// A source's window observations grouped by instance (window, reset, length) with their sorted times, so that the
/// fingerprint of QUOTAS 10.1 compares instances instead of every pair of observations.
/// </summary>
public sealed class WindowPointSet
{
    private readonly record struct InstanceKey(string Window, long R, int Minutes);

    private readonly List<(InstanceKey Key, long[] Times)> _groups;

    public WindowPointSet(IEnumerable<WindowPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _groups = points.GroupBy(p => new InstanceKey(p.Window, p.R, p.Minutes))
            .Select(g => (g.Key, g.Select(p => p.T).Order().ToArray()))
            .ToList();
    }

    public bool IsEmpty => _groups.Count == 0;

    /// <summary>
    /// The latest comparable pair observed by <paramref name="at"/> decides (the later time, then the earlier, then a
    /// contradiction first); with none by then the first pair after it; null without any pair.
    /// </summary>
    public static bool? Fingerprint(WindowPointSet a, WindowPointSet b, long at)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        (long Late, long Early, bool Contradicts)? latest = null, first = null;
        foreach (var (x, xs) in a._groups)
        {
            foreach (var (y, ys) in b._groups)
            {
                if (!string.Equals(x.Window, y.Window, StringComparison.Ordinal)
                    || Math.Min(x.R, y.R) - Math.Max(x.R - x.Minutes * EstimateLimits.Minute, y.R - y.Minutes * EstimateLimits.Minute) <= EstimateLimits.FingerprintMs)
                {
                    continue;
                }

                var contradicts = Math.Abs(x.R - y.R) > EstimateLimits.FingerprintMs;
                if (LastAtOrBefore(xs, at) is { } tx && LastAtOrBefore(ys, at) is { } ty)
                {
                    var pair = (Math.Max(tx, ty), Math.Min(tx, ty), contradicts);
                    if (latest is not { } l || Later(pair, l))
                    {
                        latest = pair;
                    }
                }

                var early = (Math.Max(xs[0], ys[0]), Math.Min(xs[0], ys[0]), contradicts);
                if (first is not { } f || Sooner(early, f))
                {
                    first = early;
                }
            }
        }

        return latest is { } decided ? !decided.Contradicts : first is { } next ? !next.Contradicts : null;
    }

    private static long? LastAtOrBefore(long[] times, long at)
    {
        var i = EstimateMath.BisectRight(times, at);
        return i > 0 ? times[i - 1] : null;
    }

    private static bool Later((long Late, long Early, bool Contradicts) x, (long Late, long Early, bool Contradicts) y) =>
        x.Late != y.Late ? x.Late > y.Late : x.Early != y.Early ? x.Early > y.Early : x.Contradicts && !y.Contradicts;

    private static bool Sooner((long Late, long Early, bool Contradicts) x, (long Late, long Early, bool Contradicts) y) =>
        x.Late != y.Late ? x.Late < y.Late : x.Early != y.Early ? x.Early < y.Early : x.Contradicts && !y.Contradicts;
}
