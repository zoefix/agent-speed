using System.Globalization;
using System.Numerics;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace AgentSpeed.Boards.Layers;

/// <summary>
/// 霓虹座艙 (<c>spec/prototypes/board/board-neon.html</c>): a night cockpit in the LED orb's language: digits lit like LEDs in
/// the heat color, a 24-segment LED bar with today's peak marked, glowing badges and a plasma tint on the leader.
/// </summary>
internal sealed class NeonBoardPainter : BoardStylePainter
{
    private const float ValueColumn = 52;

    private static readonly BoardTextStyle Rank = new(BoardFont.Mono, 12, FontWeight.Bold);
    private static readonly BoardTextStyle Name = new(BoardFont.Ui, 12.5f, (FontWeight)650);
    private static readonly BoardTextStyle SubName = new(BoardFont.Ui, 11.5f, (FontWeight)540);
    private static readonly BoardTextStyle Detail = new(BoardFont.Mono, 10, FontWeight.Medium, 0.02f);
    private static readonly BoardTextStyle More = new(BoardFont.Mono, 10, FontWeight.Medium, 0.03f);
    private static readonly BoardTextStyle Value = new(BoardFont.Mono, 13, FontWeight.Bold, -0.02f);
    private static readonly BoardTextStyle SubValue = new(BoardFont.Mono, 11, FontWeight.Bold, -0.02f);
    private static readonly BoardTextStyle Unit = new(BoardFont.Mono, 10.5f, FontWeight.Bold, 0.14f);
    private static readonly BoardTextStyle Title = new(BoardFont.Ui, 12, FontWeight.Bold, 0.06f);
    private static readonly BoardTextStyle Counts = new(BoardFont.Mono, 10.5f, FontWeight.SemiBold, 0.06f);
    private static readonly BoardTextStyle Foot = new(BoardFont.Mono, 10, FontWeight.SemiBold, 0.06f);
    private static readonly BoardTextStyle FootValue = new(BoardFont.Mono, 10, FontWeight.Bold, 0.06f);
    private static readonly BoardTextStyle Chip = new(BoardFont.Mono, 10, FontWeight.Bold);
    private static readonly BoardTextStyle PeakChip = new(BoardFont.Mono, 9.5f, FontWeight.Bold, 0.16f);
    private static readonly Rgba Gold = Rgba.FromHex("#FACC15");
    private static readonly Rgba Mint = Rgba.FromHex("#34D399");
    private static readonly Rgba Plasma = new(232 / 255.0, 121 / 255.0, 249 / 255.0);
    private static readonly Rgba Lit = Rgba.FromHex("#FFFDF5");
    private static readonly Rgba Peak = Rgba.FromHex("#FFF7D6");

    public NeonBoardPainter(Localizer localizer, BoardText text)
        : base(BoardStyle.Neon, localizer, text)
    {
    }

    protected override float ValueWidth(BoardSize size) => ValueColumn;

    protected override BoardCapsuleLook CapsuleLook(BoardScene scene, bool sub) =>
        new(sub ? 8 : 9, 6, 5, 1, new BoardTextStyle(BoardFont.Mono, 10, FontWeight.Bold, 0.04f), true, 6, 3, scene.Size == BoardSize.S,
            scene.Palette, ConfiguredMark);

    protected override BoardTextStyle NumberStyle(BoardScene scene) => new(BoardFont.Mono, (float)scene.Metrics.ValueSize, FontWeight.Bold);

    protected override BoardTextStyle ValueStyle(BoardValueSet set, bool dim) =>
        (set == BoardValueSet.Sub ? SubValue : Value) with { Weight = dim ? FontWeight.Medium : FontWeight.Bold };

    protected override bool HasValueLayer(in BoardRowContent row) => row.Lead && !row.Palette.HighContrast;

    protected override BoardTextStyle ReadoutStyle(BoardScene scene, BoardReadoutFont font)
    {
        var spec = BoardGaugeSpec.For(BoardStyle.Neon, scene.Size);
        return font switch
        {
            BoardReadoutFont.MainLabel => new(BoardFont.Ui, (float)spec.LabelSize, FontWeight.SemiBold, (float)spec.LabelTracking),
            BoardReadoutFont.TableLabel or BoardReadoutFont.TabLabel => new(BoardFont.Ui, (float)spec.TableLabelSize, FontWeight.SemiBold, 0.06f),
            BoardReadoutFont.Unit => new(BoardFont.Mono, (float)spec.UnitSize, FontWeight.SemiBold),
            BoardReadoutFont.AtLeast => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.AtLeastScale), FontWeight.Normal),
            BoardReadoutFont.Tag => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.TagScale), FontWeight.SemiBold),
            BoardReadoutFont.GaugeMain => new(BoardFont.Mono, (float)spec.MainSize, FontWeight.Bold),
            BoardReadoutFont.GaugeTable => new(BoardFont.Mono, (float)spec.TableSize, FontWeight.Bold),
            BoardReadoutFont.IntelGroup or BoardReadoutFont.IntelGroupRest => new(BoardFont.Mono, 11, FontWeight.SemiBold),
            BoardReadoutFont.IntelSub or BoardReadoutFont.IntelSubRest => new(BoardFont.Mono, 10, FontWeight.SemiBold),
            BoardReadoutFont.Captions => new(BoardFont.Mono, 8.5f, FontWeight.SemiBold, 0.12f),
            _ => throw new ArgumentOutOfRangeException(nameof(font)),
        };
    }

    // On dark the lit digits carry the LED glow (性能: 4 pt and 12 pt; the table and the rows' 性能: 6 pt).
    protected override (Rgba Color, (Rgba Color, float Radius)[] Glows)? Baked(BoardScene scene, BoardValueSet set)
    {
        if (!scene.Dark || scene.Palette.HighContrast)
        {
            return null;
        }

        var led = Rgba.FromHex("#EAF2FF");
        var soft = new Rgba(140 / 255.0, 180 / 255.0, 1, 0.45);
        return set switch
        {
            BoardValueSet.GaugeMain => (led, [(new Rgba(120 / 255.0, 160 / 255.0, 1, 0.40), 12f), (new Rgba(150 / 255.0, 190 / 255.0, 1, 0.75), 4f)]),
            BoardValueSet.GaugeTable or BoardValueSet.IntelGroup or BoardValueSet.IntelSub => (led, [(soft, 6f)]),
            _ => null,
        };
    }

    // The LED bar shows the heat and today's peak, not the history.
    protected override int HistoryKey(IReadOnlyList<double> history) => 0;

    protected override string GlyphKey(BoardScene scene) => HeatKey(GlyphHeat(scene.Tick?.Heat ?? 0)).ToString(CultureInfo.InvariantCulture);

    // The LED bar's lit segments and today's peak mark.
    protected override int HistoryLookKey(BoardTick tick) =>
        (int)Math.Round(Math.Clamp(tick.Heat, 0, 1) * BoardHeroLayout.Segments) * 64 + HeatKey(HeatMath.Heat(tick.PeakToday, tick.Redline));

    protected override bool Shimmers => true;

    // Generating: the 6 pt dot with its glow (box-shadow 0 0 6px, 0 0 12px of the source); the scene breathes it.
    protected override void PaintBadge(ID2D1DeviceContext context, BoardPalette palette, AgentPhase phase, Rgba source, bool sub, float width,
        float height)
    {
        if (phase != AgentPhase.Generating)
        {
            base.PaintBadge(context, palette, phase, source, sub, width, height);
            return;
        }

        if (!palette.HighContrast)
        {
            BoardPaint.Glow(context, source, 12, c => Dot(c, width / 2, height / 2, 3, Rgba.White));
        }

        Dot(context, width / 2, height / 2, 3, source);
    }

    protected override void PaintGroup(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var mid = height / 2;
        if (row.Lead)
        {
            LeadTint(context, row, 0, height);
        }

        if (row.Entry.TwoLine)
        {
            RankText(context, row, CenterOnBaseline(Rank, (float)BoardScene.FirstBaseline));
            var first = DrawCapsule(context, row, RowCapsuleRight(row, width), 12, 16);
            DrawTwoLines(context, row, Name, palette.Ink, Detail, palette.Ink3, 48, first - 8);
            return;
        }

        RankText(context, row, mid);
        var right = DrawCapsule(context, row, RowCapsuleRight(row, width), mid, 18);
        DrawName(context, row.Name, Name, palette.Ink, row.Detail, Detail, palette.Ink3, 48, right - 8, mid);
    }

    protected override void PaintSub(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var mid = height / 2;
        Elbow(context, 50, mid, palette.Line);
        var right = DrawCapsule(context, row, RowCapsuleRight(row, width), mid, 16);
        DrawName(context, row.Name, SubName, palette.Ink2, string.Empty, Detail, palette.Ink3, 60, right - 8, mid);
    }

    protected override void PaintMore(ID2D1DeviceContext context, in BoardRowContent row, string text, float width, float height)
    {
        var mid = height / 2;
        var glow = row.Palette.HighContrast ? 0 : 4;
        var x = 60 + Stack(context, 60, mid, 5, 1, SourceColor(row.Source), default, glow) + 6;
        using var brush = BoardPaint.Brush(context, row.Palette.Ink3);
        Text.Draw(context, text, More, x, mid, brush, width - 6 - x);
    }

    protected override void PaintValue(ID2D1DeviceContext context, in BoardRowContent row, float width, float height) =>
        LeadTint(context, row, width - (float)row.Scene.Metrics.ListWidth, height);

    protected override BoardTextStyle UnitStyle => Unit;

    protected override void PaintHero(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height)
    {
        var palette = scene.Palette;
        var hero = scene.Hero;
        var soft = HeatPalette.Sample(GlyphHeat(tick.Heat)).WithAlpha(0.55);
        var glows = !palette.HighContrast;
        var tone = glows ? HeroHeat(tick) : palette.Ink;
        var meta = (float)hero.MetaTop + 6;
        var x = 0f;
        var limit = scene.Metrics.Gauge ? HeroLimit(scene, tick) : width;
        if (!string.IsNullOrEmpty(tick.Title))
        {
            using var title = Text.Layout(tick.Title, Title, Math.Min(width / 2, limit));
            var top = BoardText.Top(title, meta);
            if (glows)
            {
                BoardPaint.Glow(context, soft, 10, c => DrawLayout(c, title, 0, top, Rgba.White));
            }

            DrawLayout(context, title, 0, top, tone);
            x = Math.Min(title.Metrics.WidthIncludingTrailingWhitespace, Math.Min(width / 2, limit)) + 8;
        }

        using var ink3 = BoardPaint.Brush(context, palette.Ink3);
        Text.Draw(context, "· " + Loc.Format("agents.context", tick.GeneratingCount, tick.ToolsCount), Counts, x, meta, ink3, limit - x);
    }

    protected override void PaintUnit(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float x)
    {
        var glows = !scene.Palette.HighContrast;
        using var unit = Text.Layout(Loc["unit.tokS"], Unit);
        var top = NumberBaseline(scene) - unit.LineMetrics[0].Baseline;
        if (glows)
        {
            BoardPaint.Glow(context, HeatPalette.Sample(GlyphHeat(tick.Heat)).WithAlpha(0.55), 8, c => DrawLayout(c, unit, x, top, Rgba.White));
        }

        DrawLayout(context, unit, x, top, glows ? HeroHeat(tick) : scene.Palette.Ink);
    }

    protected override void PaintHistory(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, BoardBox box) =>
        Led(context, tick, box, scene.Palette);

    protected override void PaintFooter(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height)
    {
        var palette = scene.Palette;
        var mid = height / 2;
        DrawLabeled(context, Loc["boards.today"], tick.TodayText, " tok", Foot, palette.Ink3, FootValue, palette.Ink2, 0, mid, -1);
        var compact = scene.Size == BoardSize.S;
        if (tick.CostText is { } cost)
        {
            DrawLabeled(context, "%@", cost, string.Empty, Foot, palette.Ink3, FootValue, palette.Ink2, compact ? width : width / 2, mid,
                compact ? 1 : 0);
        }

        if (!compact)
        {
            DrawLabeled(context, Loc["boards.peak"], tick.PeakText, string.Empty, Foot, palette.Ink3, FootValue, palette.Ink2, width, mid, 1);
        }
    }

    protected override BoardLayerSpec? DescribeBackdrop(BoardScene scene, string theme) =>
        new BoardLayerSpec(scene.Metrics.Side, scene.Metrics.Side, string.Join('|', theme, "bd"));

    // The deck's 22 pt grid: 1 pt lines from the left and from the bottom.
    protected override void PaintBackdrop(ID2D1DeviceContext context, BoardScene scene, float width, float height)
    {
        if (scene.Palette.HighContrast)
        {
            return;
        }

        using var grid = BoardPaint.Brush(context, scene.Palette.Pattern);
        for (var x = 0f; x < width; x += 22)
        {
            context.FillRectangle(new RawRectF(x, 0, x + 1, height), grid);
        }

        for (var y = height; y > 0; y -= 22)
        {
            context.FillRectangle(new RawRectF(0, y - 1, width, y), grid);
        }
    }

    protected override void PaintGlyph(ID2D1DeviceContext context, BoardScene scene, char glyph, float width, float height)
    {
        var palette = scene.Palette;
        var heat = GlyphHeat(scene.Tick?.Heat ?? 0);
        if (palette.HighContrast)
        {
            DrawGlyph(context, scene, glyph, palette.Ink, null);
            return;
        }

        if (!palette.Dark)
        {
            DrawGlyph(context, scene, glyph, palette.Ink, (c, draw) => BoardPaint.Glow(c, HeatPalette.Sample(heat).WithAlpha(0.55), 8, draw));
            return;
        }

        // drop-shadow(0 0 3px) drop-shadow(0 0 10px) drop-shadow(0 0 4 + 16h px), all in the heat color.
        var color = HeatPalette.Sample(Math.Max(0.06, heat));
        DrawGlyph(context, scene, glyph, Lit, (c, draw) =>
        {
            BoardPaint.Glow(c, color, (float)(4 + heat * 16), draw);
            BoardPaint.Glow(c, color, 10, draw);
            BoardPaint.Glow(c, color, 3, draw);
        });
    }

    protected override BoardLayerSpec? DescribeCallout(BoardScene scene, BoardCallout callout, string theme)
    {
        var pad = (float)BoardCallouts.ChipPad;
        return callout.Kind switch
        {
            BoardCalloutKind.Climb => new BoardLayerSpec(Text.Measure(callout.Text, Chip) + 10 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "c", callout.Text)),
            BoardCalloutKind.NewPeak => new BoardLayerSpec(Text.Measure(Loc["boards.newPeak"], PeakChip) + 12 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "p")),
            _ => null,
        };
    }

    protected override void PaintCallout(ID2D1DeviceContext context, BoardScene scene, BoardCallout callout, float width, float height)
    {
        var pad = (float)BoardCallouts.ChipPad;
        var w = width - 2 * pad;
        var h = height - 2 * pad;
        var peak = callout.Kind == BoardCalloutKind.NewPeak;
        var fill = peak ? Gold : Mint;
        if (!scene.Palette.HighContrast)
        {
            BoardPaint.Glow(context, fill.WithAlpha(peak ? 0.7 : 0.8), peak ? 16 : 12, c => BoardPaint.FillRound(c, pad, pad, w, h, 4, Rgba.White));
        }

        BoardPaint.FillRound(context, pad, pad, w, h, 4, fill);
        using var ink = BoardPaint.Brush(context, Rgba.FromHex(peak ? "#241A00" : "#021A10"));
        if (peak)
        {
            Text.Draw(context, Loc["boards.newPeak"], PeakChip, pad + 6, height / 2, ink);
        }
        else
        {
            Text.Draw(context, callout.Text, Chip, pad + 5, height / 2, ink);
        }
    }

    // The leader's plasma tint (100°, gold → magenta → clear over twice the row, the first half shown), drawn across the
    // whole row from x = left so the body and the value layers continue each other.
    private static void LeadTint(ID2D1DeviceContext context, in BoardRowContent row, float left, float height)
    {
        if (row.Palette.HighContrast)
        {
            return;
        }

        var full = (float)row.Scene.Metrics.ListWidth;
        using var tint = BoardPaint.Gradient(context, left, 0, full, height,
            [(0, Gold.WithAlpha(0.12)), (0.9f, Plasma.WithAlpha(0.07)), (1, Plasma.WithAlpha(0.058))]);
        context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(left, 0, left + full, height), 9, 9), tint);
    }

    private void RankText(ID2D1DeviceContext context, in BoardRowContent row, float mid)
    {
        var palette = row.Palette;
        var text = row.Entry.Rank.ToString(CultureInfo.InvariantCulture);
        var x = 14 - Text.Measure(text, Rank) / 2;
        if (!row.Lead || palette.HighContrast)
        {
            using var ink3 = BoardPaint.Brush(context, palette.HighContrast && row.Lead ? palette.Ink : palette.Ink3);
            Text.Draw(context, text, Rank, x, mid, ink3);
            return;
        }

        if (palette.Dark)
        {
            BoardPaint.Glow(context, Gold.WithAlpha(0.9), 8, c =>
            {
                using var white = BoardPaint.Brush(c, Rgba.White);
                Text.Draw(c, text, Rank, x, mid, white);
            });
        }

        using var gold = BoardPaint.Brush(context, Rgba.FromHex(palette.Dark ? "#FFE58A" : "#B07800"));
        Text.Draw(context, text, Rank, x, mid, gold);
    }

    // 24 segments lit to the heat in their own heat colors, today's peak marked when it is above the lit ones.
    private static void Led(ID2D1DeviceContext context, BoardTick tick, BoardBox box, BoardPalette palette)
    {
        const int segments = BoardHeroLayout.Segments;
        const float gap = 2;
        var lit = (int)Math.Round(Math.Clamp(tick.Heat, 0, 1) * segments);
        var peak = Math.Min(segments - 1, (int)Math.Round(HeatMath.Heat(tick.PeakToday, tick.Redline) * segments) - 1);
        var width = ((float)box.Width - (segments - 1) * gap) / segments;
        var left = (float)box.Left;
        var top = (float)box.Top;
        var height = (float)box.Height;
        var glows = !palette.HighContrast;

        void Segment(ID2D1DeviceContext c, int i, Rgba color) => BoardPaint.FillRound(c, left + i * (width + gap), top, width, height, 1.5f, color);

        if (glows && lit > 0)
        {
            BoardPaint.Bloom(context, 6, c =>
            {
                for (var i = 0; i < lit; i++)
                {
                    Segment(c, i, HeatPalette.Sample((i + 1) / (double)segments));
                }
            });
        }

        if (glows && peak >= lit)
        {
            BoardPaint.Glow(context, Peak, 8, c => Segment(c, peak, Rgba.White));
        }

        for (var i = 0; i < segments; i++)
        {
            Segment(context, i, i < lit ? HeatPalette.Sample((i + 1) / (double)segments) : i == peak ? Peak : palette.Unlit);
        }
    }

    private static void DrawLayout(ID2D1DeviceContext context, IDWriteTextLayout layout, float x, float y, Rgba color)
    {
        using var brush = BoardPaint.Brush(context, color);
        context.DrawTextLayout(new Vector2(x, y), layout, brush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
    }
}
