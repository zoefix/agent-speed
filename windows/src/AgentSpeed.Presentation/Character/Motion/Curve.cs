namespace AgentSpeed.Presentation.Character;

/// <summary>One key of a motion curve: a value at a time.</summary>
public readonly record struct CurveKey(double T, double V)
{
    /// <summary>Keys in the reference's tuple order: by time, then by value.</summary>
    internal static CurveKey[] Sorted(IEnumerable<CurveKey> keys) => keys.OrderBy(k => k.T).ThenBy(k => k.V).ToArray();
}

/// <summary>
/// A keyframed curve through its keys on a monotone cubic (PCHIP, Fritsch-Carlson): holds stay flat, peaks stop
/// smoothly and a run in one direction never pauses at a key. Ends have zero slope; a periodic curve whose ends meet
/// joins smoothly across the wrap.
/// </summary>
public sealed class Curve
{
    private readonly double[] _m;

    public Curve(IEnumerable<CurveKey> keys, bool periodic = false)
    {
        var sorted = CurveKey.Sorted(keys);
        T = sorted.Select(k => k.T).ToArray();
        V = sorted.Select(k => k.V).ToArray();
        _m = Slopes(T, V, periodic);
    }

    public double[] T { get; }

    public double[] V { get; }

    public double Start => V[0];

    private static double Blend(double h0, double h1, double d0, double d1)
    {
        if (d0 * d1 <= 0)
        {
            return 0;
        }

        var w1 = 2 * h1 + h0;
        var w2 = h1 + 2 * h0;
        return (w1 + w2) / (w1 / d0 + w2 / d1);
    }

    private static double[] Slopes(double[] t, double[] v, bool periodic)
    {
        var n = t.Length;
        var m = new double[n];
        if (n < 3)
        {
            return m;
        }

        var h = new double[n - 1];
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = t[i + 1] - t[i];
            d[i] = h[i] > 1e-9 ? (v[i + 1] - v[i]) / h[i] : 0;
        }

        for (var i = 1; i < n - 1; i++)
        {
            m[i] = Blend(h[i - 1], h[i], d[i - 1], d[i]);
        }

        if (periodic && Math.Abs(v[0] - v[n - 1]) < 1e-9)
        {
            var s = Blend(h[n - 2], h[0], d[n - 2], d[0]);
            m[0] = s;
            m[n - 1] = s;
        }

        return m;
    }

    public double Evaluate(double x)
    {
        var t = T;
        var v = V;
        var n = t.Length;
        if (x <= t[0])
        {
            return v[0];
        }

        if (x >= t[n - 1])
        {
            return v[n - 1];
        }

        var lo = 0;
        var hi = n;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (x < t[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        var i = lo - 1;
        var h = t[i + 1] - t[i];
        if (h <= 1e-9)
        {
            return v[i + 1];
        }

        var s = (x - t[i]) / h;
        var sq = (1 - s) * (1 - s);
        var y = (1 + 2 * s) * sq * v[i];
        y += s * sq * h * _m[i];
        y += s * s * (3 - 2 * s) * v[i + 1];
        y += s * s * (s - 1) * h * _m[i + 1];
        return y;
    }
}
