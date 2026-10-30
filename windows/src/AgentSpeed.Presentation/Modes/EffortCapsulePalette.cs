using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Modes;

/// <summary>The capsule's heat and amber colors (DESIGN 4, Agents), legible on both appearances.</summary>
public static class EffortCapsulePalette
{
    public const double MaxHeat = 0.82;
    public const double TopRungLow = 0.72;
    public const double TopRungHigh = 1.0;

    /// <summary>The top rung's outer glow: 3 DIP of the gradient at 45 %.</summary>
    public const double GlowRadius = 3;
    public const double GlowOpacity = 0.45;

    /// <summary>Unlit bars: label 14 % (dark 18 %).</summary>
    public static double UnlitOpacity(bool dark) => dark ? 0.18 : 0.14;

    /// <summary>Capsule fill: label 5.5 % (dark 7 %); its 0.5 DIP stroke is label 8 %.</summary>
    public static double FillOpacity(bool dark) => dark ? 0.07 : 0.055;

    public const double StrokeOpacity = 0.08;

    /// <summary>The configured form's dashed stroke: label 22 % (dark 26 %).</summary>
    public static double ConfiguredStrokeOpacity(bool dark) => dark ? 0.26 : 0.22;

    /// <summary>The configured form's unlit bars: label 10 % (dark 12 %).</summary>
    public static double ConfiguredUnlitOpacity(bool dark) => dark ? 0.12 : 0.10;

    /// <summary>The history's unknown bar: label 12 %.</summary>
    public const double UnknownBarOpacity = 0.12;

    /// <summary>Heat as text and marks: the palette itself on dark, its darkened label tone on light.</summary>
    public static Rgba Heat(double heat, bool dark) => dark ? HeatPalette.Sample(heat) : OrbTokens.Light.TierLabel(heat);

    public static Rgba Bolt(bool dark) => Rgba.FromHex(dark ? "#F5A524" : "#B86E00");

    /// <summary>
    /// A top-rung bar's gradient from its top to its bottom: the meter spans heat 0.72 at the bottom to 1.0 at the top of
    /// the tallest bar, so a shorter bar ends lower on the gradient.
    /// </summary>
    public static (Rgba Top, Rgba Bottom) TopRungBar(double barHeight, bool dark)
    {
        var tallest = EffortCapsule.BarHeights[^1];
        var top = TopRungLow + (TopRungHigh - TopRungLow) * Math.Clamp(barHeight / tallest, 0, 1);
        return (Heat(top, dark), Heat(TopRungLow, dark));
    }
}
