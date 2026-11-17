using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 2 and 2b: an orb's fade when idle and hide when idle share its idle state; hiding replaces the fade, turning
/// hide off brings the orb back and starts the 60 s again, and the pointer only reaches an orb still on the screen.
/// </summary>
public class OrbIdleTests
{
    private static OrbIdle Idle(bool fade, bool hide, double idleSince = 0)
    {
        var idle = new OrbIdle();
        idle.Configure(fade, hide, reduceMotion: false, now: idleSince);
        idle.NoteActivity(false, idleSince, reduceMotion: false);
        return idle;
    }

    [Fact]
    public void FadeAloneDimsToFortyFivePercent()
    {
        var idle = Idle(fade: true, hide: false);
        Assert.False(idle.Tick(engaged: false, reduceMotion: false, now: 59.9));
        Assert.True(idle.Tick(false, false, 60));

        Assert.True(idle.Faded);
        Assert.False(idle.Hidden);
        Assert.Equal(IdleFade.Factor, idle.Value(60.6), 6);
        Assert.False(idle.IsAway(1000));
    }

    [Fact]
    public void HidingReplacesTheFade()
    {
        var idle = Idle(fade: true, hide: true);
        Assert.True(idle.Tick(false, false, 60));

        Assert.True(idle.Hidden);
        Assert.False(idle.Faded, "no 45 % step");
        Assert.Equal(0.5, idle.Value(60.3), 6);
        Assert.Equal(0, idle.Target);
        Assert.Equal(0, idle.Value(60.6), 6);
        Assert.True(idle.IsAway(60.6));
        Assert.False(idle.Tick(false, false, 200), "nothing more to start");
    }

    [Fact]
    public void HoverOrADragDefersTheHide()
    {
        var idle = Idle(fade: false, hide: true);
        Assert.False(idle.Tick(engaged: true, reduceMotion: false, now: 90));
        Assert.False(idle.Hidden);
        Assert.True(idle.Tick(engaged: false, reduceMotion: false, now: 92));
    }

    [Fact]
    public void ActivityBringsTheOrbBackAtItsOwnOpacity()
    {
        var idle = Idle(fade: false, hide: true);
        idle.Tick(false, false, 60);

        Assert.True(idle.NoteActivity(true, 100, reduceMotion: false));
        Assert.False(idle.Hidden);
        Assert.False(idle.IsAway(100));
        Assert.True(idle.IsReturning(100.05));
        Assert.True(idle.IsMoving(100.05));
        Assert.Equal(1, idle.Target);
        Assert.Equal(1, idle.Value(100.12), 6);
        Assert.False(idle.IsMoving(100.12));
    }

    [Fact]
    public void ThePointerBringsBackAnOrbStillFadingOutButNeverOneOffTheScreen()
    {
        var leaving = Idle(fade: false, hide: true);
        leaving.Tick(false, false, 60);
        Assert.True(leaving.Engage(60.3, reduceMotion: false));
        Assert.False(leaving.Hidden);

        var away = Idle(fade: false, hide: true);
        away.Tick(false, false, 60);
        Assert.False(away.Engage(61, reduceMotion: false));
        Assert.True(away.Hidden);
        Assert.True(away.IsAway(61));

        var faded = Idle(fade: true, hide: false);
        faded.Tick(false, false, 60);
        Assert.True(faded.Engage(61, reduceMotion: false));
        Assert.False(faded.Faded);
    }

    [Fact]
    public void TurningHideOffBringsTheOrbBackAndStartsTheSixtySecondsAgain()
    {
        var idle = Idle(fade: true, hide: true);
        idle.Tick(false, false, 60);
        Assert.True(idle.IsAway(61));

        Assert.True(idle.Configure(fadeWhenIdle: true, hideWhenIdle: false, reduceMotion: false, now: 100));
        Assert.False(idle.Hidden);
        Assert.Equal(1, idle.Value(100.12), 6);
        Assert.False(idle.Tick(false, false, 102), "the fade does not take over at once");
        Assert.False(idle.Tick(false, false, 159.9));
        Assert.True(idle.Tick(false, false, 160));
        Assert.True(idle.Faded);
    }

    [Fact]
    public void TurningHideOnEndsTheFadeAndWaitsSixtySeconds()
    {
        var idle = Idle(fade: true, hide: false);
        idle.Tick(false, false, 60);
        Assert.True(idle.Faded);

        Assert.True(idle.Configure(fadeWhenIdle: true, hideWhenIdle: true, reduceMotion: false, now: 100));
        Assert.False(idle.Faded);
        Assert.Equal(1, idle.Value(100));
        Assert.False(idle.Tick(false, false, 159.9));
        Assert.True(idle.Tick(false, false, 160));
        Assert.True(idle.Hidden);
    }

    [Fact]
    public void UnchangedSwitchesChangeNothing()
    {
        var idle = Idle(fade: false, hide: true);
        idle.Tick(false, false, 60);

        Assert.False(idle.Configure(fadeWhenIdle: false, hideWhenIdle: true, reduceMotion: true, now: 100));
        Assert.True(idle.IsAway(100));
    }

    [Fact]
    public void ReduceMotionCutsTheHideAndTheReturn()
    {
        var idle = Idle(fade: false, hide: true);
        Assert.True(idle.Tick(false, reduceMotion: true, now: 60));
        Assert.True(idle.IsAway(60));
        Assert.False(idle.IsMoving(60));

        Assert.True(idle.NoteActivity(true, 70, reduceMotion: true));
        Assert.Equal(1, idle.Value(70));
        Assert.False(idle.IsMoving(70));
    }
}
