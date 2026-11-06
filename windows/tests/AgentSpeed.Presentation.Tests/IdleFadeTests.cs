using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 2 "fade when idle", shared by the orbs and the boards (2c): 60 s of idle fades to 45 % over 0.6 s ease-in-out,
/// activity, hover and a drag restore at once, Reduce Motion cuts, and nothing runs once it has settled.
/// </summary>
public class IdleFadeTests
{
    private static IdleFade IdleSince(double at)
    {
        var fade = new IdleFade();
        fade.NoteActivity(false, at);
        return fade;
    }

    [Fact]
    public void FadesAfterSixtySecondsOfIdle()
    {
        var fade = IdleSince(100);
        fade.NoteActivity(false, 130);
        Assert.False(fade.TryFade(enabled: true, engaged: false, reduceMotion: false, now: 159.9));
        Assert.Equal(1, fade.Value(159.9));

        Assert.True(fade.TryFade(enabled: true, engaged: false, reduceMotion: false, now: 160));
        Assert.True(fade.Faded);
        Assert.False(fade.TryFade(enabled: true, engaged: false, reduceMotion: false, now: 161), "only once");
    }

    [Fact]
    public void TheFadeEasesInOutOverSixTenthsOfASecond()
    {
        var fade = IdleSince(0);
        fade.TryFade(true, false, false, 60);

        Assert.Equal(1, fade.Value(60), 6);
        Assert.Equal(1 - 0.55 * 4 * 0.25 * 0.25 * 0.25, fade.Value(60.15), 6);
        Assert.Equal(0.725, fade.Value(60.3), 6);
        Assert.True(fade.IsFading(60.59));
        Assert.Equal(IdleFade.Factor, fade.Value(60.6), 6);
        Assert.False(fade.IsFading(60.6), "settled: no more frames");
        Assert.Equal(0.45, fade.Value(500), 6);
    }

    [Fact]
    public void ReduceMotionCutsTheFade()
    {
        var fade = IdleSince(0);
        Assert.True(fade.TryFade(true, false, reduceMotion: true, now: 60));

        Assert.False(fade.IsFading(60));
        Assert.Equal(0.45, fade.Value(60), 6);
    }

    [Fact]
    public void ActivityRestoresAtOnceAndStartsTheSixtySecondsAgain()
    {
        var fade = IdleSince(0);
        fade.TryFade(true, false, false, 60);

        Assert.True(fade.NoteActivity(true, 70));
        Assert.False(fade.Faded);
        Assert.Equal(1, fade.Value(70));
        Assert.False(fade.IsFading(70));
        Assert.False(fade.NoteActivity(true, 71), "nothing to restore");

        fade.NoteActivity(false, 80);
        Assert.False(fade.TryFade(true, false, false, 139));
        Assert.True(fade.TryFade(true, false, false, 140));
    }

    [Fact]
    public void HoverOrADragKeepsItAndRestoresIt()
    {
        var fade = IdleSince(0);
        Assert.False(fade.TryFade(enabled: true, engaged: true, reduceMotion: false, now: 90));

        Assert.True(fade.TryFade(true, false, false, 91));
        Assert.True(fade.Restore());
        Assert.Equal(1, fade.Value(91.2));
        Assert.False(fade.Restore());
        Assert.True(fade.TryFade(true, false, false, 93), "the pointer gone, it fades again");
    }

    [Fact]
    public void SwitchedOffOrNeverIdleItNeverFades()
    {
        var fade = IdleSince(0);
        Assert.False(fade.TryFade(enabled: false, engaged: false, reduceMotion: false, now: 1000));

        var fresh = new IdleFade();
        Assert.False(fresh.TryFade(true, false, false, 1000), "no idle tick yet");
        Assert.Equal(1, fresh.Value(1000));
    }
}
