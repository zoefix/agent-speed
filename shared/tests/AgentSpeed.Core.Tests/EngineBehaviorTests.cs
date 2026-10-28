using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.EngineCaseTests;

namespace AgentSpeed.Core.Tests;

public sealed class EngineBehaviorTests
{
    private const long Day1 = 1_792_454_400_000;
    private const long Day2 = Day1 + 86_400_000;
    private const string Agent = "a1";

    [Fact]
    public void DayRolloverRestartsTodayTotalsAndPeak()
    {
        var engine = new SpeedEngine(Day1);
        engine.UpdateAgent(Info(lastLineMs: Day2 - 60_000));
        engine.Upsert(Response("r1", Day2 - 62_000, Day2 - 60_000, output: 2000, input: 100), isCompletion: true, seeded: false);

        var before = engine.Compute(Day2 - 60_000, Day1, observeLive: true);
        Assert.Equal(1, before.Today.Requests);
        Assert.Equal(2000, before.Today.Output);
        AssertClose(2000 / 3.0, before.Live.Output, "live before rollover");
        AssertClose(2000 / 3.0, before.PeakToday.Output, "peak before rollover");

        engine.Upsert(Response("r2", Day2 - 1000, Day2, output: 30, input: 10), isCompletion: true, seeded: false);
        engine.Upsert(Response("r3", Day2 + 2000, Day2 + 5000, output: 300, input: 1000), isCompletion: true, seeded: false);
        engine.UpdateAgent(Info(lastLineMs: Day2 + 5000));

        var after = engine.Compute(Day2 + 10_000, Day2, observeLive: true);
        Assert.Equal(Day2, engine.TodayStartMs);
        Assert.Equal(2, after.Today.Requests);
        Assert.Equal(330, after.Today.Output);
        Assert.Equal(1010, after.Today.Input);
        Assert.Equal(after.Today, after.SourceToday[(int)SourceKind.Codex]);
        Assert.True(after.AverageToday.HasValue);
        AssertClose(330 / 4.0, after.AverageToday.GetValueOrDefault(), "average after rollover");
        AssertClose(100, after.PeakToday.Output, "peak after rollover");

        engine.Upsert(Response("r-late", Day2 - 510, Day2 - 10, output: 7, input: 1), isCompletion: true, seeded: false);
        var later = engine.Compute(Day2 + 11_000, Day2, observeLive: true);
        Assert.Equal(2, later.Today.Requests);
        Assert.Equal(330, later.Today.Output);
        AssertClose(100, later.PeakToday.Output, "peak after late response");
    }

    [Fact]
    public void ResponsesBeforeTodayStartAreExcluded()
    {
        var engine = new SpeedEngine(Day2);
        engine.Upsert(Response("old", Day2 - 3000, Day2 - 1, output: 50, input: 5), isCompletion: true, seeded: true);
        engine.Upsert(Response("edge", Day2 - 1000, Day2, output: 20, input: 2), isCompletion: true, seeded: true);

        var frame = engine.Compute(Day2 + 1000, Day2, observeLive: false);

        Assert.Equal(1, frame.Today.Requests);
        Assert.Equal(20, frame.Today.Output);
        Assert.Equal(2, frame.Today.Input);
    }

    [Fact]
    public void UpsertWithSameKeyReplacesTheStoredResponse()
    {
        var engine = new SpeedEngine(Day1);
        engine.UpdateAgent(Info(lastLineMs: Day1 + 4000));
        engine.Upsert(Response("r1", Day1 + 1000, Day1 + 3000, output: 100, input: 10), isCompletion: true, seeded: true);
        engine.Upsert(Response("r1", Day1 + 1000, Day1 + 4000, output: 300, input: 10), isCompletion: false, seeded: true);

        var frame = engine.Compute(Day1 + 4000, Day1, observeLive: false);

        Assert.Equal(1, frame.Today.Requests);
        Assert.Equal(300, frame.Today.Output);
        Assert.Equal(10, frame.Today.Input);
        AssertClose(300 / 3.0, frame.Live.Output, "live after replacement");
        Assert.True(frame.AverageToday.HasValue);
        AssertClose(100, frame.AverageToday.GetValueOrDefault(), "average after replacement");
    }

    [Fact]
    public void OnlyLiveCompletionsProduceResponseEvents()
    {
        var engine = new SpeedEngine(Day1);
        engine.UpdateAgent(Info(lastLineMs: Day1 + 9000));
        engine.Upsert(Response("seeded", Day1 + 1000, Day1 + 2000, output: 10, input: 1), isCompletion: true, seeded: true);
        engine.Upsert(Response("live", Day1 + 3000, Day1 + 5000, output: 40, input: 4), isCompletion: true, seeded: false);
        engine.Upsert(Response("live", Day1 + 3000, Day1 + 6000, output: 60, input: 4), isCompletion: false, seeded: false);

        var frame = engine.Compute(Day1 + 9000, Day1, observeLive: true);

        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(frame.Events));
        Assert.Equal(SourceKind.Codex, completed.Response.Source);
        Assert.Equal("proj", completed.Response.Project);
        Assert.NotNull(completed.Response.Speed);
        Assert.Empty(engine.Compute(Day1 + 9500, Day1, observeLive: true).Events);
    }

    [Fact]
    public void HistoryHasThreeHundredBucketsEndingAtNow()
    {
        var engine = new SpeedEngine(Day1);
        var now = Day1 + 600_250;

        var frame = engine.Compute(now, Day1, observeLive: false);

        Assert.Equal(SpeedEngine.HistorySeconds, frame.History.Count);
        Assert.Equal(Day1 + 600_000, frame.History[^1].Time.ToUnixTimeMilliseconds());
        Assert.Equal(Day1 + 301_000, frame.History[0].Time.ToUnixTimeMilliseconds());
        for (var i = 1; i < frame.History.Count; i++)
        {
            Assert.Equal(1000, (frame.History[i].Time - frame.History[i - 1].Time).TotalMilliseconds);
        }
    }

    [Fact]
    public void AgentsListFiltersStaleAgentsAndSorts()
    {
        const long now = 1_000_001_000_000;
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(new AgentInfo("recent-idle", SourceKind.Codex, null, "m1", false, AgentPhase.Idle, null, now - 540_000));
        engine.UpdateAgent(new AgentInfo("old-idle", SourceKind.Codex, null, "m1", false, AgentPhase.Idle, null, now - 660_000));
        engine.UpdateAgent(new AgentInfo("gen-fresh", SourceKind.Codex, null, "m1", false, AgentPhase.Generating, now - 60_000, now - 60_000));
        engine.UpdateAgent(new AgentInfo("gen-stale", SourceKind.Codex, null, "m1", false, AgentPhase.Generating, now - 400_000, now - 400_000));
        engine.UpdateAgent(new AgentInfo("quiet-new", SourceKind.ClaudeCode, null, "m2", false, AgentPhase.Tools, null, now - 1000));
        engine.UpdateAgent(new AgentInfo("busy-old", SourceKind.Codex, null, "m1", false, AgentPhase.Idle, null, now - 2000));
        engine.Upsert(
            new ParsedResponse("r", SourceKind.Codex, "busy-old", "m1", now - 2500, now - 2000, new TokenCounts(Output: 100)),
            isCompletion: true,
            seeded: true);

        var frame = engine.Compute(now, 0, observeLive: false);

        Assert.Equal(["gen-fresh", "busy-old", "quiet-new", "gen-stale", "recent-idle"], frame.Agents.Select(a => a.Info.Id));
        Assert.Equal(
            [AgentPhase.Generating, AgentPhase.Idle, AgentPhase.Tools, AgentPhase.Idle, AgentPhase.Idle],
            frame.Agents.Select(a => a.Phase));
        Assert.Equal(1, frame.GeneratingCount);
        Assert.Equal(1, frame.ToolsCount);
        AssertClose(100 / 3.0, frame.Agents[1].Live.Output, "busy-old live");
    }

    [Fact]
    public void LiveRateIsSplitPerSourceAndAgent()
    {
        const long b = 1_000_000_000_000;
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(new AgentInfo("a1", SourceKind.Codex, null, "m1", false, AgentPhase.Idle, null, b + 4000));
        engine.UpdateAgent(new AgentInfo("a2", SourceKind.ClaudeCode, null, "m2", false, AgentPhase.Idle, null, b + 3000));
        engine.Upsert(new ParsedResponse("r1", SourceKind.Codex, "a1", "m1", b, b + 4000, new TokenCounts(Output: 200)), true, true);
        engine.Upsert(new ParsedResponse("r2", SourceKind.ClaudeCode, "a2", "m2", b + 1000, b + 3000, new TokenCounts(Output: 100)), true, true);

        var frame = engine.Compute(b + 3000, 0, observeLive: false);

        AssertClose(50, frame.SourceLive[(int)SourceKind.Codex].Output, "codex live");
        AssertClose(100 / 3.0, frame.SourceLive[(int)SourceKind.ClaudeCode].Output, "claude live");
        AssertClose(0, frame.SourceLive[(int)SourceKind.ClaudeDesktop].Output, "desktop live");
        AssertClose(50, frame.Agents.Single(a => a.Info.Id == "a1").Live.Output, "a1 live");
        AssertClose(100 / 3.0, frame.Agents.Single(a => a.Info.Id == "a2").Live.Output, "a2 live");
    }

    [Theory]
    [InlineData(true, 100.0)]
    [InlineData(false, 100.0 / 3)]
    public void PeakIncludesObservedLiveValueOnlyWhileObserving(bool observeLive, double expectedPeak)
    {
        const long b = 1_000_000_000_000;
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(new AgentInfo("a1", SourceKind.Codex, null, "m1", false, AgentPhase.Generating, b + 1000, b + 1000));
        engine.Upsert(new ParsedResponse("r1", SourceKind.Codex, "a1", "m1", b, b + 1000, new TokenCounts(Output: 100)), true, true);

        var frame = engine.Compute(b + 4000, 0, observeLive);

        AssertClose(100, frame.Live.Output, "live");
        AssertClose(1, frame.EstimatedShare, "share");
        AssertClose(expectedPeak, frame.PeakToday.Output, "peak");
        var estimated = frame.History.ToDictionary(p => p.Time.ToUnixTimeMilliseconds(), p => p.Estimated.Output);
        AssertClose(0, estimated[b], "estimated history before start");
        AssertClose(100, estimated[b + 1000], "estimated history");
        AssertClose(100, estimated[b + 3000], "estimated history");
        AssertClose(0, estimated[b + 4000], "estimated history at now");
    }

    private static AgentInfo Info(long lastLineMs) =>
        new(Agent, SourceKind.Codex, "proj", "m1", false, AgentPhase.Idle, null, lastLineMs);

    private static ParsedResponse Response(string key, long? startMs, long endMs, long output, long input) =>
        new(key, SourceKind.Codex, Agent, "m1", startMs, endMs, new TokenCounts(Input: input, Output: output));
}
