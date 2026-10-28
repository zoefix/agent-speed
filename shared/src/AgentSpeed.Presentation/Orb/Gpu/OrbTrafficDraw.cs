using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Traffic;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Colors of the traffic readout's two kinds of text: the numbers, and the arrows with the units.</summary>
/// <param name="ShadowSigma">σ of the shadow in points (the liquid below its surface).</param>
public readonly record struct OrbTrafficColors(Rgba Number, Rgba Secondary, Rgba? Shadow = null, double ShadowSigma = 0)
{
    private static readonly Rgba Ink = Rgba.FromHex("#0B0D12");

    /// <summary>DESIGN 10: numbers at 80 % of the value token (dark white 78 %, light ink 76 %); arrows and units 50 %.</summary>
    public static OrbTrafficColors Standard(OrbGpuContext ctx)
    {
        if (ctx.HighContrast)
        {
            return new OrbTrafficColors(ctx.Contrast.WindowText, ctx.Contrast.GrayText);
        }

        return ctx.Dark
            ? new OrbTrafficColors(Rgba.White.WithAlpha(0.78), Rgba.White.WithAlpha(0.50))
            : new OrbTrafficColors(Ink.WithAlpha(0.76), Rgba.Black.WithAlpha(0.50));
    }

    /// <summary>Below the liquid surface (the 2a.3 layer 10 split rule): white 94 / 72 % with a dark shadow, or ink 88 / 62 %.</summary>
    public static OrbTrafficColors UnderLiquid(OrbGpuContext ctx)
    {
        if (ctx.HighContrast)
        {
            return Standard(ctx);
        }

        return ctx.Dark
            ? new OrbTrafficColors(Rgba.White.WithAlpha(0.94), Rgba.White.WithAlpha(0.72), Rgba.Black.WithAlpha(0.55), 0.015 * ctx.Size)
            : new OrbTrafficColors(Ink.WithAlpha(0.88), Ink.WithAlpha(0.62));
    }
}

/// <summary>
/// The orb's traffic readout on the GPU (DESIGN 10, Floating orb readout): two small lines, download over upload, each an
/// arrow, a number right-aligned in a cell as wide as <c>8.88</c> and a unit left-aligned in a cell as wide as <c>MB/s</c>,
/// sharing their left edge and centered on the orb. Characters are replaced in their cells once per sample (no
/// animation); the block fades by the quiet rule and steps aside for the new-peak pill. Allocates nothing per frame.
/// </summary>
public sealed class OrbTrafficDraw
{
    /// <summary>The readout fades out (and back) around the new-peak pill.</summary>
    public const double PillFade = 0.15;

    private const string Down = "↓";
    private const string Up = "↑";
    private const string NumberCell = "8.88";

    private IOrbGlyphs _glyphs;

    public OrbTrafficDraw(IOrbGlyphs glyphs)
    {
        _glyphs = glyphs ?? throw new ArgumentNullException(nameof(glyphs));
    }

    public IOrbGlyphs Glyphs
    {
        get => _glyphs;
        set => _glyphs = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The <c>8.88</c> number cell and the <c>MB/s</c> (bits: <c>Mb/s</c>) unit cell at text size <paramref name="t"/>.</summary>
    public (double Number, double Unit) Cells(double t, bool bits)
    {
        var digit = _glyphs.DigitCell(OrbFont.TrafficNumber, t);
        var point = _glyphs.TryGet(OrbTextKey.Create(".", OrbFont.TrafficNumber, t), out var dot) ? dot.Advance : 0.3 * t;
        var unit = _glyphs.TryGet(OrbTextKey.Create(bits ? "Mb/s" : "MB/s", OrbFont.Unit, UnitSize(t)), out var cell) ? cell.Advance : 2.2 * t;
        return (3 * digit + point, unit);
    }

    /// <summary>One line: arrow, gap, number cell, gap, unit cell (≈ 5.6 t).</summary>
    public double LineWidth(double t, bool bits)
    {
        var (number, unit) = Cells(t, bits);
        return (OrbTrafficPlacement.ArrowWidth + 2 * OrbTrafficPlacement.Gap) * t + number + unit;
    }

    /// <summary>A line fits the style's free area at this size (computed with the platform font, never truncated or shrunk).</summary>
    public bool Fits(OrbTrafficPlacement placement, double s, bool bits) => s >= 64 && LineWidth(placement.TextSize(s), bits) <= placement.Fit * s;

    /// <summary>The block's opacity at <paramref name="time"/>: the quiet rule, and around the new-peak pill where the pill shares the area.</summary>
    public static double Alpha(in OrbTrafficReading reading, OrbScene scene, bool yieldsToPill)
    {
        var reduce = scene.Effects.ReduceMotion;
        var alpha = reading.Alpha(scene.Time, reduce);
        if (yieldsToPill && scene.PeakAge is { } age)
        {
            var fade = reduce ? 0.001 : PillFade;
            var total = OrbScene.PeakPillDuration;
            if (age < total)
            {
                alpha *= 1 - Math.Min(1, age / fade);
            }
            else if (age < total + fade)
            {
                alpha *= (age - total) / fade;
            }
        }

        return alpha;
    }

    /// <param name="shift">The readout stack's shift (points, DESIGN 10 Centering).</param>
    public void Add(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbTrafficReading reading, OrbTrafficPlacement placement,
        double shift, in OrbTrafficColors colors, OrbTextClip clip, bool yieldsToPill)
    {
        var alpha = Alpha(reading, scene, yieldsToPill);
        if (alpha <= 0.01)
        {
            return;
        }

        var s = ctx.Size;
        var t = placement.TextSize(s);
        var (numberCell, _) = Cells(t, reading.Bits);
        var width = LineWidth(t, reading.Bits);
        if (s < 64 || width > placement.Fit * s)
        {
            return;
        }

        var left = ctx.CenterX - width / 2;
        var (down, up) = placement.Baselines(ctx.CenterY + shift, s);
        var gap = OrbTrafficPlacement.Gap * t;
        var numberRight = left + OrbTrafficPlacement.ArrowWidth * t + gap + numberCell;
        var number = colors.Number.MultiplyAlpha(alpha);
        var secondary = colors.Secondary.MultiplyAlpha(alpha);
        var shadow = colors.Shadow?.MultiplyAlpha(alpha);
        var sigma = colors.Shadow is null ? 0 : colors.ShadowSigma;
        Line(list, Down, reading.Rx, left, numberRight, gap, down, t, number, secondary, shadow, sigma, clip);
        Line(list, Up, reading.Tx, left, numberRight, gap, up, t, number, secondary, shadow, sigma, clip);
    }

    private void Line(OrbDrawList list, string arrow, Core.Traffic.TrafficFormat.Parts parts, double left, double numberRight, double gap,
        double baseline, double t, Rgba number, Rgba secondary, Rgba? shadow, double sigma, OrbTextClip clip)
    {
        if (_glyphs.TryGet(OrbTextKey.Create(arrow, OrbFont.Arrow, t, 0, sigma), out var glyph))
        {
            list.AddText(glyph, left, baseline, secondary, shadow, clip);
        }

        var digit = _glyphs.DigitCell(OrbFont.TrafficNumber, t);
        var advance = 0.0;
        foreach (var ch in parts.Number)
        {
            advance += CellWidth(ch, t, digit, sigma);
        }

        var x = numberRight - advance;
        foreach (var ch in parts.Number)
        {
            var cell = CellWidth(ch, t, digit, sigma);
            if (_glyphs.TryGet(OrbTextKey.Create(OrbReadoutDraw.CharText(ch), OrbFont.TrafficNumber, t, 0, sigma), out var character))
            {
                list.AddText(character, x + (cell - character.Advance) / 2, baseline, number, shadow, clip);
            }

            x += cell;
        }

        if (_glyphs.TryGet(OrbTextKey.Create(parts.Unit, OrbFont.Unit, UnitSize(t), 0, sigma), out var unit))
        {
            list.AddText(unit, numberRight + gap, baseline, secondary, shadow, clip);
        }
    }

    private double CellWidth(char ch, double t, double digit, double sigma) =>
        char.IsAsciiDigit(ch) ? digit
        : _glyphs.TryGet(OrbTextKey.Create(OrbReadoutDraw.CharText(ch), OrbFont.TrafficNumber, t, 0, sigma), out var glyph) ? glyph.Advance : 0;

    private static double UnitSize(double t) => OrbTrafficPlacement.UnitScale * Math.Max(OrbReadoutLayout.MinTextSize, t);
}
