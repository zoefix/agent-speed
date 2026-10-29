using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The score card's hover and keyboard timing (DESIGN 12, <i>Placement and timing</i>).</summary>
public class PerfCardHoverTests
{
    [Fact]
    public void TheDashboardCardShowsAfterItsDelay()
    {
        var hover = new PerfCardHover();
        Assert.Equal(PerfCardAction.None, hover.Hover("nova", PerfCardHost.Dashboard, 10));
        Assert.Equal(10.45, hover.NextDue!.Value, 9);
        Assert.Equal(PerfCardAction.None, hover.Tick(10.4));
        Assert.Equal(PerfCardAction.Show, hover.Tick(10.45));
        Assert.Equal("nova", hover.Shown);
        Assert.Null(hover.NextDue);
    }

    [Fact]
    public void BoardsUseTheirShorterTooltipDelay()
    {
        var hover = new PerfCardHover();
        hover.Hover("hero", PerfCardHost.Board, 0);
        Assert.Equal(0.35, hover.NextDue!.Value, 9);
        Assert.Equal(PerfCardHost.Board, hover.Host);
    }

    [Fact]
    public void LeavingBeforeTheDelayShowsNothing()
    {
        var hover = new PerfCardHover();
        hover.Hover("nova", PerfCardHost.Dashboard, 0);
        hover.Leave("nova", 0.3);
        Assert.Null(hover.NextDue);
        Assert.Equal(PerfCardAction.None, hover.Tick(1));
        Assert.False(hover.IsShown);
    }

    [Fact]
    public void WhileShownAnotherRowSwitchesAtOnce()
    {
        var hover = Shown("nova");
        hover.Leave("nova", 5);
        Assert.Equal(5.15, hover.NextDue!.Value, 9);
        Assert.Equal(PerfCardAction.Switch, hover.Hover("rocket", PerfCardHost.Dashboard, 5.05));
        Assert.Equal("rocket", hover.Shown);
        Assert.Null(hover.NextDue);
        Assert.Equal(PerfCardAction.None, hover.Tick(6));
    }

    [Fact]
    public void ItHidesShortlyAfterThePointerLeaves()
    {
        var hover = Shown("nova");
        hover.Leave("nova", 2);
        Assert.Equal(PerfCardAction.None, hover.Tick(2.1));
        Assert.Equal(PerfCardAction.Hide, hover.Tick(2.15));
        Assert.False(hover.IsShown);
    }

    [Fact]
    public void SpaceShowsAtOnceFocusFollowsAndEscHides()
    {
        var hover = new PerfCardHover();
        Assert.Equal(PerfCardAction.None, hover.Focus("nova", PerfCardHost.Dashboard));
        Assert.Equal(PerfCardAction.Show, hover.Show("nova", PerfCardHost.Dashboard));
        Assert.Equal(PerfCardAction.None, hover.Show("nova", PerfCardHost.Dashboard));
        Assert.Equal(PerfCardAction.Switch, hover.Focus("rocket", PerfCardHost.Dashboard));
        Assert.Equal("rocket", hover.Shown);
        Assert.Equal(PerfCardAction.Hide, hover.Hide());
        Assert.Equal(PerfCardAction.None, hover.Hide());

        hover.Show("nova", PerfCardHost.Dashboard);
        Assert.Equal(PerfCardAction.Hide, hover.Focus(null, PerfCardHost.Dashboard));
    }

    [Fact]
    public void ThePointerWinsOverTheFocus()
    {
        var hover = Shown("nova");
        Assert.Equal(PerfCardAction.None, hover.Focus("rocket", PerfCardHost.Dashboard));
        Assert.Equal("nova", hover.Shown);
    }

    private static PerfCardHover Shown(string key)
    {
        var hover = new PerfCardHover();
        hover.Hover(key, PerfCardHost.Dashboard, 0);
        hover.Tick(1);
        Assert.Equal(key, hover.Shown);
        return hover;
    }
}
