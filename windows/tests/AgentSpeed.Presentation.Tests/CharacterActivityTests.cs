using AgentSpeed.Core;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The agents' inputs of a living character (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>) from an orb's agents.</summary>
public class CharacterActivityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static AgentStatus Agent(string id, AgentPhase phase, double? inFlight = null, bool output = false,
        double? lastEnd = null, int? looping = null, double? repeatFailure = null) =>
        new(id, SourceKind.ClaudeCode, "p", null, phase, false, Rate.Zero, null, Start)
        {
            Work = new WorkSignals(looping, null, null, 0, 0),
            InFlightSince = At(inFlight),
            OutputSinceInFlight = output,
            LastResponseEnd = At(lastEnd),
            RepeatFailureAt = At(repeatFailure),
        };

    private static DateTimeOffset? At(double? seconds) => seconds is { } s ? Start.AddSeconds(s) : null;

    [Fact]
    public void ThePhaseIsGeneratingThenToolsThenIdle()
    {
        var activity = new CharacterActivity(Start);
        Assert.Equal(AgentPhase.Idle, activity.Phase);
        activity.Update([Agent("a", AgentPhase.Tools), Agent("b", AgentPhase.Generating, inFlight: 5)]);
        Assert.Equal(AgentPhase.Generating, activity.Phase);
        activity.Update([Agent("a", AgentPhase.Tools), Agent("b", AgentPhase.Idle)]);
        Assert.Equal(AgentPhase.Tools, activity.Phase);
        activity.Update([Agent("a", AgentPhase.Idle)]);
        Assert.Equal(AgentPhase.Idle, activity.Phase);
        activity.Update([]);
        Assert.Equal(AgentPhase.Idle, activity.Phase);
    }

    [Fact]
    public void TheWaitRunsFromTheEarliestRequestUntilAnyOutput()
    {
        var activity = new CharacterActivity(Start);
        activity.Update([Agent("a", AgentPhase.Generating, inFlight: 12), Agent("b", AgentPhase.Generating, inFlight: 7)]);
        Assert.Equal(At(7), activity.WaitingSince);
        activity.Update([Agent("a", AgentPhase.Generating, inFlight: 12), Agent("b", AgentPhase.Generating, inFlight: 7, output: true)]);
        Assert.Null(activity.WaitingSince);
        activity.Update([Agent("a", AgentPhase.Tools, inFlight: 12)]);
        Assert.Null(activity.WaitingSince);
    }

    [Fact]
    public void IdleCountsFromTheNewestEndOrTheStart()
    {
        var activity = new CharacterActivity(Start);
        activity.Update([Agent("a", AgentPhase.Idle)]);
        Assert.Equal(Start, activity.IdleSince);
        activity.Update([Agent("a", AgentPhase.Idle, lastEnd: 40), Agent("b", AgentPhase.Idle, lastEnd: 25)]);
        Assert.Equal(At(40), activity.IdleSince);
        activity.NoteResponseEnd(Start.AddSeconds(55));
        activity.Update([], lastResponseEnd: Start.AddSeconds(50));
        // An older end never moves it back.
        Assert.Equal(At(55), activity.IdleSince);
    }

    [Fact]
    public void TroubleFiresWhenAnAgentStartsLoopingNotWhenTheOrbFirstSeesIt()
    {
        var activity = new CharacterActivity(Start);
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools, looping: 3), Agent("b", AgentPhase.Tools)]));
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools, looping: 4), Agent("b", AgentPhase.Tools)]));
        Assert.True(activity.Update([Agent("a", AgentPhase.Tools, looping: 4), Agent("b", AgentPhase.Tools, looping: 3)]));
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools), Agent("b", AgentPhase.Tools, looping: 3)]));
        Assert.True(activity.Update([Agent("a", AgentPhase.Tools, looping: 3), Agent("b", AgentPhase.Tools, looping: 3)]));
        Assert.Equal(2, activity.Troubles);
    }

    [Fact]
    public void TroubleFiresOnceForEachFailureRightAfterAFailure()
    {
        var activity = new CharacterActivity(Start);
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools, repeatFailure: 10)]));
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools, repeatFailure: 10)]));
        Assert.True(activity.Update([Agent("a", AgentPhase.Tools, repeatFailure: 14)]));
        Assert.False(activity.Update([Agent("a", AgentPhase.Generating)]));
        Assert.True(activity.Update([Agent("a", AgentPhase.Tools, repeatFailure: 20)]));
        // Leaving the list and coming back with the failure already met fires nothing.
        Assert.False(activity.Update([]));
        Assert.False(activity.Update([Agent("a", AgentPhase.Tools, repeatFailure: 20)]));
        Assert.Equal(2, activity.Troubles);
    }
}

/// <summary>A single click on the orb (DESIGN 9 <i>Switching</i> 1, CHARACTERS 5 <c>click</c>): versus a double-click and a drag.</summary>
public class CharacterClickRuleTests
{
    [Fact]
    public void AQuickClickFiresOnceTheDoubleClickTimePassed()
    {
        var rule = new CharacterClickRule(0.5);
        rule.Down(10, 100, 200, clickCount: 1);
        var delay = rule.Up(10.1, 102, 201, moved: false);
        Assert.True(delay is { } d && Math.Abs(d - 0.4) < 1e-9);
        Assert.NotNull(rule.Pending);
        Assert.Null(rule.Take(10.3));
        // The press point.
        Assert.Equal((100.0, 200.0), rule.Take(10.5));
        // Taken once.
        Assert.Null(rule.Take(11));
        Assert.Null(rule.Pending);
    }

    [Fact]
    public void ADoubleClickNeverFires()
    {
        var rule = new CharacterClickRule(0.5);
        rule.Down(10, 100, 200, clickCount: 1);
        rule.Up(10.08, 100, 200, moved: false);
        rule.Down(10.2, 100, 200, clickCount: 2);
        Assert.Null(rule.Up(10.28, 100, 200, moved: false));
        Assert.Null(rule.Take(11));
    }

    [Fact]
    public void ADragALongPressAndAWanderingPressNeverFire()
    {
        var rule = new CharacterClickRule(0.5);
        rule.Down(10, 100, 200, clickCount: 1);
        Assert.Null(rule.Up(10.1, 100, 200, moved: true));
        rule.Down(20, 100, 200, clickCount: 1);
        Assert.Null(rule.Up(20.6, 100, 200, moved: false));
        rule.Down(30, 100, 200, clickCount: 1);
        Assert.Null(rule.Up(30.1, 105, 200, moved: false));
        rule.Down(40, 100, 200, clickCount: 1, armed: false);
        Assert.Null(rule.Up(40.1, 100, 200, moved: false));
        Assert.Null(rule.Take(50));
    }

    [Fact]
    public void AReleaseAfterTheDoubleClickTimeFiresAtOnceAndCancelDropsIt()
    {
        var rule = new CharacterClickRule(0.2) { DoubleClickInterval = 0.3 };
        rule.Down(10, 0, 0, clickCount: 1);
        Assert.Equal(0.0, rule.Up(10.45, 4, 0, moved: false));
        Assert.Equal((0.0, 0.0), rule.Take(10.45));

        rule.Down(20, 0, 0, clickCount: 1);
        Assert.NotNull(rule.Up(20.1, 0, 0, moved: false));
        rule.Cancel();
        Assert.Null(rule.Take(30));
        Assert.Null(rule.Up(30.1, 0, 0, moved: false));
    }
}
