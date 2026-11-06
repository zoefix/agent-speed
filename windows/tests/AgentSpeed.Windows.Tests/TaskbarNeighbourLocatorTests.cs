using System.Collections.Immutable;
using AgentSpeed.Interop;
using AgentSpeed.Taskbar;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// DESIGN 3, Position on the taskbar: the located taskbar's foreign windows reach the placement with the gap at the
/// taskbar's DPI, for drawing and for the offset a drag stores.
/// </summary>
public class TaskbarNeighbourLocatorTests
{
    private static readonly RECT TrafficMonitor = new(1494, 1112, 1805, 1144);

    private static TaskbarGeometry Geometry(uint dpi, params RECT[] occupied) =>
        new(0, 0, new RECT(0, 1104, 2048, 1152), new RECT(1804, 1104, 2048, 1152), dpi, false, [.. occupied]);

    [Theory]
    [InlineData(96u, 4)]
    [InlineData(144u, 6)]
    [InlineData(192u, 8)]
    public void TheWidgetEndsTheScaledGapBeforeTheNeighbour(uint dpi, int gap)
    {
        var geometry = Geometry(dpi, TrafficMonitor);
        var rect = TaskbarLocator.WidgetRect(geometry, 79, 0)!.Value;
        Assert.Equal(1494 - gap, rect.Right);
        var stored = TaskbarLocator.WidgetOffset(geometry, 79, 0)!.Value;
        Assert.Equal(rect, TaskbarLocator.WidgetRect(geometry, 79, stored)!.Value);
    }

    [Fact]
    public void WithoutNeighboursTheDefaultSpotIsUnchanged()
    {
        Assert.Equal(new RECT(1721, 1104, 1800, 1152), TaskbarLocator.WidgetRect(Geometry(96), 79, 0)!.Value);
        Assert.Equal(new RECT(1721, 1104, 1800, 1152), TaskbarLocator.WidgetRect(Geometry(96) with { Occupied = default }, 79, 0)!.Value);
    }

    [Fact]
    public void ANeighbourFillingTheRoomMakesTheWidgetUnavailable()
    {
        var geometry = Geometry(96, new RECT(510, 1104, 1510, 1152), new RECT(1500, 1104, 1805, 1152));
        Assert.Null(TaskbarLocator.WidgetRect(geometry, 79, 0));
        Assert.Null(TaskbarLocator.WidgetOffset(geometry, 79, 0));
    }

    [Fact]
    public void TheGeometryKeepsWhatItFound()
    {
        var geometry = Geometry(96, TrafficMonitor);
        Assert.Equal([TrafficMonitor], geometry.Occupied.ToArray());
        Assert.True((geometry with { Occupied = ImmutableArray<RECT>.Empty }).Occupied.IsEmpty);
    }
}
