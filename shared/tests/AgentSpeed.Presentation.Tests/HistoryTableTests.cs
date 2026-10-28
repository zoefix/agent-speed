using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistoryTableTests
{
    private static readonly DaySummary R1 = Summary(D(2026, 10, 17), requests: 5, output: 500, input: 1000, cached: 500,
        measuredOutput: 100, measuredMs: 1000, peak: 300, agents: 2, models: [new ModelUsage("b", 1, 1)])
        with { Cost = new HistoryCost(new CostTally(3_000_000), 5) };

    /// <summary>Nothing priced.</summary>
    private static readonly DaySummary R2 = Summary(D(2026, 10, 18), requests: 9, output: 100, input: 0, cached: 0,
        measuredMs: 0, peak: null, agents: 5) with { Cost = new HistoryCost(new CostTally(0, 9), 9) };

    /// <summary>Partly priced: sorts by its known cost.</summary>
    private static readonly DaySummary R3 = Summary(D(2026, 10, 19), requests: 5, output: 900, input: 1000, cached: 900,
        measuredOutput: 50, measuredMs: 1000, peak: 100, agents: 1, models: [new ModelUsage("A", 1, 1)])
        with { Cost = new HistoryCost(new CostTally(5_000_000, 1), 5) };

    /// <summary>Only an excluded subscription source.</summary>
    private static readonly DaySummary R4 = Summary(D(2026, 10, 20), requests: 1, output: 300, input: 1000, cached: 100,
        measuredOutput: 200, measuredMs: 1000, peak: null, agents: 3, models: [new ModelUsage("c", 1, 1)])
        with { Cost = new HistoryCost(null, 0, SourceSet.Of(SourceKind.ClaudeCode)) };

    private static readonly DaySummary[] Rows = [R1, R2, R3, R4];

    public static TheoryData<HistoryColumn, bool, int[]> SortCases => new()
    {
        { HistoryColumn.Date, true, [4, 3, 2, 1] },
        { HistoryColumn.Date, false, [1, 2, 3, 4] },
        { HistoryColumn.Requests, true, [2, 3, 1, 4] },
        { HistoryColumn.Requests, false, [4, 3, 1, 2] },
        { HistoryColumn.Output, true, [3, 1, 4, 2] },
        { HistoryColumn.Output, false, [2, 4, 1, 3] },
        { HistoryColumn.Input, true, [4, 3, 1, 2] },
        { HistoryColumn.Input, false, [2, 4, 3, 1] },
        { HistoryColumn.Cost, true, [3, 1, 4, 2] },
        { HistoryColumn.Cost, false, [1, 3, 4, 2] },
        { HistoryColumn.CacheHit, true, [3, 1, 4, 2] },
        { HistoryColumn.CacheHit, false, [4, 1, 3, 2] },
        { HistoryColumn.Speed, true, [4, 1, 3, 2] },
        { HistoryColumn.Speed, false, [3, 1, 4, 2] },
        { HistoryColumn.Peak, true, [1, 3, 4, 2] },
        { HistoryColumn.Peak, false, [3, 1, 4, 2] },
        { HistoryColumn.Agents, true, [2, 4, 1, 3] },
        { HistoryColumn.Agents, false, [3, 1, 4, 2] },
        { HistoryColumn.Models, false, [3, 1, 4, 2] },
        { HistoryColumn.Models, true, [4, 1, 3, 2] },
    };

    private static int Number(DaySummary row) => Array.IndexOf(Rows, row) + 1;

    [Theory]
    [MemberData(nameof(SortCases))]
    public void SortsEveryColumnWithMissingValuesLast(HistoryColumn column, bool descending, int[] expected)
    {
        Assert.Equal(expected, HistoryTable.Sort(Rows, column, descending).Select(Number));
        Assert.Equal(expected, HistoryTable.Sort(Enumerable.Reverse(Rows), column, descending).Select(Number));
    }

    [Fact]
    public void TiesKeepTheNewestDayFirstInBothDirections()
    {
        DaySummary[] rows =
        [
            Summary(D(2026, 10, 1), requests: 3, models: [new ModelUsage("x", 1, 1)]),
            Summary(D(2026, 10, 3), requests: 3, models: [new ModelUsage("X", 1, 1)]),
            Summary(D(2026, 10, 2), requests: 3, models: [new ModelUsage("x", 1, 1)]),
        ];

        foreach (var column in new[] { HistoryColumn.Requests, HistoryColumn.Models })
        {
            Assert.Equal([D(2026, 10, 3), D(2026, 10, 2), D(2026, 10, 1)], HistoryTable.Sort(rows, column, true).Select(r => r.Day));
            Assert.Equal([D(2026, 10, 3), D(2026, 10, 2), D(2026, 10, 1)], HistoryTable.Sort(rows, column, false).Select(r => r.Day));
        }
    }

    [Fact]
    public void SortingNothingIsEmpty()
    {
        foreach (var column in HistoryTable.Columns)
        {
            Assert.Empty(HistoryTable.Sort([], column, true));
        }
    }

    [Fact]
    public void ColumnsAndTheirDefaults()
    {
        Assert.Equal(
            [
                HistoryColumn.Date, HistoryColumn.Requests, HistoryColumn.Output, HistoryColumn.Input, HistoryColumn.Cost,
                HistoryColumn.Traffic, HistoryColumn.CacheHit, HistoryColumn.Speed, HistoryColumn.Peak, HistoryColumn.Agents,
                HistoryColumn.Models,
            ],
            HistoryTable.Columns);
        Assert.All(HistoryTable.Columns.Where(c => c != HistoryColumn.Models), c => Assert.True(HistoryTable.DefaultDescending(c)));
        Assert.False(HistoryTable.DefaultDescending(HistoryColumn.Models));
        Assert.False(HistoryTable.IsNumeric(HistoryColumn.Date));
        Assert.False(HistoryTable.IsNumeric(HistoryColumn.Models));
        Assert.All(HistoryTable.Columns.Skip(1).SkipLast(1), c => Assert.True(HistoryTable.IsNumeric(c)));
    }

    [Fact]
    public void EveryHeaderIsLocalized()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = For(language);
            Assert.All(HistoryTable.Columns, c => Assert.NotEqual(HistoryTable.HeaderKey(c), loc[HistoryTable.HeaderKey(c)]));
        }

        Assert.Equal("日期\t請求\t輸出\t輸入（含快取）\t費用\t網路\t快取命中\t平均速度\t峰值\tAgent\t主要模型", HistoryTable.HeaderText(For(LanguageCodes.ZhHant)));
        Assert.Equal("Date\tRequests\tOutput\tInput (incl. cache)\tCost\tNetwork\tCache hit\tAvg speed\tPeak\tAgents\tTop models",
            HistoryTable.HeaderText(For(LanguageCodes.En)));
    }

    [Fact]
    public void CellsFormatEachColumn()
    {
        var loc = For(LanguageCodes.ZhHant);
        var money = Money(loc);
        var row = Summary(Today, requests: 12_345, output: 98_765, input: 2_000_000, cached: 1_840_000, measuredOutput: 85_000,
            measuredMs: 1_000_000, peak: 412, agents: 4,
            models: [new ModelUsage("opus", 3, 50_000), new ModelUsage("gpt", 2, 40_000), new ModelUsage("flash", 1, 8_765)])
            with { Cost = new HistoryCost(new CostTally(4_200_000), 12_345) };

        Assert.Equal("今天", HistoryTable.Cell(row, HistoryColumn.Date, Today, loc, money));
        Assert.Equal("12,345", HistoryTable.Cell(row, HistoryColumn.Requests, Today, loc, money));
        Assert.Equal("98.8k", HistoryTable.Cell(row, HistoryColumn.Output, Today, loc, money));
        Assert.Equal("2.00M", HistoryTable.Cell(row, HistoryColumn.Input, Today, loc, money));
        Assert.Equal("92%", HistoryTable.Cell(row, HistoryColumn.CacheHit, Today, loc, money));
        Assert.Equal("85 tok/s", HistoryTable.Cell(row, HistoryColumn.Speed, Today, loc, money));
        Assert.Equal("412 tok/s", HistoryTable.Cell(row, HistoryColumn.Peak, Today, loc, money));
        Assert.Equal("4", HistoryTable.Cell(row, HistoryColumn.Agents, Today, loc, money));
        Assert.Equal("opus, gpt +1", HistoryTable.Cell(row, HistoryColumn.Models, Today, loc, money));

        Assert.Equal("US$4.20", HistoryTable.Cell(row, HistoryColumn.Cost, Today, loc, money));

        Assert.Equal("今天\t12,345\t98.8k\t2.00M\tUS$4.20\t–\t92%\t85 tok/s\t412 tok/s\t4\topus, gpt +1", HistoryTable.RowText(row, Today, loc, money));
    }

    [Fact]
    public void CellsWithoutValues()
    {
        var loc = For(LanguageCodes.En);
        var money = Money(loc);
        Assert.Equal("—", HistoryTable.Cell(R2, HistoryColumn.CacheHit, Today, loc, money));
        Assert.Equal("No measurement", HistoryTable.Cell(R2, HistoryColumn.Speed, Today, loc, money));
        Assert.Equal("—", HistoryTable.Cell(R2, HistoryColumn.Peak, Today, loc, money));
        Assert.Equal("—", HistoryTable.Cell(R2, HistoryColumn.Models, Today, loc, money));
        Assert.Equal("Oct 18, Sun", HistoryTable.Cell(R2, HistoryColumn.Date, Today, loc, money));
        Assert.Equal("–", HistoryTable.Cell(R2, HistoryColumn.Cost, Today, loc, money));
        Assert.Equal(11, HistoryTable.RowText(R2, Today, loc, money).Split('\t').Length);
        Assert.Equal("Oct 18, Sun\t–", HistoryTable.RowText(R2, Today, loc, money, [HistoryColumn.Date, HistoryColumn.Cost]));
    }

    [Fact]
    public void CostCellsReadAtLeastADashOrTheSubscriptionTag()
    {
        var loc = For(LanguageCodes.En);
        var money = Money(loc);
        Assert.Equal("≥$2.25", HistoryTable.Cell(DayOf(CostDays(), D(2026, 10, 21)), HistoryColumn.Cost, Today, loc, money));
        Assert.Equal("–", HistoryTable.Cell(DayOf(CostDays(), D(2026, 10, 19)), HistoryColumn.Cost, Today, loc, money));
        Assert.Equal("–", HistoryTable.Cell(DayOf(CostDays(), D(2026, 10, 18)), HistoryColumn.Cost, Today, loc, money));
        Assert.Equal(loc["cost.subscription.tag"],
            HistoryTable.Cell(DayOf(CostDays(SourceSet.Of(SourceKind.ClaudeCode)), D(2026, 10, 18)), HistoryColumn.Cost, Today, loc, money));
        Assert.Equal("NT$130",
            HistoryTable.Cell(DayOf(CostDays(SourceSet.Of(SourceKind.ClaudeCode)), D(2026, 10, 20)), HistoryColumn.Cost, Today, loc, Twd(loc)));
    }

    [Fact]
    public void CostSortsByKnownCostWithTheRestLast()
    {
        var excluded = CostDays(SourceSet.Of(SourceKind.ClaudeCode));
        Assert.Equal([D(2026, 10, 20), Today, D(2026, 10, 19), D(2026, 10, 18)],
            HistoryTable.Sort(excluded, HistoryColumn.Cost, true).Select(r => r.Day));
        Assert.Equal([Today, D(2026, 10, 20), D(2026, 10, 19), D(2026, 10, 18)],
            HistoryTable.Sort(excluded, HistoryColumn.Cost, false).Select(r => r.Day));
        Assert.All(HistoryTable.Sort(excluded, HistoryColumn.Cost, true).TakeLast(2), r => Assert.Null(r.Cost.SortValue));

        var all = HistoryTable.Sort(CostDays(), HistoryColumn.Cost, true);
        Assert.Equal([D(2026, 10, 20), Today, D(2026, 10, 19), D(2026, 10, 18)], all.Select(r => r.Day));
        Assert.Equal([HistoryCostKind.Unknown, HistoryCostKind.Unavailable], all.TakeLast(2).Select(r => r.Cost.Kind));
    }

    [Fact]
    public void TheCostCellReadsWithTheCurrencyName()
    {
        var loc = For(LanguageCodes.ZhHant);
        var day = DayOf(CostDays(), Today);
        Assert.Equal("至少 73 新臺幣", HistoryTable.AccessibleCell(day, HistoryColumn.Cost, Today, loc, Twd(loc)));
        Assert.Equal(loc["cost.unknown"], HistoryTable.AccessibleCell(DayOf(CostDays(), D(2026, 10, 19)), HistoryColumn.Cost, Today, loc, Twd(loc)));
        foreach (var column in HistoryTable.Columns.Where(c => c is not (HistoryColumn.Cost or HistoryColumn.Traffic)))
        {
            Assert.Equal(HistoryTable.Cell(day, column, Today, loc, Twd(loc)), HistoryTable.AccessibleCell(day, column, Today, loc, Twd(loc)));
        }
    }

    [Fact]
    public void TheCostTooltipSaysWhatTheCellCannot()
    {
        var loc = For(LanguageCodes.ZhHant);
        var twd = Twd(loc);
        var claude = SourceSet.Of(SourceKind.ClaudeCode);
        Assert.Equal("≥NT$178（US$5.50）" + Environment.NewLine + "1 筆請求價格未知",
            HistoryTable.CostTooltip(DayOf(CostDays(), D(2026, 10, 20)).Cost, twd, loc));
        Assert.Equal("NT$130（US$4.00）", HistoryTable.CostTooltip(DayOf(CostDays(claude), D(2026, 10, 20)).Cost, twd, loc));
        Assert.Equal("2 筆請求價格未知", HistoryTable.CostTooltip(DayOf(CostDays(), D(2026, 10, 19)).Cost, twd, loc));
        Assert.Equal(loc["history.costUnavailable"], HistoryTable.CostTooltip(DayOf(CostDays(), D(2026, 10, 18)).Cost, twd, loc));
        Assert.Equal("未計入訂閱來源：Claude Code", HistoryTable.CostTooltip(DayOf(CostDays(claude), D(2026, 10, 18)).Cost, twd, loc));
    }

    // Fit: content widths without padding, 8 DIP padding each side, at least 4.
    private static readonly ColumnSpec[] Content = [new(50, 40), new(60, 30), new(100, 80, 1)];

    [Fact]
    public void FitKeepsTheFullPaddingWhileEveryColumnFitsAtItsMinimum()
    {
        var roomy = HistoryTable.Fit(Content, 400, 8, 4);
        Assert.Equal(8, roomy.Padding);
        Assert.Equal([66.0, 76.0, 258.0], roomy.Spans.Select(s => s.Width));
        Assert.Equal(400, roomy.Spans[^1].Right, 9);

        var shrunk = HistoryTable.Fit(Content, 250, 8, 4);
        Assert.Equal(8, shrunk.Padding);
        Assert.Equal(250, shrunk.Spans[^1].Right, 9);
        Assert.All(shrunk.Spans.Zip(Content), p => Assert.True(p.First.Width >= p.Second.Minimum + 16 - 1e-9));
    }

    [Fact]
    public void FitTightensThePaddingBeforeAnyColumnGoesBelowItsMinimum()
    {
        var fit = HistoryTable.Fit(Content, 180, 8, 4);
        Assert.Equal(5, fit.Padding, 9);
        Assert.Equal([50.0, 40.0, 90.0], fit.Spans.Select(s => Math.Round(s.Width, 9)));
        Assert.Equal(180, fit.Spans[^1].Right, 9);
    }

    [Fact]
    public void FitScalesBelowTheMinimumsOnlyAsTheLastResort()
    {
        var fit = HistoryTable.Fit(Content, 120, 8, 4);
        Assert.Equal(4, fit.Padding);
        Assert.Equal(120, fit.Spans[^1].Right, 9);
        var scale = 120 / 174.0;
        Assert.Equal([48 * scale, 38 * scale, 88 * scale], fit.Spans.Select(s => s.Width), new Close());
        Assert.Equal([0, 48 * scale, 86 * scale], fit.Spans.Select(s => s.X), new Close());
    }

    [Fact]
    public void FitGivesHiddenColumnsNoWidth()
    {
        ColumnSpec[] content = [new(50, 40), new(0, 0), new(100, 80, 1)];
        var fit = HistoryTable.Fit(content, 300, 8, 4);
        Assert.Equal(8, fit.Padding);
        Assert.Equal([66.0, 0, 234.0], fit.Spans.Select(s => s.Width));

        var squeezed = HistoryTable.Fit(content, 50, 8, 4);
        Assert.Equal(0, squeezed.Spans[1].Width);
        Assert.Equal(50, squeezed.Spans[^1].Right, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(174)]
    [InlineData(186)]
    [InlineData(198)]
    [InlineData(258)]
    [InlineData(1000)]
    [InlineData(double.NaN)]
    public void FitNeverExceedsTheWidth(double width)
    {
        var fit = HistoryTable.Fit(Content, width, 8, 4);
        var available = double.IsFinite(width) ? width : 0;
        Assert.True(fit.Spans[^1].Right <= available + 1e-6, $"{fit.Spans[^1].Right} > {available}");
        Assert.InRange(fit.Padding, 4, 8);
        Assert.All(fit.Spans, s => Assert.True(s.Width >= 0));
        if (available >= 150 + 6 * 4)
        {
            Assert.All(fit.Spans.Zip(Content), p => Assert.True(p.First.Width >= p.Second.Minimum + 2 * fit.Padding - 1e-9));
        }
    }

    private sealed class Close : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;

        public int GetHashCode(double obj) => 0;
    }

    [Fact]
    public void LayoutGivesSpareWidthToWeightedColumns()
    {
        ColumnSpec[] specs = [new(50, 40), new(60, 30), new(100, 80, 1)];
        var spans = HistoryTable.Layout(specs, 300, 10);
        Assert.Equal([new ColumnSpan(0, 50), new ColumnSpan(60, 60), new ColumnSpan(130, 170)], spans);
        Assert.Equal(300, spans[^1].Right, 9);

        ColumnSpec[] weighted = [new(50, 40, 1), new(60, 30), new(100, 80, 3)];
        var shared = HistoryTable.Layout(weighted, 290);
        Assert.Equal(70, shared[0].Width, 9);
        Assert.Equal(60, shared[1].Width, 9);
        Assert.Equal(160, shared[2].Width, 9);
    }

    [Fact]
    public void LayoutWithoutWeightsGivesSpareWidthToTheLastColumn()
    {
        ColumnSpec[] specs = [new(50, 40), new(60, 30), new(100, 80)];
        var spans = HistoryTable.Layout(specs, 250);
        Assert.Equal([50.0, 60.0, 140.0], spans.Select(s => s.Width));
        Assert.Equal([0.0, 50.0, 110.0], spans.Select(s => s.X));
    }

    [Fact]
    public void LayoutShrinksInProportionToSlack()
    {
        ColumnSpec[] specs = [new(50, 40), new(60, 30), new(100, 80, 1)];
        var spans = HistoryTable.Layout(specs, 180);
        Assert.Equal([45.0, 45.0, 90.0], spans.Select(s => s.Width));
        Assert.Equal(180, spans[^1].Right, 9);
    }

    [Fact]
    public void LayoutNeverGoesBelowTheMinimum()
    {
        ColumnSpec[] specs = [new(50, 40), new(60, 30), new(100, 80, 1)];
        Assert.Equal([40.0, 30.0, 80.0], HistoryTable.Layout(specs, 100).Select(s => s.Width));
        Assert.Equal([40.0, 30.0, 80.0], HistoryTable.Layout(specs, double.NaN).Select(s => s.Width));
        Assert.Equal([40.0, 30.0, 80.0], HistoryTable.Layout(specs, -50).Select(s => s.Width));
    }

    [Fact]
    public void LayoutTreatsAMinimumAboveTheNaturalWidthAsTheNaturalWidth()
    {
        ColumnSpec[] specs = [new(20, 60), new(-5, 0)];
        var spans = HistoryTable.Layout(specs, 100);
        Assert.Equal([60.0, 40.0], spans.Select(s => s.Width));
    }

    [Fact]
    public void LayoutCountsGapsBetweenColumnsOnly()
    {
        ColumnSpec[] specs = [new(50, 50), new(50, 50), new(50, 50)];
        var spans = HistoryTable.Layout(specs, 170, 10);
        Assert.Equal([0.0, 60.0, 120.0], spans.Select(s => s.X));
        Assert.Equal(170, spans[^1].Right, 9);
        Assert.Empty(HistoryTable.Layout([], 300, 10));
    }
}
