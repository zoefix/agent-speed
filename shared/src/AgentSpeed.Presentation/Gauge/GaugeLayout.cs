using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Gauge;

public readonly record struct Vector2D(double X, double Y)
{
    public double Length => Math.Sqrt(X * X + Y * Y);
}

public readonly record struct SegmentArc(int Index, double StartDegrees, double EndDegrees, double InnerRadius, double OuterRadius)
{
    public double MidDegrees => (StartDegrees + EndDegrees) / 2;

    public double MidRadius => (InnerRadius + OuterRadius) / 2;
}

/// <summary>
/// Gauge geometry in units of the orb diameter S, centered on the disc. Angles are in degrees on screen
/// (y down), so increasing angles run clockwise: 135° is bottom-left, 270° is the top, 405° is bottom-right.
/// </summary>
public static class GaugeLayout
{
    public const int SegmentCount = 36;
    public const double StartDegrees = 135;
    public const double SweepDegrees = 270;
    public const double GapDegrees = 2.2;
    public const double Radius = 0.41;
    public const double SegmentLength = 0.045;
    public const double DiscRadius = 0.5;
    public const double CometRadius = 0.468;
    public const double TickRadius = 0.452;

    public static double SegmentSpanDegrees => (SweepDegrees - GapDegrees * (SegmentCount - 1)) / SegmentCount;

    public static double InnerRadius => Radius - SegmentLength / 2;

    public static double OuterRadius => Radius + SegmentLength / 2;

    public static SegmentArc Segment(int index)
    {
        if ((uint)index >= SegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var start = StartDegrees + index * (SegmentSpanDegrees + GapDegrees);
        return new SegmentArc(index, start, start + SegmentSpanDegrees, InnerRadius, OuterRadius);
    }

    /// <summary>Heat color position of segment <paramref name="index"/>.</summary>
    public static double SegmentHeat(int index) => index / (double)(SegmentCount - 1);

    /// <summary>Brightness 0..1 of a segment for a fill heat; the last lit segment is fractional.</summary>
    public static double SegmentFill(double heat, int index)
    {
        var lit = Math.Clamp(double.IsFinite(heat) ? heat : 0, 0, 1) * SegmentCount;
        return Math.Clamp(lit - index, 0, 1);
    }

    /// <summary>Index of the leading (brightest-edge) lit segment, or -1 when nothing is lit.</summary>
    public static int LeadingIndex(double heat)
    {
        var lit = Math.Clamp(double.IsFinite(heat) ? heat : 0, 0, 1) * SegmentCount;
        if (lit < 0.05)
        {
            return -1;
        }

        return Math.Clamp((int)Math.Ceiling(lit - 1e-9) - 1, 0, SegmentCount - 1);
    }

    public static double AngleForFraction(double fraction) =>
        StartDegrees + SweepDegrees * Math.Clamp(fraction, 0, 1);

    public static Vector2D Polar(double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector2D(radius * Math.Cos(radians), radius * Math.Sin(radians));
    }

    /// <summary>Gauge fractions of the four tier thresholds (cruise, boost, overdrive, redline).</summary>
    public static IReadOnlyList<(SpeedTier Tier, double Fraction)> Ticks(double redline) =>
        HeatMath.ActiveTiers.Select(t => (t, HeatMath.HeatAtThreshold(t, redline))).ToArray();

    /// <summary>Gap center angle at the bottom of the gauge.</summary>
    public static double GapCenterDegrees => StartDegrees + SweepDegrees + (360 - SweepDegrees) / 2;
}
