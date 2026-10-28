using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class LiveDayTests
{
    private static readonly DateTimeOffset Asked = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void OnlyTodayOnItsOwnIsTheLiveDay()
    {
        Assert.True(LiveDay.Shows(DayRange.Single(Today), Today));
        Assert.True(LiveDay.Shows(HistoryRanges.Resolve(HistoryRangeKind.Custom, Today, null, DayRange.Single(Today)), Today));
        Assert.False(LiveDay.Shows(DayRange.Single(Today.AddDays(-1)), Today));
        Assert.False(LiveDay.Shows(HistoryRanges.Resolve(HistoryRangeKind.Days7, Today, null), Today));
        Assert.Equal(TimeSpan.FromSeconds(30), LiveDay.RefreshInterval);
    }

    [Fact]
    public void AQuietRefreshStartsOnlyWhenNoAnalysisShows()
    {
        Assert.True(LiveDay.CanRefreshQuietly(HistoryState.Ready(Asked)));
        Assert.True(LiveDay.CanRefreshQuietly(HistoryState.Failed("x")));
        Assert.False(LiveDay.CanRefreshQuietly(HistoryState.NotLoaded));
        Assert.False(LiveDay.CanRefreshQuietly(HistoryState.Analyzing(0.4, 3, 10)));
    }

    [Fact]
    public void AQuietRefreshHidesOnlyTheProgress()
    {
        Assert.True(LiveDay.HidesState(HistoryState.Analyzing(0.4, 3, 10), quiet: true));
        Assert.False(LiveDay.HidesState(HistoryState.Analyzing(0.4, 3, 10), quiet: false));
        Assert.False(LiveDay.HidesState(HistoryState.Ready(Asked), quiet: true));
        Assert.False(LiveDay.HidesState(HistoryState.Failed("x"), quiet: true));
    }

    [Fact]
    public void AQuietRefreshEndsWithTheNextFinishedAnalysis()
    {
        var soon = Asked.AddSeconds(3);
        Assert.False(LiveDay.EndsQuiet(HistoryState.Ready(Asked.AddMinutes(-5)), Asked, soon));
        Assert.False(LiveDay.EndsQuiet(HistoryState.Analyzing(0.5, 1, 2), Asked, soon));
        Assert.True(LiveDay.EndsQuiet(HistoryState.Ready(Asked.AddSeconds(2)), Asked, soon));
        Assert.True(LiveDay.EndsQuiet(HistoryState.Failed("x"), Asked, soon));
        Assert.True(LiveDay.EndsQuiet(HistoryState.Analyzing(0.5, 1, 2), Asked, Asked + LiveDay.QuietLimit + TimeSpan.FromSeconds(1)));
    }
}
