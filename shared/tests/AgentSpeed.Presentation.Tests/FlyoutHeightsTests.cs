using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class FlyoutHeightsTests
{
    [Theory]
    [InlineData(500, 1008, 500)]
    [InlineData(1300, 1008, 1008)]
    [InlineData(1008, 1008, 1008)]
    [InlineData(500, double.PositiveInfinity, 500)]
    [InlineData(500, double.NaN, 500)]
    [InlineData(500, -5, 0)]
    [InlineData(-5, 1008, 0)]
    [InlineData(double.NaN, 1008, 0)]
    [InlineData(double.PositiveInfinity, 1008, 0)]
    public void FitHugsContentUpToTheCap(double content, double max, double expected)
    {
        Assert.Equal(expected, FlyoutHeights.Fit(content, max));
    }

    [Fact]
    public void ForAddsTheViewportToTheRestOfTheContent()
    {
        Assert.Equal(new FlyoutHeights(480, 1268), FlyoutHeights.For(788, 480, double.PositiveInfinity));
        Assert.Equal(new FlyoutHeights(480, 1008), FlyoutHeights.For(788, 480, 1008));
        Assert.Equal(new FlyoutHeights(0, 788), FlyoutHeights.For(788, -40, double.PositiveInfinity));
        Assert.Equal(new FlyoutHeights(0, 0), FlyoutHeights.For(double.NaN, double.NaN, 1008));
    }

    [Theory]
    [InlineData(500, 1, 500)]
    [InlineData(500, 1.25, 625)]
    [InlineData(500.3, 1.5, 751)]
    [InlineData(400.0004, 1, 400)]
    [InlineData(400.004, 1, 401)]
    [InlineData(0.5, 1, 1)]
    [InlineData(0, 1.5, 0)]
    [InlineData(-3, 1.5, 0)]
    [InlineData(500, 0, 0)]
    [InlineData(double.NaN, 1, 0)]
    public void ToPixelsRoundsUpToWholePixels(double dip, double scale, double expected)
    {
        Assert.Equal(expected, FlyoutHeights.ToPixels(dip, scale));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void SnappedFramesKeepTheContentBelowTheListOnOnePixel(double scale)
    {
        // The rest of the content is laid out on whole pixels, like WPF layout rounding does.
        var chrome = Math.Round(788 * scale) / scale;
        for (var viewport = 240.0; viewport <= 480; viewport += 0.173)
        {
            var frame = FlyoutHeights.For(chrome, viewport, double.PositiveInfinity).SnapToPixels(scale);
            var viewportPixels = frame.Viewport * scale;
            Assert.Equal(Math.Round(viewportPixels), viewportPixels, 6);

            // The window is exactly the rest plus the laid-out viewport, so the gap to the anchored edge never changes.
            var windowPixels = FlyoutHeights.ToPixels(frame.Window, scale);
            Assert.Equal(Math.Round(chrome * scale) + Math.Round(viewportPixels), windowPixels);
        }
    }

    [Fact]
    public void SnapIgnoresBadScales()
    {
        var heights = new FlyoutHeights(240.3, 1028.3);
        Assert.Equal(heights, heights.SnapToPixels(0));
        Assert.Equal(heights, heights.SnapToPixels(double.NaN));
        Assert.Equal(new FlyoutHeights(240, 1028), heights.SnapToPixels(1));
        Assert.Equal(new FlyoutHeights(0, 0), new FlyoutHeights(-3, double.NaN).SnapToPixels(1));
    }

    [Fact]
    public void ToPixelsNeverLeavesTheContentTallerThanTheWindow()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25 })
        {
            for (var dip = 240.0; dip < 1400; dip += 0.37)
            {
                var pixels = FlyoutHeights.ToPixels(dip, scale);
                Assert.Equal(Math.Round(pixels), pixels);
                Assert.True(pixels >= dip * scale - 0.0011, $"{dip} at {scale}");
                Assert.True(pixels < dip * scale + 1, $"{dip} at {scale}");
            }
        }
    }
}
