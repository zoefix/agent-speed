using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Live completion events only for responses that ended in the last two minutes (METRICS 6.4).</summary>
public sealed class CompletionWindowTests
{
    private const long Now = 1_792_490_400_000;
    private const long TodayStart = Now - 3_600_000;
    private const string AgentId = "/home/me/.gemini/tmp/rocket/chats/session.jsonl";

    [Fact]
    public void WindowIsTwoMinutes() => Assert.Equal(120_000, SpeedEngine.CompletionEventWindowMs);

    [Fact]
    public void LiveCompletionThatEndedThreeMinutesAgoRaisesNoEventButCountsToday()
    {
        var engine = new SpeedEngine(TodayStart);

        engine.Upsert(Response("old", Now - 180_000, output: 120), isCompletion: true, seeded: false);
        var frame = engine.Compute(Now, TodayStart, observeLive: true);

        Assert.Empty(frame.Events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal(1, frame.Today.Requests);
        Assert.Equal(120, frame.Today.Output);
    }

    [Fact]
    public void LiveCompletionThatEndedThirtySecondsAgoRaisesAnEvent()
    {
        var engine = new SpeedEngine(TodayStart);
        engine.UpdateAgent(new AgentInfo(AgentId, SourceKind.Gemini, "rocket", "gemini-2.5-pro", false, AgentPhase.Idle, null, Now - 30_000));

        engine.Upsert(Response("recent", Now - 30_000, output: 90), isCompletion: true, seeded: false);
        var frame = engine.Compute(Now, TodayStart, observeLive: true);

        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(frame.Events));
        Assert.Equal((SourceKind.Gemini, "rocket", 90L, TimeSpan.FromSeconds(5)),
            (completed.Response.Source, completed.Response.Project, completed.Response.OutputTokens, completed.Response.Duration));
        Assert.Equal(1, frame.Today.Requests);
        Assert.Empty(engine.Compute(Now + 500, TodayStart, observeLive: true).Events);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(119_999, true)]
    [InlineData(120_000, true)]
    [InlineData(120_001, false)]
    public void WindowBoundaryIsInclusive(long ageMs, bool expected)
    {
        var engine = new SpeedEngine(TodayStart);

        engine.Upsert(Response("edge", Now - ageMs, output: 10), isCompletion: true, seeded: false);

        Assert.Equal(expected, engine.Compute(Now, TodayStart, observeLive: true).Events.OfType<SpeedEvent.ResponseCompleted>().Any());
    }

    [Fact]
    public void SeededResponsesNeverRaiseEvents()
    {
        var engine = new SpeedEngine(TodayStart);

        engine.Upsert(Response("seeded", Now - 1000, output: 50), isCompletion: true, seeded: true);
        var frame = engine.Compute(Now, TodayStart, observeLive: true);

        Assert.Empty(frame.Events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal(1, frame.Today.Requests);
        Assert.Empty(engine.Compute(Now + 500, TodayStart, observeLive: true).Events.OfType<SpeedEvent.ResponseCompleted>());
    }

    [Fact]
    public void UpdatesNeverRaiseEventsAndReplaceTheTotals()
    {
        var engine = new SpeedEngine(TodayStart);
        engine.Upsert(Response("k", Now - 1000, output: 50), isCompletion: true, seeded: true);
        engine.Compute(Now, TodayStart, observeLive: true);

        engine.Upsert(Response("k", Now - 1000, output: 70), isCompletion: false, seeded: false);
        var frame = engine.Compute(Now + 500, TodayStart, observeLive: true);

        Assert.Empty(frame.Events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal((1L, 70L), (frame.Today.Requests, frame.Today.Output));
    }

    [Fact]
    public void WindowIsMeasuredWhenTheFrameIsComputed()
    {
        var engine = new SpeedEngine(TodayStart);

        engine.Upsert(Response("late", Now - 30_000, output: 10), isCompletion: true, seeded: false);
        var frame = engine.Compute(Now + 100_000, TodayStart, observeLive: true);

        Assert.Empty(frame.Events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal(1, frame.Today.Requests);
    }

    [Fact]
    public void OnlyRecentResponsesOfABatchRaiseEvents()
    {
        var engine = new SpeedEngine(TodayStart);

        engine.Upsert(Response("a", Now - 600_000, output: 1), isCompletion: true, seeded: false);
        engine.Upsert(Response("b", Now - 10_000, output: 2), isCompletion: true, seeded: false);
        engine.Upsert(Response("c", Now - 200_000, output: 3), isCompletion: true, seeded: false);
        engine.Upsert(Response("d", Now - 1_000, output: 4), isCompletion: true, seeded: false);
        var frame = engine.Compute(Now, TodayStart, observeLive: true);

        Assert.Equal([2L, 4L], frame.Events.OfType<SpeedEvent.ResponseCompleted>().Select(e => e.Response.OutputTokens));
        Assert.Equal((4L, 10L), (frame.Today.Requests, frame.Today.Output));
    }

    private static ParsedResponse Response(string key, long endMs, long output) => new(
        key, SourceKind.Gemini, AgentId, "gemini-2.5-pro", endMs - 5000, endMs,
        new TokenCounts(Input: 1000, Output: output));
}
