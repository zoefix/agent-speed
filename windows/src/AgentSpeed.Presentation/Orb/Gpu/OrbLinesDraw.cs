using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Colors of the two-line readout's two kinds of text: the numbers, and the glyphs with the units.</summary>
/// <param name="ShadowSigma">σ of the shadow in points (the liquid below its surface).</param>
public readonly record struct OrbLinesColors(Rgba Number, Rgba Secondary, Rgba? Shadow = null, double ShadowSigma = 0)
{
    private static readonly Rgba Ink = Rgba.FromHex("#0B0D12");

    /// <summary>DESIGN 10: numbers at 80 % of the value token (dark white 78 %, light ink 76 %); glyphs and units 50 %.</summary>
    public static OrbLinesColors Standard(OrbGpuContext ctx)
    {
        if (ctx.HighContrast)
        {
            return new OrbLinesColors(ctx.Contrast.WindowText, ctx.Contrast.GrayText);
        }

        return ctx.Dark
            ? new OrbLinesColors(Rgba.White.WithAlpha(0.78), Rgba.White.WithAlpha(0.50))
            : new OrbLinesColors(Ink.WithAlpha(0.76), Rgba.Black.WithAlpha(0.50));
    }

    /// <summary>Below the liquid surface (the 2a.3 layer 10 split rule): white 94 / 72 % with a dark shadow, or ink 88 / 62 %.</summary>
    public static OrbLinesColors UnderLiquid(OrbGpuContext ctx)
    {
        if (ctx.HighContrast)
        {
            return Standard(ctx);
        }

        return ctx.Dark
            ? new OrbLinesColors(Rgba.White.WithAlpha(0.94), Rgba.White.WithAlpha(0.72), Rgba.Black.WithAlpha(0.55), 0.015 * ctx.Size)
            : new OrbLinesColors(Ink.WithAlpha(0.88), Ink.WithAlpha(0.62));
    }
}

/// <summary>
/// The orb's two-line readout on the GPU (DESIGN 10). Network speed: each line a vector arrow, a number right-aligned in a
/// cell as wide as <c>8.88</c> and a unit left-aligned in a cell as wide as <c>MB/s</c>, sharing their left edge and
/// centered on the orb. Intelligence: a larger intelligence line (glyph · 性能) over a smaller thinking line
/// (glyph · tok/s value · unit), each centered on the orb by its own width with every piece on a device pixel; after 3 s
/// at 0 the thinking line fades out and the intelligence line glides to its place by the value. 性能 moves as DESIGN 12
/// <i>Motion</i> says (counts, crossfades, the pale color) and sits on the dotted provisional mark while provisional; every other
/// character is replaced once per sample. The block fades by the quiet rule and steps aside for the new-peak pill.
/// Allocates nothing per frame.
/// </summary>
public sealed class OrbLinesDraw
{
    /// <summary>The readout fades out (and back) around the new-peak pill.</summary>
    public const double PillFade = 0.15;

    private IOrbGlyphs _glyphs;
    private PerfUnderline? _underline;
    private (double Size, double Width, double Scale, bool HighContrast) _underlineKey;

    public OrbLinesDraw(IOrbGlyphs glyphs)
    {
        _glyphs = glyphs ?? throw new ArgumentNullException(nameof(glyphs));
    }

    public IOrbGlyphs Glyphs
    {
        get => _glyphs;
        set => _glyphs = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Network speed: the <c>8.88</c> number cell and the <c>MB/s</c> (bits: <c>Mb/s</c>) unit cell at text size <paramref name="t"/>.</summary>
    public (double Number, double Unit) Cells(double t, bool bits)
    {
        var unit = _glyphs.TryGet(OrbTextKey.Create(bits ? "Mb/s" : "MB/s", OrbFont.Unit, UnitSize(t)), out var cell) ? cell.Advance : 2.2 * t;
        return (DecimalCell(t), unit);
    }

    /// <summary>Network speed: one line, arrow box · gap · number cell · gap · unit cell (≈ 5.6 t).</summary>
    public double LineWidth(double t, bool bits)
    {
        var (number, unit) = Cells(t, bits);
        return OrbLinesPlacement.ArrowWidth * t + 2 * OrbLinesPlacement.Gap * t + number + unit;
    }

    /// <summary>
    /// Intelligence at diameter <paramref name="s"/>: the widths the two lines reach with their widest numbers, the 性能 line
    /// with <c>8,888</c> (the widest compact score) and the thinking line with <c>8.88</c> (and <c>tok/s</c> where it carries it).
    /// </summary>
    public (double Intelligence, double Thinking) WidestLines(OrbLinesPlacement placement, double s, OrbLinesContent content)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(content);
        var t = placement.TextSize(s);
        var p = placement.ScoreScale * t;
        return (Width(content.First, p, OrbLinesPlacement.IntelligenceGap * p, NumberWidth(PerfNumber.WidestCompact, p, 0), unit: false),
            Width(content.Second, t, OrbLinesPlacement.Gap * t, DecimalCell(t), placement.ThinkingUnit));
    }

    /// <summary>
    /// The readout fits the style's free area at this size (computed with the platform font, never truncated or shrunk):
    /// network speed by its line, intelligence by both lines with their widest numbers, in a hub with their bottom corners
    /// within its reach.
    /// </summary>
    public bool Fits(OrbLinesPlacement placement, double s, OrbLinesContent? content)
    {
        if (s < 64)
        {
            return false;
        }

        var t = placement.TextSize(s);
        if (content is not { Kind: OrbLinesKind.Intelligence })
        {
            return LineWidth(t, content?.Bits ?? false) <= placement.Fit * s;
        }

        var (intelligence, thinking) = WidestLines(placement, s, content);
        if (Math.Max(intelligence, thinking) > placement.Fit * s)
        {
            return false;
        }

        if (placement.HubReach is not { } reach)
        {
            return true;
        }

        var (top, bottom) = placement.Baselines(0, s, OrbLinesKind.Intelligence);
        return Corner(intelligence, top) <= reach * s && Corner(thinking, bottom) <= reach * s;
    }

    /// <summary>The block's opacity at <paramref name="time"/>: the quiet rule, and around the new-peak pill where the pill shares the area.</summary>
    public static double Alpha(in OrbLinesReading reading, OrbScene scene, bool yieldsToPill)
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
    public void Add(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbLinesReading reading, OrbLinesPlacement placement,
        double shift, in OrbLinesColors colors, OrbTextClip clip, bool yieldsToPill)
    {
        var alpha = Alpha(reading, scene, yieldsToPill);
        var content = reading.Content;
        if (alpha <= 0.01 || !Fits(placement, ctx.Size, content))
        {
            return;
        }

        var t = placement.TextSize(ctx.Size);
        var (top, bottom) = placement.Baselines(ctx.CenterY + shift, ctx.Size, content.Kind);
        var paint = new LinePaint(colors.Number.MultiplyAlpha(alpha), colors.Secondary.MultiplyAlpha(alpha), colors.Shadow?.MultiplyAlpha(alpha),
            colors.Shadow is null ? 0 : colors.ShadowSigma, clip);
        if (content.Kind == OrbLinesKind.Traffic)
        {
            var gap = OrbLinesPlacement.Gap * t;
            var left = ctx.CenterX - LineWidth(t, content.Bits) / 2;
            var cells = new LineCells(left, left + OrbLinesPlacement.ArrowWidth * t + gap + DecimalCell(t), gap, t);
            TrafficLine(list, content.First, cells, top, paint);
            TrafficLine(list, content.Second, cells, bottom, paint);
            return;
        }

        // The thinking line hidden at zero: it fades while the intelligence line glides to the single-line position.
        var p = placement.ScoreScale * t;
        var reduce = scene.Effects.ReduceMotion;
        var single = reading.Thinking.Single(scene.Time, reduce);
        var first = single > 0 ? Easing.Lerp(top, placement.SingleBaseline(ctx.CenterY + shift, ctx.Size), single) : top;
        ScoreLine(list, ctx, content.First, reading.Score.Frame(scene.Time, reduce), p, first, paint);
        var thinking = reading.Thinking.Alpha(scene.Time, reduce);
        if (thinking > 0.01)
        {
            var faded = thinking < 1 ? paint.Faded(thinking) : paint;
            TextLine(list, ctx, content.Second, t, OrbLinesPlacement.Gap * t, placement.ThinkingUnit, bottom, faded);
        }
    }

    /// <summary>Network speed: the arrow centered in its box, the number right-aligned in its cell, the unit after it.</summary>
    private void TrafficLine(OrbDrawList list, in OrbLine line, in LineCells cells, double baseline, in LinePaint paint)
    {
        var t = cells.Size;
        var arrowBox = OrbLinesPlacement.ArrowWidth * t;
        if (_glyphs.TryGet(OrbTextKey.Create(line.Glyph, OrbFont.Arrow, t, 0, paint.Sigma), out var arrow))
        {
            list.AddText(arrow, cells.Left + (arrowBox - arrow.Advance) / 2, baseline, paint.Secondary, paint.Shadow, paint.Clip);
        }

        Number(list, line.Number, t, cells.NumberRight - NumberWidth(line.Number, t, paint.Sigma), baseline, paint);
        if (line.Unit.Length > 0)
        {
            Text(list, line.Unit, OrbFont.Unit, UnitSize(t), cells.NumberRight + cells.Gap, baseline, paint);
        }
    }

    /// <summary>
    /// Intelligence: glyph · gap · number (· gap · unit), centered on the orb by its current width. Every piece starts on a
    /// device pixel, so the line moves only when its width changes and the atlas glyphs are drawn unresampled.
    /// </summary>
    private void TextLine(OrbDrawList list, OrbGpuContext ctx, in OrbLine line, double size, double gap, bool unit, double baseline,
        in LinePaint paint)
    {
        var small = OrbLinesPlacement.UnitScale * size;
        var pixels = ctx.Scale;
        var x = ctx.CenterX - Width(line, size, gap, NumberWidth(line.Number, size, paint.Sigma), unit) / 2;
        x = Text(list, line.Glyph, OrbFont.Title, small, x, baseline, paint, pixels) + gap;
        x = Number(list, line.Number, size, x, baseline, paint, pixels);
        if (unit && line.Unit.Length > 0)
        {
            Text(list, line.Unit, OrbFont.Unit, small, x + gap, baseline, paint, pixels);
        }
    }

    /// <summary>
    /// A string in the secondary color with its origin at <paramref name="x"/> (on the device pixel with
    /// <paramref name="pixels"/> per point); returns the end of its advance from the unsnapped origin.
    /// </summary>
    private double Text(OrbDrawList list, string text, OrbFont font, double size, double x, double baseline, in LinePaint paint,
        double pixels = 0)
    {
        if (!_glyphs.TryGet(OrbTextKey.Create(text, font, size, 0, paint.Sigma), out var glyph))
        {
            return x;
        }

        list.AddText(glyph, Snap(x, pixels), baseline, paint.Secondary, paint.Shadow, paint.Clip);
        return x + glyph.Advance;
    }

    /// <summary>
    /// The intelligence line, glyph · gap · 性能, centered by the score as drawn (DESIGN 10; DESIGN 12, <i>Values and forms</i>,
    /// <i>Provisional mark</i>, <i>Motion</i>): live in the number color, resting and <c>–</c> in the unit token's, crossfading
    /// between them; a count draws its current integer and <c>–</c> ↔ a number crossfades in place. A provisional score sits
    /// on the dotted underline, which takes no width and fades with the state.
    /// </summary>
    private void ScoreLine(OrbDrawList list, OrbGpuContext ctx, in OrbLine line, in OrbScoreFrame score, double size, double baseline,
        in LinePaint paint)
    {
        var small = OrbLinesPlacement.UnitScale * size;
        var gap = OrbLinesPlacement.IntelligenceGap * size;
        var pixels = ctx.Scale;
        Span<char> text = stackalloc char[PerfNumber.MaxCompactLength];
        text = text[..PerfNumber.FormatCompact(score.Shown, text)];
        var x = ctx.CenterX - (Advance(line.Glyph, OrbFont.Title, small) + gap + NumberWidth(text, size, paint.Sigma)) / 2;
        var left = Text(list, line.Glyph, OrbFont.Title, small, x, baseline, paint, pixels) + gap;
        var color = Rgba.Lerp(paint.Number, paint.Secondary, score.Pale);
        Span<char> other = stackalloc char[PerfNumber.MaxCompactLength];
        if (score.OutgoingAlpha > 0.01)
        {
            Number(list, other[..PerfNumber.FormatCompact(score.Outgoing, other)], size, left, baseline,
                paint.Inked(color, score.OutgoingAlpha), pixels);
        }

        if (score.Alpha > 0.01)
        {
            Number(list, text, size, left, baseline, paint.Inked(color, score.Alpha), pixels);
        }

        if (score.Mark > 0.01 && score.Marked is { } marked)
        {
            var width = NumberWidth(other[..PerfNumber.FormatCompact(marked, other)], size, paint.Sigma);
            ProvisionalMark(list, ctx, Snap(left, pixels), width, size, baseline, color.MultiplyAlpha(score.Mark), paint.Clip);
        }
    }

    /// <summary>
    /// The provisional mark under a number <paramref name="width"/> wide from <paramref name="left"/> (DESIGN 12): round dots in the
    /// content layer, under the text and never a glow, the number's color at 70 % (high contrast: 100 % and d + 0.5), each on
    /// the device pixel grid. Split by the liquid surface like the text: the pass for one side draws the dots on that side.
    /// </summary>
    private void ProvisionalMark(OrbDrawList list, OrbGpuContext ctx, double left, double width, double size, double baseline, Rgba color,
        OrbTextClip clip)
    {
        var mark = Underline(size, width, ctx.Scale, ctx.HighContrast);
        var ink = color.MultiplyAlpha(mark.Opacity);
        if (ink.A <= 0.004)
        {
            return;
        }

        var pixels = ctx.Scale;
        var r = mark.Diameter / 2;
        var y = Snap(baseline + mark.Below - r, pixels) + r;
        var side = (clip.Mode is OrbTextClipMode.AboveLiquid or OrbTextClipMode.BelowLiquid) && list.HasLiquid ? clip.Mode : OrbTextClipMode.None;
        var centers = mark.Centers;
        for (var i = 0; i < centers.Count; i++)
        {
            var x = Snap(left + centers[i] - r, pixels) + r;
            if (side != OrbTextClipMode.None && (y > list.Liquid.SurfaceY(x)) != (side == OrbTextClipMode.BelowLiquid))
            {
                continue;
            }

            list.Add(OrbGpuInstance.Circle(x, y, r, ink));
        }
    }

    /// <summary>The underline's geometry, kept while the number's width, size, scale and contrast stay (a count crosses few widths).</summary>
    private PerfUnderline Underline(double size, double width, double scale, bool highContrast)
    {
        var key = (size, width, scale, highContrast);
        if (_underline is null || _underlineKey != key)
        {
            _underline = PerfProvisionalMark.Underline(size, width, scale, barBlock: false, highContrast);
            _underlineKey = key;
        }

        return _underline;
    }

    /// <summary>
    /// A number from <paramref name="x"/>, each character centered in its tabular cell (on the device pixel with
    /// <paramref name="pixels"/> per point); returns its right end.
    /// </summary>
    private double Number(OrbDrawList list, ReadOnlySpan<char> number, double size, double x, double baseline, in LinePaint paint, double pixels = 0)
    {
        var digit = _glyphs.DigitCell(OrbFont.TrafficNumber, size);
        foreach (var ch in number)
        {
            var cell = CellWidth(ch, size, digit, paint.Sigma);
            if (_glyphs.TryGet(OrbTextKey.Create(OrbReadoutDraw.CharText(ch), OrbFont.TrafficNumber, size, 0, paint.Sigma), out var character))
            {
                list.AddText(character, Snap(x + (cell - character.Advance) / 2, pixels), baseline, paint.Number, paint.Shadow, paint.Clip);
            }

            x += cell;
        }

        return x;
    }

    /// <summary>Rounds <paramref name="x"/> to the device pixel; 0 pixels per point leaves it as it is (the network readout).</summary>
    private static double Snap(double x, double pixels) => pixels > 0 ? Math.Round(x * pixels) / pixels : x;

    /// <summary>The distance from the center to a line's bottom corner: half its width out, its baseline down.</summary>
    private static double Corner(double width, double baseline) => Math.Sqrt(width * width / 4 + baseline * baseline);

    /// <summary>An intelligence line's width, advances only: glyph · gap · number (· gap · unit).</summary>
    private double Width(in OrbLine line, double size, double gap, double number, bool unit)
    {
        var small = OrbLinesPlacement.UnitScale * size;
        var tail = unit && line.Unit.Length > 0 ? gap + Advance(line.Unit, OrbFont.Unit, small) : 0;
        return Advance(line.Glyph, OrbFont.Title, small) + gap + number + tail;
    }

    private double NumberWidth(ReadOnlySpan<char> number, double size, double sigma)
    {
        var digit = _glyphs.DigitCell(OrbFont.TrafficNumber, size);
        var width = 0.0;
        foreach (var ch in number)
        {
            width += CellWidth(ch, size, digit, sigma);
        }

        return width;
    }

    /// <summary>The <c>8.88</c> cell.</summary>
    private double DecimalCell(double size) =>
        3 * _glyphs.DigitCell(OrbFont.TrafficNumber, size)
        + (_glyphs.TryGet(OrbTextKey.Create(".", OrbFont.TrafficNumber, size), out var point) ? point.Advance : 0.3 * size);

    private double Advance(string text, OrbFont font, double size) =>
        _glyphs.TryGet(OrbTextKey.Create(text, font, size), out var glyph) ? glyph.Advance : 0;

    private double CellWidth(char ch, double size, double digit, double sigma) =>
        char.IsAsciiDigit(ch) ? digit
        : _glyphs.TryGet(OrbTextKey.Create(OrbReadoutDraw.CharText(ch), OrbFont.TrafficNumber, size, 0, sigma), out var glyph) ? glyph.Advance : 0;

    private static double UnitSize(double t) => OrbLinesPlacement.UnitScale * Math.Max(OrbReadoutLayout.MinTextSize, t);

    /// <summary>Network speed: the left edge and the number cell's right edge shared by both lines at text size <see cref="Size"/>.</summary>
    private readonly record struct LineCells(double Left, double NumberRight, double Gap, double Size);

    private readonly record struct LinePaint(Rgba Number, Rgba Secondary, Rgba? Shadow, double Sigma, OrbTextClip Clip)
    {
        public LinePaint Faded(double alpha) =>
            this with { Number = Number.MultiplyAlpha(alpha), Secondary = Secondary.MultiplyAlpha(alpha), Shadow = Shadow?.MultiplyAlpha(alpha) };

        /// <summary>The numbers in <paramref name="number"/> at <paramref name="alpha"/>, the shadow faded with them (a crossfade's side).</summary>
        public LinePaint Inked(Rgba number, double alpha) =>
            this with { Number = number.MultiplyAlpha(alpha), Shadow = alpha < 1 ? Shadow?.MultiplyAlpha(alpha) : Shadow };
    }
}
