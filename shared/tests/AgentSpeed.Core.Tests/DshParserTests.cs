using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.DshLines;

namespace AgentSpeed.Core.Tests;

public sealed class DshParserTests
{
    private const string AgentA = "/home/me/.dsh/sessions/--home-me-quasar--/session-a/session.v4.jsonl.zstd";
    private const string AgentB = "/home/me/.dsh/sessions/--home-me-quasar--/session-b/session.v4.jsonl.zstd";

    [Fact]
    public void HeaderSetsProjectAndSessionButNotLastLine()
    {
        var parser = new DshParser(AgentA, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Session(T0, id: "session-a", cwd: @"C:\\work\\quasar\\"));

        Assert.Equal(SourceKind.DeepSeek, parser.Source);
        Assert.Equal("quasar", parser.Project);
        Assert.Equal("session-a", parser.SessionId);
        Assert.Equal(T0, parser.CreatedMs);
        Assert.False(parser.IsSubagent);
        Assert.Null(parser.LastLineMs);
    }

    [Fact]
    public void StepStartToAssistantMessageIsOneMeasuredResponse()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0), RequestHeader(1, T0 + 10, "deepseek-flash"), StepStart(2, T0 + 1000));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, Message(3, T0 + 5000, "m-1", Usage(120, 300, cacheRead: 9216, cacheWrite: 64), model: "deepseek-pro"));

        var response = Assert.Single(sink.Latest);
        Assert.Equal(new ParsedResponse(
            "m-1", SourceKind.DeepSeek, AgentA, "deepseek-pro", T0 + 1000, T0 + 5000,
            new TokenCounts(Input: 9400, CachedInput: 9216, CacheWriteInput: 64, Output: 300, Reasoning: 0),
            Provider: "deepseek-account"), response);
        Assert.True(Assert.Single(sink.Emissions).IsCompletion);
        Assert.Equal("deepseek-pro", parser.Model);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 5000, parser.LastLineMs);
    }

    [Fact]
    public void ToolCallBlockMeansToolsAndTurnEndMeansIdle()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0), StepStart(1, T0 + 100), Message(2, T0 + 900, "m-1", Usage(1, 2), blockType: "tool-call"));
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Null(parser.InFlightStartMs);

        Feed.Lines(parser, sink, Event("tool/result", 3, T0 + 1200), StepStart(4, T0 + 1300));
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, sink, TurnEnd(5, T0 + 1400));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 1400, parser.LastLineMs);

        Feed.Lines(parser, sink, Message(6, T0 + 2000, "m-2", Usage(1, 2)));
        Assert.Null(sink.Latest[^1].StartMs);
    }

    [Fact]
    public void ModelFallsBackToTheRequestHeader()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0), RequestHeader(1, T0 + 1, "deepseek-flash"), StepStart(2, T0 + 2),
            Message(3, T0 + 600, "m-1", Usage(1, 2), model: null));

        Assert.Equal("deepseek-flash", Assert.Single(sink.Latest).Model);
    }

    [Fact]
    public void MessageWithoutUsageEmitsNothingButStillEndsTheStep()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0), StepStart(1, T0 + 1), Message(2, T0 + 500, "m-1", null, blockType: "tool-call"));
        Assert.Equal(AgentPhase.Tools, parser.Phase);

        // The key was claimed even without usage, so a repeat is skipped entirely.
        Feed.Lines(parser, sink, StepStart(3, T0 + 600), Message(4, T0 + 900, "m-1", Usage(5, 6)));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 600, parser.InFlightStartMs);
        Assert.Equal(T0 + 900, parser.LastLineMs);
    }

    [Fact]
    public void KeySeenAnywhereSkipsTheMessageEntirely()
    {
        var deduper = new Deduper();
        var first = new DshParser(AgentA, deduper);
        var second = new DshParser(AgentB, deduper);
        var sink = new RecordingSink();

        Feed.Lines(first, sink, Session(T0, id: "a"), StepStart(1, T0 + 1), Message(2, T0 + 400, "m-1", Usage(1, 2)),
            Message(3, T0 + 450, "m-1", Usage(7, 8), blockType: "tool-call"));
        Feed.Lines(second, sink, Session(T0, id: "b"), StepStart(1, T0 + 10), Message(2, T0 + 500, "m-1", Usage(1, 2)));

        var response = Assert.Single(sink.Emissions).Response;
        Assert.Equal(AgentA, response.AgentId);
        Assert.Equal(2, response.Tokens.Output);
        Assert.Equal(AgentPhase.Idle, first.Phase);
        Assert.Equal(AgentPhase.Generating, second.Phase);
    }

    [Fact]
    public void MessageWithoutIdIsKeyedBySessionAndSeq()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0, id: "session-a"), StepStart(6, T0 + 1), Message(7, T0 + 400, null, Usage(1, 2)));

        Assert.Equal("dsh:session-a:7", Assert.Single(sink.Latest).Key);
    }

    [Fact]
    public void InheritedHistoryBeforeCreationIsIgnoredCompletely()
    {
        var deduper = new Deduper();
        var child = new DshParser(AgentB, deduper);
        var sink = new RecordingSink();

        Feed.Lines(child, sink, Session(T0, id: "child", extra: ",\"parentSession\":\"parent\""),
            StepStart(1, T0 - 2001), Message(2, T0 - 2001, "inherited", Usage(1, 2)), TurnEnd(3, T0 - 9000));
        Assert.Empty(sink.Emissions);
        Assert.Null(child.LastLineMs);
        Assert.Equal(AgentPhase.Idle, child.Phase);

        Feed.Lines(child, sink, StepStart(4, T0 - 2000));
        Assert.Equal(AgentPhase.Generating, child.Phase);
        Assert.Equal(T0 - 2000, child.LastLineMs);

        var parent = new DshParser(AgentA, deduper);
        Feed.Lines(parent, sink, Session(T0 - 60_000, id: "parent"), StepStart(1, T0 - 3000), Message(2, T0 - 2001, "inherited", Usage(1, 2)));
        Assert.Equal(AgentA, Assert.Single(sink.Emissions).Response.AgentId);
    }

    [Theory]
    [InlineData(",\"origin\":\"subagent\"", true)]
    [InlineData(",\"parentSession\":\"p\"", true)]
    [InlineData(",\"parentSession\":\"\"", false)]
    [InlineData(",\"origin\":\"user\"", false)]
    public void SubagentFromOriginOrParentSession(string extra, bool expected)
    {
        var parser = new DshParser(AgentA, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Session(T0, extra: extra));

        Assert.Equal(expected, parser.IsSubagent);
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("0.5", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("\"1\"", false)]
    public void SubagentFromDelegationDepth(string depth, bool expected)
    {
        var parser = new DshParser(AgentA, new Deduper());

        Feed.Lines(parser, new RecordingSink(),
            $$"""{"type":"session","id":"s","createdAt":{{T0}},"cwd":"/q","delegationDepth":{{depth}} }""");

        Assert.Equal(expected, parser.IsSubagent);
    }

    [Fact]
    public void OnlyTheFirstHeaderApplies()
    {
        var parser = new DshParser(AgentA, new Deduper());

        Feed.Lines(parser, new RecordingSink(),
            Session(T0, id: "first", cwd: "/a/first"),
            Session(T0 + 99_000, id: "second", cwd: "/a/second", extra: ",\"origin\":\"subagent\",\"parentSession\":\"x\""));

        Assert.Equal("first", parser.SessionId);
        Assert.Equal("first", parser.Project);
        Assert.Equal(T0, parser.CreatedMs);
        Assert.Null(parser.ParentSession);
        Assert.False(parser.IsSubagent);
    }

    [Fact]
    public void EventWrappersAreUnwrapped()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink,
            $$"""{"event":{{Session(T0, id: "wrapped")}},"type":"event"}""",
            $$"""{"type":"event","event":{{StepStart(1, T0 + 100)}} }""",
            $$"""{"type":"event","event":{"type":"event","event":{{Message(2, T0 + 700, "m-1", Usage(10, 20))}} } }""");

        Assert.Equal("wrapped", parser.SessionId);
        var response = Assert.Single(sink.Latest);
        Assert.Equal(T0 + 100, response.StartMs);
        Assert.Equal(T0 + 700, response.EndMs);
    }

    [Fact]
    public void SnapshotIsItsHeaderFollowedByItsRecordsInOrder()
    {
        var parser = new DshParser(AgentB, new Deduper());
        var sink = new RecordingSink();
        var header = $$"""{"id":"snap","createdAt":{{T0}},"cwd":"/p/nebula","origin":"subagent","parentSession":"root"}""";
        var records = string.Join(',',
            StepStart(1, T0 - 5000),
            Message(2, T0 - 4000, "old", Usage(1, 2)),
            $$"""{"type":"event","event":{{StepStart(3, T0 + 100)}} }""",
            "42",
            Message(4, T0 + 900, "new", Usage(3, 4), blockType: "tool-call"));

        Feed.Lines(parser, sink, $$"""{"records":[{{records}}],"type":"snapshot","header":{{header}} }""");

        Assert.Equal("snap", parser.SessionId);
        Assert.Equal("nebula", parser.Project);
        Assert.Equal("root", parser.ParentSession);
        Assert.True(parser.IsSubagent);
        var response = Assert.Single(sink.Latest);
        Assert.Equal(("new", T0 + 100), (response.Key, response.StartMs));
        Assert.Equal(AgentPhase.Tools, parser.Phase);
    }

    [Fact]
    public void TimesMustBeNumbers()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0),
            """{"type":"step/start","seq":1,"time":"2026-10-20T10:00:00Z"}""",
            """{"type":"step/start","seq":2,"time":true}""");
        Assert.Null(parser.LastLineMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(parser, sink, $$"""{"type":"step/start","seq":3,"time":{{T0 + 1}}.75}""");
        Assert.Equal(T0 + 1, parser.LastLineMs);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
    }

    [Fact]
    public void NegativeAndMissingCountsAreZero()
    {
        var parser = new DshParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Session(T0), StepStart(1, T0 + 1),
            Message(2, T0 + 900, "m-1", """{"inputTokens":-5,"outputTokens":40,"cacheReadTokens":"7"}"""));

        Assert.Equal(new TokenCounts(Output: 40), Assert.Single(sink.Latest).Tokens);
    }

    [Fact]
    public void MalformedLinesAreCounted()
    {
        var parser = new DshParser(AgentA, new Deduper());

        Feed.Lines(parser, new RecordingSink(), "not json", "[1,2]", "{\"type\":\"step/start\"", "{} trailing", "   ");

        Assert.Equal(4, parser.MalformedLines);
    }

    [Fact]
    public void ResetClearsSessionState()
    {
        var parser = new DshParser(AgentA, new Deduper());
        Feed.Lines(parser, new RecordingSink(), Session(T0, id: "s", extra: ",\"parentSession\":\"p\""), StepStart(1, T0 + 5));

        parser.Reset();

        Assert.Null(parser.SessionId);
        Assert.Null(parser.CreatedMs);
        Assert.Null(parser.ParentSession);
        Assert.False(parser.IsSubagent);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.LastLineMs);
    }
}
