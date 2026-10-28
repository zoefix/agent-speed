using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleSettingsMotionTests
{
    [Fact]
    public void SpringStartsAtRestAndSettlesOnTheTarget()
    {
        Assert.Equal(0, TitleEditorMotion.Spring(0));
        Assert.Equal(0, TitleEditorMotion.Spring(-1));
        var settle = TitleEditorMotion.SettleTime();
        Assert.InRange(settle, 0.3, 0.7);
        for (var t = settle; t < settle + 1; t += 0.01)
        {
            Assert.InRange(TitleEditorMotion.Spring(t), 0.998, 1.002);
        }
    }

    [Fact]
    public void SpringOvershootsOnlySlightly()
    {
        var peak = 0.0;
        var previous = 0.0;
        for (var t = 0.0; t <= 1; t += 0.002)
        {
            var value = TitleEditorMotion.Spring(t);
            if (t < 0.15)
            {
                Assert.True(value >= previous, $"rises monotonically at first ({t:0.000})");
            }

            peak = Math.Max(peak, value);
            previous = value;
        }

        Assert.InRange(peak, 1.0, 1.03);
    }

    [Fact]
    public void CriticallyDampedSpringNeverOvershoots()
    {
        for (var t = 0.0; t <= 2; t += 0.01)
        {
            Assert.InRange(TitleEditorMotion.Spring(t, 0.35, 1), 0, 1);
        }
    }

    [Theory]
    [InlineData(null, 13)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(20, 13)]
    public void PreviewsShowTheFocusedLevelElseThirteen(int? focused, int expected)
    {
        Assert.Equal(expected, TitleEditorMotion.PreviewLevel(focused));
    }

    [Theory]
    [InlineData(0, 2000, 0)]
    [InlineData(1, 2000, 0.525)]
    [InlineData(2, 2000, 42)]
    [InlineData(13, 2000, 2100)]
    [InlineData(13, 400, 420)]
    public void PreviewValueIsFivePercentAboveTheThreshold(int level, double redline, double expected)
    {
        Assert.Equal(expected, TitleEditorMotion.PreviewValue(level, redline), 6);
    }

    [Fact]
    public void PreviewStaysInPlaceWhileItsPlaceIsVisible()
    {
        Assert.Equal(0, TitleEditorMotion.StickyOffset(naturalTop: 50, height: 300, containerBottom: 900, viewportTop: 0, inset: 12));
        Assert.Equal(0, TitleEditorMotion.StickyOffset(naturalTop: 12, height: 300, containerBottom: 900, viewportTop: 0, inset: 12));
    }

    [Fact]
    public void PreviewFollowsTheViewportTopWhileTheEditorScrollsPast()
    {
        Assert.Equal(112, TitleEditorMotion.StickyOffset(naturalTop: -100, height: 300, containerBottom: 900, viewportTop: 0, inset: 12));
    }

    [Fact]
    public void PreviewNeverLeavesTheBottomOfTheEditor()
    {
        Assert.Equal(500, TitleEditorMotion.StickyOffset(naturalTop: -700, height: 300, containerBottom: 100, viewportTop: 0, inset: 12));
        Assert.Equal(0, TitleEditorMotion.StickyOffset(naturalTop: -100, height: 300, containerBottom: 150, viewportTop: 0, inset: 12));
    }
}
