using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.CodexLines;

namespace AgentSpeed.Core.Tests;

public sealed class CodexParserTests
{
    private const string AgentA = "/home/me/.codex/sessions/2026/10/20/rollout-a.jsonl";
    private const string AgentB = "/home/me/.codex/sessions/2026/10/20/rollout-b.jsonl";

    [Fact]
    public void CopiedHistoryBeforeFileCreationIsIgnoredCompletely()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, SessionMeta(T0));
        Feed.Lines(
            parser,
            sink,
            UserMessage(T0 - 2001),
            UsageRecord(T0 - 2001, "resp-old", Usage(100, 0, 10, 0)),
            FunctionCall(T0 - 5000),
            TaskComplete(T0 - 9000));

        Assert.Empty(sink.Emissions);
        Assert.Equal(T0, parser.LastLineMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(parser, sink, UsageRecord(T0 - 2000, "resp-edge", Usage(100, 0, 10, 0)));
        var response = Assert.Single(sink.Latest);
        Assert.Equal("resp-edge", response.Key);
        Assert.Null(response.StartMs);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
    }

    [Fact]
    public void CopiedHistoryRuleOnlyAppliesAfterSessionMeta()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            UsageRecord(T0 - 600_000, "resp-pre", Usage(100, 0, 10, 0)),
            SessionMeta(T0),
            UsageRecord(T0 - 500_000, "resp-copied", Usage(100, 0, 10, 0)));

        Assert.Equal(["resp-pre"], sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void CopiedHistoryDoesNotClaimKeys()
    {
        var deduper = new Deduper();
        var child = new CodexParser(AgentB, deduper);
        var parent = new CodexParser(AgentA, deduper);
        var sink = new RecordingSink();

        Feed.Lines(child, sink, SessionMeta(T0 + 60_000, "thread-child"), UsageRecord(T0 + 5000, "resp-shared", Usage(100, 0, 10, 0)));
        Feed.Lines(parent, sink, SessionMeta(T0, "thread-parent"), UsageRecord(T0 + 5000, "resp-shared", Usage(100, 0, 10, 0)));

        var response = Assert.Single(sink.Latest);
        Assert.Equal(AgentA, response.AgentId);
    }

    [Fact]
    public void LegacyDuplicateTokenCountIsIgnoredWithoutStateChange()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        var total1 = Usage(1000, 0, 100, 10);
        var total2 = Usage(3000, 500, 250, 30);
        var last2 = Usage(2000, 500, 150, 20);

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0, "thread-L", "/w/legacy"),
            TurnContext(T0 + 100, "gpt-5.5"),
            TaskStarted(T0 + 500),
            TokenCount(T0 + 4000, total1, total1),
            FunctionCallOutput(T0 + 6000));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, TokenCount(T0 + 7000, total1, total1));
        Assert.Single(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, TokenCount(T0 + 9000, total2, last2));
        Assert.Equal(AgentPhase.Tools, parser.Phase);

        var responses = sink.Latest;
        Assert.Equal(["codex-legacy:thread-L:1100", "codex-legacy:thread-L:3250"], responses.Select(r => r.Key));
        Assert.Equal(T0 + 500, responses[0].StartMs);
        Assert.Equal(T0 + 4000, responses[0].EndMs);
        Assert.Equal(new TokenCounts(1000, 0, 0, 100, 10), responses[0].Tokens);
        Assert.Equal(T0 + 6000, responses[1].StartMs);
        Assert.Equal(T0 + 9000, responses[1].EndMs);
        Assert.Equal(new TokenCounts(2000, 500, 0, 150, 20), responses[1].Tokens);
        Assert.Equal("gpt-5.5", responses[1].Model);
    }

    [Fact]
    public void LegacyNegativeDeltaFallsBackToLastUsage()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0, "thread-L"),
            TaskStarted(T0 + 500),
            TokenCount(T0 + 4000, Usage(5000, 0, 300, 0), Usage(5000, 0, 300, 0)),
            TokenCount(T0 + 8000, Usage(500, 0, 50, 0), Usage(400, 0, 40, 0)));

        var responses = sink.Latest;
        Assert.Equal(["codex-legacy:thread-L:5300", "codex-legacy:thread-L:550"], responses.Select(r => r.Key));
        Assert.Equal(new TokenCounts(400, 0, 0, 40, 0), responses[1].Tokens);
        Assert.Equal(T0 + 4000, responses[1].StartMs);
    }

    [Fact]
    public void LegacyUsageIsIgnoredAfterModernUsage()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        var usage = Usage(1000, 0, 100, 10);

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0),
            TaskStarted(T0 + 500),
            UsageRecord(T0 + 4000, "resp-1", usage),
            TokenCount(T0 + 4001, usage, usage),
            Envelope(T0 + 4002, "event_msg", """{"type":"token_count","info":null}"""));

        Assert.Equal(["resp-1"], sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void ResponseIdSeenInAnotherFileIsSkippedWithoutStateChange()
    {
        var deduper = new Deduper();
        var a = new CodexParser(AgentA, deduper);
        var b = new CodexParser(AgentB, deduper);
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();

        Feed.Lines(a, sinkA, UserMessage(T0), UsageRecord(T0 + 2000, "resp-X", Usage(100, 0, 10, 0)));
        Assert.Single(sinkA.Emissions);

        Feed.Lines(b, sinkB, UserMessage(T0 + 5000), UsageRecord(T0 + 6000, "resp-X", Usage(100, 0, 10, 0)));
        Assert.Empty(sinkB.Emissions);
        Assert.Equal(AgentPhase.Generating, b.Phase);
        Assert.Equal(T0 + 5000, b.InFlightStartMs);

        Feed.Lines(b, sinkB, UsageRecord(T0 + 8000, "resp-Y", Usage(100, 0, 20, 0)));
        var response = Assert.Single(sinkB.Latest);
        Assert.Equal("resp-Y", response.Key);
        Assert.Equal(T0 + 5000, response.StartMs);

        Feed.Lines(a, sinkA, UsageRecord(T0 + 2100, "resp-X", Usage(100, 0, 99, 0)));
        Assert.Single(sinkA.Emissions);
    }

    [Fact]
    public void ModernUsageWithoutResponseIdUsesThreadKey()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        var lines = new[]
        {
            SessionMeta(T0, "thread-meta"),
            Envelope(T0 + 1000, "token_usage_record", $$"""{"usage":{{Usage(10, 0, 1, 0)}} }"""),
            UsageRecord(T0 + 2000, null, Usage(10, 0, 2, 0), threadId: "thread-own"),
        };

        Feed.Lines(parser, sink, lines);

        Assert.Equal(
            [$"codex:thread-meta:{T0 + 1000}", $"codex:thread-own:{T0 + 2000}"],
            sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void ModernUsageIsNormalizedAndIgnoresTurnAndThreadTotals()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            Envelope(
                T0,
                "token_usage_record",
                """{"response_id":"r","turn_token_usage":{"output_tokens":999},"usage":{"input_tokens":700,"cached_input_tokens":600,"cache_write_input_tokens":50,"output_tokens":-3,"reasoning_output_tokens":4},"thread_token_usage":{"output_tokens":9999}}"""));

        Assert.Equal(new TokenCounts(700, 600, 50, 0, 4), Assert.Single(sink.Latest).Tokens);
    }

    [Fact]
    public void StreamingOutputKeepsPendingStartForLaterInputs()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            UserMessage(T0),
            FunctionCall(T0 + 1000),
            FunctionCallOutput(T0 + 1500),
            UsageRecord(T0 + 3000, "resp-1", Usage(10, 0, 5, 0)));

        Assert.Equal(T0, Assert.Single(sink.Latest).StartMs);
    }

    [Fact]
    public void TaskCompleteAndTurnAbortedClearPendingStart()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UserMessage(T0), Envelope(T0 + 500, "event_msg", """{"type":"turn_aborted"}"""));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);

        Feed.Lines(parser, sink, UsageRecord(T0 + 1000, "resp-1", Usage(10, 0, 5, 0)));
        Assert.Null(Assert.Single(sink.Latest).StartMs);
    }

    [Fact]
    public void NestedTypeAndTimestampAreNotTopLevel()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        var early = Ts.Iso(T0 - 3_600_000);

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0),
            Envelope(T0 + 1000, "event_msg", $$"""{"type":"task_started","timestamp":"{{early}}"}"""));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
        Assert.Equal(T0 + 1000, parser.LastLineMs);

        Feed.Lines(
            parser,
            sink,
            $$"""{"timestamp":"{{Ts.Iso(T0 + 2000)}}","payload":{"type":"token_usage_record","response_id":"nested","usage":{{Usage(1, 0, 1, 0)}} } }""",
            $$"""{"type":"event_msg","payload":{"type":"task_complete","timestamp":"{{Ts.Iso(T0 + 3000)}}"} }""");

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 2000, parser.LastLineMs);

        Feed.Lines(
            parser,
            sink,
            $$"""{"payload":{"type":"message","role":"user","timestamp":"{{early}}"},"timestamp":"{{Ts.Iso(T0 + 4000)}}","type":"response_item"}""",
            $$"""{"payload":{"response_id":"resp-late","usage":{{Usage(1, 0, 1, 0)}} },"type":"token_usage_record","timestamp":"{{Ts.Iso(T0 + 5000)}}"}""");

        var response = Assert.Single(sink.Latest);
        Assert.Equal("resp-late", response.Key);
        Assert.Equal(T0 + 4000, response.StartMs);
        Assert.Equal(T0 + 5000, response.EndMs);
    }

    [Theory]
    [InlineData("C:\\\\Users\\\\me\\\\rocket", "rocket")]
    [InlineData("C:\\\\Users\\\\me\\\\rocket\\\\", "rocket")]
    [InlineData("/Users/dev/projects/rocket", "rocket")]
    public void SessionMetaCwdSetsProject(string cwdJson, string expected)
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, SessionMeta(T0, cwd: cwdJson));

        Assert.Equal(expected, parser.Project);
    }

    [Fact]
    public void TurnContextCwdOverridesProjectAndSetsModel()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, SessionMeta(T0, cwd: "C:\\\\work\\\\first"), TurnContext(T0 + 1, "gpt-x", "D:\\\\work\\\\second"));
        Assert.Equal("second", parser.Project);
        Assert.Equal("gpt-x", parser.Model);

        Feed.Lines(parser, sink, TurnContext(T0 + 2, "gpt-y"));
        Assert.Equal("second", parser.Project);
        Assert.Equal("gpt-y", parser.Model);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",\"thread_source\":\"subagent\"", true)]
    [InlineData(",\"thread_source\":\"user\"", false)]
    [InlineData(",\"source\":{\"subagent\":{\"depth\":1}}", true)]
    [InlineData(",\"source\":\"cli\"", false)]
    [InlineData(",\"source\":{\"vscode\":{}}", false)]
    public void SessionMetaDecidesSubagent(string extra, bool expected)
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, SessionMeta(T0, extra: extra));

        Assert.Equal(expected, parser.IsSubagent);
    }

    [Theory]
    [InlineData("""{"type":"message","role":"user"}""", "input")]
    [InlineData("""{"type":"message","role":"developer"}""", "input")]
    [InlineData("""{"type":"message","role":"system"}""", "input")]
    [InlineData("""{"type":"local_shell_call_output","output":"x"}""", "input")]
    [InlineData("""{"type":"message","role":"assistant"}""", "output")]
    [InlineData("""{"type":"reasoning","summary":[]}""", "output")]
    [InlineData("""{"type":"web_search_call"}""", "output")]
    [InlineData("""{"type":"message","role":"tool"}""", "none")]
    [InlineData("""{"type":"something_else"}""", "none")]
    public void ResponseItemsAreClassifiedAsInputOrOutput(string payload, string expected)
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UsageRecord(T0, "r0", Usage(1, 0, 1, 0)), Envelope(T0 + 1000, "response_item", payload));

        switch (expected)
        {
            case "input":
                Assert.Equal(AgentPhase.Generating, parser.Phase);
                Assert.Equal(T0 + 1000, parser.InFlightStartMs);
                break;
            case "output":
                Assert.Equal(AgentPhase.Generating, parser.Phase);
                Assert.Equal(T0, parser.InFlightStartMs);
                break;
            default:
                Assert.Equal(AgentPhase.Tools, parser.Phase);
                Assert.Null(parser.InFlightStartMs);
                break;
        }
    }

    [Fact]
    public void OutputWithoutPendingStartUsesItsTimestamp()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, FunctionCall(T0 + 1000));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
    }

    [Fact]
    public void ResetClearsFileStateButKeepsDedupe()
    {
        var deduper = new Deduper();
        var parser = new CodexParser(AgentA, deduper);
        var sink = new RecordingSink();
        var lines = new[]
        {
            SessionMeta(T0, cwd: "/w/rocket", extra: ",\"thread_source\":\"subagent\""),
            TurnContext(T0 + 100, "gpt-x"),
            UserMessage(T0 + 200),
            UsageRecord(T0 + 3000, "resp-1", Usage(10, 0, 5, 0)),
        };

        Feed.Lines(parser, sink, lines);
        parser.Reset();

        Assert.Null(parser.Project);
        Assert.Null(parser.Model);
        Assert.False(parser.IsSubagent);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.LastLineMs);
        Assert.Equal(SourceKind.Codex, parser.Source);

        Feed.Lines(parser, sink, lines);
        Assert.Single(sink.Emissions);
        Assert.Equal("rocket", parser.Project);
    }

    [Fact]
    public void AgentMessageToThisAgentIsMailboxInputThatKeepsThePhase()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0, extra: ",\"agent_path\":\"/root/worker\""),
            Envelope(T0 + 1000, "response_item", """{"type":"agent_message","author":"/root/worker","recipient":"/root"}"""),
            Envelope(T0 + 2000, "response_item", """{"type":"agent_message","author":"/root","recipient":"/root/worker"}"""));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);

        Feed.Lines(
            parser,
            sink,
            Envelope(T0 + 3000, "response_item", """{"type":"reasoning","summary":[]}"""),
            Envelope(T0 + 5000, "token_usage_record", """{"response_id":"resp-m","usage":{"output_tokens":10}}"""));
        Assert.Equal(T0 + 2000, Assert.Single(sink.Latest).StartMs);
    }

    [Fact]
    public void LaterSessionMetaLinesAreIgnored()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0 + 20_000, id: "thread-worker", cwd: "/w/worker", extra: ",\"agent_path\":\"/root/worker\""),
            SessionMeta(T0 + 20_000, id: "thread-main", cwd: "/w/main"),
            Envelope(T0 + 21_000, "token_usage_record", """{"usage":{"output_tokens":10}}"""));

        Assert.Equal("worker", parser.Project);
        Assert.Equal($"codex:thread-worker:{T0 + 21_000}", Assert.Single(sink.Latest).Key);
    }

    [Fact]
    public void TokenCountWhileStreamingEndsAResponseWithoutUsage()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0),
            Envelope(T0 + 1000, "token_usage_record", """{"response_id":"r0","usage":{"output_tokens":5}}"""),
            Envelope(T0 + 2000, "response_item", """{"type":"reasoning","summary":[]}"""),
            Envelope(T0 + 3000, "event_msg", """{"type":"token_count","info":null}"""));

        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Feed.Lines(
            parser,
            sink,
            Envelope(T0 + 4000, "response_item", """{"type":"reasoning","summary":[]}"""),
            Envelope(T0 + 6000, "token_usage_record", """{"response_id":"r1","usage":{"output_tokens":5}}"""));
        Assert.Equal(T0 + 3000, sink.Latest.Single(r => r.Key == "r1").StartMs);
    }

    [Fact]
    public void InputWhileStreamingChangesNothing()
    {
        var parser = new CodexParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            SessionMeta(T0),
            Envelope(T0 + 1000, "response_item", """{"type":"reasoning","summary":[]}"""),
            Envelope(T0 + 2000, "response_item", """{"type":"function_call_output","call_id":"c","output":"x"}"""));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
    }
}
