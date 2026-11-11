namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>Rounding and search helpers that match the reference (spec/tools/quota_estimate.py).</summary>
public static class EstimateMath
{
    /// <summary>Rounds half away from zero to <paramref name="digits"/> decimals (REPORTING-EXTENSIONS 11.2).</summary>
    public static double RoundHalfAway(double x, int digits)
    {
        var m = Math.Pow(10, digits);
        var v = Math.Floor(Math.Abs(x) * m + 0.5 + 1e-9) / m;
        return Math.CopySign(v, x);
    }

    /// <summary>Whole micro-USD: <c>floor(x + 0.5)</c>.</summary>
    public static long? Micro(double? x) => x is { } v ? (long)Math.Floor(v + 0.5) : null;

    /// <summary>Rounds to nine decimals, half to even, the way the vote compares losses and weights.</summary>
    public static double Round9(double x) => Math.Round(x, 9, MidpointRounding.ToEven);

    public static int BisectLeft(long[] values, long x)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (values[mid] < x)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    public static int BisectRight(long[] values, long x)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (x < values[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    public static int BisectLeft(double[] values, double x)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (values[mid] < x)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    public static int BisectRight(double[] values, double x)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (x < values[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}
