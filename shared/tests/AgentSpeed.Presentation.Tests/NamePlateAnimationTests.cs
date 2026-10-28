using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class NamePlateAnimationTests
{
    private static NamePlateAnimation Shown(double width = 100)
    {
        var animation = new NamePlateAnimation();
        animation.SetWidth(width, 0, animate: false);
        animation.Show(0);
        new FrameClock().Advance(1, animation.Step);
        return animation;
    }

    [Fact]
    public void AppearingFadesInFromTheDiscSideAndSettles()
    {
        var animation = new NamePlateAnimation();
        Assert.False(animation.IsVisible);
        Assert.True(animation.IsSettled);

        animation.Show(0);
        Assert.Equal(0, animation.Opacity);
        Assert.Equal(4, animation.Inset);
        Assert.Equal(0.96, animation.Scale);
        Assert.False(animation.IsSettled);

        var clock = new FrameClock();
        clock.AdvanceTo(0.14, animation.Step);
        Assert.InRange(animation.Opacity, 0.8, 0.95);
        Assert.InRange(animation.Inset, 0.2, 0.8);

        clock.AdvanceTo(0.28, animation.Step);
        Assert.Equal(1, animation.Opacity);
        Assert.Equal(0, animation.Inset);
        Assert.Equal(1, animation.Scale);
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void ANewOrbsPlateWaitsForItsLanding()
    {
        var animation = new NamePlateAnimation();
        animation.Show(0, NamePlateMotion.LandingDelay);
        Assert.True(animation.IsShowing);
        var clock = new FrameClock();
        clock.AdvanceTo(0.25, animation.Step);
        Assert.Equal(0, animation.Opacity);
        clock.AdvanceTo(0.56, animation.Step);
        Assert.Equal(1, animation.Opacity);
    }

    [Fact]
    public void DisappearingFadesWhileMovingTwoPointsTowardTheDisc()
    {
        var animation = Shown();
        animation.Hide(1);
        Assert.False(animation.IsShowing);
        Assert.True(animation.IsVisible);
        var clock = new FrameClock(1);
        clock.AdvanceTo(1.18, animation.Step);
        Assert.Equal(0, animation.Opacity);
        Assert.False(animation.IsVisible);
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void DisappearingReachesTheTwoPointInsetBeforeItEnds()
    {
        var animation = Shown();
        animation.Hide(1);
        var clock = new FrameClock(1);
        clock.AdvanceTo(1.17, animation.Step);
        Assert.InRange(animation.Inset, 1.9, 2);
        Assert.InRange(animation.Opacity, 0, 0.05);
    }

    [Fact]
    public void ANewSlotFadesOutAtTheOldOneAndInAtTheNewOne()
    {
        var animation = Shown();
        Assert.True(animation.Relocate(1));
        var clock = new FrameClock(1);
        clock.AdvanceTo(1.06, animation.Step);
        Assert.False(animation.SlotSwapDue);
        clock.AdvanceTo(1.13, animation.Step);
        Assert.True(animation.SlotSwapDue);
        Assert.Equal(0, animation.Opacity);

        animation.EnterSlot(clock.Now);
        Assert.False(animation.SlotSwapDue);
        clock.AdvanceTo(clock.Now + 0.21, animation.Step);
        Assert.Equal(1, animation.Opacity);
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void TheWidthSpringsAndSettlesOnItsTarget()
    {
        var animation = Shown();
        animation.SetWidth(180, 1, animate: true);
        Assert.Equal(100, animation.Width);
        Assert.False(animation.IsSettled);
        var clock = new FrameClock(1);
        var peak = 0.0;
        clock.Advance(1.2, now =>
        {
            animation.Step(now);
            peak = Math.Max(peak, animation.Width);
        });

        Assert.Equal(180, animation.Width);
        Assert.InRange(peak, 180, 181.5);
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void TheContentCrossfades()
    {
        var animation = Shown();
        animation.Crossfade(1, NamePlateMotion.NameCrossfadeSeconds);
        Assert.Equal(0, animation.ContentBlend);
        var clock = new FrameClock(1);
        clock.AdvanceTo(1.1, animation.Step);
        Assert.InRange(animation.ContentBlend, 0.45, 0.55);
        clock.AdvanceTo(1.22, animation.Step);
        Assert.Equal(1, animation.ContentBlend);
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void ReduceMotionFadesIn015SecondsAndJumpsWidthsAndSlots()
    {
        var animation = new NamePlateAnimation { ReduceMotion = true };
        animation.Show(0);
        Assert.Equal(0, animation.Inset);
        Assert.Equal(1, animation.Scale);
        var clock = new FrameClock();
        clock.AdvanceTo(0.17, animation.Step);
        Assert.Equal(1, animation.Opacity);

        animation.SetWidth(150, 0.2, animate: true);
        Assert.Equal(150, animation.Width);
        Assert.False(animation.Relocate(0.2));

        animation.Crossfade(0.2, NamePlateMotion.NameCrossfadeSeconds);
        clock.AdvanceTo(0.37, animation.Step);
        Assert.Equal(1, animation.ContentBlend);

        animation.Hide(0.4);
        clock.AdvanceTo(0.57, animation.Step);
        Assert.Equal(0, animation.Opacity);
        Assert.Equal(0, animation.Inset);
    }

    [Fact]
    public void NothingHiddenAnimatesItsWidthOrContent()
    {
        var animation = new NamePlateAnimation();
        animation.SetWidth(120, 0, animate: true);
        Assert.Equal(120, animation.Width);
        animation.Crossfade(0, 0.2);
        Assert.Equal(1, animation.ContentBlend);
        Assert.False(animation.Relocate(0));
        Assert.True(animation.IsSettled);
    }

    [Fact]
    public void ShowingAgainWhileFadingOutResumesFromTheCurrentOpacity()
    {
        var animation = Shown();
        animation.Hide(1);
        var clock = new FrameClock(1);
        clock.AdvanceTo(1.09, animation.Step);
        var halfway = animation.Opacity;
        animation.Show(1.09);
        Assert.Equal(halfway, animation.Opacity, 9);
        clock.AdvanceTo(1.4, animation.Step);
        Assert.Equal(1, animation.Opacity);
        Assert.Equal(0, animation.Inset);
    }
}
