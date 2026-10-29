using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests.Perf;

/// <summary>The score's calm motion (DESIGN 12, <i>Motion</i>): counts both ways from 1 % and 3, replaces below, crossfades to and from –.</summary>
public sealed class PerfMotionTests
{
    [Theory]
    [InlineData(1000, 1009, false)]
    [InlineData(1000, 1010, true)]
    [InlineData(1010, 1000, false)]
    [InlineData(1000, 990, true)]
    [InlineData(100, 102, false)]
    [InlineData(100, 103, true)]
    [InlineData(5, 8, true)]
    public void ACountNeedsOnePercentAndThree(int from, int to, bool counts) => Assert.Equal(counts, PerfMotion.Counts(from, to));

    [Fact]
    public void DurationsFollowTheRatio()
    {
        Assert.Equal(0.3 + 0.35 * Math.Log2(1.1), PerfMotion.CountDuration(1000, 1100), 9);
        Assert.Equal(0.65, PerfMotion.CountDuration(1000, 2000), 9);
        Assert.Equal(0.65, PerfMotion.CountDuration(2000, 1000), 9);
        Assert.Equal(0.7, PerfMotion.CountDuration(1000, 8000), 9);
        Assert.Equal(0.3, PerfMotion.CountDuration(1000, 1000), 9);
    }

    [Fact]
    public void EachSurfaceChangesItsOwnWay()
    {
        Assert.Equal(PerfChangeKind.Count, PerfMotion.Plan(1200, 1000, PerfSurface.Orb, false).Kind);
        Assert.Equal(PerfChangeKind.Count, PerfMotion.Plan(986, 1240, PerfSurface.DashboardRow, false).Kind);
        Assert.Equal(PerfChangeKind.Replace, PerfMotion.Plan(986, 1240, PerfSurface.BoardRow, false).Kind);
        Assert.Equal(PerfChangeKind.Replace, PerfMotion.Plan(986, 1240, PerfSurface.Taskbar, false).Kind);
        Assert.Equal(PerfChangeKind.Replace, PerfMotion.Plan(1000, 1005, PerfSurface.Card, false).Kind);
        Assert.Equal(new PerfChange(PerfChangeKind.Crossfade, null, 1240, 0.15), PerfMotion.Plan(null, 1240, PerfSurface.Orb, false));
        Assert.Equal(PerfChangeKind.Crossfade, PerfMotion.Plan(1240, null, PerfSurface.BoardRow, false).Kind);
        Assert.Equal(new PerfChange(PerfChangeKind.Crossfade, 986, 1240, 0.15), PerfMotion.Plan(986, 1240, PerfSurface.Card, true));
    }

    [Fact]
    public void ACountPassesTheIntermediateValuesAndARetargetStartsFromTheShownOne()
    {
        var ticker = new PerfTicker(PerfSurface.Card);
        ticker.Set(986, 0, false);
        ticker.Set(1240, 1, false);
        Assert.True(ticker.IsAnimating(1.1));
        var middle = ticker.Shown(1.1)!.Value;
        Assert.InRange(middle, 987, 1239);
        Assert.Equal(1240, ticker.Shown(2));
        ticker.Set(1000, 1.1, false);
        Assert.Equal(middle, ticker.Change.From);
        Assert.Equal(1000, ticker.Shown(5));
    }

    [Fact]
    public void TheFirstValueShowsAtOnce()
    {
        var ticker = new PerfTicker(PerfSurface.Orb);
        ticker.Set(1213, 0, false);
        Assert.False(ticker.IsAnimating(0));
        Assert.Equal(1213, ticker.Shown(0));
    }
}
