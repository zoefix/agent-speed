using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb.Traffic;

/// <summary>
/// Where a style puts the traffic readout (DESIGN 10, Floating orb readout), in diameters from the center, y down.
/// </summary>
/// <param name="NearestBaseline">Baseline of the line nearest the value.</param>
/// <param name="Below">The lines sit below the value (<c>needle</c>, ↓ line nearest); otherwise above it (↑ line nearest).</param>
/// <param name="Size">Text size <c>t</c> = max(7 pt, size × S).</param>
/// <param name="Fit">A line must fit in this × S, else the readout is not drawn.</param>
/// <param name="Recenters">
/// The readout stack moves down while the block shows, so block, value, unit, title and pill stay centered as one stack;
/// <c>reactor</c> and <c>needle</c> keep their readout in a fixed window or hub.
/// </param>
public sealed record OrbTrafficPlacement(double NearestBaseline, bool Below, double Size, double Fit, bool Recenters = false)
{
    /// <summary>Line pitch, × t.</summary>
    public const double Pitch = 1.32;

    /// <summary>Gaps between arrow, number and unit, × t.</summary>
    public const double Gap = 0.16;

    /// <summary>Arrow box, × t.</summary>
    public const double ArrowWidth = 0.50;

    public const double ArrowHeight = 0.66;

    /// <summary>Unit size, × t.</summary>
    public const double UnitScale = 0.82;

    /// <summary>Cap height of the orb fonts, × size.</summary>
    public const double CapHeight = 0.70;

    /// <summary>Descent of a title line below its baseline, × its size.</summary>
    public const double TitleDescent = 0.22;

    /// <summary>Clearance kept between the title and the agent dots, × S.</summary>
    public const double DotsClearance = 0.05;

    public static OrbTrafficPlacement Led { get; } = new(-0.245, false, 0.050, 0.44, Recenters: true);

    public static OrbTrafficPlacement Liquid { get; } = Led;

    public static OrbTrafficPlacement Reactor { get; } = new(-0.175, false, 0.046, 0.32);

    public static OrbTrafficPlacement Needle { get; } = new(0.108, true, 0.046, 0.285);

    public static OrbTrafficPlacement Minimal { get; } = new(-0.228, false, 0.054, 0.50, Recenters: true);

    /// <summary>
    /// <c>needle</c>: while the readout is enabled and fits, <c>tok/s</c> leaves the hub for the dial face above it
    /// (baseline c − 0.254 S, max(7 pt, 0.046 S), weight 500, secondary at 75 %, tracking +0.4).
    /// </summary>
    public const double NeedleDialUnitBaseline = -0.254;

    public const double NeedleDialUnitSize = 0.046;

    public static OrbTrafficPlacement For(OrbStyle style) => style switch
    {
        OrbStyle.Needle => Needle,
        OrbStyle.Liquid => Liquid,
        OrbStyle.Reactor => Reactor,
        OrbStyle.Minimal => Minimal,
        _ => Led,
    };

    /// <summary>Never below the 7 pt floor of every orb label.</summary>
    public double TextSize(double s) => Math.Max(OrbReadoutLayout.MinTextSize, Size * s);

    /// <summary>Baselines of the download (first) and upload lines, from the center <paramref name="y"/>.</summary>
    public (double Down, double Up) Baselines(double y, double s)
    {
        var nearest = y + NearestBaseline * s;
        var pitch = Pitch * TextSize(s);
        return Below ? (nearest, nearest + pitch) : (nearest - pitch, nearest);
    }

    /// <summary>
    /// How far the readout stack (value, unit, title, pill and this block) moves down while the block shows above the value,
    /// so the whole stack stays centered (DESIGN 10): half the block's height over the value's cap top, but never so far
    /// that the title's bottom comes closer to the agent dots than the clearance.
    /// </summary>
    public double StackShift(OrbReadoutLayout layout, double s)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Recenters || Below)
        {
            return 0;
        }

        var t = TextSize(s);
        var blockTop = NearestBaseline * s - Pitch * t - CapHeight * t;
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

    /// <summary>The stack's shift at a moment: follows the block's quiet-rule opacity (smoothstep), never the pill's fade.</summary>
    public double StackShift(OrbReadoutLayout layout, double s, double shown) =>
        StackShift(layout, s) * Easing.SmoothStep(0, 1, Math.Clamp(shown, 0, 1));
}
