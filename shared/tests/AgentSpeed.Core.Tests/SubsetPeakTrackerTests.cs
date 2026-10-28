using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary><c>peakToday_A</c> and <c>newPeak_A</c> (METRICS 6.8).</summary>
public sealed class SubsetPeakTrackerTests
{
    private const long B = 1_000_000_000_000;
    private const long Day = B - 3_600_000;

    [Fact]
    public void NothingFiresWhileTheMeasuredPeakIsUnknown()
    {
        var tracker = new SubsetPeakTracker();

        Assert.False(tracker.Update(50, null, Day, B));
        Assert.False(tracker.Update(500, null, Day, B + 500));
        Assert.Equal(500, tracker.PeakTodayOutput);

        Assert.False(tracker.Update(900, 40, Day, B + 1000));
        Assert.Equal(900, tracker.PeakTodayOutput);
        Assert.True(tracker.Update(901, 40, Day, B + 1500));
    }

    [Fact]
    public void TheFirstKnownTickOnlyTakesThePeakIn()
    {
        var tracker = new SubsetPeakTracker();

        Assert.False(tracker.Update(300, 100, Day, B));
        Assert.Equal(300, tracker.PeakTodayOutput);
        Assert.False(tracker.Update(300.5, 100, Day, B + 500));
        Assert.True(tracker.Update(301.5, 100, Day, B + 1000));
        Assert.Equal(301.5, tracker.PeakTodayOutput);
    }

    [Fact]
    public void NeedsTwentyTokensPerSecondAndAMarginOfOne()
    {
        var below = new SubsetPeakTracker();
        below.Update(0, 0, Day, B);
        Assert.False(below.Update(19.99, 0, Day, B + 500));

        var tracker = new SubsetPeakTracker();
        tracker.Update(0, 0, Day, B);
        Assert.True(tracker.Update(20, 0, Day, B + 500));

        var later = B + 100_000;
        Assert.False(tracker.Update(20.99, 0, Day, later));
        Assert.False(tracker.Update(21.98, 0, Day, later + 500), "the peak took 20.99 in");
        Assert.True(tracker.Update(22.98, 0, Day, later + 1000));

        var measured = B + 200_000;
        Assert.False(tracker.Update(10, 40, Day, measured));
        Assert.False(tracker.Update(30, 40, Day, measured + 500), "below the measured peak");
        Assert.True(tracker.Update(41, 40, Day, measured + 1000));
    }

    [Fact]
    public void FiresAtMostOncePerMinute()
    {
        var tracker = new SubsetPeakTracker();
        tracker.Update(0, 0, Day, B);

        Assert.True(tracker.Update(100, 0, Day, B + 500));
        Assert.False(tracker.Update(200, 0, Day, B + 60_499));
        Assert.True(tracker.Update(300, 0, Day, B + 60_500));
        Assert.Equal(300, tracker.PeakTodayOutput);
    }

    [Fact]
    public void RestartForgetsTheObservedPeakAndStaysQuietForAMinute()
    {
        var tracker = new SubsetPeakTracker();
        tracker.Update(0, 50, Day, B);
        tracker.Update(500, 50, Day, B + 500);
        Assert.Equal(500, tracker.PeakTodayOutput);

        tracker.Restart(B + 1000);
        Assert.Equal(0, tracker.PeakTodayOutput);
        Assert.False(tracker.Update(80, 50, Day, B + 1000));
        Assert.Equal(80, tracker.PeakTodayOutput);
        Assert.False(tracker.Update(400, 50, Day, B + 30_000));
        Assert.False(tracker.Update(800, 50, Day, B + 60_999));
        Assert.True(tracker.Update(900, 50, Day, B + 61_000));
        Assert.Equal(60_000, SubsetPeakTracker.QuietPeriodMs);
    }

    [Fact]
    public void ANewDayResetsThePeaksAndDoesNotFireOnItsFirstTick()
    {
        var tracker = new SubsetPeakTracker();
        tracker.Update(0, 300, Day, B);
        tracker.Update(250, 300, Day, B + 500);
        Assert.Equal(300, tracker.PeakTodayOutput);

        const long nextDay = Day + 86_400_000;
        Assert.False(tracker.Update(120, 0, nextDay, nextDay + 1000));
        Assert.Equal(120, tracker.PeakTodayOutput);
        Assert.True(tracker.Update(121, 0, nextDay, nextDay + 1500));
    }

    /// <summary>A single-source orb fires exactly when the engine's all-sources new peak does (METRICS 6.4 parity).</summary>
    [Fact]
    public void MatchesTheEngineRuleForOneSource()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(new AgentInfo("a", SourceKind.Codex, "p", "m", false, AgentPhase.Idle, null, B));
        var tracker = new SubsetPeakTracker();
        var codex = SourceSet.Of(SourceKind.Codex);
        (long Offset, ParsedResponse? Response)[] steps =
        [
            (0, null),
            (3000, Response("slow", B, B + 3000, 30)),
            (6000, Response("fast", B + 3000, B + 6000, 300)),
            (9000, Response("faster", B + 6000, B + 9000, 600)),
            (40_000, null),
            (73_000, Response("fastest", B + 70_000, B + 73_000, 1200)),
            (74_000, null),
        ];

        var fired = new List<long>();
        var engineFired = new List<long>();
        foreach (var (offset, response) in steps)
        {
            var now = B + offset;
            if (response is not null)
            {
                engine.Upsert(response, isCompletion: true, seeded: false);
            }

            var frame = engine.Compute(now, Day, observeLive: true);
            if (frame.Events.OfType<SpeedEvent.NewPeak>().Any())
            {
                engineFired.Add(offset);
            }

            if (tracker.Update(frame.SourceLive[(int)SourceKind.Codex].Output, engine.MeasuredPeakOutput(codex), Day, now))
            {
                fired.Add(offset);
            }

            EngineCaseTests.AssertClose(frame.PeakToday.Output, tracker.PeakTodayOutput, $"peak @{offset}");
        }

        Assert.Equal(new long[] { 6000, 73_000 }, engineFired);
        Assert.Equal(engineFired, fired);
    }

    private static ParsedResponse Response(string key, long start, long end, long output) =>
        new(key, SourceKind.Codex, "a", "m", start, end, new TokenCounts(Output: output));
}
