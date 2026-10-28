using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class DayBreakdownTests
{
    private static readonly DailyUsage[] Records =
    [
        Usage("2026-10-21", SourceKind.Codex, output: 1000, requests: 4, measuredOutput: 800, measuredMs: 20_000,
            models: [new ModelUsage("gpt-6.1-sol", 3, 700, 600, 12_000), new ModelUsage("gpt-6.1-sol-mini", 1, 300, 200, 8_000)]),
        Usage("2026-10-21", SourceKind.ClaudeCode, output: 3000, requests: 6, measuredOutput: 3000, measuredMs: 30_000,
            models: [new ModelUsage("claude-opus-5-5", 2, 1000, 1000, 20_000), new ModelUsage("claude-sonnet-5-5", 4, 2000, 2000, 10_000)]),
        Usage("2026-10-21", SourceKind.Gemini, output: 1000, requests: 1, measuredOutput: 0, measuredMs: 0),
        Usage("2026-10-21", SourceKind.DeepSeek, output: 0, requests: 2, input: 500),
        Usage("2026-10-21", SourceKind.OpenCode, output: 0, requests: 0, input: 0),
        Usage("2026-10-20", SourceKind.Antigravity, output: 9000, requests: 9),
    ];

    [Fact]
    public void BySourceIsSortedByOutputWithSharesAndLengths()
    {
        var breakdown = DayBreakdown.BySource(Records, Today);

        Assert.Equal(BreakdownKind.Sources, breakdown.Kind);
        Assert.Equal(Today, breakdown.Day);
        Assert.Equal(5000, breakdown.Output);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex, SourceKind.Gemini, SourceKind.DeepSeek], breakdown.Rows.Select(r => r.Source));
        Assert.All(breakdown.Rows, r => Assert.Null(r.Model));
        Assert.All(breakdown.Rows, r => Assert.False(r.OtherModels));
        Assert.Equal([0.6, 0.2, 0.2, 0], breakdown.Rows.Select(r => Math.Round(r.Share, 9)));
        Assert.Equal([1, 1 / 3.0, 1 / 3.0, 0], breakdown.Rows.Select(r => r.Fraction));
        Assert.Equal([6L, 4, 1, 2], breakdown.Rows.Select(r => r.Requests));
        Assert.Equal("history.chart.bySource", breakdown.TitleKey);
    }

    [Fact]
    public void SourceSpeedIsMeasuredOutputOverMeasuredTime()
    {
        var rows = DayBreakdown.BySource(Records, Today).Rows;
        Assert.Equal(100, rows[0].Speed);
        Assert.Equal(40, rows[1].Speed);
        Assert.Null(rows[2].Speed);
    }

    [Fact]
    public void EqualSourcesAreOrderedByRequestsThenSourceOrder()
    {
        DailyUsage[] tied =
        [
            Usage("2026-10-21", SourceKind.Antigravity, output: 500, requests: 1),
            Usage("2026-10-21", SourceKind.Omp, output: 500, requests: 1),
            Usage("2026-10-21", SourceKind.Gemini, output: 500, requests: 3),
        ];
        Assert.Equal([SourceKind.Gemini, SourceKind.Omp, SourceKind.Antigravity], DayBreakdown.BySource(tied, Today).Rows.Select(r => r.Source));
    }

    [Fact]
    public void RecordsOfOneSourceOnTheSameDayAreAddedUp()
    {
        DailyUsage[] split =
        [
            Usage("2026-10-21", SourceKind.Codex, output: 600, requests: 2, measuredOutput: 600, measuredMs: 6_000),
            Usage("2026-10-21", SourceKind.Codex, output: 400, requests: 1, measuredOutput: 400, measuredMs: 14_000),
        ];
        var row = Assert.Single(DayBreakdown.BySource(split, Today).Rows);
        Assert.Equal((1000L, 3L, 1.0), (row.Output, row.Requests, row.Share));
        Assert.Equal(50, row.Speed);
    }

    [Fact]
    public void ByModelGivesEveryModelItsOwnSpeed()
    {
        var breakdown = DayBreakdown.ByModel(Records, Today, SourceKind.ClaudeCode);

        Assert.Equal(BreakdownKind.Models, breakdown.Kind);
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5"], breakdown.Rows.Select(r => r.Model));
        Assert.All(breakdown.Rows, r => Assert.Equal(SourceKind.ClaudeCode, r.Source));
        Assert.Equal([2000L, 1000], breakdown.Rows.Select(r => r.Output));
        Assert.Equal([4L, 2], breakdown.Rows.Select(r => r.Requests));
        Assert.Equal([2 / 3.0, 1 / 3.0], breakdown.Rows.Select(r => r.Share));
        Assert.Equal([1, 0.5], breakdown.Rows.Select(r => r.Fraction));
        Assert.Equal(new double?[] { 200, 50 }, breakdown.Rows.Select(r => r.Speed));
        Assert.Equal("history.chart.byModel", breakdown.TitleKey);
    }

    [Fact]
    public void OutputWithoutANamedModelIsGatheredLast()
    {
        DailyUsage[] partial =
        [
            Usage("2026-10-21", SourceKind.Codex, output: 5000, requests: 10, measuredOutput: 4000, measuredMs: 100_000,
                models: [new ModelUsage("gpt-6.1-sol", 1, 300, 300, 3_000), new ModelUsage(" gpt-6.1-sol ", 2, 300, 300, 3_000),
                    new ModelUsage(" ", 1, 50), new ModelUsage("gpt-6.1-sol-mini", 4, 400, 400, 4_000), new ModelUsage("idle", 0, 0)]),
        ];
        var breakdown = DayBreakdown.ByModel(partial, Today, SourceKind.Codex);

        Assert.Equal(new string?[] { "gpt-6.1-sol", "gpt-6.1-sol-mini", null }, breakdown.Rows.Select(r => r.Model));
        Assert.Equal([false, false, true], breakdown.Rows.Select(r => r.OtherModels));
        Assert.Equal(5000, breakdown.Output);
        Assert.Equal([600L, 400, 4000], breakdown.Rows.Select(r => r.Output));
        Assert.Equal([3L, 4, 3], breakdown.Rows.Select(r => r.Requests));
        Assert.Equal([0.12, 0.08, 0.8], breakdown.Rows.Select(r => Math.Round(r.Share, 9)));
        Assert.Equal(new double?[] { 100, 100, 3000 / 90.0 }, breakdown.Rows.Select(r => r.Speed));
        Assert.Equal([0.15, 0.1, 1], breakdown.Rows.Select(r => r.Fraction));
    }

    [Fact]
    public void WithoutModelDetailsAllOutputIsOtherModels()
    {
        var breakdown = DayBreakdown.ByModel(Records, Today, SourceKind.Gemini);
        Assert.Equal(BreakdownKind.Models, breakdown.Kind);
        var row = Assert.Single(breakdown.Rows);
        Assert.True(row.OtherModels);
        Assert.Equal((SourceKind.Gemini, (string?)null, 1000L, 1.0), (row.Source, row.Model, row.Output, row.Share));
        Assert.Null(row.Speed);
    }

    [Fact]
    public void ModelsWithRequestsButNoOutputStay()
    {
        DailyUsage[] tied =
        [
            Usage("2026-10-21", SourceKind.Codex, output: 1500, requests: 8,
                models: [new ModelUsage("b", 1, 500), new ModelUsage("a", 1, 500), new ModelUsage("c", 5, 500), new ModelUsage("d", 1, 0)]),
        ];
        var rows = DayBreakdown.ByModel(tied, Today, SourceKind.Codex).Rows;
        Assert.Equal(["c", "a", "b", "d"], rows.Select(r => r.Model));
        Assert.Equal(0, rows[3].Fraction);
    }

    [Fact]
    public void DaysWithoutUsageAreEmpty()
    {
        Assert.Empty(DayBreakdown.BySource(Records, D(2026, 10, 19)).Rows);
        Assert.Empty(DayBreakdown.ByModel(Records, Today, SourceKind.DeepSeek).Rows);
        Assert.Empty(DayBreakdown.ByModel(Records, Today, SourceKind.OpenCode).Rows);
        Assert.Equal(0, DayBreakdown.BySource([], Today).Output);
    }

    [Fact]
    public void OnlySingleDayRangesHaveABreakdown()
    {
        Assert.Null(DayBreakdown.For(Records, new DayRange(D(2026, 10, 20), Today), null));
        Assert.Equal(BreakdownKind.Sources, DayBreakdown.For(Records, DayRange.Single(Today), null)!.Kind);
        Assert.Equal(BreakdownKind.Models, DayBreakdown.For(Records, DayRange.Single(Today), SourceKind.Codex)!.Kind);
        var yesterday = DayBreakdown.For(Records, DayRange.Single(D(2026, 10, 20)), null)!;
        Assert.Equal(SourceKind.Antigravity, Assert.Single(yesterday.Rows).Source);
    }

    [Fact]
    public void KeysIdentifyBarsAcrossUpdates()
    {
        Assert.Equal("ClaudeCode", DayBreakdown.BySource(Records, Today).Rows[0].Key);
        Assert.Equal("claude-sonnet-5-5", DayBreakdown.ByModel(Records, Today, SourceKind.ClaudeCode).Rows[0].Key);
        var other = DayBreakdown.ByModel(Records, Today, SourceKind.Gemini).Rows[0];
        Assert.NotEqual(other.Key, DayBreakdown.BySource(Records, Today).Rows[2].Key);
    }

    [Fact]
    public void LabelsExistInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = For(language);
            foreach (var key in new[]
                     {
                         "history.chart.bySource", "history.chart.byModel", "win.history.share", "win.history.breakdown.otherModels",
                         "win.history.breakdown.sourceHint",
                     })
            {
                Assert.NotEqual(key, loc[key]);
            }
        }
    }
}
