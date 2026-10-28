using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistoryAggregatorTests
{
    private static readonly DailyUsage[] Mixed =
    [
        Usage("2026-10-19", SourceKind.Codex, output: 100, requests: 1),
        Usage("2026-10-20", SourceKind.ClaudeCode, output: 300, requests: 3),
        Usage("2026-10-20", SourceKind.Codex, output: 200, requests: 2),
        Usage("2026-10-21", SourceKind.Gemini, output: 50, requests: 5),
    ];

    [Fact]
    public void DailySumsEverySourceOldestFirst()
    {
        var days = HistoryAggregator.Daily(Mixed, null);
        Assert.Equal([D(2026, 10, 19), D(2026, 10, 20), D(2026, 10, 21)], days.Select(d => d.Day));
        var twentieth = days[1];
        Assert.Equal(500, twentieth.Output);
        Assert.Equal(5, twentieth.Requests);
        // Records without cost data: each source's requests are unavailable for cost.
        Assert.Equal(
            [
                new SourceOutput(SourceKind.Codex, 200, 2, new HistoryCost(null, 2)),
                new SourceOutput(SourceKind.ClaudeCode, 300, 3, new HistoryCost(null, 3)),
            ],
            twentieth.Sources);
        Assert.Equal(200, twentieth.OutputOf(SourceKind.Codex));
        Assert.Equal(300, twentieth.OutputOf(SourceKind.ClaudeCode));
        Assert.Equal(0, twentieth.OutputOf(SourceKind.Gemini));
    }

    [Fact]
    public void DailyFiltersBySource()
    {
        var codex = HistoryAggregator.Daily(Mixed, SourceKind.Codex);
        Assert.Equal([D(2026, 10, 19), D(2026, 10, 20)], codex.Select(d => d.Day));
        Assert.Equal(200, codex[1].Output);
        Assert.Equal([new SourceOutput(SourceKind.Codex, 200, 2, new HistoryCost(null, 2))], codex[1].Sources);

        Assert.Empty(HistoryAggregator.Daily(Mixed, SourceKind.DeepSeek));
    }

    [Fact]
    public void DailyFiltersByRange()
    {
        var days = HistoryAggregator.Daily(Mixed, null, new DayRange(D(2026, 10, 20), D(2026, 10, 20)));
        Assert.Equal(D(2026, 10, 20), Assert.Single(days).Day);
        Assert.Empty(HistoryAggregator.Daily(Mixed, null, new DayRange(D(2026, 1, 1), D(2026, 1, 31))));
    }

    [Fact]
    public void DailyIgnoresUnreadableAndEmptyRecords()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", output: 10),
            Usage("20261020", output: 999),
            Usage("2026-02-30", output: 999),
            Usage("", output: 999),
            Usage("2026-10-21", output: 0, requests: 0, input: 0, cached: 0),
            Usage("2026-10-22", output: 0, requests: 0, input: 5, cached: 0),
        ];

        var days = HistoryAggregator.Daily(usage, null);
        Assert.Equal([D(2026, 10, 20), D(2026, 10, 22)], days.Select(d => d.Day));
        Assert.Equal(10, days[0].Output);
    }

    [Fact]
    public void ModelsMergeAcrossSourcesByOutput()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Codex, models: [new ModelUsage("gpt", 3, 100), new ModelUsage(" ", 9, 999)]),
            Usage("2026-10-20", SourceKind.ClaudeCode, models: [new ModelUsage("gpt", 2, 50), new ModelUsage("opus", 1, 200)]),
            Usage("2026-10-20", SourceKind.OpenCode, models: [new ModelUsage("beta", 4, 150), new ModelUsage("alpha", 4, 150), new ModelUsage("zeta", 9, 150)]),
        ];

        var day = Assert.Single(HistoryAggregator.Daily(usage, null));
        Assert.Equal(
            [
                new ModelUsage("opus", 1, 200),
                new ModelUsage("zeta", 9, 150),
                new ModelUsage("gpt", 5, 150),
                new ModelUsage("alpha", 4, 150),
                new ModelUsage("beta", 4, 150),
            ],
            day.Models);
    }

    [Fact]
    public void MergedModelsAddUpTheirMeasuredOutputAndTime()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Codex, models: [new ModelUsage("gpt", 3, 100, 100, 2_000)]),
            Usage("2026-10-20", SourceKind.Omp, models: [new ModelUsage("gpt", 2, 50, 40, 2_000), new ModelUsage("opus", 1, 30)]),
            Usage("2026-10-21", SourceKind.Codex, models: [new ModelUsage("gpt", 1, 900, 900, 1_000)]),
        ];

        var day = HistoryAggregator.Daily(usage, null)[0];
        Assert.Equal([new ModelUsage("gpt", 5, 150, 140, 4_000), new ModelUsage("opus", 1, 30)], day.Models);
        Assert.Equal(35, day.Models[0].AverageSpeed);
        Assert.Null(day.Models[1].AverageSpeed);
    }

    [Fact]
    public void PeakIsTheHighestStoredPeak()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Codex, peak: 120),
            Usage("2026-10-20", SourceKind.ClaudeCode, peak: null),
            Usage("2026-10-20", SourceKind.DeepSeek, peak: 300),
            Usage("2026-10-20", SourceKind.Gemini, peak: double.NaN),
            Usage("2026-10-21", SourceKind.Codex, peak: null),
            Usage("2026-10-22", SourceKind.Codex, peak: 0),
        ];

        var days = HistoryAggregator.Daily(usage, null);
        Assert.Equal(300, days[0].Peak);
        Assert.Null(days[1].Peak);
        Assert.Null(days[2].Peak);
    }

    [Fact]
    public void CountsAreSummedAndNegativeValuesIgnored()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Codex, output: 100, requests: 2, input: 1000, cached: 600, agents: 2, unfinalized: 1,
                cacheWrite: 30, reasoning: 20),
            Usage("2026-10-20", SourceKind.ClaudeCode, output: 50, requests: 1, input: 500, cached: 300, agents: 3, unfinalized: 2,
                cacheWrite: 10, reasoning: 5),
            Usage("2026-10-20", SourceKind.DeepSeek, output: -40, requests: 1, input: -10, cached: -5, agents: -1, unfinalized: -3),
        ];

        var day = Assert.Single(HistoryAggregator.Daily(usage, null));
        Assert.Equal(150, day.Output);
        Assert.Equal(4, day.Requests);
        Assert.Equal(1500, day.Input);
        Assert.Equal(900, day.CachedInput);
        Assert.Equal(40, day.CacheWriteInput);
        Assert.Equal(25, day.Reasoning);
        Assert.Equal(5, day.Agents);
        Assert.Equal(3, day.Unfinalized);
        Assert.Equal(0.6, day.CacheHitRatio!.Value, 9);
        // A source with requests stays listed (for its cost), its negative output counting as none.
        Assert.Equal(new SourceOutput(SourceKind.DeepSeek, 0, 1, new HistoryCost(null, 1)),
            day.Sources.Single(s => s.Source == SourceKind.DeepSeek));
    }

    [Fact]
    public void AverageSpeedIsMeasuredOutputOverMeasuredTime()
    {
        // Codex: 1000 tok in 100 s (10 tok/s); Claude Code: 100 tok in 1 s (100 tok/s).
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Codex, output: 1200, measuredOutput: 1000, measuredMs: 100_000),
            Usage("2026-10-20", SourceKind.ClaudeCode, output: 100, measuredOutput: 100, measuredMs: 1_000),
            Usage("2026-10-21", SourceKind.Codex, output: 100, measuredOutput: 100, measuredMs: 1_000),
        ];

        var days = HistoryAggregator.Daily(usage, null);
        Assert.Equal(1100 / 101.0, days[0].AverageSpeed!.Value, 9);
        Assert.NotEqual((10 + 100) / 2.0, days[0].AverageSpeed!.Value, 3);

        var totals = HistoryAggregator.Totals(days);
        Assert.Equal(1200 / 102.0, totals.AverageSpeed!.Value, 9);
        var meanOfDailyAverages = days.Average(d => d.AverageSpeed!.Value);
        Assert.NotEqual(meanOfDailyAverages, totals.AverageSpeed!.Value, 3);
    }

    [Fact]
    public void DaysWithoutMeasurementsHaveNoSpeedOrCacheRatio()
    {
        var day = Assert.Single(HistoryAggregator.Daily([Usage("2026-10-20", input: 0, cached: 0, measuredMs: 0)], null));
        Assert.Null(day.AverageSpeed);
        Assert.Null(day.CacheHitRatio);
    }

    [Fact]
    public void CacheHitRatioIsClamped()
    {
        var day = Summary(Today, input: 100, cached: 250);
        Assert.Equal(1, day.CacheHitRatio);
    }

    [Fact]
    public void TotalsCountActiveDaysAndTheBusiestDay()
    {
        DaySummary[] days =
        [
            Summary(D(2026, 10, 18), output: 500, requests: 1, input: 10, cached: 5, measuredMs: 1000),
            Summary(D(2026, 10, 19), output: 900, requests: 2, input: 20, cached: 10, measuredMs: 1000),
            Summary(D(2026, 10, 20), output: 900, requests: 3, input: 30, cached: 15, measuredMs: 1000),
            Summary(D(2026, 10, 21), output: 100, requests: 4, input: 40, cached: 20, measuredMs: 0),
        ];

        var totals = HistoryAggregator.Totals(days);
        Assert.Equal(4, totals.ActiveDays);
        Assert.Equal(2400, totals.Output);
        Assert.Equal(10, totals.Requests);
        Assert.Equal(100, totals.Input);
        Assert.Equal(50, totals.CachedInput);
        Assert.Equal(2400, totals.MeasuredOutput);
        Assert.Equal(3000, totals.MeasuredMs);
        Assert.Equal(800, totals.AverageSpeed!.Value, 9);
        Assert.Equal(D(2026, 10, 20), totals.BusiestDay!.Day);
        Assert.True(totals.HasData);
    }

    [Fact]
    public void TotalsOfNothingAreEmpty()
    {
        var totals = HistoryAggregator.Totals([]);
        Assert.Same(HistoryTotals.Empty, totals);
        Assert.False(totals.HasData);
        Assert.Null(totals.AverageSpeed);
        Assert.Null(totals.BusiestDay);
    }

    [Fact]
    public void SourcesWithHistoryFollowTheEnumOrder()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-20", SourceKind.Gemini),
            Usage("2026-10-20", SourceKind.Codex),
            Usage("2026-10-21", SourceKind.OpenCode),
            Usage("2026-10-21", SourceKind.Codex),
            Usage("2026-10-21", SourceKind.DeepSeek, output: 0, requests: 0, input: 0, cached: 0),
        ];

        Assert.Equal([SourceKind.Codex, SourceKind.OpenCode, SourceKind.Gemini], HistoryAggregator.SourcesWithHistory(usage));
        Assert.Empty(HistoryAggregator.SourcesWithHistory([]));
    }

    [Fact]
    public void BoundsCoverTheDaysWithUsage()
    {
        DailyUsage[] usage =
        [
            Usage("2026-10-21", SourceKind.Codex),
            Usage("2026-03-02", SourceKind.ClaudeCode),
            Usage("2026-05-07", SourceKind.Codex),
            Usage("2025-01-01", SourceKind.Codex, output: 0, requests: 0, input: 0, cached: 0),
            Usage("garbage", SourceKind.Codex),
        ];

        Assert.Equal(new DayRange(D(2026, 3, 2), D(2026, 10, 21)), HistoryAggregator.Bounds(usage));
        Assert.Equal(new DayRange(D(2026, 5, 7), D(2026, 10, 21)), HistoryAggregator.Bounds(usage, SourceKind.Codex));
        Assert.Equal(DayRange.Single(D(2026, 3, 2)), HistoryAggregator.Bounds(usage, SourceKind.ClaudeCode));
        Assert.Null(HistoryAggregator.Bounds(usage, SourceKind.Gemini));
        Assert.Null(HistoryAggregator.Bounds([]));
    }

    [Fact]
    public void SeriesHasOneValuePerDayOfTheRange()
    {
        var range = new DayRange(D(2026, 10, 17), D(2026, 10, 21));
        DaySummary[] days =
        [
            Summary(D(2026, 10, 16), output: 999),
            Summary(D(2026, 10, 18), output: 10, measuredMs: 1000),
            Summary(D(2026, 10, 20), output: 30, measuredMs: 0),
        ];

        Assert.Equal(new double?[] { 0, 10, 0, 30, 0 }, HistoryAggregator.Series(days, range, d => d.Output));
        Assert.Equal(new double?[] { null, 10, null, null, null }, HistoryAggregator.Series(days, range, d => d.AverageSpeed, missing: null));
        Assert.Equal(5, HistoryAggregator.Series([], range, d => d.Output).Length);
    }

    [Theory]
    [InlineData(110.0, 100.0, 0.1)]
    [InlineData(50.0, 100.0, -0.5)]
    [InlineData(0.0, 100.0, -1.0)]
    [InlineData(100.0, 100.0, 0.0)]
    public void ChangeIsRelativeToThePreviousValue(double current, double previous, double expected)
    {
        Assert.Equal(expected, HistoryAggregator.Change(current, previous)!.Value, 9);
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(100.0, -5.0)]
    [InlineData(100.0, null)]
    [InlineData(null, 100.0)]
    [InlineData(double.NaN, 100.0)]
    [InlineData(100.0, double.PositiveInfinity)]
    public void ChangeIsNullWithoutABaseline(double? current, double? previous)
    {
        Assert.Null(HistoryAggregator.Change(current, previous));
    }
}
