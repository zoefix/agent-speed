using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 3, Position on the taskbar: the widget keeps the gap to windows other programs show on the taskbar (for example
/// TrafficMonitor beside the notification area), for drawing and for the offset a drag stores.
/// </summary>
public class TaskbarNeighbourTests
{
    private static readonly RectD Bar = new(0, 1104, 2048, 48);
    private static readonly RectD Tray = new(1804, 1104, 244, 48);
    private static readonly RectD TrafficMonitor = new(1494, 1112, 311, 32);

    private static RectD? Widget(double length, double offset, double gap, params RectD[] occupied) =>
        PlacementMath.TaskbarWidget(Bar, Tray, length, gap, vertical: false, offset, occupied);

    private static double? Offset(double length, double offset, double gap, params RectD[] occupied) =>
        PlacementMath.TaskbarWidgetOffset(Bar, Tray, length, gap, vertical: false, offset, occupied);

    [Fact]
    public void TheDefaultSpotMovesPastAWindowBesideTheTray()
    {
        Assert.Equal(new RectD(1721, 1104, 79, 48), Widget(79, 0, 4));
        Assert.Equal(new RectD(1411, 1104, 79, 48), Widget(79, 0, 4, TrafficMonitor));
        Assert.Equal(310, Offset(79, 0, 4, TrafficMonitor));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void TheGapIsKeptAtEveryScaleAndAWiderWidgetGrowsAwayFromIt(double gap)
    {
        var narrow = Widget(79, 0, gap, TrafficMonitor)!.Value;
        var wide = Widget(200, 0, gap, TrafficMonitor)!.Value;
        Assert.Equal(TrafficMonitor.X - gap, narrow.Right);
        Assert.Equal(narrow.Right, wide.Right);
    }

    [Fact]
    public void AStoredOffsetClearOfTheWindowStaysAndOneOnItMovesPast()
    {
        Assert.Equal(new RectD(1221, 1104, 79, 48), Widget(79, 500, 4, TrafficMonitor));
        Assert.Equal(310, Offset(79, 120, 4, TrafficMonitor));
        Assert.Equal(new RectD(1411, 1104, 79, 48), Widget(79, 120, 4, TrafficMonitor));
    }

    [Fact]
    public void WhenTheWindowMovesOrClosesTheSpotFollows()
    {
        var moved = new RectD(1300, 1112, 200, 32);
        Assert.Equal(new RectD(1721, 1104, 79, 48), Widget(79, 0, 4, moved));
        Assert.Equal(new RectD(1217, 1104, 79, 48), Widget(79, 350, 4, moved));
        Assert.Equal(new RectD(1721, 1104, 79, 48), Widget(79, 0, 4));
    }

    [Fact]
    public void NeighboursInARowArePassedTogether()
    {
        var lyrics = new RectD(1300, 1104, 190, 48);
        Assert.Equal(new RectD(1217, 1104, 79, 48), Widget(79, 0, 4, TrafficMonitor, lyrics));
    }

    [Fact]
    public void WithoutRoomTowardTheStartItGoesTowardTheTray()
    {
        var nearStart = new RectD(300, 1104, 400, 48);
        Assert.Equal(new RectD(704, 1104, 79, 48), Widget(79, 5000, 4, nearStart));
        Assert.Equal(1017, Offset(79, 5000, 4, nearStart));
    }

    [Fact]
    public void WithoutRoomAnywhereTheWidgetIsUnavailable()
    {
        var wall = new RectD(510, 1104, 1000, 48);
        var next = new RectD(1500, 1104, 300, 48);
        Assert.Null(Widget(79, 0, 4, wall, next));
        Assert.Null(Offset(79, 0, 4, wall, next));
    }

    [Fact]
    public void WindowsOffTheTaskbarOrCoveringMostOfItAreIgnored()
    {
        var otherScreen = new RectD(1494, -48, 311, 48);
        var overlay = new RectD(0, 1104, 1200, 48);
        Assert.Equal(new RectD(1721, 1104, 79, 48), Widget(79, 0, 4, otherScreen, overlay));
    }

    [Fact]
    public void AVerticalTaskbarMovesUpPastTheWindow()
    {
        var bar = new RectD(-64, 0, 64, 1080);
        var tray = new RectD(-64, 900, 64, 180);
        var monitor = new RectD(-60, 780, 56, 122);
        Assert.Equal(new RectD(-64, 736, 64, 40), PlacementMath.TaskbarWidget(bar, tray, 40, 4, vertical: true, 0, [monitor]));
    }

    [Fact]
    public void ADragOntoTheWindowStoresWhereTheWidgetLanded()
    {
        var drag = new TaskbarDrag(new PointD(1760, 1128), Offset(79, 0, 4, TrafficMonitor)!.Value, vertical: false, threshold: 4);
        Assert.True(drag.Move(new PointD(1900, 1128)));
        var during = Widget(79, drag.Requested!.Value, 4, TrafficMonitor);
        var stored = Offset(79, drag.Requested.Value, 4, TrafficMonitor);
        Assert.Equal(during, Widget(79, stored!.Value, 4, TrafficMonitor));
        Assert.Equal(new RectD(1411, 1104, 79, 48), during);
    }
}
