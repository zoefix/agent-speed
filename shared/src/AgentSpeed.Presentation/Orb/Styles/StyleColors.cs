using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Colors shared by the orb styles (DESIGN 2a, Conventions), backed by lookup tables so a frame never runs the OKLab
/// search: <c>heat(x)</c>, the <i>label color</i> (dark: heat; light: heat darkened 18 % OKLab L, stepped to ≥ 4.5:1)
/// and <i>hot</i>.
/// </summary>
public static class StyleColors
{
    private const int TableSize = HeatPalette.TableSize;

    public static readonly Rgba WhiteHot = HeatPalette.WhiteHot;
    public static readonly Rgba AmberHot = HeatPalette.LightSparkHot;

    private static readonly Rgba[] LightLabel = BuildLightLabel();

    public static Rgba Heat(double x) => HeatPalette.Sample(x);

    /// <summary>The title label color in this appearance (the stroke color of DESIGN 2a).</summary>
    public static Rgba Label(double x, bool dark) => dark ? HeatPalette.Sample(x) : Sample(LightLabel, x);

    /// <summary><c>hot</c>: <c>mix(heat(h), #FFF7D6, 0.55 + 0.45 o)</c> dark, <c>mix(label, #FFB020, 0.3)</c> light.</summary>
    public static Rgba Hot(double h, double o, bool dark) => dark
        ? OkLab.Mix(HeatPalette.Sample(h), WhiteHot, 0.55 + 0.45 * StyleMath.Clamp01(o))
        : OkLab.Mix(Label(h, false), AmberHot, 0.3);

    /// <summary>Raises OKLab lightness toward 1 by <paramref name="amount"/> (a brightness flash on an opaque color).</summary>
    public static Rgba Lift(Rgba color, double amount)
    {
        if (amount <= 0)
        {
            return color;
        }

        var lab = OkLab.FromRgb(color);
        return (lab with { L = lab.L + (1 - lab.L) * Math.Min(1, amount) }).ToRgb(color.A);
    }

    private static Rgba Sample(Rgba[] table, double x)
    {
        x = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
        var position = x * (TableSize - 1);
        var index = (int)position;
        return index >= TableSize - 1 ? table[TableSize - 1] : Rgba.Lerp(table[index], table[index + 1], position - index);
    }

    private static Rgba[] BuildLightLabel()
    {
        var table = new Rgba[TableSize];
        for (var i = 0; i < TableSize; i++)
        {
            table[i] = HeroPalette.Stroke(i / (double)(TableSize - 1), dark: false);
        }

        return table;
    }
}
