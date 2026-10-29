using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using KeyedLayers = AgentSpeed.Presentation.Tests.Boards.BoardGaugeSceneTests.KeyedLayers;

namespace AgentSpeed.Presentation.Tests.Boards;

/// <summary>Two-line group rows' figures (DESIGN 2c, Ranking rows: The second line): centred on the row, 2 pt larger, wider cells.</summary>
public class BoardRowFiguresTests
{
    private static readonly Localizer Zh = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Timing)]
    [InlineData(BoardStyle.Neon)]
    public void TwoLineFiguresAreTwoPointsLarger(BoardStyle style)
    {
        Assert.Equal(13, BoardRowColumns.IntelligenceSize(style, sub: false, twoLine: true));
        Assert.Equal(11, BoardRowColumns.IntelligenceSize(style, sub: false));
        Assert.Equal(BoardValueGlyphs.Size(style, sub: false) + 2, BoardValueGlyphs.Size(style, sub: false, twoLine: true));
        // Sub rows keep their sizes.
        Assert.Equal(BoardRowColumns.IntelligenceSize(style, sub: true), BoardRowColumns.IntelligenceSize(style, sub: true, twoLine: true));
        Assert.Equal(BoardValueGlyphs.Size(style, sub: true), BoardValueGlyphs.Size(style, sub: true, twoLine: true));
        Assert.Equal(-0.02 * 15, BoardValueGlyphs.Tracking(style, sub: false, twoLine: true), 9);
    }

    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M)]
    [InlineData(BoardStyle.Timing, BoardSize.L)]
    [InlineData(BoardStyle.Neon, BoardSize.M)]
    public void TheCellsWidenToTheMeasuredFiguresAndTheNameBlockGivesWay(BoardStyle style, BoardSize size)
    {
        var table = BoardRowColumns.For(style, size, BoardReadouts.All);
        var wide = BoardRowColumns.For(style, size, BoardReadouts.All, new BoardRowFigures(table.Intelligence + 1.3, table.Value + 2.2, 9, 10, 7));
        Assert.Equal(table.Intelligence + 2, wide.Intelligence, 9);
        Assert.Equal(table.Value + 3, wide.Value, 9);
        Assert.Equal(table.CapsuleRight + 5, wide.CapsuleRight, 9);
        // Figures that fit keep the table's widths; S has no two-line rows; with 性能 off there is no cell to widen.
        Assert.Equal(table, BoardRowColumns.For(style, size, BoardReadouts.All, new BoardRowFigures(10, 20, 9, 10, 7)));
        Assert.Equal(BoardRowColumns.For(style, BoardSize.S, BoardReadouts.All),
            BoardRowColumns.For(style, BoardSize.S, BoardReadouts.All, new BoardRowFigures(99, 99, 9, 10, 7)));
        Assert.Equal(0, BoardRowColumns.For(style, size, BoardReadouts.All with { Intelligence = false }, new BoardRowFigures(99, 0, 9, 10, 7)).Intelligence);
    }

    [Fact]
    public void AFigureIsMeasuredAsTheSceneComposesIt()
    {
        static double Advance(char c) => c switch { '.' => 3, 'k' => 7, _ => 8 };
        Assert.Equal(24 - 2 * 0.26, BoardRowFigures.Width("888", Advance, -0.26), 9);
        // 88.8k: three digits, the point and the suffix, four gaps.
        Assert.Equal(8 * 3 + 3 + 7 - 4 * 0.3, BoardRowFigures.WidestValue(Advance, -0.3), 9);
    }

    [Fact]
    public void TheSceneWidensItsColumnsWhenTheFiguresAreMeasured()
    {
        var scene = new BoardScene(BoardStyle.Neon, BoardSize.M, 3);
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        scene.Apply(feed.Step(BoardStyle.Neon, BoardSize.M), 0);
        var table = scene.Columns;
        scene.Figures = new BoardRowFigures(45, 50, 9, 10.4, 7.3);
        Assert.Equal((45.0, 50.0), (scene.Columns.Intelligence, scene.Columns.Value));
        scene.Figures = BoardRowFigures.Nominal;
        Assert.Equal(table, scene.Columns);
    }

    // Each figure's cap-height centre within 0.4 pt of the row's centre line (19 under the top at M, 20 at L): the two sizes share
    // one baseline at their caps' mean. Cap heights from 0.65 to 0.75 em cover the boards' fonts (Segoe UI Variable, Cascadia
    // Mono); the last case gives the 性能 and value weights slightly different shares.
    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M, 0.70, 0.70)]
    [InlineData(BoardStyle.Glass, BoardSize.L, 0.65, 0.65)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 0.75, 0.75)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 0.70, 0.70)]
    [InlineData(BoardStyle.Neon, BoardSize.M, 0.70, 0.70)]
    [InlineData(BoardStyle.Neon, BoardSize.L, 0.70, 0.71)]
    public void TheFiguresCentreOnTheRowWithinFourTenthsOfAPoint(BoardStyle style, BoardSize size, double intelligenceShare, double valueShare)
    {
        var figures = new BoardRowFigures(0, 0, intelligenceShare * 13, valueShare * BoardValueGlyphs.Size(style, false, true),
            valueShare * BoardRowFigures.IntervalSize);
        var (scene, layers, list) = Render(style, size, figures);
        var m = BoardCanvas.Margin;
        var rows = scene.Rows.Where(r => r.Entry.Kind == BoardEntryKind.Group).ToList();
        Assert.NotEmpty(rows);
        var checkedIntervals = 0;
        foreach (var row in rows)
        {
            var top = m + scene.Metrics.ListTop + row.Y;
            var height = scene.RowHeight(row.Entry);
            var centre = top + height / 2;
            Assert.Equal(size == BoardSize.M ? 19 : 20, height / 2);
            var value = Baselines(layers, list, top, height, BoardValueSet.Group);
            var intelligence = Baselines(layers, list, top, height, BoardValueSet.IntelGroup, BoardValueSet.IntelGroupRest);
            Assert.NotEmpty(value);
            Assert.NotEmpty(intelligence);
            var baseline = Assert.Single(value.Concat(intelligence).Distinct());
            Assert.InRange(baseline - figures.ValueCap / 2 - centre, -0.4, 0.4);
            Assert.InRange(baseline - figures.IntelligenceCap / 2 - centre, -0.4, 0.4);
            // Timing's interval keeps its own baseline: its cap centre exactly on the line.
            foreach (var interval in Baselines(layers, list, top, height, BoardValueSet.Interval))
            {
                Assert.Equal(centre, interval - figures.IntervalCap / 2, 3);
                checkedIntervals++;
            }
        }

        Assert.Equal(style == BoardStyle.Timing, checkedIntervals > 0);
        // The bound holds by construction: a quarter of the two caps' difference.
        Assert.True(Math.Abs(figures.Offset(38, true, figures.ValueCap)) <= 0.4);
        Assert.True(Math.Abs(figures.Offset(38, true, figures.IntelligenceCap)) <= 0.4);
    }

    [Fact]
    public void WithoutThe性能ColumnTheValueAloneIsCentred()
    {
        var figures = new BoardRowFigures(0, 0, 9.1, 10.5, 7.35);
        var (scene, layers, list) = Render(BoardStyle.Glass, BoardSize.M, figures, BoardReadouts.All with { Intelligence = false });
        var row = scene.Rows.First(r => r.Entry.Kind == BoardEntryKind.Group);
        var top = BoardCanvas.Margin + scene.Metrics.ListTop + row.Y;
        var baseline = Assert.Single(Baselines(layers, list, top, scene.RowHeight(row.Entry), BoardValueSet.Group).Distinct());
        Assert.Equal(top + 19, baseline - figures.ValueCap / 2, 3);
    }

    [Fact]
    public void SmallRowsAndSubRowsKeepTheirLines()
    {
        var figures = new BoardRowFigures(0, 0, 9.1, 10.5, 7.35);
        var (small, _, _) = Render(BoardStyle.Glass, BoardSize.S, figures);
        Assert.DoesNotContain(small.Rows, r => r.Entry.TwoLine);
        var (scene, layers, list) = Render(BoardStyle.Timing, BoardSize.L, figures);
        var sub = scene.Rows.FirstOrDefault(r => r.Entry.Kind == BoardEntryKind.Sub);
        Assert.NotNull(sub);
        var top = BoardCanvas.Margin + scene.Metrics.ListTop + sub!.Y;
        var height = scene.RowHeight(sub.Entry);
        // A sub row's value and 性能 sit on the sub row's own line, not on a figure baseline.
        var value = Baselines(layers, list, top, height, BoardValueSet.Sub);
        var intelligence = Baselines(layers, list, top, height, BoardValueSet.IntelSub, BoardValueSet.IntelSubRest);
        Assert.NotEmpty(value);
        Assert.Equal(value.Distinct(), intelligence.Distinct());
        Assert.DoesNotContain(value, b => Math.Abs(b - (top + figures.Baseline(height, true))) < 1e-3);
    }

    private static (BoardScene Scene, KeyedLayers Layers, BoardDrawList List) Render(BoardStyle style, BoardSize size, BoardRowFigures figures,
        BoardReadouts? readouts = null)
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = readouts ?? BoardReadouts.All };
        var scene = new BoardScene(style, size, 3);
        scene.Configure(style, size, new BoardEffectsPolicy(ReduceMotion: true));
        scene.Figures = figures;
        for (var i = 0; i < 40; i++)
        {
            scene.Apply(feed.Step(style, size), i * 0.5);
        }

        scene.Settle();
        var layers = new KeyedLayers();
        var list = new BoardDrawList();
        scene.Render(100, layers, list);
        return (scene, layers, list);
    }

    // The baselines of a set's glyphs drawn inside a row (the test layers put a glyph's baseline 14 under its padding).
    private static List<double> Baselines(KeyedLayers layers, BoardDrawList list, double top, double height, params BoardValueSet[] sets)
    {
        var found = new List<double>();
        foreach (var layer in list.Layers)
        {
            var key = layers.KeyOf(layer);
            if (!key.StartsWith("#v", StringComparison.Ordinal) || key.Length <= 3)
            {
                continue;
            }

            var set = (BoardValueSet)int.Parse(key[2..^1], System.Globalization.CultureInfo.InvariantCulture);
            var baseline = Math.Round(layer.Rect.Y + BoardValueGlyphs.PadOf(set) + 14, 3);
            if (sets.Contains(set) && baseline > top && baseline < top + height)
            {
                found.Add(baseline);
            }
        }

        return found;
    }
}
