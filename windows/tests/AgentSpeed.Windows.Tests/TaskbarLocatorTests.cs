using AgentSpeed.Interop;
using AgentSpeed.Taskbar;

namespace AgentSpeed.Windows.Tests;

public class TaskbarLocatorTests
{
    private static readonly TaskbarGeometry Geometry = new(0, 0,
        new RECT(0, 1104, 2048, 1152), new RECT(1804, 1104, 2048, 1152), 96, false,
        new RECT(1494, 1112, 1805, 1144));

    [Fact]
    public void TrafficMonitorOnTheUsersTaskbarIsAvoided()
    {
        var rect = TaskbarLocator.WidgetRect(Geometry, 79, 0)!.Value;
        Assert.Equal(new RECT(1411, 1104, 1490, 1152), rect);
        var offset = TaskbarLocator.WidgetOffset(Geometry, 79, 0)!.Value;
        Assert.Equal(310, offset);
        Assert.Equal(rect, TaskbarLocator.WidgetRect(Geometry, 79, offset));
    }

    [Fact]
    public void MovingOrClosingTrafficMonitorRecomputesPlacement()
    {
        var moved = Geometry with { TrafficMonitorRect = new RECT(1300, 1112, 1500, 1144) };
        Assert.Equal(new RECT(1721, 1104, 1800, 1152), TaskbarLocator.WidgetRect(moved, 79, 0));
        Assert.Equal(new RECT(1217, 1104, 1296, 1152), TaskbarLocator.WidgetRect(moved, 79, 350));
        var closed = Geometry with { TrafficMonitorRect = default };
        Assert.Equal(new RECT(1721, 1104, 1800, 1152), TaskbarLocator.WidgetRect(closed, 79, 0));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void GapScalesWithDpiAndWidthGrowsAwayFromTrafficMonitor(uint dpi)
    {
        var geometry = Geometry with { Dpi = dpi };
        var narrow = TaskbarLocator.WidgetRect(geometry, 79, 0)!.Value;
        var wide = TaskbarLocator.WidgetRect(geometry, 200, 0)!.Value;
        Assert.Equal(1494 - Math.Round(4 * dpi / 96.0), narrow.Right);
        Assert.Equal(narrow.Right, wide.Right);
    }

    [Fact]
    public void VerticalTaskbarMovesAboveTrafficMonitor()
    {
        var geometry = new TaskbarGeometry(0, 0, new RECT(-64, 0, 0, 1080),
            new RECT(-64, 900, 0, 1080), 96, true, new RECT(-60, 780, -4, 902));
        Assert.Equal(new RECT(-64, 736, 0, 776), TaskbarLocator.WidgetRect(geometry, 40, 0));
    }

    [Fact]
    public void NoRoomToAvoidTrafficMonitorReturnsUnavailable()
    {
        var geometry = Geometry with { TrafficMonitorRect = new RECT(500, 1104, 1805, 1152) };
        Assert.Null(TaskbarLocator.WidgetRect(geometry, 79, 0));
        Assert.Null(TaskbarLocator.WidgetOffset(geometry, 79, 0));
    }

    [Fact]
    public void AnotherMonitorDoesNotDisplaceTheWidget()
    {
        var geometry = Geometry with { TrafficMonitorRect = new RECT(1494, -48, 1805, 0) };
        Assert.Equal(new RECT(1721, 1104, 1800, 1152), TaskbarLocator.WidgetRect(geometry, 79, 0));
        Assert.Equal(new RECT(1221, 1104, 1300, 1152), TaskbarLocator.WidgetRect(Geometry, 79, 500));
    }
}
