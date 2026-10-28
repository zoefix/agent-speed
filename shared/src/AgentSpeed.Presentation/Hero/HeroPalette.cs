using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Hero;

/// <summary>Colors of the hero vehicle (DESIGN 4c "Color and glow"); every mix is in OKLab.</summary>
public static class HeroPalette
{
    public static readonly Rgba AmberHot = HeatPalette.LightSparkHot;

    private static readonly Rgba DarkSurface = Rgba.FromHex("#3A3A3A");
    private static readonly Rgba LightSurface = Rgba.FromHex("#E6E6E6");

    /// <summary>
    /// Stroke color <c>C</c>: the heat color (dark); light: the title label color, heat darkened 18 % and stepped to at
    /// least 4.5:1 (7:1 with increased contrast).
    /// </summary>
    public static Rgba Stroke(double heat, bool dark, bool increasedContrast = false)
    {
        var color = HeatPalette.Sample(heat);
        if (dark)
        {
            return increasedContrast ? Legible(color, true, 4.5) : color;
        }

        return Legible(OkLab.Darken(color, 0.18), false, increasedContrast ? 7 : 4.5);
    }

    /// <summary>Accent <c>H</c> for lights, glow parts and flame outlines.</summary>
    public static Rgba Hot(double heat, double overdrive, bool dark, bool increasedContrast = false) => dark
        ? OkLab.Mix(HeatPalette.Sample(heat), HeatPalette.WhiteHot, 0.65 + 0.35 * Math.Clamp(overdrive, 0, 1))
        : OkLab.Mix(Stroke(heat, false, increasedContrast), AmberHot, 0.45);

    /// <summary>Flame core: white-hot pushed toward white by overdrive (dark); amber (light).</summary>
    public static Rgba Core(double overdrive, bool dark) =>
        dark ? OkLab.Mix(HeatPalette.WhiteHot, Rgba.White, Math.Clamp(overdrive, 0, 1)) : AmberHot;

    /// <summary>Steps OKLab lightness (lighter on dark, darker on light) until the contrast reaches the minimum.</summary>
    public static Rgba Legible(Rgba color, bool dark, double minimum)
    {
        var surface = dark ? DarkSurface : LightSurface;
        var lab = OkLab.FromRgb(color);
        var candidate = color;
        for (var i = 0; i < 40; i++)
        {
            if (Rgba.ContrastRatio(candidate, surface) >= minimum)
            {
                return candidate;
            }

            lab = lab with { L = Math.Clamp(lab.L + (dark ? 0.015 : -0.015), 0, 1) };
            candidate = lab.ToRgb();
        }

        return candidate;
    }
}

/// <summary>The resolved colors and presence of one frame.</summary>
public readonly record struct HeroPaint(Rgba Color, Rgba Hot, Rgba Core, double Presence, bool Dark, bool HighContrast);
