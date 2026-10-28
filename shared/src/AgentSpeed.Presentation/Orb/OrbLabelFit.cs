using AgentSpeed.Presentation.Gauge;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// Fits the title label under the orb's value (DESIGN section 2, item 6): the label may use the chord inside the gauge
/// ring at its lowest point, minus a margin, and a title wider than that shrinks down to <see cref="MinimumScale"/> of
/// its size. It is never truncated. Lengths are in units of the orb diameter S.
/// </summary>
public static class OrbLabelFit
{
    /// <summary>Label size as a fraction of S.</summary>
    public const double Size = 0.062;

    /// <summary>Part of the label size that hangs below the baseline (CJK and Latin descent).</summary>
    public const double DescentRatio = 0.16;

    /// <summary>Clear space kept between the label and the inner edge of the gauge segments.</summary>
    public const double RingMargin = 0.035;

    public const double MinimumScale = 0.8;

    /// <summary>
    /// Widest label (in S) whose bottom, <paramref name="baselineOffset"/> below the orb center plus the descent, still
    /// keeps <see cref="RingMargin"/> from the ring on both sides.
    /// </summary>
    public static double AvailableWidth(double baselineOffset)
    {
        var bottom = Math.Abs(baselineOffset) + Size * DescentRatio;
        var inner = GaugeLayout.InnerRadius;
        if (bottom >= inner)
        {
            return 0;
        }

        return Math.Max(0, 2 * (Math.Sqrt(inner * inner - bottom * bottom) - RingMargin));
    }

    /// <summary>
    /// How a title fits <paramref name="limit"/> (DESIGN 1): at its own size and tracking when it fits; otherwise the
    /// tracking tightens linearly down to 0, then the size shrinks down to <see cref="MinimumScale"/> (never below
    /// <paramref name="floor"/>). A title that still does not fit is drawn centered past the limit; it is never cut.
    /// </summary>
    /// <param name="untrackedWidth">Advance width of the title at <paramref name="size"/> with no tracking.</param>
    /// <param name="tracking">The style's tracking per gap between characters.</param>
    /// <param name="gaps">Gaps between grapheme clusters (count − 1).</param>
    /// <returns>The tracking and size to draw with (size rounded down to 0.25, tracking to 0.05).</returns>
    public static (double Tracking, double Size) Fit(double untrackedWidth, double tracking, int gaps, double limit, double size,
        double floor = OrbReadoutLayout.MinTextSize)
    {
        gaps = Math.Max(1, gaps);
        tracking = Math.Max(0, tracking);
        if (!double.IsFinite(untrackedWidth) || untrackedWidth + tracking * gaps <= limit || limit <= 0)
        {
            return (tracking, size);
        }

        if (untrackedWidth <= limit && tracking > 0)
        {
            return (Math.Max(0, Math.Floor((limit - untrackedWidth) / gaps * 20) / 20), size);
        }

        var scale = Math.Max(MinimumScale, limit / untrackedWidth);
        return (0, Math.Max(Math.Min(floor, size), Math.Floor(size * scale * 4) / 4));
    }

    /// <summary>Scale for a label of <paramref name="naturalWidth"/>: 1 when it fits, never below <see cref="MinimumScale"/>.</summary>
    public static double Scale(double naturalWidth, double availableWidth)
    {
        if (!double.IsFinite(naturalWidth) || naturalWidth <= 0 || !double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return 1;
        }

        return Math.Clamp(availableWidth / naturalWidth, MinimumScale, 1);
    }
}
