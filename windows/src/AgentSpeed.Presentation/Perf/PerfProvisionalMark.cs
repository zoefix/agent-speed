namespace AgentSpeed.Presentation.TaskScore;

/// <summary>A dotted underline laid out under a number, relative to the number's left edge and baseline.</summary>
/// <param name="Diameter">The dot diameter in DIP, a whole number of device pixels.</param>
/// <param name="Centers">Dot centres from the left edge of the number's advance width.</param>
/// <param name="Below">The dots' centre line below the baseline.</param>
/// <param name="Opacity">Of the number's color (70 %; 100 % in high contrast).</param>
public sealed record TaskUnderline(double Diameter, IReadOnlyList<double> Centers, double Below, double Opacity);

/// <summary>One stretch of a bar in DIP.</summary>
public readonly record struct TaskBarRect(double X, double Width, bool IsGhost);

/// <summary>
/// The proof mark (待證記號, DESIGN 12): "not yet proven" drawn dotted on every surface. Geometry only, so the WPF
/// drawing, the orb and board renderers and the tests share one rule.
/// </summary>
public static class TaskProofMark
{
    /// <summary>The number's color at 70 %, never a glow.</summary>
    public const double Opacity = 0.7;

    /// <summary>It fades in and out with the state; Reduce Motion: at once.</summary>
    public const double FadeSeconds = 0.25;

    /// <summary>Dot diameter as a share of the text size.</summary>
    public const double DiameterShare = 0.1;

    /// <summary>Dot pitch as a multiple of the diameter.</summary>
    public const double Pitch = 2.4;

    /// <summary>The dots' centre line below the baseline, as a share of the text size.</summary>
    public const double Below = 0.22;

    /// <summary>The taskbar block's line 1 keeps less room under it.</summary>
    public const double BarBelow = 0.17;

    /// <summary>High contrast thickens the dots by this much (DESIGN 12: Increase Contrast, <c>d + 0.5 pt</c>).</summary>
    public const double ContrastExtra = 0.5;

    /// <summary>The card's dotted tracks: 3 DIP dots on a 5 DIP pitch (tertiary).</summary>
    public const double TrackDot = 3;

    public const double TrackPitch = 5;

    /// <summary>A dimension bar's held credit: 4 DIP dots on a 5 DIP pitch in the score ink.</summary>
    public const double GhostDot = 4;

    public const double GhostPitch = 5;

    /// <summary>The composition bar's held credit: 3 DIP dots on a 4 DIP pitch.</summary>
    public const double CompositionGhostDot = 3;

    public const double CompositionGhostPitch = 4;

    /// <summary><c>d = max(1 device px, 0.1 × size)</c>, rounded to the device pixel (plus the contrast extra).</summary>
    public static double Diameter(double textSize, double pixelsPerDip, bool highContrast = false)
    {
        var scale = PositiveOr(pixelsPerDip, 1);
        var d = Math.Max(1 / scale, DiameterShare * Math.Max(0, textSize)) + (highContrast ? ContrastExtra : 0);
        return Math.Max(1, Math.Round(d * scale, MidpointRounding.AwayFromZero)) / scale;
    }

    /// <summary><c>n = max(2, ⌊(w − d) / (2.4 d)⌋ + 1)</c> dots spread evenly from the left to the right edge of <paramref name="width"/>.</summary>
    public static IReadOnlyList<double> UnderlineCenters(double width, double diameter)
    {
        var d = PositiveOr(diameter, 1);
        var w = Math.Max(d, double.IsFinite(width) ? width : 0);
        var count = Math.Max(2, (int)Math.Floor((w - d) / (Pitch * d)) + 1);
        var centers = new double[count];
        var step = (w - d) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            centers[i] = d / 2 + i * step;
        }

        return centers;
    }

    /// <summary>The dotted underline under a number of <paramref name="textSize"/> whose advance width is <paramref name="width"/>.</summary>
    /// <param name="barBlock">The taskbar block's line 1 (<see cref="BarBelow"/>).</param>
    public static TaskUnderline Underline(double textSize, double width, double pixelsPerDip, bool barBlock = false, bool highContrast = false)
    {
        var d = Diameter(textSize, pixelsPerDip, highContrast);
        return new TaskUnderline(d, UnderlineCenters(width, d), (barBlock ? BarBelow : Below) * Math.Max(0, textSize), highContrast ? 1 : Opacity);
    }

    /// <summary>
    /// Dots of <paramref name="diameter"/> tiled at <paramref name="pitch"/> over <paramref name="width"/>, the leftover spread
    /// between them so the first and last touch the ends (CSS <c>space</c>): the card's dotted tracks and held credits.
    /// </summary>
    public static IReadOnlyList<double> SpacedCenters(double width, double pitch, double diameter)
    {
        if (!double.IsFinite(width) || width < diameter || pitch <= 0)
        {
            return [];
        }

        var count = (int)Math.Floor(width / pitch);
        if (count <= 1)
        {
            return [width / 2];
        }

        var extra = (width - count * pitch) / (count - 1);
        var centers = new double[count];
        for (var i = 0; i < count; i++)
        {
            centers[i] = i * (pitch + extra) + pitch / 2;
        }

        return centers;
    }

    /// <summary>
    /// The dots of a dotted arc (glass's value arc: dash 0, gap 2 × the stroke, round caps), as angles in radians from
    /// <paramref name="start"/> towards <paramref name="end"/>.
    /// </summary>
    public static IReadOnlyList<double> ArcDots(double radius, double start, double end, double stroke)
    {
        if (!(radius > 0) || !(stroke > 0) || !double.IsFinite(start) || !double.IsFinite(end) || start == end)
        {
            return [];
        }

        var length = Math.Abs(end - start) * radius;
        var step = 2 * stroke / radius * Math.Sign(end - start);
        var count = (int)Math.Floor(length / (2 * stroke) + 1e-9) + 1;
        var angles = new double[count];
        for (var i = 0; i < count; i++)
        {
            angles[i] = start + i * step;
        }

        return angles;
    }

    /// <summary>Neon's lit segments drawn hollow: a 1 DIP stroke inside the segment's outline, inset by half of it.</summary>
    public const double HollowStroke = 1;

    public static double HollowInset => HollowStroke / 2;

    /// <summary>
    /// The composition bar's stretches over <paramref name="width"/>: each as long as its points, with a gap between
    /// neighbours (taken from the end of each but the last).
    /// </summary>
    public static IReadOnlyList<TaskBarRect> Composition(IReadOnlyList<TaskCompositionSegment> segments, double width, double gap = 2)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var rects = new List<TaskBarRect>(segments.Count);
        if (!(width > 0))
        {
            return rects;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var x = segment.Start / TaskScoreText.MaxScore * width;
            var right = (segment.Start + segment.Length) / TaskScoreText.MaxScore * width - (i < segments.Count - 1 ? gap : 0);
            if (right - x > 0)
            {
                rects.Add(new TaskBarRect(x, right - x, segment.IsGhost));
            }
        }

        return rects;
    }

    private static double PositiveOr(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}
