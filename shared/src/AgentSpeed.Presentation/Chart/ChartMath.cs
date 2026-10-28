namespace AgentSpeed.Presentation.Chart;

public readonly record struct ChartPoint(double X, double Y);

public readonly record struct BezierSegment(ChartPoint Control1, ChartPoint Control2, ChartPoint End);

public readonly record struct ChartScale(double MaxValue, bool PeakInRange);

/// <summary>Math for the live area chart (DESIGN section 4.2).</summary>
public static class ChartMath
{
    public static IReadOnlyList<int> RangeOptions { get; } = [60, 120, 300];

    public static int NormalizeRange(int seconds) =>
        RangeOptions.Contains(seconds) ? seconds : 120;

    /// <summary>
    /// Uniform Catmull-Rom spline through <paramref name="points"/> converted to cubic Bézier segments.
    /// Control points are clamped to each segment's value range so the curve never overshoots
    /// (no dips below zero, no false peaks).
    /// </summary>
    public static IReadOnlyList<BezierSegment> CatmullRom(IReadOnlyList<ChartPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return [];
        }

        var segments = new BezierSegment[points.Count - 1];
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];

            var lo = Math.Min(p1.Y, p2.Y);
            var hi = Math.Max(p1.Y, p2.Y);
            var c1 = new ChartPoint(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, lo, hi));
            var c2 = new ChartPoint(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, lo, hi));
            segments[i] = new BezierSegment(c1, c2, p2);
        }

        return segments;
    }

    public static ChartPoint EvaluateBezier(ChartPoint start, BezierSegment segment, double t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return new ChartPoint(
            a * start.X + b * segment.Control1.X + c * segment.Control2.X + d * segment.End.X,
            a * start.Y + b * segment.Control1.Y + c * segment.Control2.Y + d * segment.End.Y);
    }

    /// <summary>Rounds up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    public static double NiceCeiling(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10, exponent);
        var fraction = value / magnitude;
        double nice = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };
        return nice * magnitude;
    }

    /// <summary>
    /// Vertical scale: fits the data with headroom and includes today's peak when it is not so high that it
    /// would flatten the visible data.
    /// </summary>
    public static ChartScale ComputeScale(double dataMax, double peak, double minimum = 10)
    {
        dataMax = double.IsFinite(dataMax) ? Math.Max(dataMax, 0) : 0;
        peak = double.IsFinite(peak) ? Math.Max(peak, 0) : 0;
        var target = Math.Max(minimum, dataMax * 1.15);
        var peakInRange = peak > 0 && peak <= Math.Max(dataMax * 2.5, minimum * 2);
        if (peakInRange)
        {
            target = Math.Max(target, peak * 1.08);
        }

        var max = NiceCeiling(target);
        return new ChartScale(max, peakInRange && peak <= max);
    }

    /// <summary>Maps values to chart points: x spans the width, y = 0 at the bottom.</summary>
    public static ChartPoint[] Project(IReadOnlyList<double> values, double width, double height, double maxValue)
    {
        ArgumentNullException.ThrowIfNull(values);
        var points = new ChartPoint[values.Count];
        if (values.Count == 0)
        {
            return points;
        }

        maxValue = double.IsFinite(maxValue) ? Math.Max(maxValue, 0) : 0;
        var step = values.Count > 1 ? width / (values.Count - 1) : 0;
        var scale = maxValue > 0 ? height / maxValue : 0;
        for (var i = 0; i < values.Count; i++)
        {
            var v = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, maxValue) : 0;
            points[i] = new ChartPoint(i * step, height - v * scale);
        }

        return points;
    }

    /// <summary>Index of the bucket nearest to a horizontal position.</summary>
    public static int IndexAt(double x, double width, int count)
    {
        if (count <= 1 || width <= 0 || !double.IsFinite(x))
        {
            return 0;
        }

        var index = (int)Math.Round(x / width * (count - 1));
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>The last <paramref name="seconds"/> buckets of a history list.</summary>
    public static (int Start, int Count) Window(int total, int seconds)
    {
        total = Math.Max(total, 0);
        var count = Math.Clamp(seconds, 0, total);
        return (total - count, count);
    }

    /// <summary>Heat-palette gradient stops (offset 0 = top) for a vertical gradient over 0..maxValue.</summary>
    public static IReadOnlyList<(double Offset, double Heat)> HeatGradient(double maxValue, double redline, int steps = 8)
    {
        var stops = new List<(double, double)>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var fraction = i / (double)steps;
            var value = maxValue * (1 - fraction);
            stops.Add((fraction, Heat.HeatMath.Heat(value, redline)));
        }

        return stops;
    }
}
