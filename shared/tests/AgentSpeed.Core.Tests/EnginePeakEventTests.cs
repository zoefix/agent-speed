using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class EnginePeakEventTests
{
    private const long B = 1_000_000_000_000;
    private const long Day = B - 3_600_000;

    [Fact]
    public void NewPeakFiresWhenLiveExceedsThePreviousPeakAndRespectsTheCooldown()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(Agent());
        Assert.Empty(engine.Compute(B, Day, true).Events);

        engine.Upsert(Response("r1", B, B + 3000, 90), isCompletion: true, seeded: false);
        Assert.Single(engine.Compute(B + 3000, Day, true).Events.OfType<SpeedEvent.NewPeak>());

        engine.Upsert(Response("r2", B + 3000, B + 6000, 180), isCompletion: true, seeded: false);
        var withinCooldown = engine.Compute(B + 6000, Day, true);
        Assert.Empty(withinCooldown.Events.OfType<SpeedEvent.NewPeak>());
        Assert.Equal(60, withinCooldown.PeakToday.Output, 6);

        engine.Upsert(Response("r3", B + 63_000, B + 66_000, 300), isCompletion: true, seeded: false);
        var peak = Assert.Single(engine.Compute(B + 66_000, Day, true).Events.OfType<SpeedEvent.NewPeak>());
        Assert.Equal(100, peak.Value, 6);
    }

    [Fact]
    public void NewPeakNeedsTwentyTokensPerSecondAndAMarginOfOne()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(Agent());
        Assert.Empty(engine.Compute(B, Day, true).Events);

        Assert.Empty(PeakEvents(engine, "slow", B, 57));
        Assert.Single(PeakEvents(engine, "first", B + 100_000, 63));
        Assert.Empty(PeakEvents(engine, "near", B + 200_000, 65));
        Assert.Single(PeakEvents(engine, "above", B + 300_000, 69));
    }

    private static IEnumerable<SpeedEvent.NewPeak> PeakEvents(SpeedEngine engine, string key, long start, long output)
    {
        engine.Upsert(Response(key, start, start + 3000, output), isCompletion: true, seeded: false);
        return engine.Compute(start + 3000, Day, true).Events.OfType<SpeedEvent.NewPeak>().ToList();
    }

    [Fact]
    public void SeededHistoryIsThePreviousPeakAndNoEventIsRaisedWithoutObservation()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(Agent());
        engine.Upsert(Response("seeded", B, B + 3000, 600), isCompletion: true, seeded: true);

        var seeding = engine.Compute(B + 3000, Day, observeLive: false);
        Assert.Empty(seeding.Events);
        Assert.Equal(200, seeding.PeakToday.Output, 6);

        engine.Upsert(Response("live", B + 100_000, B + 103_000, 300), isCompletion: true, seeded: false);
        var live = engine.Compute(B + 103_000, Day, observeLive: true);
        Assert.Single(live.Events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Empty(live.Events.OfType<SpeedEvent.NewPeak>());
    }

    private static AgentInfo Agent() =>
        new("a1", SourceKind.Codex, "p", "m1", false, AgentPhase.Idle, null, B);

    private static ParsedResponse Response(string key, long start, long end, long output) =>
        new(key, SourceKind.Codex, "a1", "m1", start, end, new TokenCounts(Output: output));
}
