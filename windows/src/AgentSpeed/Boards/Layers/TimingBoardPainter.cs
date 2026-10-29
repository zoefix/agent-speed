using System.Globalization;
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
/// 計時塔 (<c>spec/prototypes/board/board-timing.html</c>): a broadcast timing tower on a hatched slab: position blocks, team
/// stripes in source colors, an interval column, the fastest agent's purple dot and 30 one-second bars.
/// </summary>
internal sealed class TimingBoardPainter : BoardStylePainter
{
    private const float Bleed = 18;
    private const float CapsuleHeight = 17;

    private static readonly BoardTextStyle Position = new(BoardFont.Mono, 12, FontWeight.ExtraBold);
    private static readonly BoardTextStyle Name = new(BoardFont.Display, 12.5f, FontWeight.Bold, 0.005f);
    private static readonly BoardTextStyle SubName = new(BoardFont.Ui, 11, FontWeight.SemiBold);
    private static readonly BoardTextStyle Detail = new(BoardFont.Mono, 10, FontWeight.SemiBold, 0.02f);
    private static readonly BoardTextStyle Gap = new(BoardFont.Mono, 10.5f, FontWeight.SemiBold);
    private static readonly BoardTextStyle LeaderGap = new(BoardFont.Mono, 9.5f, FontWeight.ExtraBold, 0.08f, true);
    private static readonly BoardTextStyle More = new(BoardFont.Mono, 10, FontWeight.SemiBold, 0.03f);
    private static readonly BoardTextStyle Value = new(BoardFont.Mono, 13, FontWeight.ExtraBold, -0.02f);
    private static readonly BoardTextStyle SubValue = new(BoardFont.Mono, 11, FontWeight.Bold, -0.02f);
    private static readonly BoardTextStyle Live = new(BoardFont.Display, 10, FontWeight.Bold, 0.14f, true);
    private static readonly BoardTextStyle Unit = new(BoardFont.Mono, 11, FontWeight.SemiBold, 0.04f, true);
    private static readonly BoardTextStyle Pill = new(BoardFont.Ui, 11, FontWeight.Bold, 0.04f);
    private static readonly BoardTextStyle Counts = new(BoardFont.Mono, 10.5f, FontWeight.SemiBold, 0.03f, true);
    private static readonly BoardTextStyle Foot = new(BoardFont.Mono, 10, FontWeight.Bold, 0.06f, true);
    private static readonly BoardTextStyle FootValue = new(BoardFont.Mono, 10, FontWeight.Bold, 0.06f);
    private static readonly BoardTextStyle Delta = new(BoardFont.Mono, 10, FontWeight.ExtraBold);
    private static readonly BoardTextStyle Banner = new(BoardFont.Display, 11, FontWeight.ExtraBold, 0.16f, true);
    private static readonly BoardTextStyle BannerName = new(BoardFont.Ui, 11, (FontWeight)750, 0.02f);
    private static readonly BoardTextStyle PeakChip = new(BoardFont.Mono, 9.5f, FontWeight.Bold, 0.16f, true);
    private static readonly Rgba Gold = Rgba.FromHex("#FACC15");
    private static readonly Rgba GoldLight = Rgba.FromHex("#FFF1A8");
    private static readonly Rgba GoldDeep = Rgba.FromHex("#D49B00");
    private static readonly Rgba GoldInk = Rgba.FromHex("#241A00");
    private static readonly Rgba Green = Rgba.FromHex("#22C55E");
    private static readonly Rgba GreenInk = Rgba.FromHex("#04130A");
    private static readonly Rgba Purple = Rgba.FromHex("#A855F7");
    private static readonly Rgba Red = Rgba.FromHex("#EF4444");

    public TimingBoardPainter(Localizer localizer, BoardText text)
        : base(BoardStyle.Timing, localizer, text)
    {
    }

    protected override float ValueBleed => Bleed;

    protected override float ValueWidth(BoardSize size) => size == BoardSize.S ? 48 : 50;

    // With the 性能 column the tower drops the capsule's word below L (meter and bolts stay; the word is in the tooltip).
    protected override BoardCapsuleLook CapsuleLook(BoardScene scene, bool sub) =>
        new(4, 5, 4, 1, new BoardTextStyle(BoardFont.Mono, 10, FontWeight.Bold, 0.02f, true), false, 3, 0,
            scene.Size == BoardSize.S || (scene.Metrics.IntelligenceColumn && scene.Size != BoardSize.L), scene.Palette);

    protected override BoardTextStyle ReadoutStyle(BoardScene scene, BoardReadoutFont font)
    {
        var spec = BoardGaugeSpec.For(BoardStyle.Timing, scene.Size);
        return font switch
        {
            BoardReadoutFont.MainLabel or BoardReadoutFont.TableLabel or BoardReadoutFont.TabLabel =>
                new(BoardFont.Ui, (float)spec.LabelSize, FontWeight.Bold, (float)spec.LabelTracking, true),
            BoardReadoutFont.Unit => new(BoardFont.Mono, (float)spec.UnitSize, FontWeight.Bold, 0, true),
            BoardReadoutFont.AtLeast => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.AtLeastScale), FontWeight.Normal),
            BoardReadoutFont.Tag => new(BoardFont.Ui, (float)(spec.TableSize * BoardGaugeSpec.TagScale), FontWeight.SemiBold),
            BoardReadoutFont.GaugeMain => new(BoardFont.Mono, (float)spec.MainSize, FontWeight.ExtraBold),
            BoardReadoutFont.GaugeTable => new(BoardFont.Mono, (float)spec.TableSize, FontWeight.ExtraBold),
            BoardReadoutFont.IntelGroup => new(BoardFont.Mono, 11, FontWeight.Bold),
            BoardReadoutFont.IntelSub => new(BoardFont.Mono, 10, FontWeight.Bold),
            BoardReadoutFont.IntelGroupRest => new(BoardFont.Mono, 11, FontWeight.SemiBold),
            BoardReadoutFont.IntelSubRest => new(BoardFont.Mono, 10, FontWeight.SemiBold),
            BoardReadoutFont.Captions => new(BoardFont.Mono, 8.5f, FontWeight.Bold, 0.1f, true),
            _ => throw new ArgumentOutOfRangeException(nameof(font)),
        };
    }

    protected override BoardTextStyle NumberStyle(BoardScene scene) => new(BoardFont.Mono, (float)scene.Metrics.ValueSize, FontWeight.ExtraBold);

    protected override BoardTextStyle ValueStyle(BoardValueSet set, bool dim) => set switch
    {
        BoardValueSet.Interval => Gap,
        BoardValueSet.Sub => dim ? SubValue with { Weight = FontWeight.SemiBold } : SubValue,
        _ => dim ? Value with { Weight = FontWeight.SemiBold } : Value,
    };

    protected override bool HasValueLayer(in BoardRowContent row) => row.Entry.Kind == BoardEntryKind.Group || row.Fastest;

    protected override string ValueKey(in BoardRowContent row) => row.Fastest ? "f" : string.Empty;

    // The bars show the last 30 s, a missing second as an empty bar.
    protected override int HistoryKey(IReadOnlyList<double> history)
    {
        var hash = new HashCode();
        for (var i = history.Count - BoardHeroLayout.Bars; i < history.Count; i++)
        {
            hash.Add(i >= 0 ? Math.Round(history[i], 1) : 0);
        }

        return hash.ToHashCode();
    }

    // The team stripe (timing has no phase badge); the scene fades a generating row's in and out (.gen .stripe).
    protected override void PaintBadge(ID2D1DeviceContext context, BoardPalette palette, AgentPhase phase, Rgba source, bool sub, float width,
        float height)
    {
        var (w, h, opacity) = sub ? (2f, 10f, 0.7) : (3f, height > 40 ? 24f : 18f, 1.0);
        Stripe(context, source, (width - w) / 2, height / 2, w, h, opacity, !palette.HighContrast);
    }

    protected override float CapsuleRight(in BoardRowContent row, float width) => width - (row.Compact ? 14 : 52);

    protected override void PaintGroup(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var mid = (height - 1) / 2;
        var two = row.Entry.TwoLine;
        BoardPaint.FillRound(context, 0, height - 1, width, 1, 0, palette.Line);
        // A two-line row's position block (26 tall) spans both lines.
        PositionBlock(context, row.Entry.Rank.ToString(CultureInfo.InvariantCulture), row.Lead, palette, 4, mid, two ? 26 : 22);
        var compact = row.Compact;
        var right = DrawCapsule(context, row, RowCapsuleRight(row, width), two ? 12 : mid, two ? 16 : CapsuleHeight);
        if (two)
        {
            DrawTwoLines(context, row, Name, palette.Ink, Detail, palette.Ink3, 45, right - 7);
        }
        else
        {
            DrawName(context, row.Name, Name, palette.Ink, row.Detail, Detail, palette.Ink3, 45, right - 7, mid);
        }

        if (compact || string.IsNullOrEmpty(row.Interval))
        {
            return;
        }

        // The leader's word; the scene composes the other rows' intervals from glyphs. In a two-line row it is a figure, its cap
        // height centred on the row's centre line like the intervals'.
        var scene = row.Scene;
        var edge = scene.Metrics.IntelligenceColumn ? (float)(scene.Metrics.ListWidth - scene.Columns.IntervalRight) : width - 7;
        var room = scene.Metrics.IntelligenceColumn ? (float)scene.Columns.Interval : 38;
        var centre = (float)BoardRowFigures.Centre(height);
        using var brush = BoardPaint.Brush(context, palette.HighContrast ? palette.Ink3 : Gold);
        Text.Draw(context, row.Interval, LeaderGap, edge, two ? CenterOnBaseline(LeaderGap, centre + Text.CapHeight(LeaderGap) / 2) : mid, brush, room,
            right: true);
    }

    protected override void PaintSub(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var mid = height / 2;
        BoardPaint.FillRound(context, 45, mid, 6, 1, 0, palette.Ink3.MultiplyAlpha(0.6));
        var right = DrawCapsule(context, row, RowCapsuleRight(row, width), mid, CapsuleHeight);
        DrawName(context, row.Name, SubName, palette.Ink2, string.Empty, Detail, palette.Ink3, 55, right - 7, mid);
    }

    protected override void PaintMore(ID2D1DeviceContext context, in BoardRowContent row, string text, float width, float height)
    {
        using var brush = BoardPaint.Brush(context, row.Palette.Ink3);
        Text.Draw(context, text, More, 55, height / 2, brush, width - 6 - 55);
    }

    protected override void PaintValue(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
        var palette = row.Palette;
        var group = row.Entry.Kind == BoardEntryKind.Group;
        // A two-line row's dot keeps to its value, centred on the row.
        var mid = row.Entry.TwoLine ? (float)BoardRowFigures.Centre(height) : group ? (height - 1) / 2 : height / 2;
        if (group)
        {
            BoardPaint.FillRound(context, Bleed, height - 1, width - Bleed, 1, 0, palette.Line);
        }

        if (!row.Fastest)
        {
            return;
        }

        // The fastest agent anywhere: a purple dot 9 pt before the value column, like a fastest lap; with the 性能 column it
        // keeps to the gap before the value.
        var scene = row.Scene;
        var cx = scene.Metrics.IntelligenceColumn ? width - 4 - (float)scene.Columns.Value - 3.5f : width - 4 - (row.Compact ? 44 : 46) - 6;
        if (!palette.HighContrast)
        {
            BoardPaint.Glow(context, Purple, 8, c => Dot(c, cx, mid, 3, Rgba.White));
        }

        Dot(context, cx, mid, 3, Purple);
    }

    protected override BoardTextStyle UnitStyle => Unit;

    protected override void PaintHero(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height)
    {
        var palette = scene.Palette;
        var hero = scene.Hero;
        var limit = HeroLimit(scene, tick);
        using var ink2 = BoardPaint.Brush(context, palette.Ink2);
        using var ink3 = BoardPaint.Brush(context, palette.Ink3);

        var dot = hero.LiveDot;
        Dot(context, (float)dot.CenterX, (float)dot.CenterY, 3.5f, tick.Generating ? Red : palette.Ink3);
        Text.Draw(context, Loc["boards.live"], Live, (float)dot.Right + 6, (float)dot.CenterY, ink2, limit - (float)dot.Right - 6);

        var meta = (float)hero.MetaTop;
        var x = 0f;
        if (!string.IsNullOrEmpty(tick.Title))
        {
            var pill = Math.Min(Text.Measure(tick.Title, Pill) + 14, limit);
            BoardPaint.FillRound(context, 0, meta, pill, 17, 5, palette.HighContrast ? palette.Ink : HeroHeat(tick, 0.1));
            using var pillInk = BoardPaint.Brush(context, palette.HighContrast ? palette.Panel : Rgba.FromHex("#0B0D12"));
            Text.Draw(context, tick.Title, Pill, 7, meta + 8.5f, pillInk, pill - 12);
            x = pill + 7;
        }

        // The meta line sits below the bars, so the counts may run under them (localized counts are longer than GEN · TOOL); beside
        // the sub-gauge they end before it.
        Text.Draw(context, Loc.Format("agents.context", tick.GeneratingCount, tick.ToolsCount), Counts, x, meta + 8.5f, ink3,
            (scene.Metrics.Gauge ? limit : width) - x);
    }

    protected override void PaintUnit(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float x)
    {
        using var unit = Text.Layout(Loc["unit.tokS"], Unit);
        using var ink3 = BoardPaint.Brush(context, scene.Palette.Ink3);
        context.DrawTextLayout(new Vector2(x, NumberBaseline(scene) - unit.LineMetrics[0].Baseline), unit, ink3);
    }

    protected override void PaintHistory(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, BoardBox box)
    {
        var color = scene.Palette.HighContrast ? scene.Palette.Ink : HeroHeat(tick, 0.1);
        if (scene.Metrics.Gauge)
        {
            Strip(context, tick, box, color);
            return;
        }

        Bars(context, tick, box, scene.Size == BoardSize.S ? 20 : BoardHeroLayout.Bars, color);
    }

    // Layout B's strip (DESIGN 2c, Live metrics: Hero): the 30 one-second bars, 3 wide, radius 1, the first at the left inset and the
    // last ending at the right; max(1.5, value / max(10, the 30's maximum) × the strip's height) tall, the heat color at 85 %.
    private static void Strip(ID2D1DeviceContext context, BoardTick tick, BoardBox box, Rgba color)
    {
        var history = tick.History;
        var max = 10.0;
        for (var i = Math.Max(0, history.Count - BoardHeroLayout.Bars); i < history.Count; i++)
        {
            max = Math.Max(max, history[i]);
        }

        using var brush = BoardPaint.Brush(context, color.MultiplyAlpha(0.85));
        var bar = (float)BoardHeroLayout.BarWidth;
        var step = ((float)box.Width - bar) / (BoardHeroLayout.Bars - 1);
        for (var i = 0; i < BoardHeroLayout.Bars; i++)
        {
            var at = history.Count - BoardHeroLayout.Bars + i;
            var value = at >= 0 ? history[at] : 0;
            var h = (float)Math.Max(1.5, value / max * box.Height);
            var left = (float)box.Left + i * step;
            context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(left, (float)box.Bottom - h, left + bar, (float)box.Bottom), 1, 1), brush);
        }
    }

    protected override void PaintFooter(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height)
    {
        var palette = scene.Palette;
        var mid = height / 2;
        DrawLabeled(context, Loc["boards.today"], tick.TodayText + " TOK", string.Empty, Foot, palette.Ink3, FootValue, palette.Ink2, 0, mid, -1);
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

    // The slab's 135° hatch (2 pt stripes every 7 pt) and the footer's slab and rule.
    protected override void PaintBackdrop(ID2D1DeviceContext context, BoardScene scene, float width, float height)
    {
        var palette = scene.Palette;
        if (!palette.HighContrast)
        {
            using var hatch = BoardPaint.Brush(context, palette.Pattern);
            var step = 7 * MathF.Sqrt(2);
            for (var c = MathF.Sqrt(2); c < width + height + step; c += step)
            {
                context.DrawLine(new Vector2(c, 0), new Vector2(0, c), hatch, 2);
            }
        }

        var top = (float)scene.Metrics.FooterTop;
        BoardPaint.FillRound(context, 0, top, width, height - top, 0, palette.Panel2);
        BoardPaint.FillRound(context, 0, top, width, 1, 0, palette.Line);
    }

    protected override void PaintGlyph(ID2D1DeviceContext context, BoardScene scene, char glyph, float width, float height) =>
        DrawGlyph(context, scene, glyph, scene.Palette.Ink, null);

    protected override BoardLayerSpec? DescribeCallout(BoardScene scene, BoardCallout callout, string theme)
    {
        var pad = (float)BoardCallouts.ChipPad;
        return callout.Kind switch
        {
            BoardCalloutKind.Climb => new BoardLayerSpec(Text.Measure(callout.Text, Delta) + 10 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "c", callout.Text)),
            BoardCalloutKind.Block => new BoardLayerSpec(24, 22, string.Join('|', theme, "k", callout.Text)),
            BoardCalloutKind.NewLeader => new BoardLayerSpec(scene.Metrics.Side, 22, string.Join('|', theme, "l", callout.Text)),
            BoardCalloutKind.NewPeak => new BoardLayerSpec(Text.Measure(Loc["boards.newPeak"], PeakChip) + 12 + 2 * pad, 16 + 2 * pad,
                string.Join('|', theme, "p")),
            _ => null,
        };
    }

    protected override void PaintCallout(ID2D1DeviceContext context, BoardScene scene, BoardCallout callout, float width, float height)
    {
        var pad = (float)BoardCallouts.ChipPad;
        switch (callout.Kind)
        {
            case BoardCalloutKind.Climb:
            {
                BoardPaint.FillRound(context, pad, pad, width - 2 * pad, height - 2 * pad, 4, Green);
                using var ink = BoardPaint.Brush(context, GreenInk);
                Text.Draw(context, callout.Text, Delta, pad + 5, height / 2, ink);
                break;
            }

            case BoardCalloutKind.Block:
            {
                // The position block flashing green as it takes its new place.
                BoardPaint.FillRound(context, 0, 0, width, height, 5, Green);
                using var ink = BoardPaint.Brush(context, GreenInk);
                Text.Draw(context, callout.Text, Position, (width - Text.Measure(callout.Text, Position)) / 2, height / 2, ink);
                break;
            }

            case BoardCalloutKind.NewLeader:
            {
                using (var fill = BoardPaint.Gradient(context, 0, 0, width, height,
                           [(0, Gold), (0.4f, GoldLight), (0.7f, Gold), (1, Gold.WithAlpha(0))]))
                {
                    context.FillRectangle(new RawRectF(0, 0, width, height), fill);
                }

                var ink = Rgba.FromHex("#120D00");
                DrawLabeled(context, Loc["boards.newLeader"], callout.Text, string.Empty, Banner, ink, BannerName, ink,
                    (float)scene.Metrics.Pad, height / 2, -1);
                break;
            }

            case BoardCalloutKind.NewPeak:
            {
                // The prototype has no timing chip; it takes the leader's gold block.
                var w = width - 2 * pad;
                var h = height - 2 * pad;
                BoardPaint.Glow(context, Gold.WithAlpha(0.35), 12, c => BoardPaint.FillRound(c, pad, pad, w, h, 4, Rgba.White));
                using (var fill = BoardPaint.Gradient(context, pad, pad, w, h, [(0, GoldLight), (0.45f, Gold), (1, GoldDeep)], 160))
                {
                    context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(pad, pad, pad + w, pad + h), 4, 4), fill);
                }

                using var ink = BoardPaint.Brush(context, GoldInk);
                Text.Draw(context, Loc["boards.newPeak"], PeakChip, pad + 6, height / 2, ink);
                break;
            }
        }
    }

    private void PositionBlock(ID2D1DeviceContext context, string rank, bool lead, BoardPalette palette, float x, float mid, float height = 22)
    {
        var top = mid - height / 2;
        Rgba ink;
        if (lead && !palette.HighContrast)
        {
            BoardPaint.Glow(context, Gold.WithAlpha(0.35), 12, c => BoardPaint.FillRound(c, x, top, 24, height, 5, Rgba.White));
            using var fill = BoardPaint.Gradient(context, x, top, 24, height, [(0, GoldLight), (0.45f, Gold), (1, GoldDeep)], 160);
            context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, top, x + 24, top + height), 5, 5), fill);
            ink = GoldInk;
        }
        else
        {
            BoardPaint.FillRound(context, x, top, 24, height, 5, palette.Block);
            if (lead)
            {
                BoardPaint.StrokeRound(context, x, top, 24, height, 5, palette.Ink, 1);
            }

            ink = palette.BlockInk;
        }

        using var brush = BoardPaint.Brush(context, ink);
        Text.Draw(context, rank, Position, x + (24 - Text.Measure(rank, Position)) / 2, mid, brush);
    }

    // The team stripe in the source color with its soft glow.
    private static void Stripe(ID2D1DeviceContext context, Rgba color, float x, float mid, float width, float height, double opacity, bool glows)
    {
        var top = mid - height / 2;
        if (glows)
        {
            BoardPaint.Glow(context, color.WithAlpha(0.6 * opacity), 6, c => BoardPaint.FillRound(c, x, top, width, height, 2, Rgba.White));
        }

        BoardPaint.FillRound(context, x, top, width, height, 2, color.MultiplyAlpha(opacity));
    }

    // The last seconds of the board's value as 3 pt bars on a 38 pt baseline, scaled to the last 30 s (at least 10 tok/s).
    private static void Bars(ID2D1DeviceContext context, BoardTick tick, BoardBox box, int count, Rgba color)
    {
        var history = tick.History;
        var max = 10.0;
        for (var i = Math.Max(0, history.Count - BoardHeroLayout.Bars); i < history.Count; i++)
        {
            max = Math.Max(max, history[i]);
        }

        using var brush = BoardPaint.Brush(context, color.MultiplyAlpha(0.85));
        var step = (float)(BoardHeroLayout.BarWidth + BoardHeroLayout.BarGap);
        for (var i = 0; i < count; i++)
        {
            var at = history.Count - count + i;
            var value = at >= 0 ? history[at] : 0;
            var h = (float)Math.Max(2, value / max * box.Height);
            var left = (float)box.Left + i * step;
            context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(left, (float)box.Bottom - h, left + (float)BoardHeroLayout.BarWidth,
                (float)box.Bottom), 1, 1), brush);
        }
    }
}
