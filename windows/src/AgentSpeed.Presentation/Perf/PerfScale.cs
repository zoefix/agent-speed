namespace AgentSpeed.Presentation.Perf;

/// <summary>
/// The log scale of every meter of the score (DESIGN 12, <i>Log scale</i>): <c>f = clamp(log2(v / 250) / 4, 0, 1)</c>, so
/// 250 is empty, 1000 half and 4000 full; above 4000 the meter stays full while the number keeps rising.
/// </summary>
public static class PerfScale
{
    public const double Empty = 250;
    public const double Reference = 1000;
    public const double Full = 4000;

    /// <summary>The card's scale ticks: 500, 1000 (the reference, taller) and 2000; 4000 labels the end.</summary>
    public static IReadOnlyList<int> Ticks { get; } = [500, 1000, 2000];

    public static double Fraction(double value) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(Math.Log2(value / Empty) / 4, 0, 1) : 0;

    /// <summary>Neon's meter: <c>round(f × 12)</c> lit segments (6 at 1000, all 12 from 4000).</summary>
    public static int LitSegments(double value, int segments = 12) =>
        (int)Math.Round(Fraction(value) * segments, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The History chart's top: <c>max(4000, 250 · 2^⌈log2(max / 250)⌉)</c>, so a day above 4000 lifts it a whole doubling.
    /// </summary>
    public static double ChartTop(double highest)
    {
        if (!double.IsFinite(highest) || highest <= Full)
        {
            return Full;
        }

        return Math.Max(Full, Empty * Math.Pow(2, Math.Ceiling(Math.Log2(highest / Empty))));
    }

    /// <summary>A value's height on a log chart from 250 at the bottom (0) to <paramref name="top"/> (1).</summary>
    public static double ChartFraction(double value, double top) =>
        double.IsFinite(value) && value > 0 && top > Empty ? Math.Clamp(Math.Log2(value / Empty) / Math.Log2(top / Empty), 0, 1) : 0;

    /// <summary>The chart's gridlines: every power-of-two step from 500 up to <paramref name="top"/>.</summary>
    public static IReadOnlyList<int> Gridlines(double top)
    {
        var lines = new List<int>();
        for (var v = 500.0; v <= top + 0.5; v *= 2)
        {
            lines.Add((int)v);
        }

        return lines;
    }
}
