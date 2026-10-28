using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Tests;

public class TierTrackerTests
{
    private const double R = 400;
    private const double Step = 0.05;

    /// <summary>Feeds a constant value every 50 ms from <paramref name="from"/> (inclusive) to <paramref name="to"/> (inclusive).</summary>
    private static List<(double Time, SpeedTier Displayed, bool Changed)> Hold(TierTracker tracker, double value, double from, double to)
    {
        var log = new List<(double, SpeedTier, bool)>();
        var steps = (int)Math.Round((to - from) / Step);
        for (var i = 0; i <= steps; i++)
        {
            var t = from + i * Step;
            var changed = tracker.Update(value, R, t);
            log.Add((t, tracker.Displayed, changed));
        }

        return log;
    }

    private static TierTracker TrackerShowing(double value, out double now)
    {
        var tracker = new TierTracker();
        Hold(tracker, value, 0, 2);
        now = 2;
        return tracker;
    }

    [Fact]
    public void StartsIdleWithNoDeadline()
    {
        var tracker = new TierTracker();
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);
        Assert.Equal(SpeedTier.Idle, tracker.Candidate);
        Assert.Null(tracker.NextDeadline);
        Assert.Equal(TierTracker.DefaultHoldSeconds, tracker.HoldSeconds);
        Assert.Equal(TierTracker.DefaultIdleSeconds, tracker.IdleSeconds);
    }

    [Fact]
    public void EnteringATierRequiresAHoldOf800Milliseconds()
    {
        var tracker = new TierTracker();

        Assert.False(tracker.Update(100, R, 0));
        Assert.Equal(SpeedTier.Boost, tracker.Candidate);
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);

        Assert.False(tracker.Update(100, R, 0.4));
        Assert.False(tracker.Update(100, R, 0.79));
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);

        Assert.True(tracker.Update(100, R, 0.8));
        Assert.Equal(SpeedTier.Boost, tracker.Displayed);

        Assert.False(tracker.Update(100, R, 0.85));
        Assert.Equal(SpeedTier.Boost, tracker.Displayed);
    }

    [Theory]
    [InlineData(10, SpeedTier.Cruise)]
    [InlineData(60, SpeedTier.Boost)]
    [InlineData(150, SpeedTier.Overdrive)]
    [InlineData(300, SpeedTier.Redline)]
    [InlineData(5000, SpeedTier.Redline)]
    public void HeldValueShowsItsRawTier(double value, SpeedTier expected)
    {
        var tracker = TrackerShowing(value, out _);
        Assert.Equal(expected, tracker.Displayed);
    }

    [Theory]
    [InlineData(100, SpeedTier.Boost, 57, 53, SpeedTier.Cruise)]
    [InlineData(200, SpeedTier.Overdrive, 140, 130, SpeedTier.Boost)]
    [InlineData(350, SpeedTier.Redline, 280, 260, SpeedTier.Overdrive)]
    public void HysteresisKeepsTierUntilNinetyPercentOfThreshold(
        double enter, SpeedTier tier, double insideBand, double belowBand, SpeedTier lower)
    {
        var tracker = TrackerShowing(enter, out var now);
        Assert.Equal(tier, tracker.Displayed);
        Assert.InRange(insideBand, HeatMath.Threshold(tier, R) * TierTracker.ExitRatio, HeatMath.Threshold(tier, R));

        var inside = Hold(tracker, insideBand, now, now + 5);
        Assert.All(inside, e => Assert.Equal(tier, e.Displayed));
        Assert.Equal(tier, tracker.Candidate);

        Hold(tracker, belowBand, now + 5.05, now + 5.75);
        Assert.Equal(tier, tracker.Displayed);
        Assert.Equal(lower, tracker.Candidate);

        Hold(tracker, belowBand, now + 5.8, now + 6.0);
        Assert.Equal(lower, tracker.Displayed);
    }

    [Fact]
    public void FallingThroughSeveralBandsLandsOnTheTierStillAboveItsExit()
    {
        var tracker = TrackerShowing(400, out var now);
        Assert.Equal(SpeedTier.Redline, tracker.Displayed);

        // 140 is below the redline exit (270) but inside the overdrive band (exit 135, entry 150).
        Hold(tracker, 140, now, now + 2);
        Assert.Equal(SpeedTier.Overdrive, tracker.Displayed);
    }

    [Fact]
    public void FlappingShorterThanHoldNeverChangesTheDisplayedTier()
    {
        var tracker = TrackerShowing(10, out var now);
        Assert.Equal(SpeedTier.Cruise, tracker.Displayed);

        var t = now;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var high = Hold(tracker, 250, t, t + 0.7);
            var low = Hold(tracker, 10, t + 0.75, t + 1.45);
            Assert.All(high, e => Assert.False(e.Changed));
            Assert.All(low, e => Assert.False(e.Changed));
            t += 1.5;
        }

        Assert.Equal(SpeedTier.Cruise, tracker.Displayed);
    }

    [Fact]
    public void DroppingToZeroShowsCruiseFirstThenIdleAfterThreeSeconds()
    {
        var tracker = TrackerShowing(100, out var now);
        Assert.Equal(SpeedTier.Boost, tracker.Displayed);

        var log = Hold(tracker, 0, now, now + 4);

        var cruiseShown = log.First(e => e.Displayed != SpeedTier.Boost);
        Assert.Equal(SpeedTier.Cruise, cruiseShown.Displayed);
        Assert.Equal(now + 0.8, cruiseShown.Time, 6);

        var idleShown = log.First(e => e.Displayed == SpeedTier.Idle);
        Assert.Equal(now + 3, idleShown.Time, 6);

        Assert.All(log.Where(e => e.Time < now + 3 - 1e-6), e => Assert.NotEqual(SpeedTier.Idle, e.Displayed));
        Assert.All(log.Where(e => e.Time >= now + 3 - 1e-6), e => Assert.Equal(SpeedTier.Idle, e.Displayed));
    }

    [Fact]
    public void ActivityBeforeIdleTimeoutRestartsTheIdleClock()
    {
        var tracker = TrackerShowing(10, out var now);
        Hold(tracker, 0, now, now + 2.5);
        Assert.Equal(SpeedTier.Cruise, tracker.Displayed);

        tracker.Update(10, R, now + 2.55);
        Hold(tracker, 0, now + 2.6, now + 5.5);
        Assert.Equal(SpeedTier.Cruise, tracker.Displayed);

        Hold(tracker, 0, now + 5.55, now + 5.65);
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);
    }

    [Fact]
    public void ValueBelowCruiseFromIdleStaysIdle()
    {
        var tracker = new TierTracker();
        var log = Hold(tracker, 0.3, 0, 10);
        Assert.All(log, e => Assert.Equal(SpeedTier.Idle, e.Displayed));
        Assert.Null(tracker.NextDeadline);
    }

    [Fact]
    public void NextDeadlineTracksPendingTierAndIdleTimeout()
    {
        var tracker = new TierTracker();
        tracker.Update(100, R, 10);
        Assert.Equal(10.8, tracker.NextDeadline!.Value, 9);

        tracker.Update(100, R, 10.8);
        Assert.Equal(SpeedTier.Boost, tracker.Displayed);
        Assert.Null(tracker.NextDeadline);

        tracker.Update(0, R, 20);
        Assert.Equal(SpeedTier.Cruise, tracker.Candidate);
        Assert.Equal(20.8, tracker.NextDeadline!.Value, 9);

        tracker.Update(0, R, 20.8);
        Assert.Equal(SpeedTier.Cruise, tracker.Displayed);
        Assert.Equal(23, tracker.NextDeadline!.Value, 9);

        tracker.Update(0, R, 23);
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);
        Assert.Null(tracker.NextDeadline);
    }

    [Fact]
    public void NextDeadlineIsEarliestOfPendingAndIdle()
    {
        var tracker = new TierTracker(holdSeconds: 0.8, idleSeconds: 0.5);
        Hold(tracker, 100, 0, 2);
        tracker.Update(0, R, 5);
        Assert.Equal(5.5, tracker.NextDeadline!.Value, 9);
    }

    [Fact]
    public void UpdatingAtTheDeadlineChangesTheTier()
    {
        var tracker = new TierTracker();
        tracker.Update(250, R, 3);
        var deadline = tracker.NextDeadline!.Value;
        Assert.False(tracker.Update(250, R, deadline - 0.01));
        Assert.True(tracker.Update(250, R, deadline));
        Assert.Equal(SpeedTier.Overdrive, tracker.Displayed);
    }

    [Fact]
    public void MonotonicRampReachesRedlineWithoutGoingDown()
    {
        var tracker = new TierTracker();
        var displayed = new List<SpeedTier>();
        const int steps = 200;
        for (var i = 0; i <= steps; i++)
        {
            var t = i * Step;
            tracker.Update(1.2 * R * i / steps, R, t);
            displayed.Add(tracker.Displayed);
        }

        Hold(tracker, 1.2 * R, (steps + 1) * Step, (steps + 1) * Step + 1);
        displayed.Add(tracker.Displayed);

        for (var i = 1; i < displayed.Count; i++)
        {
            Assert.True(displayed[i] >= displayed[i - 1], $"Tier went down at step {i}");
        }

        Assert.Equal(SpeedTier.Redline, tracker.Displayed);
        Assert.Contains(SpeedTier.Cruise, displayed);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-50)]
    public void InvalidValuesAreTreatedAsZero(double value)
    {
        var tracker = TrackerShowing(100, out var now);
        Hold(tracker, value, now, now + 3.5);
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);
    }

    [Fact]
    public void ResetSetsDisplayedAndCandidate()
    {
        var tracker = new TierTracker();
        tracker.Update(100, R, 0);
        tracker.Reset(SpeedTier.Overdrive);
        Assert.Equal(SpeedTier.Overdrive, tracker.Displayed);
        Assert.Equal(SpeedTier.Overdrive, tracker.Candidate);
        Assert.Null(tracker.NextDeadline);

        tracker.Reset();
        Assert.Equal(SpeedTier.Idle, tracker.Displayed);
    }

    [Fact]
    public void UpdateReturnsTrueExactlyWhenDisplayedChanges()
    {
        var tracker = new TierTracker();
        var previous = tracker.Displayed;
        var values = new[] { 0, 10, 10, 100, 100, 400, 400, 60, 5, 0, 0 };
        var t = 0.0;
        foreach (var value in values)
        {
            foreach (var entry in Hold(tracker, value, t, t + 1))
            {
                Assert.Equal(entry.Displayed != previous, entry.Changed);
                previous = entry.Displayed;
            }

            t += 1.05;
        }
    }
}
