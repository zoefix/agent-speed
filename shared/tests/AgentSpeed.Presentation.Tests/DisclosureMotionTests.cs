using AgentSpeed.Core;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

public class DisclosureMotionTests
{
    private const double Dt = 1.0 / 60;
    private const double RowHeight = 40;

    // A collapsed flyout of 1028 DIPs: 240 for six agent rows and 788 for everything else.
    private const double Chrome = 788;

    private static readonly FlyoutHeights Collapsed = FlyoutHeights.For(Chrome, 240, double.PositiveInfinity);
    private static readonly FlyoutHeights Expanded = FlyoutHeights.For(Chrome, 480, double.PositiveInfinity);

    private static List<FlyoutHeights> Run(DisclosureMotion motion, double seconds = 2)
    {
        var frames = new List<FlyoutHeights>();
        for (var t = 0.0; t < seconds && !motion.IsSettled; t += Dt)
        {
            frames.Add(motion.Step(Dt));
        }

        return frames;
    }

    private static DisclosureMotion Started(FlyoutHeights from, FlyoutHeights to, double cap = double.PositiveInfinity)
    {
        var motion = new DisclosureMotion();
        motion.Start(from, to, cap);
        return motion;
    }

    [Fact]
    public void ExpandSettlesExactlyOnTheTargetWithinASecond()
    {
        var motion = Started(Collapsed, Expanded);
        var frames = Run(motion);

        Assert.True(motion.IsSettled);
        Assert.InRange(frames.Count * Dt, 0.3, 1.0);
        Assert.Equal(Expanded, frames[^1]);
        Assert.Equal(Expanded, motion.Current);
    }

    [Fact]
    public void WindowAndViewportMoveInLockstepWhenNothingCapsTheWindow()
    {
        foreach (var (from, to) in new[] { (Collapsed, Expanded), (Expanded, Collapsed) })
        {
            foreach (var frame in Run(Started(from, to)))
            {
                Assert.Equal(Chrome, frame.Window - frame.Viewport, 6);
            }
        }
    }

    [Fact]
    public void MotionStartsFromRestAndNeverJumps()
    {
        var frames = Run(Started(Collapsed, Expanded));
        var span = Expanded.Viewport - Collapsed.Viewport;

        Assert.InRange(frames[0].Viewport - Collapsed.Viewport, 0, 0.05 * span);
        var previous = Collapsed;
        foreach (var frame in frames)
        {
            Assert.InRange(Math.Abs(frame.Viewport - previous.Viewport), 0, 0.1 * span);
            Assert.InRange(Math.Abs(frame.Window - previous.Window), 0, 0.1 * span);
            previous = frame;
        }
    }

    [Fact]
    public void OvershootStaysSmall()
    {
        var frames = Run(Started(Collapsed, Expanded));
        var span = Expanded.Viewport - Collapsed.Viewport;

        Assert.InRange(frames.Max(f => f.Viewport), Expanded.Viewport, Expanded.Viewport + 0.03 * span);
        Assert.InRange(frames.Min(f => f.Viewport), Collapsed.Viewport, Expanded.Viewport + 1);
    }

    [Fact]
    public void CappedWindowNeverPassesTheCapAndArrivesWithTheViewport()
    {
        const double cap = 1100;
        var to = FlyoutHeights.For(Chrome, 480, cap);
        var motion = Started(Collapsed, to, cap);
        var frames = Run(motion);

        Assert.Equal(cap, to.Window);
        Assert.All(frames, f => Assert.InRange(f.Window, Collapsed.Window, cap));
        Assert.Equal(new FlyoutHeights(480, cap), frames[^1]);

        // Both start together: the window covers the same share of its way as the viewport, until it reaches the cap.
        foreach (var frame in frames.Where(f => f.Window < cap - 0.5))
        {
            var viewportShare = (frame.Viewport - 240) / 240;
            var windowShare = (frame.Window - Collapsed.Window) / (cap - Collapsed.Window);
            Assert.Equal(viewportShare, windowShare, 6);
        }
    }

    [Fact]
    public void AlreadyCappedWindowStaysPutWhileTheListGrowsInside()
    {
        const double cap = 1008;
        var from = FlyoutHeights.For(Chrome, 240, cap);
        var to = FlyoutHeights.For(Chrome, 480, cap);
        var frames = Run(Started(from, to, cap));

        Assert.All(frames, f => Assert.Equal(cap, f.Window));
        Assert.Equal(480, frames[^1].Viewport);
    }

    [Fact]
    public void RetargetMidFlightKeepsMovingSmoothlyThenSettlesOnTheNewTarget()
    {
        var motion = Started(Collapsed, Expanded);
        var before = new List<FlyoutHeights>();
        for (var i = 0; i < 12; i++)
        {
            before.Add(motion.Step(Dt));
        }

        motion.Retarget(Collapsed, double.PositiveInfinity);
        var after = Run(motion);

        var lastStep = before[^1].Viewport - before[^2].Viewport;
        var firstStep = after[0].Viewport - before[^1].Viewport;
        Assert.True(lastStep > 0);
        Assert.InRange(firstStep, -lastStep, lastStep);
        Assert.Equal(Collapsed, after[^1]);
        Assert.All(after, f => Assert.Equal(Chrome, f.Window - f.Viewport, 6));
    }

    [Fact]
    public void ViewportAndWindowNeverGoNegative()
    {
        var motion = Started(new FlyoutHeights(40, 40), new FlyoutHeights(0, 0), double.PositiveInfinity);
        foreach (var frame in Run(motion))
        {
            Assert.True(frame.Viewport >= 0);
            Assert.True(frame.Window >= 0);
        }

        Assert.Equal(new FlyoutHeights(0, 0), motion.Current);
    }

    [Fact]
    public void StartingAtTheTargetSettlesOnTheFirstFrame()
    {
        var motion = Started(Expanded, Expanded);
        Assert.Equal(Expanded, motion.Step(Dt));
        Assert.True(motion.IsSettled);
    }

    [Fact]
    public void BadInputsAreSanitized()
    {
        var motion = Started(new FlyoutHeights(double.NaN, double.PositiveInfinity), new FlyoutHeights(-10, double.NaN), double.NaN);
        Assert.Equal(new FlyoutHeights(0, 0), motion.Target);
        Assert.Equal(double.PositiveInfinity, motion.Cap);

        var still = Started(Collapsed, Expanded);
        Assert.Equal(Collapsed, still.Step(0));
        Assert.Equal(Collapsed, still.Step(-1));
    }

    [Fact]
    public void TargetsComeFromTheRowCountAlone()
    {
        var agents = Enumerable.Range(0, 20)
            .Select(i => new AgentStatus("a" + i, SourceKind.Codex, "p", null, AgentPhase.Generating, false, Rate.Zero, null,
                DateTimeOffset.UnixEpoch))
            .ToList();
        var list = AgentList.Arrange(agents);
        var from = FlyoutHeights.For(Chrome, list.ViewportHeightFor(expanded: false, RowHeight), double.PositiveInfinity);
        var to = FlyoutHeights.For(Chrome, list.ViewportHeightFor(expanded: true, RowHeight), double.PositiveInfinity);
        var motion = Started(from, to);
        Run(motion);

        Assert.Equal(AgentList.CollapsedRows * RowHeight, from.Viewport);
        Assert.Equal(AgentList.ViewportRows * RowHeight, motion.Current.Viewport);
        Assert.Equal(Chrome + AgentList.ViewportRows * RowHeight, motion.Current.Window);
    }
}

public class AgentsDisclosureTests
{
    [Theory]
    [InlineData(false, false, DisclosureTransition.Instant)]
    [InlineData(false, true, DisclosureTransition.Instant)]
    [InlineData(true, false, DisclosureTransition.Spring)]
    [InlineData(true, true, DisclosureTransition.Crossfade)]
    public void ReduceMotionCrossfadesInsteadOfSpringing(bool visible, bool reduceMotion, DisclosureTransition expected)
    {
        Assert.Equal(expected, AgentsDisclosure.TransitionFor(visible, reduceMotion));
    }

    [Fact]
    public void CrossfadeMatchesTheOpacityTiming()
    {
        Assert.Equal(0.2, AgentsDisclosure.CrossfadeSeconds);
    }

    [Theory]
    [InlineData(300, 480, 240, 480, 300)]
    [InlineData(300, 480, 240, 360, 150)]
    [InlineData(300, 480, 240, 240, 0)]
    [InlineData(300, 480, 240, 236, 0)]
    [InlineData(300, 480, 240, 490, 300)]
    [InlineData(300, 240, 240, 240, 0)]
    [InlineData(0, 480, 240, 360, 0)]
    [InlineData(-5, 480, 240, 360, 0)]
    [InlineData(300, 480, 240, double.NaN, 0)]
    public void CollapseScrollsBackToTheTopAsTheViewportCloses(double fromOffset, double fromViewport, double toViewport,
        double viewport, double expected)
    {
        Assert.Equal(expected, AgentsDisclosure.CollapseOffset(fromOffset, fromViewport, toViewport, viewport), 9);
    }

    [Theory]
    [InlineData(6, 20, 6, 14)]
    [InlineData(6, 6, 6, 0)]
    [InlineData(6, 4, 6, 0)]
    [InlineData(-1, 3, 0, 3)]
    public void ChangingRowsAreTheRowsPastTheCollapsedList(int collapsed, int shown, int first, int count)
    {
        Assert.Equal((first, count), AgentsDisclosure.ChangingRows(collapsed, shown));
    }
}
