using System.Text;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.GrokBuild;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests;

public sealed class GrokBatchTests
{
    [Fact]
    public void NativeBatchRequestCountsReachTodayHistoryAndReportingWithTheSameSpeedFormula()
    {
        var response = new ParsedResponse("batch", SourceKind.GrokBuild, "agent", "grok-4.5", 1000, 7000,
            new TokenCounts(Input: 1200, CachedInput: 300, Output: 300, Reasoning: 30), RequestCount: 3);
        var today = new TodayLedger(0);
        today.Upsert(response);
        Assert.Equal(3, today.Totals.Requests);
        Assert.Equal(3, today.Cost.UnpricedRequests);
        Assert.Equal(50, today.AverageOutputSpeed);
        today.Upsert(response with { RequestCount = 2, Tokens = response.Tokens with { Output = 400 } });
        Assert.Equal(2, today.Totals.Requests);
        Assert.Equal(2, today.Cost.UnpricedRequests);
        Assert.Equal(400d / 6, today.AverageOutputSpeed);

        var day = new DayAggregate();
        day.Add(response, cost: null);
        Assert.Equal((3L, 300L, 6000L), (day.Requests, day.MeasuredOutput, day.MeasuredMs));
        Assert.Equal(3, day.Models["grok-4.5"].Requests);
        Assert.Equal(3, day.Cost!.Value.UnpricedRequests);

        var report = new ReportLedger();
        report.Count(response, "grok-4.5");
        report.Count(response, "grok-4.5");
        Assert.Equal((3L, 3L, 300L), (report.Counters.Total.Requests, report.Counters.Total.UnpricedRequests, report.Counters.Total.Output));
        report.Grow(response with { RequestCount = 4, Tokens = response.Tokens with { Output = 400 } }, "grok-4.5");
        Assert.Equal((4L, 4L, 400L), (report.Counters.Total.Requests, report.Counters.Total.UnpricedRequests, report.Counters.Total.Output));
        report.Grow(response, "grok-4.5");
        Assert.Equal(4, report.Counters.Total.Requests);
    }

    [Fact]
    public void WrappedTotalsAndSecondsTimestampFollowNativeFallbacks()
    {
        using var temp = new TempDirectory();
        var parser = Parser(temp);
        var sink = new RecordingSink();
        Feed(parser, sink, """{"timestamp":1000,"method":"_x.ai/session/update","params":{"sessionId":"session","update":{"sessionUpdate":"turn_completed","prompt_id":"p","model_id":"grok-4.5","usage":{"totals":{"inputTokens":1500,"cachedReadTokens":400,"cacheCreationTokens":100,"outputTokens":300,"reasoningTokens":40,"modelCalls":2,"apiDurationMs":6000}}}}}""");
        var response = Assert.Single(sink.Latest);
        Assert.Equal(("grok:p:grok-4.5", (long?)994_000, 1_000_000L, 2L),
            (response.Key, response.StartMs, response.EndMs, response.Requests));
        Assert.Equal(new TokenCounts(1500, 400, 100, 300, 40), response.Tokens);
        Assert.Null(response.Provider);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(900001)]
    public void MissingOrInvalidBatchTimingKeepsTotalsAndUsesExistingNullStart(long duration)
    {
        using var temp = new TempDirectory();
        var parser = Parser(temp);
        var sink = new RecordingSink();
        var json = """{"timestamp":1000,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"p","usage":{"inputTokens":100,"outputTokens":20,"modelCalls":2,"apiDurationMs":DURATION}}}}""".Replace("DURATION", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Feed(parser, sink, json);
        Assert.Null(Assert.Single(sink.Latest).StartMs);
        Assert.Equal(2, sink.Latest[0].Requests);
    }

    [Fact]
    public void NativeToolUpdatesAndTurnCompletionSetTheActualPhase()
    {
        using var temp = new TempDirectory();
        var parser = Parser(temp);
        var sink = new RecordingSink();
        Feed(parser, sink, """{"timestamp":1000,"method":"session/update","params":{"update":{"sessionUpdate":"user_message_chunk"}}}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Feed(parser, sink, """{"timestamp":1001,"method":"session/update","params":{"update":{"sessionUpdate":"tool_call","status":"in_progress"}}}""");
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Feed(parser, sink, """{"timestamp":1002,"method":"session/update","params":{"update":{"sessionUpdate":"tool_call_update","status":"completed"}}}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Feed(parser, sink, """{"timestamp":1003,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"no-model","usage":null}}}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Empty(sink.Latest);
    }

    [Fact]
    public void AnExplicitZeroUsageResponseStillCountsAsARequest()
    {
        using var temp = new TempDirectory();
        var parser = Parser(temp);
        var sink = new RecordingSink();
        Feed(parser, sink, """{"timestamp":1000,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"p","usage":{"outputTokens":0,"modelCalls":1}}}}""");
        Assert.Equal(1, Assert.Single(sink.Latest).Requests);
        Assert.Equal(0, sink.Latest[0].Tokens.Total);
    }

    private static GrokParser Parser(TempDirectory temp)
    {
        temp.WriteFile("workspace/session/summary.json", "{}");
        return new GrokParser(temp.Combine("workspace", "session", "updates.jsonl"), new Deduper());
    }

    private static void Feed(GrokParser parser, RecordingSink sink, string line) => parser.Feed(Encoding.UTF8.GetBytes(line), sink);
}
