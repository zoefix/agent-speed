using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Presentation.Tests;

public class PreviewHistoryTests
{
    private static readonly DateTime End = new(2026, 10, 21, 15, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(End);

    private static string[] ModelsOf(IEnumerable<DailyUsage> usage, SourceKind source) =>
        [.. usage.Where(u => u.Source == source).SelectMany(u => u.Models).Select(m => m.Model).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void PreviewHistoryIncludesOmpAndAntigravity()
    {
        var usage = PreviewHistory.Days(End);
        var sources = HistoryAggregator.SourcesWithHistory(usage);
        Assert.Contains(SourceKind.Omp, sources);
        Assert.Contains(SourceKind.Antigravity, sources);
        Assert.Equal(["claude-opus-5-5", "gpt-5.5"], ModelsOf(usage, SourceKind.Omp));
        Assert.Equal(["gemini-3.1-flash", "gemini-3.1-pro"], ModelsOf(usage, SourceKind.Antigravity));
    }

    [Fact]
    public void PreviewHistoryIncludesBothWorkBuddyEditions()
    {
        var usage = PreviewHistory.Days(End);
        var sources = HistoryAggregator.SourcesWithHistory(usage);
        Assert.Contains(SourceKind.WorkBuddy, sources);
        Assert.Contains(SourceKind.WorkBuddyAi, sources);
        Assert.Equal(["deepseek-v4-pro", "glm-5.3"], ModelsOf(usage, SourceKind.WorkBuddy));
        Assert.Equal(["gpt-5.5", "hy4-preview"], ModelsOf(usage, SourceKind.WorkBuddyAi));
        Assert.All(usage.Where(u => u.Source is SourceKind.WorkBuddy or SourceKind.WorkBuddyAi), u =>
        {
            Assert.NotNull(u.Cost);
            Assert.Equal(0, u.Cost.Value.UnpricedRequests);
            Assert.True(u.Cost.Value.CostMicroUsd > 0);
        });
    }

    [Fact]
    public void PreviewModelsHaveTheirOwnSpeeds()
    {
        var usage = PreviewHistory.Days(End);
        Assert.All(usage.SelectMany(u => u.Models), m => Assert.True(m.AverageSpeed is > 0, m.Model));
        Assert.All(usage, u => Assert.Equal(u.MeasuredOutput, u.Models.Sum(m => m.MeasuredOutput)));
        Assert.All(usage, u => Assert.Equal(u.MeasuredMs, u.Models.Sum(m => m.MeasuredMs)));
        var twoModels = usage.First(u => u.Models.Count == 2);
        Assert.NotEqual(twoModels.Models[0].AverageSpeed, twoModels.Models[1].AverageSpeed);

        Assert.True(HistoryRanges.TryParseDay(usage.Last(u => u.Source == SourceKind.Omp).Day, out var ompDay));
        var models = DayBreakdown.ByModel(usage, ompDay, SourceKind.Omp).Rows;
        Assert.NotEmpty(models);
        Assert.All(models, r => Assert.False(r.OtherModels));
        Assert.All(models, r => Assert.NotNull(r.Speed));
    }

    [Theory]
    [InlineData(SourceKind.Omp, 45)]
    [InlineData(SourceKind.Antigravity, 30)]
    [InlineData(SourceKind.OpenCode, 20)]
    [InlineData(SourceKind.WorkBuddy, 14)]
    [InlineData(SourceKind.WorkBuddyAi, 10)]
    public void NewerSourcesOnlyCoverTheirRecentDays(SourceKind source, int days)
    {
        var bounds = HistoryAggregator.Bounds(PreviewHistory.Days(End), source);
        Assert.NotNull(bounds);
        Assert.InRange(bounds.Value.Start, Today.AddDays(-days), Today);
        Assert.InRange(bounds.Value.End, bounds.Value.Start, Today);
    }

    [Fact]
    public void TheOlderSourcesStillCoverTheWholeRange()
    {
        var usage = PreviewHistory.Days(End);
        foreach (var source in new[] { SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop, SourceKind.DeepSeek })
        {
            Assert.True(HistoryAggregator.Bounds(usage, source)!.Value.Start <= Today.AddDays(-100), source.ToString());
        }
    }

    [Fact]
    public void TheChartStacksAndColorsTheNewSources()
    {
        var days = HistoryAggregator.Daily(PreviewHistory.Days(End), null);
        var order = HistoryChartMath.StackOrder(days);
        Assert.Contains(SourceKind.Omp, order);
        Assert.Contains(SourceKind.Antigravity, order);
        Assert.Contains(SourceKind.WorkBuddy, order);
        Assert.Contains(SourceKind.WorkBuddyAi, order);
        Assert.Equal(order.Count, order.Select(s => HeatPalette.SourceColor(s)).Distinct().Count());

        var range = new DayRange(Today.AddDays(-29), Today);
        var bars = HistoryBars.From(days, range, null);
        Assert.Contains(SourceKind.Omp, bars.Order);
        Assert.Contains(SourceKind.Antigravity, bars.Order);
        Assert.Contains(SourceKind.WorkBuddy, bars.Order);
        Assert.Contains(SourceKind.WorkBuddyAi, bars.Order);
    }

    [Theory]
    [InlineData(SourceKind.Omp, "omp")]
    [InlineData(SourceKind.Antigravity, "antigravity")]
    [InlineData(SourceKind.WorkBuddy, "workbuddy")]
    [InlineData(SourceKind.WorkBuddyAi, "workbuddy-ai")]
    public void ExportsNameTheNewSources(SourceKind source, string id)
    {
        var days = HistoryAggregator.Daily(PreviewHistory.Days(End), source);
        Assert.NotEmpty(days);
        var rows = HistoryCsv.Build(days, source).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.Equal(days.Count, rows.Length);
        Assert.All(rows, row => Assert.Equal(id, row.Split(',')[1]));
        Assert.StartsWith($"agent-speed-history-{id}-", HistoryCsv.FileName(new DayRange(Today.AddDays(-6), Today), source), StringComparison.Ordinal);
    }
}
