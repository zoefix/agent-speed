using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class PlacementMathTests
{
    private const double Radius = 74;
    private const double Threshold = 24;
    private const double Margin = 12;

    private static readonly RectD Work = new(0, 0, 1920, 1040);

    private static void AssertDiscInside(PointD center, double radius, RectD work, double margin = 0)
    {
        Assert.InRange(center.X - radius, work.X + margin - 1e-9, double.MaxValue);
        Assert.InRange(center.X + radius, double.MinValue, work.Right - margin + 1e-9);
        Assert.InRange(center.Y - radius, work.Y + margin - 1e-9, double.MaxValue);
        Assert.InRange(center.Y + radius, double.MinValue, work.Bottom - margin + 1e-9);
    }

    [Fact]
    public void RectHelpers()
    {
        var rect = new RectD(10, 20, 100, 50);
        Assert.Equal(110, rect.Right);
        Assert.Equal(70, rect.Bottom);
        Assert.Equal(new PointD(60, 45), rect.Center);
        Assert.True(rect.Contains(new PointD(10, 20)));
        Assert.False(rect.Contains(new PointD(110, 20)));
        Assert.Equal(0, rect.DistanceSquaredTo(new PointD(50, 50)));
        Assert.Equal(25, rect.DistanceSquaredTo(new PointD(115, 30)));
        Assert.True(new RectD(0, 0, 0, 10).IsEmpty);
        Assert.Equal(new RectD(5, 5, 0, 0), RectD.FromEdges(5, 5, 1, 1));
    }

    [Fact]
    public void ClampDiscPullsDiscBackInside()
    {
        Assert.Equal(new PointD(74, 966), PlacementMath.ClampDisc(new PointD(-50, 2000), Radius, Work));
        Assert.Equal(new PointD(86, 954), PlacementMath.ClampDisc(new PointD(-50, 2000), Radius, Work, Margin));
        Assert.Equal(new PointD(500, 400), PlacementMath.ClampDisc(new PointD(500, 400), Radius, Work, Margin));
    }

    [Fact]
    public void ClampDiscAlwaysKeepsDiscInside()
    {
        var random = new Random(3);
        var work = new RectD(-1920, -200, 1920, 1080);
        for (var i = 0; i < 1000; i++)
        {
            var point = new PointD(random.NextDouble() * 8000 - 5000, random.NextDouble() * 4000 - 2000);
            AssertDiscInside(PlacementMath.ClampDisc(point, Radius, work, Margin), Radius, work, Margin);
        }
    }

    [Fact]
    public void ClampDiscCentersADiscLargerThanTheWorkArea()
    {
        var work = new RectD(0, 0, 1000, 300);
        var center = PlacementMath.ClampDisc(new PointD(900, 10), 200, work);
        Assert.Equal(800, center.X);
        Assert.Equal(150, center.Y);
    }

    [Theory]
    [InlineData(74 + 10, 520, 86, 520)]
    [InlineData(1920 - 74 - 20, 520, 1834, 520)]
    [InlineData(800, 74 + 5, 800, 86)]
    [InlineData(800, 1040 - 74 - 23, 800, 954)]
    [InlineData(74 + 3, 74 + 3, 86, 86)]
    [InlineData(1920 - 74, 1040 - 74, 1834, 954)]
    [InlineData(40, 520, 86, 520)]
    public void SnapMovesDiscToMarginNearEdges(double x, double y, double expectedX, double expectedY)
    {
        var snapped = PlacementMath.Snap(new PointD(x, y), Radius, Work, Threshold, Margin);
        Assert.Equal(expectedX, snapped.X, 9);
        Assert.Equal(expectedY, snapped.Y, 9);
    }

    [Theory]
    [InlineData(74 + 24, 520)]
    [InlineData(74 + 30, 520)]
    [InlineData(1920 - 74 - 24, 520)]
    [InlineData(800, 74 + 24)]
    [InlineData(800, 1040 - 74 - 100)]
    [InlineData(960, 520)]
    public void SnapLeavesDiscAloneAwayFromEdges(double x, double y)
    {
        var point = new PointD(x, y);
        Assert.Equal(point, PlacementMath.Snap(point, Radius, Work, Threshold, Margin));
    }

    [Fact]
    public void SnapUsesTheWorkAreaOrigin()
    {
        var secondary = new RectD(1920, -200, 2560, 1400);
        var snapped = PlacementMath.Snap(new PointD(1920 + 74 + 5, 300), Radius, secondary, Threshold, Margin);
        Assert.Equal(new PointD(1920 + 12 + 74, 300), snapped);

        var top = PlacementMath.Snap(new PointD(2500, -200 + 74 + 1), Radius, secondary, Threshold, Margin);
        Assert.Equal(-200 + 12 + 74, top.Y);
    }

    [Fact]
    public void FractionRoundTrips()
    {
        var work = new RectD(1920, 0, 2560, 1400);
        var center = new PointD(2500, 333);
        var (fx, fy) = PlacementMath.ToFraction(center, work);
        Assert.InRange(fx, 0, 1);
        Assert.InRange(fy, 0, 1);
        var back = PlacementMath.FromFraction(fx, fy, work);
        Assert.Equal(center.X, back.X, 9);
        Assert.Equal(center.Y, back.Y, 9);

        var other = new RectD(0, 0, 1280, 720);
        var moved = PlacementMath.FromFraction(fx, fy, other);
        Assert.Equal(fx * 1280, moved.X, 9);
    }

    [Fact]
    public void FractionsAreClamped()
    {
        Assert.Equal((0.0, 1.0), PlacementMath.ToFraction(new PointD(-10, 5000), Work));
        Assert.Equal((0.5, 0.5), PlacementMath.ToFraction(new PointD(3, 3), new RectD(0, 0, 0, 0)));
        Assert.Equal(new PointD(1920, 0), PlacementMath.FromFraction(4, -1, Work));
    }

    [Fact]
    public void DefaultOrbCenterSitsBottomRightInside()
    {
        var center = PlacementMath.DefaultOrbCenter(Work, Radius, Margin);
        Assert.Equal(new PointD(1920 - 12 - 74, 1040 - 24 - 74), center);
        AssertDiscInside(center, Radius, Work, Margin);
    }

    [Fact]
    public void NearestIndexPrefersContainingThenNearest()
    {
        RectD[] monitors = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];
        Assert.Equal(0, PlacementMath.NearestIndex(new PointD(100, 100), monitors));
        Assert.Equal(1, PlacementMath.NearestIndex(new PointD(2000, 100), monitors));
        Assert.Equal(1, PlacementMath.NearestIndex(new PointD(1920, 500), monitors));
        Assert.Equal(1, PlacementMath.NearestIndex(new PointD(9000, 100), monitors));
        Assert.Equal(0, PlacementMath.NearestIndex(new PointD(-100, 50), monitors));
        Assert.Equal(0, PlacementMath.NearestIndex(new PointD(1000, 1300), monitors));
        Assert.Equal(1, PlacementMath.NearestIndex(new PointD(1900, 1300), monitors));
        Assert.Equal(-1, PlacementMath.NearestIndex(new PointD(0, 0), []));
        Assert.Throws<ArgumentNullException>(() => PlacementMath.NearestIndex(new PointD(0, 0), null!));
    }

    [Fact]
    public void FlyoutForBottomTaskbar()
    {
        var flyout = PlacementMath.Flyout(new RectD(0, 0, 1920, 1032), TaskbarEdge.Bottom, 368, 600, Margin);
        Assert.Equal(new RectD(1540, 420, 368, 600), flyout);
    }

    [Fact]
    public void FlyoutForTopTaskbar()
    {
        var flyout = PlacementMath.Flyout(new RectD(0, 48, 1920, 1032), TaskbarEdge.Top, 368, 600, Margin);
        Assert.Equal(new RectD(1540, 60, 368, 600), flyout);
    }

    [Fact]
    public void FlyoutForLeftTaskbar()
    {
        var flyout = PlacementMath.Flyout(new RectD(48, 0, 1872, 1080), TaskbarEdge.Left, 368, 600, Margin);
        Assert.Equal(new RectD(60, 468, 368, 600), flyout);
    }

    [Fact]
    public void FlyoutForRightTaskbar()
    {
        var flyout = PlacementMath.Flyout(new RectD(0, 0, 1872, 1080), TaskbarEdge.Right, 368, 600, Margin);
        Assert.Equal(new RectD(1492, 468, 368, 600), flyout);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom)]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void FlyoutShrinksToFitSmallWorkAreas(TaskbarEdge edge)
    {
        var work = new RectD(100, 50, 300, 400);
        var flyout = PlacementMath.Flyout(work, edge, 368, 900, Margin);
        Assert.Equal(276, flyout.Width);
        Assert.Equal(376, flyout.Height);
        Assert.True(flyout.X >= work.X + Margin && flyout.Right <= work.Right - Margin);
        Assert.True(flyout.Y >= work.Y + Margin && flyout.Bottom <= work.Bottom - Margin);
    }

    [Fact]
    public void FlyoutRoundsToWholePixels()
    {
        var flyout = PlacementMath.Flyout(new RectD(0.4, 0, 1920.3, 1032.6), TaskbarEdge.Bottom, 368.4, 600.2, 12);
        Assert.Equal(Math.Round(flyout.X), flyout.X);
        Assert.Equal(Math.Round(flyout.Y), flyout.Y);
        Assert.Equal(368, flyout.Width);
        Assert.Equal(600, flyout.Height);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom, 12)]
    [InlineData(TaskbarEdge.Bottom, 15)]
    [InlineData(TaskbarEdge.Bottom, 21)]
    [InlineData(TaskbarEdge.Top, 18)]
    [InlineData(TaskbarEdge.Left, 15)]
    [InlineData(TaskbarEdge.Right, 18)]
    public void FlyoutKeepsItsAnchoredEdgesOnOnePixelWhileItsSizeAnimates(TaskbarEdge edge, double margin)
    {
        var work = new RectD(0.4, 48.6, 2560.3, 1391.7);
        var first = PlacementMath.Flyout(work, edge, 460, 500, margin);
        for (var height = 500.0; height < 1300; height += 0.37)
        {
            var width = 460 + (height - 500) / 100;
            var flyout = PlacementMath.Flyout(work, edge, width, height, margin);
            if (edge == TaskbarEdge.Top)
            {
                Assert.Equal(first.Y, flyout.Y);
            }
            else
            {
                Assert.Equal(first.Bottom, flyout.Bottom);
            }

            if (edge == TaskbarEdge.Left)
            {
                Assert.Equal(first.X, flyout.X);
            }
            else
            {
                Assert.Equal(first.Right, flyout.Right);
            }

            Assert.Equal(Math.Round(height), flyout.Height);
            Assert.Equal(Math.Round(flyout.Y), flyout.Y);
        }
    }

    [Fact]
    public void TaskbarWidgetSitsLeftOfTheTray()
    {
        var widget = PlacementMath.TaskbarWidget(new RectD(0, 0, 1920, 48), new RectD(1700, 0, 220, 48), 90, 4, vertical: false);
        Assert.Equal(new RectD(1606, 0, 90, 48), widget);
    }

    [Fact]
    public void TaskbarWidgetMatchesAShorterTray()
    {
        var widget = PlacementMath.TaskbarWidget(new RectD(0, 0, 1920, 48), new RectD(1700, 4, 220, 40), 90, 4, vertical: false);
        Assert.Equal(new RectD(1606, 4, 90, 40), widget);
    }

    [Fact]
    public void TaskbarWidgetWithoutTrayUsesTheTaskbarEnd()
    {
        var horizontal = PlacementMath.TaskbarWidget(new RectD(0, 0, 1920, 48), default, 90, 4, vertical: false);
        Assert.Equal(new RectD(1830, 0, 90, 48), horizontal);

        var vertical = PlacementMath.TaskbarWidget(new RectD(0, 0, 64, 1080), default, 40, 4, vertical: true);
        Assert.Equal(new RectD(0, 1040, 64, 40), vertical);
    }

    [Fact]
    public void TaskbarWidgetSitsAboveTheTrayWhenVertical()
    {
        var widget = PlacementMath.TaskbarWidget(new RectD(0, 0, 64, 1080), new RectD(0, 900, 64, 180), 40, 4, vertical: true);
        Assert.Equal(new RectD(0, 856, 64, 40), widget);
    }

    [Fact]
    public void TaskbarWidgetIsNullWithoutRoom()
    {
        Assert.Null(PlacementMath.TaskbarWidget(new RectD(0, 0, 1920, 48), new RectD(400, 0, 1520, 48), 90, 4, vertical: false));
        Assert.Null(PlacementMath.TaskbarWidget(new RectD(0, 0, 64, 1080), new RectD(0, 200, 64, 880), 40, 4, vertical: true));
        Assert.Null(PlacementMath.TaskbarWidget(default, new RectD(0, 0, 10, 10), 90, 4, vertical: false));
        Assert.Null(PlacementMath.TaskbarWidget(new RectD(0, 0, 1920, 48), new RectD(1700, 0, 220, 48), 0, 4, vertical: false));
    }
}
