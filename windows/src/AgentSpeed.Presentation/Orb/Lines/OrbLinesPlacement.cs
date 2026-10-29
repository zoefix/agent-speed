using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb.Lines;

/// <summary>
/// Where a style puts the two-line readout (DESIGN 10, Floating orb readout), in diameters from the center, y down.
/// </summary>
/// <param name="NearestBaseline">Baseline of the line nearest the value.</param>
/// <param name="Below">
/// The lines sit below the value (<c>needle</c>: the top line nearest); otherwise above it (the bottom line nearest).
/// </param>
/// <param name="Size">Text size <c>t</c> = max(7 pt, size × S).</param>
/// <param name="Fit">A line must fit in this × S, else the readout is not drawn.</param>
/// <param name="Recenters">
/// The readout stack moves down while the block shows, so block, value, unit, title and pill stay centered as one stack;
/// <c>reactor</c> and <c>needle</c> keep their readout in a fixed window or hub.
/// </param>
public sealed record OrbLinesPlacement(double NearestBaseline, bool Below, double Size, double Fit, bool Recenters = false)
{
    /// <summary>
    /// Intelligence above the value: the thinking line's baseline where it sits lower than the nearest line (<c>reactor</c>,
    /// so the larger intelligence line clears the core's rim).
    /// </summary>
    public double? ThinkingBaseline { get; init; }

    /// <summary>Intelligence: the thinking line ends with <c>tok/s</c>; <c>needle</c> leaves it to its dial legend.</summary>
    public bool ThinkingUnit { get; init; } = true;

    /// <summary>Intelligence in a round hub (<c>needle</c>): each line's bottom corners stay within this × S of the center.</summary>
    public double? HubReach { get; init; }

    /// <summary>
    /// The 性能 line's size p, × t: 1.25, and 1.1 in <c>needle</c>'s hub, where <c>8,888</c> at 1.25 t would fit only from
    /// 151 pt and take the readout off the medium orb (DESIGN 10, Performance readout).
    /// </summary>
    public double ScoreScale { get; init; } = IntelligenceScale;

    /// <summary>Line pitch, × t.</summary>
    public const double Pitch = 1.32;

    /// <summary>Gaps between arrow, number and unit, × t.</summary>
    public const double Gap = 0.16;

    /// <summary>Arrow box, × t: the glyph cell, widened to a text glyph's own advance.</summary>
    public const double ArrowWidth = 0.50;

    public const double ArrowHeight = 0.66;

    /// <summary>Unit size, and a text glyph's size, × the line's size.</summary>
    public const double UnitScale = 0.82;

    /// <summary>The 性能 line's size p, × t (DESIGN 10, Performance readout).</summary>
    public const double IntelligenceScale = 1.25;

    /// <summary>Gap between the intelligence line's glyph and its number, × p.</summary>
    public const double IntelligenceGap = 0.20;

    /// <summary>Cap height of the orb fonts, × size.</summary>
    public const double CapHeight = 0.70;

    /// <summary>Descent of a title line below its baseline, × its size.</summary>
    public const double TitleDescent = 0.22;

    /// <summary>Clearance kept between the title and the agent dots, × S.</summary>
    public const double DotsClearance = 0.05;

    public static OrbLinesPlacement Led { get; } = new(-0.245, false, 0.050, 0.44, Recenters: true);

    public static OrbLinesPlacement Liquid { get; } = Led;

    public static OrbLinesPlacement Reactor { get; } = new(-0.175, false, 0.046, 0.32) { ThinkingBaseline = -0.155 };

    public static OrbLinesPlacement Needle { get; } = new(0.108, true, 0.046, 0.285)
    {
        ThinkingUnit = false,
        HubReach = 0.223,
        ScoreScale = 1.1,
    };

    public static OrbLinesPlacement Minimal { get; } = new(-0.228, false, 0.054, 0.50, Recenters: true);

    /// <summary>
    /// <c>needle</c>: while the readout is enabled and fits, <c>tok/s</c> leaves the hub for the dial face above it
    /// (baseline c − 0.254 S, max(7 pt, 0.046 S), weight 500, secondary at 75 %, tracking +0.4).
    /// </summary>
    public const double NeedleDialUnitBaseline = -0.254;

    public const double NeedleDialUnitSize = 0.046;

    public static OrbLinesPlacement For(OrbStyle style) => style switch
    {
        OrbStyle.Needle => Needle,
        OrbStyle.Liquid => Liquid,
        OrbStyle.Reactor => Reactor,
        OrbStyle.Minimal => Minimal,
        _ => Led,
    };

    /// <summary>Never below the 7 pt floor of every orb label.</summary>
    public double TextSize(double s) => Math.Max(OrbReadoutLayout.MinTextSize, Size * s);

    /// <summary>Size of the top line: the intelligence line's p, else t.</summary>
    public double TopSize(OrbLinesKind kind, double s) =>
        kind == OrbLinesKind.Intelligence ? ScoreScale * TextSize(s) : TextSize(s);

    /// <summary>
    /// Baselines of the top and bottom lines, from the center <paramref name="y"/>. Above the value the intelligence
    /// readout's thinking line may sit lower (<see cref="ThinkingBaseline"/>); below it a larger top line drops by its extra
    /// cap height, so its cap top stays where a t line's would be.
    /// </summary>
    public (double Top, double Bottom) Baselines(double y, double s, OrbLinesKind kind)
    {
        var t = TextSize(s);
        var pitch = Pitch * t;
        if (!Below)
        {
            var nearest = y + (kind == OrbLinesKind.Intelligence ? ThinkingBaseline ?? NearestBaseline : NearestBaseline) * s;
            return (nearest - pitch, nearest);
        }

        var top = y + NearestBaseline * s + CapHeight * (TopSize(kind, s) - t);
        return (top, top + pitch);
    }

    /// <summary>
    /// The intelligence line's baseline alone, its thinking line hidden at zero: the nearest baseline, where the line next to
    /// the value sits (above the value the thinking line's, below it its own), so the gap to the value is unchanged.
    /// </summary>
    public double SingleBaseline(double y, double s)
    {
        var (top, bottom) = Baselines(y, s, OrbLinesKind.Intelligence);
        return Below ? top : bottom;
    }

    /// <summary>
    /// How far the readout stack (value, unit, title, pill and this block) moves down while the block shows above the value,
    /// so the whole stack stays centered (DESIGN 10): half the block's height over the value's cap top, but never so far
    /// that the title's bottom comes closer to the agent dots than the clearance. <paramref name="single"/>: the
    /// intelligence line alone, the block's top at its own cap top.
    /// </summary>
    public double StackShift(OrbReadoutLayout layout, double s, OrbLinesKind kind, bool single = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Recenters || Below)
        {
            return 0;
        }

        var top = single && kind == OrbLinesKind.Intelligence ? SingleBaseline(0, s) : Baselines(0, s, kind).Top;
        var blockTop = top - CapHeight * TopSize(kind, s);
        var valueTop = (layout.ValueBaseline - CapHeight * layout.ValueSize) * s;
        var centered = Math.Max(0, (valueTop - blockTop) / 2);
        if (layout.DotsY is not { } dots)
        {
            return centered;
        }

        var titleSize = OrbReadoutLayout.TextSize(layout.TitleSize, s);
        var titleBottom = layout.TitleBaseline * s + TitleDescent * titleSize;
        var dotsTop = dots * s - DotDiameter(s) / 2;
        return Math.Min(centered, Math.Max(0, dotsTop - DotsClearance * s - titleBottom));
    }

    /// <summary>The agent dots' diameter (4 pt at the 148 pt orb).</summary>
    public static double DotDiameter(double s) => 4 * s / 148;

    /// <summary>
    /// The stack's shift at a moment: follows the block's quiet-rule opacity (smoothstep), never the pill's fade, and the
    /// intelligence line's glide to the single-line position (<paramref name="single"/>, already eased).
    /// </summary>
    public double StackShift(OrbReadoutLayout layout, double s, OrbLinesKind kind, double shown, double single = 0)
    {
        var shift = StackShift(layout, s, kind);
        if (single > 0)
        {
            shift = Easing.Lerp(shift, StackShift(layout, s, kind, single: true), Math.Min(single, 1));
        }

        return shift * Easing.SmoothStep(0, 1, Math.Clamp(shown, 0, 1));
    }
}
