using System.Runtime.CompilerServices;

namespace AgentSpeed.Presentation.Character;

/// <summary>Arithmetic that matches the Python reference player bit for bit (spec/CHARACTERS.md, Conformance).</summary>
public static class CharacterMath
{
    /// <summary>Python's float <c>%</c>: the result takes the sign of the divisor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Mod(double a, double b)
    {
        var r = a % b;
        if (r != 0 && (b < 0) != (r < 0))
        {
            r += b;
        }

        return r;
    }

    /// <summary>Python's <c>round()</c>: halves go to the even neighbour.</summary>
    public static double RoundHalfEven(double x) => Math.Round(x, MidpointRounding.ToEven);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double x, double lower, double upper) => Math.Min(upper, Math.Max(lower, x));

    /// <summary>numpy.interp over keys sorted by x: linear between keys, flat outside.</summary>
    public static double Interpolate(double[] xs, double[] ys, double x)
    {
        var n = xs.Length;
        if (n == 0)
        {
            return 0;
        }

        if (x <= xs[0])
        {
            return ys[0];
        }

        if (x >= xs[n - 1])
        {
            return ys[n - 1];
        }

        var lo = 0;
        var hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var slope = (ys[lo + 1] - ys[lo]) / (xs[lo + 1] - xs[lo]);
        return slope * (x - xs[lo]) + ys[lo];
    }

    public static double SmoothStep(double t) => t * t * (3 - 2 * t);
}
