using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistoryTrafficTests
{
    private const long FullDayMs = 86_400_000;
    private const char Nbsp = TrafficFormat.NoBreakSpace;

    private static readonly DateOnly Since = D(2026, 10, 19);
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly TrafficDay[] Rows =
    [
        new(Day(D(2026, 10, 19)), "codex", 1_000, 100, 500, 50, FullDayMs),
        new(Day(D(2026, 10, 19)), "machine", 9_000, 900, 0, 0, FullDayMs),
        new(Day(D(2026, 10, 20)), "codex", 2_000, 200, 0, 0, 3_600_000),
        new(Day(D(2026, 10, 20)), "claudeCode", 8_000, 800, 1_000, 100, 3_600_000),
        new(Day(D(2026, 10, 20)), "machine", 20_000, 2_000, 0, 0, 3_600_000),
        new(Day(Today), "codex", 300, 30, 0, 0, 43_200_000),
        new(Day(Today), "machine", 600, 60, 0, 0, 43_200_000),
    ];

    private static HistoryTraffic Traffic(bool includeTools = true) => new(Rows, Day(Since), includeTools);

    [Fact]
    public void DaysBeforeMeasuringBeganAreNotRecorded()
    {
        var traffic = Traffic();
        Assert.True(traffic.IsMeasured);
        Assert.Equal(Since, traffic.Since);
        Assert.Null(traffic.Day(D(2026, 10, 18), null, Today, Noon, TimeZoneInfo.Utc));
        Assert.True(traffic.NotRecorded(D(2026, 10, 18)));
        Assert.False(HistoryTraffic.None.IsMeasured);
        Assert.Null(HistoryTraffic.None.Day(Today, null, Today, Noon, TimeZoneInfo.Utc));

        // A recorded day without rows has nothing, which is 0 rather than not recorded.
        var empty = new HistoryTraffic([], Day(Since), includeTools: true).Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc);
        Assert.NotNull(empty);
        Assert.True(empty.Shown.IsZero);
        Assert.Equal(TrafficBytes.Zero, empty.Machine);
    }

    [Fact]
    public void SinceFallsBackToTheFirstStoredDay()
    {
        Assert.Equal(Since, new HistoryTraffic(Rows, null, includeTools: true).Since);
        Assert.Equal(D(2026, 10, 1), new HistoryTraffic(Rows, "2026-10-01", includeTools: true).Since);
    }

    [Fact]
    public void EverySourceAddsUpWithToolsAndThisComputer()
    {
        var day = Traffic().Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc)!;
        Assert.Equal(new TrafficBytes(10_000, 1_000), day.Own);
        Assert.Equal(new TrafficBytes(1_000, 100), day.Tools);
        Assert.Equal(new TrafficBytes(11_000, 1_100), day.Shown);
        Assert.Equal(new TrafficBytes(9_000, 900), day.BySource[SourceKind.ClaudeCode]);
        Assert.Equal(new TrafficBytes(20_000, 2_000), day.Machine);
        Assert.True(day.Partial);
        Assert.Equal(3_600_000, day.RecordedMs);

        var own = Traffic(includeTools: false).Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc)!;
        Assert.Equal(new TrafficBytes(10_000, 1_000), own.Shown);
        Assert.Equal(new TrafficBytes(8_000, 800), own.BySource[SourceKind.ClaudeCode]);
    }

    [Fact]
    public void OneSourceHasNoMachineTotal()
    {
        var day = Traffic().Day(D(2026, 10, 19), SourceKind.Codex, Today, Noon, TimeZoneInfo.Utc)!;
        Assert.Equal(new TrafficBytes(1_500, 150), day.Shown);
        Assert.Null(day.Machine);
        Assert.False(day.Partial);
        Assert.Single(day.BySource);
    }

    [Fact]
    public void TodayIsPartialOnlyWhenRecordedForLessThanItsElapsedTime()
    {
        var traffic = Traffic();
        Assert.False(traffic.Day(Today, null, Today, Noon, TimeZoneInfo.Utc)!.Partial);
        Assert.False(traffic.Day(Today, null, Today, Noon.AddMinutes(1), TimeZoneInfo.Utc)!.Partial);
        Assert.True(traffic.Day(Today, null, Today, Noon.AddMinutes(3), TimeZoneInfo.Utc)!.Partial);
    }

    [Fact]
    public void MirroredBarsStackDownloadAboveAndUploadBelow()
    {
        var range = new DayRange(D(2026, 10, 17), Today);
        var traffic = Traffic();
        var days = traffic.Days(range, null, Today, Noon, TimeZoneInfo.Utc);
        DaySummary[] usage = [Summary(D(2026, 10, 18))];
        var bars = HistoryBars.Traffic(usage, range, null, days, traffic.Since);

        Assert.True(bars.IsMirrored);
        Assert.True(bars.HasOutline);
        Assert.Equal(HistoryChartMetric.Traffic, bars.Metric);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex], bars.Order);
        Assert.Equal(2, bars.NotRecordedDays);
        var oct20 = range.IndexOf(D(2026, 10, 20));
        Assert.Equal([9_000d, 2_000d], bars.Values(oct20));
        Assert.Equal([900d, 200d], bars.Below(oct20));
        Assert.Equal(new HistoryBars.TrafficPair(20_000, 2_000), bars.Outline(oct20));
        Assert.Equal(20_000, bars.Max);
        Assert.Equal(2_000, bars.MaxBelow);
        Assert.False(bars.HasSpeed);
        Assert.True(bars.HasData(range.IndexOf(D(2026, 10, 18))));
        Assert.True(bars.HasData(oct20));
        Assert.False(bars.HasData(range.IndexOf(D(2026, 10, 17))));

        var codex = HistoryBars.Traffic(usage, range, SourceKind.Codex, traffic.Days(range, SourceKind.Codex, Today, Noon, TimeZoneInfo.Utc),
            traffic.Since);
        Assert.False(codex.HasOutline);
        Assert.Equal([SourceKind.Codex], codex.Order);
        Assert.Equal(2_000, codex.Max);
        Assert.Equal([1_500d], codex.Values(range.IndexOf(Since)));
    }

    [Fact]
    public void NothingRecordedHatchesTheWholeRange()
    {
        var range = new DayRange(D(2026, 10, 15), Today);
        var bars = HistoryBars.Traffic([], range, null, new Dictionary<DateOnly, DayTraffic>(), since: null);
        Assert.Equal(range.DayCount, bars.NotRecordedDays);
        Assert.Equal(0, bars.Max);
        Assert.Empty(bars.Order);
    }

    [Fact]
    public void BlendingMorphsUploadAndOutlineToo()
    {
        var range = new DayRange(D(2026, 10, 19), Today);
        var traffic = Traffic();
        var to = HistoryBars.Traffic([], range, null, traffic.Days(range, null, Today, Noon, TimeZoneInfo.Utc), traffic.Since);
        var from = HistoryBars.Empty(range);
        var half = HistoryBars.Blend(from, to, 0.5);
        var oct20 = D(2026, 10, 20);
        Assert.True(half.IsMirrored);
        Assert.Equal(4_500, half.ValueOf(oct20, SourceKind.ClaudeCode));
        Assert.Equal(450, half.BelowOf(oct20, SourceKind.ClaudeCode));
        Assert.Equal(new HistoryBars.TrafficPair(10_000, 1_000), half.OutlineOf(oct20));

        var back = HistoryBars.Blend(to, from, 1);
        Assert.Equal(0, back.BelowOf(oct20, SourceKind.ClaudeCode));
        Assert.Equal(0, back.Max);

        var scaled = to.Scaled(0.5);
        Assert.Equal(450, scaled.BelowOf(oct20, SourceKind.ClaudeCode));
        Assert.Equal(new HistoryBars.TrafficPair(10_000, 1_000), scaled.OutlineOf(oct20));
        Assert.Equal(to.NotRecordedDays, scaled.NotRecordedDays);
    }

    [Fact]
    public void MirroredTicksShareOneStepAndKeepTheUploadSideSmall()
    {
        var (above, below) = HistoryChartMath.MirroredTicks(950e6, 30e6);
        Assert.Equal(new AxisTicks(500e6, 2), above);
        Assert.Equal(new AxisTicks(100e6, 1), below);

        var (up, down) = HistoryChartMath.MirroredTicks(400e6, 300e6);
        Assert.Equal(up.Step, down!.Value.Step);
        Assert.True(up.Max >= 400e6 && down.Value.Max >= 300e6);

        var (only, none) = HistoryChartMath.MirroredTicks(0, 0);
        Assert.Equal(1, only.Intervals);
        Assert.Null(none);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500e6, "500 MB")]
    [InlineData(1e9, "1 GB")]
    [InlineData(2.5e9, "2.5 GB")]
    [InlineData(-250e3, "250 KB")]
    public void AxisLabelsDropTrailingZeros(double value, string expected) =>
        Assert.Equal(expected.Replace(' ', Nbsp), HistoryTrafficText.AxisBytes(value));

    [Fact]
    public void RecordedTimeReadsInHoursAndMinutes()
    {
        var zh = For(LanguageCodes.ZhHant);
        var en = For(LanguageCodes.En);
        Assert.Equal("1 分鐘", HistoryTrafficText.Duration(5_000, zh));
        Assert.Equal("45 min", HistoryTrafficText.Duration(45 * 60_000 + 59_000, en));
        Assert.Equal("3 hr", HistoryTrafficText.Duration(3 * 3_600_000, en));
        Assert.Equal("3 小時 20 分鐘", HistoryTrafficText.Duration(3 * 3_600_000 + 20 * 60_000, zh));
    }

    [Fact]
    public void TableCellsTooltipsAndHoverCard()
    {
        var loc = For(LanguageCodes.ZhHant);
        var money = Money(loc);
        var day = Traffic().Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc)!;
        var row = Summary(D(2026, 10, 20)) with { Traffic = day };

        Assert.Equal($"↓ 11.0{Nbsp}KB · ↑ 1.10{Nbsp}KB", HistoryTable.Cell(row, HistoryColumn.Traffic, Today, loc, money));
        Assert.Equal($"下載 11.0{Nbsp}KB，上傳 1.10{Nbsp}KB", HistoryTable.AccessibleCell(row, HistoryColumn.Traffic, Today, loc, money));
        Assert.Equal("–", HistoryTable.Cell(Summary(D(2026, 10, 18)), HistoryColumn.Traffic, Today, loc, money));
        Assert.Equal(loc["history.traffic.notRecorded"], HistoryTrafficText.Tooltip(null, loc));
        var tip = HistoryTrafficText.Tooltip(day, loc)!.Split(Environment.NewLine);
        Assert.Equal([$"其中工具 ↓ 1.00{Nbsp}KB · ↑ 100{Nbsp}B", "只記錄了 1 小時（Agent Speed 執行期間）"], tip);

        var card = HistoryTooltipText.BuildTraffic(D(2026, 10, 20), day, Today, loc);
        Assert.Equal(loc["history.table.traffic"], card.Rows[0].Label);
        Assert.True(card.Rows[0].Strong);
        Assert.Equal([$"其中工具 ↓ 1.00{Nbsp}KB · ↑ 100{Nbsp}B", $"這台電腦總計 ↓ 20.0{Nbsp}KB · ↑ 2.00{Nbsp}KB",
            "只記錄了 1 小時（Agent Speed 執行期間）"], card.Rows.Skip(1).Select(r => r.Label));
        Assert.All(card.Rows.Skip(1), r => Assert.True(r.IsNote));
        Assert.Empty(card.Models);

        var missing = HistoryTooltipText.BuildTraffic(D(2026, 10, 18), null, Today, loc);
        Assert.Equal(loc["history.traffic.notRecorded"], Assert.Single(missing.Rows).Label);
        Assert.Contains(loc["history.traffic.notRecorded"], HistoryTooltipText.AccessibleTraffic(D(2026, 10, 18), null, Today, loc),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrafficSortsByBothDirectionsWithUnrecordedDaysLast()
    {
        var traffic = Traffic();
        DaySummary[] rows =
        [
            Summary(D(2026, 10, 18)),
            Summary(D(2026, 10, 19)) with { Traffic = traffic.Day(D(2026, 10, 19), null, Today, Noon, TimeZoneInfo.Utc) },
            Summary(D(2026, 10, 20)) with { Traffic = traffic.Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc) },
        ];
        Assert.Equal([D(2026, 10, 20), D(2026, 10, 19), D(2026, 10, 18)], HistoryTable.Sort(rows, HistoryColumn.Traffic, true).Select(r => r.Day));
        Assert.Equal([D(2026, 10, 19), D(2026, 10, 20), D(2026, 10, 18)], HistoryTable.Sort(rows, HistoryColumn.Traffic, false).Select(r => r.Day));
    }

    [Fact]
    public void CsvAddsTrafficColumnsAndOneMachineRowPerDate()
    {
        var traffic = Traffic();
        DaySummary[] rows =
        [
            Summary(D(2026, 10, 20)) with { Traffic = traffic.Day(D(2026, 10, 20), null, Today, Noon, TimeZoneInfo.Utc) },
            Summary(D(2026, 10, 18)),
        ];
        var lines = HistoryCsv.Build(rows, null, traffic: true)[..^2].Split("\r\n");
        var header = lines[0].Split(',');
        Assert.Equal(HistoryCsv.TrafficColumns, header[^4..]);
        Assert.Equal(4, lines.Length);
        Assert.EndsWith(",10000,1000,1000,100", lines[1], StringComparison.Ordinal);
        var machine = lines[2].Split(',');
        Assert.Equal(header.Length, machine.Length);
        Assert.Equal(["2026-10-20", "machine"], machine[..2]);
        Assert.Equal(["20000", "2000", "", ""], machine[^4..]);
        Assert.All(machine[2..^4], f => Assert.Equal(string.Empty, f));
        Assert.EndsWith(",,,,", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("2026-10-18,all,", lines[3], StringComparison.Ordinal);

        Assert.DoesNotContain("traffic_", HistoryCsv.Build(rows, null), StringComparison.Ordinal);
    }

    [Fact]
    public void OneDayBreakdownSizesSourcesByTraffic()
    {
        var day = D(2026, 10, 20);
        var usage = new[]
        {
            Usage(Day(day), SourceKind.Codex, output: 5_000),
            Usage(Day(day), SourceKind.Gemini, output: 9_000),
        };
        var traffic = Traffic().Day(day, null, Today, Noon, TimeZoneInfo.Utc);
        var breakdown = DayBreakdown.For(usage, DayRange.Single(day), null, HistoryChartMetric.Traffic, traffic: traffic)!;

        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex, SourceKind.Gemini], breakdown.Rows.Select(r => r.Source));
        Assert.Equal(1, breakdown.Rows[0].Fraction);
        Assert.Equal(2_200d / 9_900, breakdown.Rows[1].Fraction, 6);
        Assert.Equal(0, breakdown.Rows[2].Fraction);
        Assert.Null(breakdown.Rows[2].Traffic);
        Assert.Equal(new TrafficBytes(9_000, 900), breakdown.Rows[0].Traffic);

        var byOutput = DayBreakdown.For(usage, DayRange.Single(day), null, HistoryChartMetric.Output, traffic: traffic)!;
        Assert.Equal(SourceKind.Gemini, byOutput.Rows[0].Source);
    }

    [Fact]
    public void AgentDaysCountSourcesOnlyWithToolsPerTheSetting()
    {
        TrafficDay[] rows =
        [
            new(Day(D(2026, 10, 18)), "machine", 9_000, 900, 0, 0, FullDayMs),
            new(Day(D(2026, 10, 19)), "gemini", 0, 0, 700, 70, FullDayMs),
            new(Day(D(2026, 10, 20)), "codex", 2_000, 200, 0, 0, FullDayMs),
            new(Day(D(2026, 10, 20)), "gemini", 0, 0, 0, 0, FullDayMs),
        ];
        var traffic = new HistoryTraffic(rows, Day(D(2026, 10, 18)), includeTools: true);

        // This computer's own total is not a day with data; a source row of zeros is not either.
        Assert.Equal([D(2026, 10, 19), D(2026, 10, 20)], traffic.AgentDays(null));
        Assert.Equal([D(2026, 10, 19)], traffic.AgentDays(SourceKind.Gemini));
        Assert.Equal([SourceKind.Codex, SourceKind.Gemini], traffic.SourcesWithTraffic().Order());

        // Without tools, a source whose bytes all came from tools shows none.
        var own = new HistoryTraffic(rows, Day(D(2026, 10, 18)), includeTools: false);
        Assert.Empty(own.AgentDays(SourceKind.Gemini));
        Assert.Equal([SourceKind.Codex], own.SourcesWithTraffic());
    }

    [Fact]
    public void ADayListedForItsTrafficIsNotAnActiveDay()
    {
        var day = DaySummary.TrafficOnly(D(2026, 10, 20));
        Assert.False(day.HasUsage);
        Assert.Equal(0, day.Requests + day.Output + day.Input);
        Assert.Null(day.AverageSpeed);
        Assert.Same(HistoryTotals.Empty, HistoryAggregator.Totals([day]));

        var totals = HistoryAggregator.Totals([day, Summary(D(2026, 10, 19), output: 500)]);
        Assert.Equal(1, totals.ActiveDays);
        Assert.Equal(D(2026, 10, 19), totals.BusiestDay?.Day);

        var cards = HistorySummary.Build([day], [], new DayRange(D(2026, 10, 15), D(2026, 10, 21)), Today, For(LanguageCodes.En));
        Assert.All(cards, c => Assert.Equal(string.Empty, c.Secondary));
    }

    [Fact]
    public void OneSourcesTrafficOnlyDayBreaksDownIntoItsOneBar()
    {
        var traffic = Traffic().Day(D(2026, 10, 20), SourceKind.ClaudeCode, Today, Noon, TimeZoneInfo.Utc);
        var day = DayRange.Single(D(2026, 10, 20));
        var breakdown = DayBreakdown.For([Usage(Day(D(2026, 10, 20)))], day, SourceKind.ClaudeCode, HistoryChartMetric.Traffic, traffic: traffic)!;
        Assert.Equal(BreakdownKind.Sources, breakdown.Kind);
        var bar = Assert.Single(breakdown.Rows);
        Assert.Equal(SourceKind.ClaudeCode, bar.Source);
        Assert.Equal(1, bar.Share);
        Assert.Equal(new TrafficBytes(9_000, 900), bar.Traffic);

        // With token usage, or another metric, it stays a breakdown by model.
        Assert.Equal(BreakdownKind.Models, DayBreakdown.For([Usage(Day(D(2026, 10, 20)), SourceKind.ClaudeCode)], day, SourceKind.ClaudeCode,
            HistoryChartMetric.Traffic, traffic: traffic)!.Kind);
        Assert.Equal(BreakdownKind.Models, DayBreakdown.For([], day, SourceKind.ClaudeCode, HistoryChartMetric.Output, traffic: traffic)!.Kind);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "下載 11.0_KB，上傳 1.10_KB")]
    [InlineData(LanguageCodes.ZhHans, "下载 11.0_KB，上传 1.10_KB")]
    [InlineData(LanguageCodes.En, "11.0_KB downloaded and 1.10_KB uploaded")]
    [InlineData(LanguageCodes.Ja, "ダウンロード 11.0_KB、アップロード 1.10_KB")]
    public void ADaysTrafficIsReadWithoutToday(string language, string expected)
    {
        var loc = For(language);
        var spoken = HistoryTrafficText.Spoken(new TrafficBytes(11_000, 1_100), loc);
        Assert.Equal(expected.Replace('_', Nbsp), spoken);
        Assert.NotEqual(loc.Format("traffic.a11y.totals", TrafficFormat.Total(11_000), TrafficFormat.Total(1_100)), spoken);
    }
}
