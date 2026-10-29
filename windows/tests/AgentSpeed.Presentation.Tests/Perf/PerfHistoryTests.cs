using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Perf;

/// <summary>The Usage History's 性能表現 (DESIGN 12): rows, sort, selection, the trend and the states.</summary>
public sealed class PerfHistoryTests
{
    private static readonly Localization.Localizer Zh = PerfData.Loc();
    private static readonly DateOnly Today = new(2027, 1, 15);

    private static PerfRecord Record(string day, SourceKind source, string model, int n, double tokS, int tools = 0)
    {
        var sums = new PerfSums { N = n, OutputSum = (long)(tokS * 10 * n), DurationMsSum = 10_000L * n, CacheN = n, CacheInput = 1000L * n,
            CacheCached = 980L * n };
        for (var i = 0; i < tools; i++)
        {
            sums.AddCall(300, failed: false);
        }

        sums.ActiveMs = 12_000L * n;
        return new PerfRecord(day, source, model, sums);
    }

    private static IReadOnlyList<PerfRecord> Records { get; } =
    [
        Record("2027-01-10", SourceKind.Codex, "gpt-6.1-sol", 30, 40, 50),
        Record("2027-01-11", SourceKind.Codex, "gpt-6.1-sol", 12, 42, 20),
        Record("2027-01-12", SourceKind.Codex, "gpt-6.1-sol", 25, 38, 40),
        Record("2027-01-11", SourceKind.ClaudeCode, "claude-opus-5-5", 120, 80, 200),
        Record("2027-01-12", SourceKind.ClaudeCode, "claude-opus-5-5", 90, 75, 150),
        Record("2027-01-12", SourceKind.ClaudeCode, "claude-haiku-5", 5, 120),
        Record("2027-01-05", SourceKind.ClaudeCode, "claude-opus-5-5", 40, 70, 60),
    ];

    [Fact]
    public void RowsAreSortedByScoreAndTheFullestRowIsSelected()
    {
        var section = PerfHistorySection.Build(Records, new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 12), null, Today, false,
            PerfSort.Default, null, Zh);
        Assert.Equal(PerfHistoryState.Rows, section.State);
        Assert.Equal(["claude-opus-5-5", "gpt-6.1-sol", "claude-haiku-5"], section.Rows.Select(r => r.Model));
        Assert.Null(section.Rows[2].Score);
        Assert.Equal("只有 5 個有效計時請求；至少要 20 個才會算出分數。", section.Rows[2].ScoreTooltip);
        Assert.Equal("claude-opus-5-5", section.Selected!.Model);
        Assert.Equal("210", section.Selected.TimedText);
        Assert.NotNull(section.Selected.Usual);
        Assert.Equal(3, section.Chart!.Days.Count);
        Assert.Null(section.Chart.Days[0].Score);
        Assert.Equal(2, section.Chart.LatestIndex);
        Assert.Equal(4000, section.Chart.Top);
    }

    [Fact]
    public void ASelectionIsKeptAndTheSortFlips()
    {
        var gpt = new SourceKindModel(SourceKind.Codex, "gpt-6.1-sol");
        var sort = PerfSort.Default.Toggle(PerfSortColumn.Timed).Toggle(PerfSortColumn.Timed);
        var section = PerfHistorySection.Build(Records, new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 12), null, Today, false, sort, gpt, Zh);
        Assert.Equal("gpt-6.1-sol", section.Selected!.Model);
        Assert.Equal(["claude-haiku-5", "gpt-6.1-sol", "claude-opus-5-5"], section.Rows.Select(r => r.Model));
        Assert.Equal(["gpt-6.1-sol"], PerfHistorySection.Build(Records, new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 12), SourceKind.Codex,
            Today, false, PerfSort.Default, null, Zh).Rows.Select(r => r.Model));
    }

    [Fact]
    public void ASingleDayShowsTheFourteenDaysEndingOnIt()
    {
        var section = PerfHistorySection.Build(Records, new DateOnly(2027, 1, 12), new DateOnly(2027, 1, 12), null, Today, false,
            PerfSort.Default, null, Zh);
        var chart = section.Chart!;
        Assert.Equal(14, chart.Days.Count);
        Assert.Equal(new DateOnly(2027, 1, 12), chart.Highlighted);
        Assert.Equal(new DateOnly(2026, 12, 30), chart.Days[0].Day);
        Assert.NotNull(chart.Days.Single(d => d.Day == new DateOnly(2027, 1, 5)).Score);
        Assert.Contains("截至", chart.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzingAndEmpty()
    {
        Assert.Equal(PerfHistoryState.Analyzing, PerfHistorySection.Build([], Today, Today, null, Today, true, PerfSort.Default, null, Zh).State);
        Assert.Equal(PerfHistoryState.Empty, PerfHistorySection.Build(Records, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), null, Today,
            false, PerfSort.Default, null, Zh).State);
    }

    [Fact]
    public void RowsCarryTheirMediumAndNarrowForms()
    {
        var row = PerfHistorySection.Build(Records, new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 12), null, Today, false, PerfSort.Default,
            null, Zh).Rows[0];
        Assert.Equal(7, row.Cells.Count);
        Assert.StartsWith("構成：輸出吞吐 ", row.PartsSpoken, StringComparison.Ordinal);
        Assert.Contains("吞吐 ", row.PresentLine, StringComparison.Ordinal);
        Assert.False(row.Cells.Single(c => c.Id == PerfComponentId.R).Present);
        Assert.Equal(PerfHistoryWidth.Medium, PerfHistoryLayout.WidthOf(720));
        Assert.Equal(PerfHistoryWidth.Narrow, PerfHistoryLayout.WidthOf(599));
        Assert.Equal(PerfHistoryWidth.Wide, PerfHistoryLayout.WidthOf(860));
    }
}
