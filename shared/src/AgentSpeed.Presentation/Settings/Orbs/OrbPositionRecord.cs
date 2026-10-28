using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Settings.Orbs;

public enum OrbAxisAnchor
{
    /// <summary>Docked to the left (x) or top (y) work-area edge.</summary>
    Low,

    /// <summary>Docked to the right (x) or bottom (y) work-area edge.</summary>
    High,

    /// <summary>Disc center as a fraction of the work area.</summary>
    Fraction,
}

/// <summary>One axis of a position record: an edge distance in DIPs (disc edge to work-area edge) or a fraction.</summary>
public readonly record struct OrbAxis(OrbAxisAnchor Anchor, double Value)
{
    /// <summary>Largest docked distance accepted from a file (the snap threshold).</summary>
    public const double MaxDockedDistance = OrbGeometry.SnapThreshold;

    public static OrbAxis Fraction(double value) => new(OrbAxisAnchor.Fraction, value);

    public static OrbAxis Low(double distance) => new(OrbAxisAnchor.Low, distance);

    public static OrbAxis High(double distance) => new(OrbAxisAnchor.High, distance);

    public bool IsDocked => Anchor != OrbAxisAnchor.Fraction;

    public static string KeyOf(OrbAxisAnchor anchor) => anchor switch
    {
        OrbAxisAnchor.Low => "low",
        OrbAxisAnchor.High => "high",
        _ => "fraction",
    };

    public static OrbAxisAnchor? ParseAnchor(string? key) => key switch
    {
        "low" => OrbAxisAnchor.Low,
        "high" => OrbAxisAnchor.High,
        "fraction" => OrbAxisAnchor.Fraction,
        _ => null,
    };

    /// <summary>Clamped and rounded (fractions to 5 decimals, distances to 0.01 DIP); null when not finite.</summary>
    public static OrbAxis? Clean(OrbAxisAnchor anchor, double value)
    {
        if (!double.IsFinite(value) || !Enum.IsDefined(anchor))
        {
            return null;
        }

        return anchor == OrbAxisAnchor.Fraction
            ? new OrbAxis(anchor, Math.Round(Math.Clamp(value, 0, 1), 5))
            : new OrbAxis(anchor, Math.Round(Math.Clamp(value, 0, MaxDockedDistance), 2));
    }
}

/// <summary>
/// Where an orb sits on one display (DESIGN 2b <c>layouts</c>), independent of its resolution and DPI:
/// <c>{ "display": "…", "xAnchor": "high", "x": 12, "yAnchor": "fraction", "y": 0.31 }</c>.
/// </summary>
public readonly record struct OrbPositionRecord(string Display, OrbAxis X, OrbAxis Y)
{
    public const int MaxDisplayKeyLength = 256;

    /// <summary>A legacy single-orb anchor (center as a work-area fraction) as a fraction/fraction record.</summary>
    public static OrbPositionRecord FromAnchor(string display, OrbAnchor anchor) => new(
        display,
        OrbAxis.Clean(OrbAxisAnchor.Fraction, anchor.X) ?? OrbAxis.Fraction(1),
        OrbAxis.Clean(OrbAxisAnchor.Fraction, anchor.Y) ?? OrbAxis.Fraction(1));

    /// <summary>
    /// The record of a disc center: an axis is docked when the disc edge is within the 12 pt margin of a work-area edge
    /// (plus 0.5 px), else a fraction.
    /// </summary>
    public static OrbPositionRecord FromCenter(string display, PointD center, double radius, RectD work, double scale)
    {
        var s = OrbGeometry.ValidScale(scale);
        return new OrbPositionRecord(
            display,
            AxisFrom(center.X, radius, work.X, work.Right, work.Width, s),
            AxisFrom(center.Y, radius, work.Y, work.Bottom, work.Height, s));
    }

    /// <summary>The disc center on that display's work area, fully inside it with the 12 pt margin.</summary>
    public PointD ToCenter(RectD work, double scale, double radius)
    {
        var s = OrbGeometry.ValidScale(scale);
        var center = new PointD(
            Resolve(X, radius, work.X, work.Right, work.Width, s),
            Resolve(Y, radius, work.Y, work.Bottom, work.Height, s));
        return PlacementMath.ClampDisc(center, radius, work, OrbGeometry.Margin * s);
    }

    /// <summary>Null unless the display key and both axes are usable; values are clamped.</summary>
    public OrbPositionRecord? Validated()
    {
        if (string.IsNullOrWhiteSpace(Display) || Display.Length > MaxDisplayKeyLength || Display.Any(char.IsControl))
        {
            return null;
        }

        return OrbAxis.Clean(X.Anchor, X.Value) is { } x && OrbAxis.Clean(Y.Anchor, Y.Value) is { } y
            ? new OrbPositionRecord(Display, x, y)
            : null;
    }

    public OrbPositionRecord OnDisplay(string display) => this with { Display = display };

    private static OrbAxis AxisFrom(double value, double radius, double low, double high, double span, double scale)
    {
        if (!double.IsFinite(value) || !double.IsFinite(radius) || !double.IsFinite(span))
        {
            return OrbAxis.Fraction(0.5);
        }

        var toLow = value - radius - low;
        var toHigh = high - (value + radius);
        var docked = OrbGeometry.Margin * scale + OrbGeometry.Tolerance;
        if (toLow <= docked && toLow <= toHigh)
        {
            return OrbAxis.Clean(OrbAxisAnchor.Low, Math.Max(0, toLow) / scale)!.Value;
        }

        if (toHigh <= docked)
        {
            return OrbAxis.Clean(OrbAxisAnchor.High, Math.Max(0, toHigh) / scale)!.Value;
        }

        return OrbAxis.Clean(OrbAxisAnchor.Fraction, span > 0 ? (value - low) / span : 0.5)!.Value;
    }

    private static double Resolve(OrbAxis axis, double radius, double low, double high, double span, double scale) =>
        axis.Anchor switch
        {
            OrbAxisAnchor.Low => low + axis.Value * scale + radius,
            OrbAxisAnchor.High => high - axis.Value * scale - radius,
            _ => low + Math.Clamp(axis.Value, 0, 1) * span,
        };
}
