using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Colors;

/// <summary>Heat palette (DESIGN section 1) interpolated in OKLab, backed by a lookup table.</summary>
public static class HeatPalette
{
    public const int TableSize = 256;

    public static readonly Rgba WhiteHot = Rgba.FromHex("#FFF7D6");
    public static readonly Rgba Gold = Rgba.FromHex("#FACC15");
    public static readonly Rgba LightSparkHot = Rgba.FromHex("#FFB020");

    public static IReadOnlyList<(double Position, Rgba Color)> Stops { get; } =
    [
        (0.00, Rgba.FromHex("#7C8798")),
        (0.10, Rgba.FromHex("#2DD4BF")),
        (0.30, Rgba.FromHex("#38BDF8")),
        (0.48, Rgba.FromHex("#818CF8")),
        (0.64, Rgba.FromHex("#E879F9")),
        (0.78, Rgba.FromHex("#FB7185")),
        (0.90, Rgba.FromHex("#FB923C")),
        (1.00, Rgba.FromHex("#FACC15")),
    ];

    private static readonly Rgba[] Table = BuildTable();

    /// <summary>Exact OKLab interpolation between the palette stops.</summary>
    public static Rgba Evaluate(double heat)
    {
        heat = double.IsFinite(heat) ? Math.Clamp(heat, 0, 1) : 0;
        for (var i = 1; i < Stops.Count; i++)
        {
            var (p1, c1) = Stops[i];
            if (heat > p1 && i < Stops.Count - 1)
            {
                continue;
            }

            var (p0, c0) = Stops[i - 1];
            var t = p1 <= p0 ? 0 : (heat - p0) / (p1 - p0);
            return OkLab.Mix(c0, c1, t);
        }

        return Stops[^1].Color;
    }

    /// <summary>Fast palette sample from the lookup table.</summary>
    public static Rgba Sample(double heat)
    {
        heat = double.IsFinite(heat) ? Math.Clamp(heat, 0, 1) : 0;
        var position = heat * (TableSize - 1);
        var index = (int)position;
        if (index >= TableSize - 1)
        {
            return Table[TableSize - 1];
        }

        return Rgba.Lerp(Table[index], Table[index + 1], position - index);
    }

    /// <summary>Quantized palette index, for caching brushes per bucket.</summary>
    public static int Bucket(double heat, int buckets)
    {
        heat = double.IsFinite(heat) ? Math.Clamp(heat, 0, 1) : 0;
        return Math.Clamp((int)Math.Round(heat * (buckets - 1)), 0, buckets - 1);
    }

    /// <summary>Highlight color: heat color pushed toward the white-hot core by the overdrive amount.</summary>
    public static Rgba Highlight(double heat, double overdrive) =>
        OkLab.Mix(Sample(heat), WhiteHot, Math.Clamp(overdrive, 0, 1));

    public static Rgba SourceColor(SourceKind source) => source switch
    {
        SourceKind.Codex => Rgba.FromHex("#34D399"),
        SourceKind.ClaudeCode => Rgba.FromHex("#FF8A65"),
        SourceKind.ClaudeDesktop => Rgba.FromHex("#B39DFF"),
        SourceKind.DeepSeek => Rgba.FromHex("#4D6BFE"),
        SourceKind.OpenCode => Rgba.FromHex("#F4B13E"),
        SourceKind.Gemini => Rgba.FromHex("#34B8F0"),
        SourceKind.Antigravity => Rgba.FromHex("#7C92F4"),
        SourceKind.AntigravityCli => Rgba.FromHex("#CE91EC"),
        _ => Rgba.FromHex("#7C8798"),
    };

    private static Rgba[] BuildTable()
    {
        var table = new Rgba[TableSize];
        for (var i = 0; i < TableSize; i++)
        {
            table[i] = Evaluate(i / (double)(TableSize - 1));
        }

        return table;
    }
}
