using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 2 "hide when idle": after 60 s of idle the orb fades to 0 over 0.6 s ease-in-out and leaves the screen, hover
/// and a drag defer it, activity brings it back over 0.12 s ease-out, Reduce Motion cuts both, and nothing runs once it
/// has settled.
/// </summary>
public class IdleHideTests
{
    private static IdleHide IdleSince(double at)
    {
        var hide = new IdleHide();
        hide.NoteActivity(false, at, reduceMotion: false);
        return hide;
    }

    [Fact]
    public void HidesAfterSixtySecondsOfIdle()
    {
        var hide = IdleSince(100);
        hide.NoteActivity(false, 130, reduceMotion: false);
        Assert.False(hide.TryHide(enabled: true, engaged: false, reduceMotion: false, now: 159.9));
        Assert.False(hide.Hidden);
        Assert.Equal(1, hide.Value(159.9));

        Assert.True(hide.TryHide(enabled: true, engaged: false, reduceMotion: false, now: 160));
        Assert.True(hide.Hidden);
        Assert.False(hide.TryHide(enabled: true, engaged: false, reduceMotion: false, now: 161), "only once");
    }

    [Fact]
    public void ItFadesToZeroInOutOverSixTenthsOfASecondThenLeavesTheScreen()
    {
        var hide = IdleSince(0);
        hide.TryHide(true, false, false, 60);

        Assert.Equal(1, hide.Value(60), 6);
        Assert.Equal(1 - 4 * 0.25 * 0.25 * 0.25, hide.Value(60.15), 6);
        Assert.Equal(0.5, hide.Value(60.3), 6);
        Assert.True(hide.IsMoving(60.59));
        Assert.False(hide.IsAway(60.59), "still on the screen while it fades");
        Assert.Equal(0, hide.Value(60.6), 6);
        Assert.False(hide.IsMoving(60.6), "settled: no more frames");
        Assert.True(hide.IsAway(60.6));
        Assert.True(hide.IsAway(5000));
    }

    [Fact]
    public void ActivityBringsItBackOverATwelfthOfASecondAndStartsTheSixtySecondsAgain()
    {
        var hide = IdleSince(0);
        hide.TryHide(true, false, false, 60);

        Assert.True(hide.NoteActivity(true, 100, reduceMotion: false));
        Assert.False(hide.Hidden);
        Assert.False(hide.IsAway(100));
        Assert.Equal(0, hide.Value(100), 6);
        Assert.Equal(1 - 0.5 * 0.5 * 0.5, hide.Value(100.06), 6);
        Assert.True(hide.IsMoving(100.11));
        Assert.Equal(1, hide.Value(100.12), 6);
        Assert.False(hide.IsMoving(100.12));
        Assert.False(hide.NoteActivity(true, 101, reduceMotion: false), "nothing to bring back");

        hide.NoteActivity(false, 110, reduceMotion: false);
        Assert.False(hide.TryHide(true, false, false, 169.9));
        Assert.True(hide.TryHide(true, false, false, 170));
    }

    [Fact]
    public void ActivityWhileItFadesOutReturnsFromWhereItIs()
    {
        var hide = IdleSince(0);
        hide.TryHide(true, false, false, 60);

        Assert.True(hide.NoteActivity(true, 60.3, reduceMotion: false));
        Assert.Equal(0.5, hide.Value(60.3), 6);
        Assert.Equal(0.5 + 0.5 * (1 - 0.5 * 0.5 * 0.5), hide.Value(60.36), 6);
        Assert.Equal(1, hide.Value(60.42), 6);
    }

    [Fact]
    public void ReduceMotionCutsBothWays()
    {
        var hide = IdleSince(0);
        Assert.True(hide.TryHide(true, false, reduceMotion: true, now: 60));
        Assert.False(hide.IsMoving(60));
        Assert.True(hide.IsAway(60));
        Assert.Equal(0, hide.Value(60));

        Assert.True(hide.Return(61, reduceMotion: true));
        Assert.False(hide.IsMoving(61));
        Assert.Equal(1, hide.Value(61));
    }

    [Fact]
    public void HoverOrADragDefersIt()
    {
        var hide = IdleSince(0);
        Assert.False(hide.TryHide(enabled: true, engaged: true, reduceMotion: false, now: 90));
        Assert.False(hide.Hidden);
        Assert.True(hide.TryHide(enabled: true, engaged: false, reduceMotion: false, now: 92), "the pointer gone, it hides");
    }

    [Fact]
    public void SwitchedOffOrNeverIdleItNeverHides()
    {
        var hide = IdleSince(0);
        Assert.False(hide.TryHide(enabled: false, engaged: false, reduceMotion: false, now: 1000));

        var fresh = new IdleHide();
        Assert.False(fresh.TryHide(true, false, false, 1000), "no idle tick yet");
        Assert.Equal(1, fresh.Value(1000));
        Assert.False(fresh.IsAway(1000));
        Assert.False(fresh.Return(1000, false));
    }

    [Fact]
    public void RearmingStartsTheSixtySecondsAgainOnlyWhileIdle()
    {
        var hide = IdleSince(0);
        hide.Rearm(50);
        Assert.False(hide.TryHide(true, false, false, 60));
        Assert.False(hide.TryHide(true, false, false, 109.9));
        Assert.True(hide.TryHide(true, false, false, 110));

        var active = new IdleHide();
        active.NoteActivity(true, 0, false);
        active.Rearm(50);
        active.NoteActivity(false, 70, false);
        Assert.False(active.TryHide(true, false, false, 129.9));
        Assert.True(active.TryHide(true, false, false, 130));
    }
}
