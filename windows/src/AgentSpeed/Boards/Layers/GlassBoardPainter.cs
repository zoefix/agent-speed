using System.Numerics;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Localization;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace AgentSpeed.Boards.Layers;

/// <summary>
/// 流光玻璃 (<c>spec/prototypes/board/board-glass.html</c>): calm typography on tinted glass, a heat sparkline, gold for the
/// leader, mint chips for climbers.
/// </summary>
internal sealed class GlassBoardPainter : BoardStylePainter
{
    private static readonly BoardTextStyle Rank = new(BoardFont.Display, 12, FontWeight.SemiBold);
    private static readonly BoardTextStyle Name = new(BoardFont.Ui, 12.5f, FontWeight.SemiBold);
    private static readonly BoardTextStyle SubName = new(BoardFont.Ui, 11.5f, FontWeight.Medium);
    private static readonly BoardTextStyle Detail = new(BoardFont.Ui, 11, FontWeight.Normal);
    private static readonly BoardTextStyle More = new(BoardFont.Ui, 11, FontWeight.Normal);
    private static readonly BoardTextStyle Value = new(BoardFont.Display, 13, FontWeight.SemiBold);
    private static readonly BoardTextStyle SubValue = new(BoardFont.Display, 11.5f, FontWeight.SemiBold);
    private static readonly BoardTextStyle Unit = new(BoardFont.Ui, 13, FontWeight.Medium);
    private static readonly BoardTextStyle Title = new(BoardFont.Ui, 12, FontWeight.SemiBold, 0.03f);
    private static readonly BoardTextStyle Meta = new(BoardFont.Ui, 12, FontWeight.Normal);
    private static readonly BoardTextStyle Foot = new(BoardFont.Ui, 11, FontWeight.Normal);
    private static readonly BoardTextStyle FootValue = new(BoardFont.Ui, 11, FontWeight.SemiBold);
    private static readonly BoardTextStyle Chip = new(BoardFont.Display, 10, FontWeight.SemiBold);
    private static readonly BoardTextStyle PeakChip = new(BoardFont.Display, 10, FontWeight.Bold, 0.08f);
    private static readonly Rgba Mint = Rgba.FromHex("#34D399");

    public GlassBoardPainter(Localizer localizer, BoardText text)
        : base(BoardStyle.Glass, localizer, text)
    {
    }

    protected override float ValueWidth(BoardSize size) => 52;

    protected override BoardCapsuleLook CapsuleLook(BoardScene scene, bool sub) =>
        new(sub ? 8 : 9, sub ? 5 : 6, 5, 0.5f, new BoardTextStyle(BoardFont.Ui, 11, (FontWeight)560), false, 3, 2, scene.Size == BoardSize.S,
            scene.Palette, ConfiguredMark);

    protected override BoardTextStyle NumberStyle(BoardScene scene) => new(BoardFont.Display, (float)scene.Metrics.ValueSize, FontWeight.Bold);

    protected override BoardTextStyle ValueStyle(BoardValueSet set, bool dim) =>
        (set == BoardValueSet.Sub ? SubValue : Value) with { Weight = dim ? FontWeight.Medium : FontWeight.SemiBold };

    protected override BoardTextStyle ReadoutStyle(BoardScene scene, BoardReadoutFont font)
    {
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, scene.Size);
        return font switch
        {
            BoardReadoutFont.MainLabel => new(BoardFont.Ui, (float)spec.LabelSize, FontWeight.SemiBold, 0.1f),
            BoardReadoutFont.TableLabel or BoardReadoutFont.TabLabel =>
                new(BoardFont.Ui, (float)spec.TableLabelSize, FontWeight.SemiBold, scene.Size == BoardSize.S ? 0.02f : 0.04f),
            BoardReadoutFont.Unit => new(BoardFont.Ui, (float)spec.UnitSize, FontWeight.Medium),
            BoardReadoutFont.AtLeast => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.AtLeastScale), FontWeight.Normal),
            BoardReadoutFont.Tag => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.TagScale), FontWeight.SemiBold),
            BoardReadoutFont.GaugeMain => new(BoardFont.Display, (float)spec.MainSize, FontWeight.SemiBold),
            BoardReadoutFont.GaugeTable => new(BoardFont.Display, (float)spec.TableSize, FontWeight.SemiBold),
            BoardReadoutFont.IntelGroup => new(BoardFont.Display, 11, FontWeight.SemiBold),
            BoardReadoutFont.IntelSub => new(BoardFont.Display, 10.5f, FontWeight.SemiBold),
            BoardReadoutFont.IntelGroupRest => new(BoardFont.Display, 11, FontWeight.Medium),
            BoardReadoutFont.IntelSubRest => new(BoardFont.Display, 10.5f, FontWeight.Medium),
            BoardReadoutFont.Captions => new(BoardFont.Ui, 9, FontWeight.SemiBold, 0.06f),
            _ => throw new ArgumentOutOfRangeException(nameof(font)),
        };
    }

    protected override string GlyphKey(BoardScene scene) => HeatKey(GlyphHeat(scene.Tick?.Heat ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override bool Shimmers => true;

    // Generating: one of the three 3 pt dots (the scene places and bounces them).
    protected override void PaintBadge(ID2D1DeviceContext context, BoardPalette palette, AgentPhase phase, Rgba source, bool sub, float width,
        float height)
    {
        if (phase == AgentPhase.Generating)
        {
            Dot(context, width / 2, height / 2, 1.5f, source);
            return;
        }

        base.PaintBadge(context, palette, phase, source, sub, width, height);
    }

    protected override void PaintGroup(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var mid = height / 2;
        var full = (float)row.Scene.Metrics.ListWidth;
        if (row.Lead && !palette.HighContrast)
        {
            var gold = palette.Dark ? new Rgba(250 / 255.0, 204 / 255.0, 21 / 255.0) : new Rgba(233 / 255.0, 168 / 255.0, 0);
            using var lead = BoardPaint.Gradient(context, 0, 0, full, height,
                [(0, gold.WithAlpha(palette.Dark ? 0.11 : 0.12)), (0.55f, gold.WithAlpha(0.03)), (1, gold.WithAlpha(0))]);
            context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, width, height), 10, 10), lead);
        }

        if (row.Entry.TwoLine)
        {
            // Line 1 on baseline 16 (the capsule in the 16 pt band 4 under the top), the detail on line 2 (baseline 32).
            RankText(context, row, CenterOnBaseline(Rank, (float)BoardScene.FirstBaseline));
            var first = DrawCapsule(context, row, RowCapsuleRight(row, width), 12, 16);
            DrawTwoLines(context, row, Name, palette.Ink, Detail with { Size = 10.5f }, palette.Ink3, 48, first - 8);
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
        var x = 60 + Stack(context, 60, mid + 0.5f, 6, 2, SourceColor(row.Source), row.Palette.Panel.WithAlpha(1), 0) + 6;
        using var brush = BoardPaint.Brush(context, row.Palette.Ink3);
        Text.Draw(context, text, More, x, mid, brush, width - 6 - x);
    }

    protected override BoardTextStyle UnitStyle => Unit;

    protected override void PaintHero(ID2D1DeviceContext context, BoardScene scene, Presentation.Boards.Scene.BoardTick tick, float width,
        float height)
    {
        var palette = scene.Palette;
        var hero = scene.Hero;
        var meta = (float)hero.MetaTop + 8;
        using var titleBrush = BoardPaint.Brush(context, palette.HighContrast ? palette.Ink : HeroHeat(tick));
        using var metaBrush = BoardPaint.Brush(context, palette.Ink2);
        // The context truncates first, then the title.
        var limit = HeroLimit(scene, tick);
        var x = Text.Draw(context, tick.Title, Title, 0, meta, titleBrush, limit);
        x += string.IsNullOrEmpty(tick.Title) ? 0 : 6;
        Text.Draw(context, "· " + Loc.Format("agents.context", tick.GeneratingCount, tick.ToolsCount), Meta, x, meta, metaBrush, limit - x);
    }

    // The unit glows with the number (.value's drop shadow of 14 px × heat).
    protected override void PaintUnit(ID2D1DeviceContext context, BoardScene scene, Presentation.Boards.Scene.BoardTick tick, float x)
    {
        var palette = scene.Palette;
        var heat = GlyphHeat(tick.Heat);
        using var layout = Text.Layout(Loc["unit.tokS"], Unit);
        var top = NumberBaseline(scene) - layout.LineMetrics[0].Baseline;
        if (!palette.HighContrast && heat > 0.02)
        {
            BoardPaint.Glow(context, HeatPalette.Sample(heat).WithAlpha(0.55), (float)(heat * 14), c =>
            {
                using var white = BoardPaint.Brush(c, Rgba.White);
                c.DrawTextLayout(new Vector2(x, top), layout, white);
            });
        }

        using var ink2 = BoardPaint.Brush(context, palette.Ink2);
        context.DrawTextLayout(new Vector2(x, top), layout, ink2);
    }

    protected override void PaintHistory(ID2D1DeviceContext context, BoardScene scene, Presentation.Boards.Scene.BoardTick tick, BoardBox box)
    {
        var color = scene.Palette.HighContrast ? scene.Palette.Ink : HeroHeat(tick);
        if (scene.Metrics.Gauge)
        {
            Strip(context, tick, box, color);
            return;
        }

        Spark(context, tick, box, color);
    }

    // Layout B's strip (DESIGN 2c, Live metrics: Hero): the 60 s sparkline stretched from inset to inset, scaled to
    // max(10, the window's maximum) × 1.12 with 1.5 pt free above and below, a 1.25 pt line, the area 28 % → 0, no end dot.
    private static void Strip(ID2D1DeviceContext context, Presentation.Boards.Scene.BoardTick tick, BoardBox box, Rgba color)
    {
        var history = tick.History;
        var count = Math.Min(60, history.Count);
        if (count < 2)
        {
            return;
        }

        var start = history.Count - count;
        var max = 10.0;
        for (var i = start; i < history.Count; i++)
        {
            max = Math.Max(max, history[i]);
        }

        max *= 1.12;
        var points = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = new Vector2((float)(box.Left + i / (double)(count - 1) * box.Width),
                (float)(box.Bottom - 1.5 - history[start + i] / max * (box.Height - 3)));
        }

        using var factory = context.Factory;
        using var line = factory.CreatePathGeometry();
        using var area = factory.CreatePathGeometry();
        Trace(line, points, null);
        Trace(area, points, box);
        using (var fill = BoardPaint.Gradient(context, (float)box.Left, (float)box.Top, (float)box.Width, (float)box.Height,
                   [(0, color.WithAlpha(0.28)), (1, color.WithAlpha(0))], vertical: true))
        {
            context.FillGeometry(area, fill);
        }

        using var brush = BoardPaint.Brush(context, color);
        using var round = factory.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        context.DrawGeometry(line, brush, 1.25f, round);
    }

    protected override void PaintFooter(ID2D1DeviceContext context, BoardScene scene, Presentation.Boards.Scene.BoardTick tick, float width,
        float height)
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

    protected override void PaintGlyph(ID2D1DeviceContext context, BoardScene scene, char glyph, float width, float height)
    {
        var palette = scene.Palette;
        var heat = GlyphHeat(scene.Tick?.Heat ?? 0);
        DrawGlyph(context, scene, glyph, palette.Ink, palette.HighContrast || heat <= 0.02 ? null
            : (c, draw) => BoardPaint.Glow(c, HeatPalette.Sample(heat).WithAlpha(0.55), (float)(heat * 14), draw));
    }

    protected override BoardLayerSpec? DescribeCallout(BoardScene scene, BoardCallout callout, string theme)
    {
        var pad = (float)BoardCallouts.ChipPad;
        return callout.Kind switch
        {
            BoardCalloutKind.Climb => new BoardLayerSpec(Text.Measure(callout.Text, Chip) + 10 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "c", callout.Text)),
            BoardCalloutKind.NewPeak => new BoardLayerSpec(Text.Measure(Loc["boards.newPeak"], PeakChip) + 12 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "p", Loc["boards.newPeak"])),
            _ => null,
        };
    }

    protected override void PaintCallout(ID2D1DeviceContext context, BoardScene scene, BoardCallout callout, float width, float height)
    {
        var pad = (float)BoardCallouts.ChipPad;
        var w = width - 2 * pad;
        var h = height - 2 * pad;
        if (callout.Kind == BoardCalloutKind.Climb)
        {
            BoardPaint.FillRound(context, pad, pad, w, h, 6, Mint.WithAlpha(0.16));
            using var mint = BoardPaint.Brush(context, Mint);
            Text.Draw(context, callout.Text, Chip, pad + 5, pad + h / 2, mint);
            return;
        }

        var gold = HeatPalette.Gold;
        BoardPaint.Glow(context, gold.WithAlpha(0.45), 14, c => BoardPaint.FillRound(c, pad, pad, w, h, 6, Rgba.White));
        using (var fill = BoardPaint.Gradient(context, pad, pad, w, h, [(0, Rgba.FromHex("#FFE58A")), (1, gold)]))
        {
            context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(pad, pad, pad + w, pad + h), 6, 6), fill);
        }

        using var ink = BoardPaint.Brush(context, Rgba.FromHex("#241A00"));
        Text.Draw(context, Loc["boards.newPeak"], PeakChip, pad + 6, pad + h / 2, ink);
    }

    private void RankText(ID2D1DeviceContext context, in BoardRowContent row, float mid)
    {
        var text = row.Entry.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var width = Text.Measure(text, Rank);
        var x = 14 - width / 2;
        if (row.Lead && !row.Palette.HighContrast)
        {
            var (top, bottom) = row.Palette.Dark ? (Rgba.FromHex("#FFE58A"), Rgba.FromHex("#E9A800")) : (Rgba.FromHex("#E0A000"), Rgba.FromHex("#9A6500"));
            using var gold = BoardPaint.Gradient(context, x, mid - 7, width, 14, [(0, top), (1, bottom)], vertical: true);
            Text.Draw(context, text, Rank, x, mid, gold);
            return;
        }

        using var brush = BoardPaint.Brush(context, row.Palette.Ink3);
        Text.Draw(context, text, Rank, x, mid, brush);
    }

    // The 60 s sparkline (drawSpark): a smoothed line with an area fading down and a dot at the latest value.
    private static void Spark(ID2D1DeviceContext context, Presentation.Boards.Scene.BoardTick tick, BoardBox box, Rgba color)
    {
        var history = tick.History;
        var count = Math.Min(60, history.Count);
        if (count < 2)
        {
            return;
        }

        var start = history.Count - count;
        var max = 10.0;
        for (var i = start; i < history.Count; i++)
        {
            max = Math.Max(max, history[i]);
        }

        max *= 1.12;
        var points = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = new Vector2((float)(box.Left + i / (double)(count - 1) * box.Width),
                (float)(box.Top + box.Height - 2 - history[start + i] / max * (box.Height - 6)));
        }

        using var factory = context.Factory;
        using var line = factory.CreatePathGeometry();
        using var area = factory.CreatePathGeometry();
        Trace(line, points, null);
        Trace(area, points, box);
        using (var fill = BoardPaint.Gradient(context, (float)box.Left, (float)box.Top, (float)box.Width, (float)box.Height,
                   [(0, color.WithAlpha(0.35)), (1, color.WithAlpha(0))], vertical: true))
        {
            context.FillGeometry(area, fill);
        }

        using var brush = BoardPaint.Brush(context, color);
        using var round = factory.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        context.DrawGeometry(line, brush, 1.5f, round);
        var last = points[^1];
        context.FillEllipse(new Ellipse(last, 2.4f, 2.4f), brush);
    }

    // The prototype's path: quadratic segments through the midpoints, then a smooth end at the last point.
    private static void Trace(ID2D1PathGeometry path, Vector2[] points, BoardBox? close)
    {
        using var sink = path.Open();
        sink.BeginFigure(points[0], close is null ? FigureBegin.Hollow : FigureBegin.Filled);
        for (var i = 1; i < points.Length; i++)
        {
            var p0 = points[i - 1];
            var p1 = points[i];
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p0, Point2 = (p0 + p1) / 2 });
        }

        sink.AddLine(points[^1]);
        if (close is { } box)
        {
            sink.AddLine(new Vector2((float)box.Right, (float)box.Bottom));
            sink.AddLine(new Vector2((float)box.Left, (float)box.Bottom));
        }

        sink.EndFigure(close is null ? FigureEnd.Open : FigureEnd.Closed);
        sink.Close();
    }
}
