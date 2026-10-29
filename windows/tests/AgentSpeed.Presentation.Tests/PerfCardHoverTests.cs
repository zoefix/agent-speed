using AgentSpeed.Presentation.TaskScore;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The score card's hover and keyboard timing (DESIGN 12, <i>Placement and timing</i>).</summary>
public class TaskCardHoverTests
{
    [Fact]
    public void TheDashboardCardShowsAfterItsDelay()
    {
        var hover = new TaskCardHover();
        Assert.Equal(TaskCardAction.None, hover.Hover("nova", TaskCardHost.Dashboard, 10));
        Assert.Equal(10.45, hover.NextDue!.Value, 9);
        Assert.Equal(TaskCardAction.None, hover.Tick(10.4));
        Assert.Equal(TaskCardAction.Show, hover.Tick(10.45));
        Assert.Equal("nova", hover.Shown);
        Assert.Null(hover.NextDue);
    }

    [Fact]
    public void BoardsUseTheirShorterTooltipDelay()
    {
        var hover = new TaskCardHover();
        hover.Hover("hero", TaskCardHost.Board, 0);
        Assert.Equal(0.35, hover.NextDue!.Value, 9);
        Assert.Equal(TaskCardHost.Board, hover.Host);
    }

    [Fact]
    public void LeavingBeforeTheDelayShowsNothing()
    {
        var hover = new TaskCardHover();
        hover.Hover("nova", TaskCardHost.Dashboard, 0);
        hover.Leave("nova", 0.3);
        Assert.Null(hover.NextDue);
        Assert.Equal(TaskCardAction.None, hover.Tick(1));
        Assert.False(hover.IsShown);
    }

    [Fact]
    public void WhileShownAnotherRowSwitchesAtOnce()
    {
        var hover = Shown("nova");
        hover.Leave("nova", 5);
        Assert.Equal(5.15, hover.NextDue!.Value, 9);
        Assert.Equal(TaskCardAction.Switch, hover.Hover("rocket", TaskCardHost.Dashboard, 5.05));
        Assert.Equal("rocket", hover.Shown);
        Assert.Null(hover.NextDue);
        Assert.Equal(TaskCardAction.None, hover.Tick(6));
    }

    [Fact]
    public void ItHidesShortlyAfterThePointerLeaves()
    {
        var hover = Shown("nova");
        hover.Leave("nova", 2);
        Assert.Equal(TaskCardAction.None, hover.Tick(2.1));
        Assert.Equal(TaskCardAction.Hide, hover.Tick(2.15));
        Assert.False(hover.IsShown);
    }

    [Fact]
    public void SpaceShowsAtOnceFocusFollowsAndEscHides()
    {
        var hover = new TaskCardHover();
        Assert.Equal(TaskCardAction.None, hover.Focus("nova", TaskCardHost.Dashboard));
        Assert.Equal(TaskCardAction.Show, hover.Show("nova", TaskCardHost.Dashboard));
        Assert.Equal(TaskCardAction.None, hover.Show("nova", TaskCardHost.Dashboard));
        Assert.Equal(TaskCardAction.Switch, hover.Focus("rocket", TaskCardHost.Dashboard));
        Assert.Equal("rocket", hover.Shown);
        Assert.Equal(TaskCardAction.Hide, hover.Hide());
        Assert.Equal(TaskCardAction.None, hover.Hide());

        hover.Show("nova", TaskCardHost.Dashboard);
        Assert.Equal(TaskCardAction.Hide, hover.Focus(null, TaskCardHost.Dashboard));
    }

    [Fact]
    public void ThePointerWinsOverTheFocus()
    {
        var hover = Shown("nova");
        Assert.Equal(TaskCardAction.None, hover.Focus("rocket", TaskCardHost.Dashboard));
        Assert.Equal("nova", hover.Shown);
    }

    private static TaskCardHover Shown(string key)
    {
        var hover = new TaskCardHover();
        hover.Hover(key, TaskCardHost.Dashboard, 0);
        hover.Tick(1);
        Assert.Equal(key, hover.Shown);
        return hover;
    }
}
