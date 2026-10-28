using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.ClaudeLines;

namespace AgentSpeed.Core.Tests;

public sealed class ClaudeParserTests
{
    private const string AgentA = "/home/me/.claude/projects/-home-me-proj/session-a.jsonl";
    private const string AgentB = "/home/me/.claude/projects/-home-me-proj/session-b.jsonl";

    [Theory]
    [InlineData("\"[Request interrupted by user]\"")]
    [InlineData("\"[Request interrupted by user for tool use]\"")]
    [InlineData("""[{"type":"text","text":"[Request interrupted by user]"}]""")]
    [InlineData("""[{"type":"text","text":"[Request interrupted by user for tool use]"}]""")]
    [InlineData("""[{"type":"image","source":{}},{"type":"text","text":"[Request interrupted by user]"}]""")]
    public void InterruptSetsIdleAndClearsPendingStart(string content)
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, ApiRequest(T0));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);

        Feed.Lines(parser, sink, User(T0 + 1000, content));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);

        Feed.Lines(parser, sink, Assistant(T0 + 3000, "msg_1", 10));
        Assert.Null(Assert.Single(sink.Emissions).Response.StartMs);

        // The precise-start flag is cleared too, so the next user line sets the pending start.
        Feed.Lines(parser, sink, UserText(T0 + 5000, "next"), Assistant(T0 + 8000, "msg_2", 20));
        Assert.Equal(T0 + 5000, sink.Latest[1].StartMs);
    }

    [Theory]
    [InlineData("\"Please stop [Request interrupted by user]\"")]
    [InlineData("""[{"type":"tool_result","tool_use_id":"t","content":"[Request interrupted by user]"}]""")]
    [InlineData("""[{"type":"text","text":"[Request was interrupted]"}]""")]
    public void NonInterruptUserLinesStartGenerating(string content)
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User(T0, content));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);
    }

    [Fact]
    public void UserLineAfterApiRequestKeepsThePreciseStart()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            ApiRequest(T0),
            User(T0 + 200, """[{"type":"tool_result","tool_use_id":"t","content":"ok"}]"""),
            Assistant(T0 + 3000, "msg_1", 10));

        Assert.Equal(T0, Assert.Single(sink.Latest).StartMs);
    }

    [Fact]
    public void NestedTypeAndTimestampAreNotTopLevel()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        var early = Ts.Iso(T0 - 60_000);

        Feed.Lines(
            parser,
            sink,
            $$"""{"message":{"type":"assistant","timestamp":"{{early}}","id":"msg_n","model":"m","stop_reason":null,"usage":{"output_tokens":5} },"type":"system","timestamp":"{{Ts.Iso(T0)}}"}""");

        Assert.Empty(sink.Emissions);
        Assert.Equal(T0, parser.LastLineMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(
            parser,
            sink,
            $$"""{"type":"user","message":{"role":"user","content":"hi","timestamp":"{{Ts.Iso(T0 + 5000)}}","type":"user"} }""");

        Assert.Equal(T0, parser.LastLineMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(
            parser,
            sink,
            $$"""{"timestamp":"{{Ts.Iso(T0 + 9000)}}","message":{"id":"msg_x","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"x","timestamp":"{{early}}"}],"stop_reason":"end_turn","usage":{"output_tokens":7} },"type":"assistant"}""");

        var response = Assert.Single(sink.Latest);
        Assert.Equal("msg_x", response.Key);
        Assert.Equal(T0 + 9000, response.EndMs);
        Assert.Equal(7, response.Tokens.Output);
        Assert.Equal(T0 + 9000, parser.LastLineMs);
    }

    [Fact]
    public void SyntheticModelIsSkipped()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            UserText(T0, "go"),
            Assistant(T0 + 1000, "msg_syn", 0, "\"stop_sequence\"", model: "<synthetic>"));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);
        Assert.Null(parser.Model);

        Feed.Lines(parser, sink, Assistant(T0 + 4000, "msg_real", 40));
        var response = Assert.Single(sink.Latest);
        Assert.Equal("msg_real", response.Key);
        Assert.Equal(T0, response.StartMs);
    }

    [Fact]
    public void RepeatedKeyInSameFileIsMergedAsUpdate()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            UserText(T0, "go"),
            Assistant(T0 + 2000, "msg_A", 100, "null", input: 2, cacheCreation: 10, cacheRead: 20),
            Assistant(T0 + 4000, "msg_A", 300, "\"tool_use\"", input: 2, cacheCreation: 10, cacheRead: 20),
            Assistant(T0 + 3000, "msg_A", 50, "\"tool_use\"", input: 1, thinking: 9));

        Assert.Equal([true, false, false], sink.Emissions.Select(e => e.IsCompletion));
        var merged = Assert.Single(sink.Latest);
        Assert.Equal(T0, merged.StartMs);
        Assert.Equal(T0 + 4000, merged.EndMs);
        Assert.Equal(300, merged.Tokens.Output);
        Assert.Equal(32, merged.Tokens.Input);
        Assert.Equal(20, merged.Tokens.CachedInput);
        Assert.Equal(10, merged.Tokens.CacheWriteInput);
        Assert.Equal(9, merged.Tokens.Reasoning);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
    }

    [Fact]
    public void KeyFirstSeenInAnotherFileIsIgnoredAndKeepsPendingStart()
    {
        var deduper = new Deduper();
        var a = new ClaudeParser(AgentA, deduper);
        var b = new ClaudeParser(AgentB, deduper);
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();

        Feed.Lines(a, sinkA, UserText(T0, "go"), Assistant(T0 + 2000, "msg_K1", 100));
        Assert.Single(sinkA.Emissions);

        Feed.Lines(b, sinkB, UserText(T0 + 10_000, "resume"), Assistant(T0 + 12_000, "msg_K1", 100));
        Assert.Empty(sinkB.Emissions);
        Assert.Equal(AgentPhase.Generating, b.Phase);
        Assert.Equal(T0 + 10_000, b.InFlightStartMs);

        Feed.Lines(b, sinkB, Assistant(T0 + 15_000, "msg_K2", 60));
        var (response, isCompletion) = Assert.Single(sinkB.Emissions);
        Assert.True(isCompletion);
        Assert.Equal("msg_K2", response.Key);
        Assert.Equal(T0 + 10_000, response.StartMs);
        Assert.Equal(AgentB, response.AgentId);

        // The owner file still merges its own repeats.
        Feed.Lines(a, sinkA, Assistant(T0 + 2500, "msg_K1", 120));
        Assert.Equal([true, false], sinkA.Emissions.Select(e => e.IsCompletion));
        Assert.Equal(120, sinkA.Latest[0].Tokens.Output);
    }

    [Theory]
    [InlineData("\"tool_use\"", AgentPhase.Tools)]
    [InlineData("null", AgentPhase.Generating)]
    [InlineData("\"pause_turn\"", AgentPhase.Generating)]
    [InlineData("\"end_turn\"", AgentPhase.Idle)]
    [InlineData("\"max_tokens\"", AgentPhase.Idle)]
    public void StopReasonDecidesPhase(string stopReasonJson, AgentPhase expected)
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UserText(T0, "go"), Assistant(T0 + 2000, "msg_1", 10, stopReasonJson));

        Assert.Equal(expected, parser.Phase);
        Assert.Equal(expected == AgentPhase.Generating ? (long?)T0 : null, parser.InFlightStartMs);
    }

    [Fact]
    public void GeneratingWithUnknownStartUsesTheLineTimestamp()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Assistant(T0 + 2000, "msg_1", 10, "null"));

        Assert.Null(Assert.Single(sink.Latest).StartMs);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 2000, parser.InFlightStartMs);
    }

    [Fact]
    public void MissingStopReasonMeansGenerating()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            UserText(T0, "go"),
            $$"""{"type":"assistant","timestamp":"{{Ts.Iso(T0 + 1000)}}","message":{"id":"msg_1","model":"m","usage":{"output_tokens":3} } }""");

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);
    }

    [Fact]
    public void KeyFallsBackToRequestIdThenUuid()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            $$"""{"type":"assistant","timestamp":"{{Ts.Iso(T0)}}","requestId":"req_9","uuid":"u-1","message":{"model":"m","usage":{"output_tokens":3} } }""",
            $$"""{"type":"assistant","timestamp":"{{Ts.Iso(T0 + 1)}}","uuid":"u-2","message":{"model":"m","usage":{"output_tokens":4} } }""");

        Assert.Equal(["req_9", "u-2"], sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void ClaudeUsageIsNormalized()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            parser,
            sink,
            $$"""{"type":"assistant","timestamp":"{{Ts.Iso(T0)}}","message":{"id":"msg_u","model":"m","stop_reason":"end_turn","usage":{"input_tokens":-5,"cache_creation_input_tokens":100,"cache_read_input_tokens":2000,"output_tokens":42,"output_tokens_details":{"thinking_tokens":12} } } }""",
            $$"""{"type":"assistant","timestamp":"{{Ts.Iso(T0 + 1)}}","message":{"id":"msg_v","model":"m","stop_reason":"end_turn","usage":{"output_tokens":8} } }""");

        var tokens = sink.Latest[0].Tokens;
        Assert.Equal(new TokenCounts(2100, 2000, 100, 42, 12), tokens);
        Assert.Equal(new TokenCounts(Output: 8), sink.Latest[1].Tokens);
    }

    [Fact]
    public void FirstEntrypointDecidesTheSource()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UserText(T0, "go"));
        Assert.Equal(SourceKind.ClaudeCode, parser.Source);

        Feed.Lines(
            parser,
            sink,
            User(T0 + 1000, "\"a\"", extra: ",\"entrypoint\":\"claude-desktop\""),
            User(T0 + 2000, "\"b\"", extra: ",\"entrypoint\":\"cli\""),
            Assistant(T0 + 3000, "msg_1", 5));

        Assert.Equal(SourceKind.ClaudeDesktop, parser.Source);
        Assert.Equal(SourceKind.ClaudeDesktop, Assert.Single(sink.Latest).Source);
    }

    [Fact]
    public void SidechainMarksSubagent()
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();
        Assert.False(parser.IsSubagent);

        Feed.Lines(parser, sink, User(T0, "\"go\"", extra: ",\"isSidechain\":false"));
        Assert.False(parser.IsSubagent);

        Feed.Lines(parser, sink, User(T0 + 1, "\"go\"", extra: ",\"isSidechain\":true"));
        Assert.True(parser.IsSubagent);
    }

    [Fact]
    public void ResetClearsFileStateAndReplaysKnownKeysAsUpdates()
    {
        var deduper = new Deduper();
        var parser = new ClaudeParser(AgentA, deduper);
        var sink = new RecordingSink();
        var lines = new[]
        {
            User(T0, "\"go\"", extra: ",\"entrypoint\":\"claude-desktop\",\"isSidechain\":true"),
            Assistant(T0 + 2000, "msg_1", 25, "\"tool_use\""),
        };

        Feed.Lines(parser, sink, lines);
        parser.Reset();

        Assert.Null(parser.Project);
        Assert.Null(parser.Model);
        Assert.False(parser.IsSubagent);
        Assert.Equal(SourceKind.ClaudeCode, parser.Source);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
        Assert.Null(parser.LastLineMs);

        Feed.Lines(parser, sink, lines);
        Assert.Equal([true, false], sink.Emissions.Select(e => e.IsCompletion));
        var replayed = sink.Emissions[1].Response;
        Assert.Equal(T0 + 2000, replayed.EndMs);
        Assert.Equal(25, replayed.Tokens.Output);
        Assert.Equal(SourceKind.ClaudeDesktop, replayed.Source);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
    }

    [Theory]
    [InlineData("C:\\\\Users\\\\me\\\\proj", "proj")]
    [InlineData("C:\\\\Users\\\\me\\\\proj\\\\", "proj")]
    [InlineData("/Users/dev/projects/nova/", "nova")]
    public void ProjectIsBasenameOfCwd(string cwdJson, string expected)
    {
        var parser = new ClaudeParser(AgentA, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User(T0, "\"go\"", cwd: cwdJson));

        Assert.Equal(expected, parser.Project);
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude\projects\-C-Users-me-proj\s1\subagents\agent-1.jsonl", true)]
    [InlineData(@"C:\Users\me\.claude\projects\-C-Users-me-proj\subagents.jsonl", false)]
    [InlineData("/home/me/.claude/projects/-home-me-proj/s1/subagents/agent-1.jsonl", true)]
    [InlineData("/home/me/.claude/projects/-home-me-proj/s1/mysubagents/agent-1.jsonl", false)]
    public void SubagentPathComponentMarksSubagent(string agentId, bool expected)
    {
        var parser = new ClaudeParser(agentId, new Deduper());

        Assert.Equal(expected, parser.IsSubagent);
    }
}
