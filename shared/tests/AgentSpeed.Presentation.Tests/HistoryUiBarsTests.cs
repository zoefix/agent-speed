using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Presentation.Tests;

public class HistoryUiBarsTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static DaySummary Day(DateOnly day, params (SourceKind Source, long Output)[] sources)
    {
        var output = sources.Sum(s => s.Output);
        return new DaySummary(day, 10, output * 50, output * 40, 0, output, 0, output, output * 10, 0, 1, null, [],
            sources.Select(s => new SourceOutput(s.Source, s.Output)).ToArray());
    }

    [Fact]
    public void StacksEverySourceLargestFirst()
    {
        var range = new DayRange(Today.AddDays(-2), Today);
        var days = new[]
        {
            Day(Today.AddDays(-2), (SourceKind.Codex, 100), (SourceKind.ClaudeCode, 300)),
            Day(Today, (SourceKind.Codex, 50), (SourceKind.Gemini, 20)),
        };

        var bars = HistoryBars.From(days, range, null);

        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex, SourceKind.Gemini], bars.Order);
        Assert.Equal(3, bars.Count);
        Assert.Equal([300.0, 100.0, 0.0], bars.Values(0));
        Assert.Equal([0.0, 0.0, 0.0], bars.Values(1));
        Assert.Equal([0.0, 50.0, 20.0], bars.Values(2));
        Assert.Equal(400, bars.Max);
        Assert.Null(bars.Speed(1));
        Assert.Equal(100, bars.Speed(0));
        Assert.True(bars.HasSpeed);
    }

    [Fact]
    public void SingleSourceUsesTheDayTotal()
    {
        var range = new DayRange(Today.AddDays(-1), Today);
        var bars = HistoryBars.From([Day(Today, (SourceKind.OpenCode, 70))], range, SourceKind.OpenCode);
        Assert.Equal([SourceKind.OpenCode], bars.Order);
        Assert.Equal([70.0], bars.Values(1));
        Assert.Equal(70, bars.ValueOf(Today, SourceKind.OpenCode));
        Assert.Equal(0, bars.ValueOf(Today, SourceKind.Codex));
        Assert.Equal(0, bars.ValueOf(Today.AddDays(5), SourceKind.OpenCode));
    }

    [Fact]
    public void DaysOutsideTheRangeAreIgnored()
    {
        var range = DayRange.Single(Today);
        var bars = HistoryBars.From([Day(Today.AddDays(-3), (SourceKind.Codex, 999)), Day(Today, (SourceKind.Codex, 5))], range, null);
        Assert.Equal(5, bars.Max);
        Assert.Equal(1, bars.Count);
    }

    [Fact]
    public void BlendMatchesDaysByDate()
    {
        var day = Today.AddDays(-1);
        var from = HistoryBars.From([Day(day, (SourceKind.Codex, 100))], new DayRange(Today.AddDays(-6), Today), null);
        var to = HistoryBars.From([Day(day, (SourceKind.Codex, 300))], new DayRange(Today.AddDays(-1), Today), null);

        var half = HistoryBars.Blend(from, to, 0.5);

        Assert.Equal(to.Range, half.Range);
        Assert.Equal(200, half.ValueOf(day, SourceKind.Codex));
        Assert.Equal(300, HistoryBars.Blend(from, to, 1).ValueOf(day, SourceKind.Codex));
        Assert.Equal(100, HistoryBars.Blend(from, to, 0).ValueOf(day, SourceKind.Codex));
    }

    [Fact]
    public void LeavingSourcesShrinkOnTop()
    {
        var all = HistoryBars.From([Day(Today, (SourceKind.Codex, 100), (SourceKind.ClaudeCode, 300))], DayRange.Single(Today), null);
        var codex = HistoryBars.From([Day(Today, (SourceKind.Codex, 100))], DayRange.Single(Today), SourceKind.Codex);

        var frame = HistoryBars.Blend(all, codex, 0.25);

        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode], frame.Order);
        Assert.Equal(100, frame.Values(0)[0]);
        Assert.Equal(225, frame.Values(0)[1]);
        Assert.Equal(0, HistoryBars.Blend(all, codex, 1).Values(0)[1]);
    }

    [Fact]
    public void BlendNeverGoesNegativeOnOvershoot()
    {
        var empty = HistoryBars.Empty(DayRange.Single(Today));
        var some = HistoryBars.From([Day(Today, (SourceKind.Codex, 100))], DayRange.Single(Today), null);
        var back = HistoryBars.Blend(some, empty, 1.2);
        Assert.All(back.Values(0), v => Assert.True(v >= 0));
        Assert.Equal(115, HistoryBars.Blend(empty, some, 1.15).Values(0)[0], 6);
    }

    [Fact]
    public void EmptyFrameHasNoBars()
    {
        var empty = HistoryBars.Empty(new DayRange(Today.AddDays(-29), Today));
        Assert.Equal(30, empty.Count);
        Assert.Equal(0, empty.Max);
        Assert.False(empty.HasSpeed);
        Assert.Empty(empty.Values(4));
    }

    [Fact]
    public void RejectsMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() => new HistoryBars(new DayRange(Today.AddDays(-1), Today), [], [[]], [null, null]));
    }
}
