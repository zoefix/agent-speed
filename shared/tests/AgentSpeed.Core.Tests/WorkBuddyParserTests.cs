using System.Text;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>Rules of METRICS 4.9 that the shared fixtures do not reach.</summary>
public sealed class WorkBuddyParserTests
{
    private const long T = 1_792_576_800_000;
    private const string Root = "/w/projects";
    private const string Session = "/w/projects/Users-me-atlas/7321aebd.jsonl";

    private readonly RecordingSink _sink = new();

    [Fact]
    public void TimestampsAreMillisecondNumbersOrIsoStringsAndOtherLinesAreIgnoredEntirely()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            """{"id":"u0","timestamp":0,"type":"message","role":"user","cwd":"/w/zero"}""",
            """{"id":"u1","timestamp":-5,"type":"message","role":"user","cwd":"/w/negative"}""",
            """{"id":"u2","timestamp":true,"type":"message","role":"user","cwd":"/w/bool"}""",
            """{"id":"u3","timestamp":"soon","type":"message","role":"user","cwd":"/w/text"}""",
            """{"id":"u4","type":"message","role":"user","cwd":"/w/missing"}""");
        Assert.Equal(((long?)null, (string?)null, AgentPhase.Idle), (parser.LastLineMs, parser.Project, parser.Phase));

        Feed.Lines(parser, _sink, """{"id":"u5","timestamp":1792576800000.9,"type":"message","role":"user","cwd":"/w/atlas"}""");
        Assert.Equal((T, (long?)T, "atlas"), (parser.LastLineMs!.Value, parser.InFlightStartMs, parser.Project));

        Feed.Lines(parser, _sink, """{"id":"u6","timestamp":"2026-10-21T10:00:05.250Z","type":"message","role":"user"}""");
        Assert.Equal(T + 5250, parser.InFlightStartMs);
    }

    [Fact]
    public void EachFieldIsTheLargerOfBothUsageRecordsAndDeepSeekMissesAreNeverCacheWrites()
    {
        var raw = """{"prompt_tokens":1200,"completion_tokens":40,"prompt_cache_hit_tokens":800,"prompt_cache_miss_tokens":400}""";
        var response = Respond(WorkBuddyLines.Usage(1000, 50, cached: 0, reasoning: 12, raw: raw));

        Assert.Equal(new TokenCounts(1200, 800, 0, 50, 12), response.Tokens);
    }

    [Fact]
    public void CacheCountsTakeTheFirstValueAboveZeroAndInputCoversThem()
    {
        var raw = """{"cache_read_input_tokens":0,"prompt_tokens_details":{"cached_tokens":300},"prompt_cache_hit_tokens":999,"cache_creation_input_tokens":0,"prompt_cache_write_tokens":70}""";
        var response = Respond(WorkBuddyLines.Usage(10, 5, raw: raw));

        Assert.Equal(new TokenCounts(370, 300, 70, 5, 0), response.Tokens);
    }

    [Theory]
    [InlineData("""{"cached_tokens":5}""", 5)]
    [InlineData("""[{"cached_tokens":2},{"cached_tokens":3},7,null,{"other":1}]""", 5)]
    [InlineData("""[{"cached_tokens":2,"cached_tokens":4}]""", 4)]
    [InlineData("12", 0)]
    public void CachedDetailsAreSummedOverObjects(string details, long cached)
    {
        var usage = "\"usage\":{\"inputTokens\":100,\"outputTokens\":1,\"inputTokensDetails\":" + details + "}";

        Assert.Equal(cached, Respond(usage).Tokens.CachedInput);
    }

    [Theory]
    [InlineData("  custom:gpt-5.5 ", null, "gpt-5.5", "custom")]
    [InlineData("CUSTOM-LOCAL: qwen3-coder", null, "qwen3-coder", "custom")]
    [InlineData("custom:", "glm-5.3", null, "custom")]
    [InlineData(null, " deepseek-v4-pro ", "deepseek-v4-pro", null)]
    [InlineData("  ", "auto", "auto", null)]
    [InlineData("custom-localx:y", null, "custom-localx:y", null)]
    [InlineData(" ", " ", null, null)]
    public void TheStreamModelWinsAndAUserProviderPrefixIsRemoved(string? stream, string? request, string? model, string? provider) =>
        Assert.Equal((model, provider), WorkBuddyLineReader.ResolveModel(stream, request));

    [Fact]
    public void ALineWithoutMessageIdUsesItsOwnIdAndOneWithoutAnyIdIsSkipped()
    {
        var parser = Parser();
        var usage = WorkBuddyLines.Usage(100, 20);
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            """{"timestamp":1792576801000,"type":"message","role":"assistant","providerData":{""" + usage + "}}",
            """{"id":"a2","timestamp":1792576802000,"type":"message","role":"assistant","providerData":{""" + usage + "}}");

        var response = Assert.Single(_sink.Latest);
        Assert.Equal(("workbuddy:a2", (long?)T, T + 2000), (response.Key, response.StartMs, response.EndMs));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void ACallFirstSeenInAnotherFileIsIgnoredWithoutChangingThePhase()
    {
        var deduper = new Deduper();
        deduper.TryClaim("m1", "/w/projects/Users-me-atlas/original.jsonl", out _);
        var parser = new WorkBuddyParser(Session, deduper, SourceKind.WorkBuddy, Root);
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.ToolCall(T + 1000, "c1", "m1", "call_1", WorkBuddyLines.Usage(100, 20)),
            WorkBuddyLines.Say(T + 2000, "a1", "m1", WorkBuddyLines.Usage(100, 20)));

        Assert.Empty(_sink.Emissions);
        Assert.Equal((AgentPhase.Generating, (long?)T, (long?)(T + 2000)), (parser.Phase, parser.InFlightStartMs, parser.LastLineMs));
    }

    [Fact]
    public void TheEndOfRunUsageMergesIntoItsCallAndEndsTheTurn()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 4000, "a1", "m1", WorkBuddyLines.Usage(1000, 30, cached: 600)),
            WorkBuddyLines.User(T + 10_000, "u2"),
            WorkBuddyLines.ModelUsage(T + 3990, "mu", "m1",
                WorkBuddyLines.Usage(1000, 30, cached: 600, reasoning: 9, raw: """{"prompt_tokens":1000,"completion_tokens":31}""")));

        var response = Assert.Single(_sink.Latest);
        Assert.Equal((T + 4000, new TokenCounts(1000, 600, 0, 31, 9)), (response.EndMs, response.Tokens));
        Assert.Equal([true, false], _sink.Emissions.Select(e => e.IsCompletion));
        Assert.Equal((AgentPhase.Idle, (long?)null), (parser.Phase, parser.InFlightStartMs));
    }

    [Fact]
    public void AnUnchangedRepeatEmitsNothingAndAResetReplaysKnownCallsAsUpdates()
    {
        var parser = Parser();
        string[] lines =
        [
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 4000, "a1", "m1", WorkBuddyLines.Usage(1000, 30)),
            WorkBuddyLines.ModelUsage(T + 4000, "mu", "m1", WorkBuddyLines.Usage(1000, 30)),
        ];
        Feed.Lines(parser, _sink, lines);
        Assert.Single(_sink.Emissions);

        parser.Reset();
        Feed.Lines(parser, _sink, lines);
        Assert.Equal([true, false], _sink.Emissions.Select(e => e.IsCompletion));
        Assert.Equal(_sink.Emissions[0].Response, _sink.Emissions[1].Response);
        Assert.Equal(SourceKind.WorkBuddy, parser.Source);
    }

    [Fact]
    public void ToolsKeepTheTurnGoingUntilEveryResultIsInAndARejectedOneEndsIt()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.ToolCall(T + 1000, "c1", "m1", "call_1"),
            WorkBuddyLines.ToolCall(T + 1003, "c2", "m1", "call_2", WorkBuddyLines.Usage(500, 40)));
        Assert.Equal(AgentPhase.Tools, parser.Phase);

        Feed.Lines(parser, _sink, WorkBuddyLines.ToolResult(T + 5000, "r1", "m1", "call_1"));
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Feed.Lines(parser, _sink, WorkBuddyLines.ToolResult(T + 5004, "r2", "m1", "call_2"));
        Assert.Equal((AgentPhase.Generating, (long?)(T + 5004)), (parser.Phase, parser.InFlightStartMs));

        Feed.Lines(parser, _sink,
            WorkBuddyLines.ToolCall(T + 7000, "c3", "m2", "call_3", WorkBuddyLines.Usage(600, 10)),
            WorkBuddyLines.Say(T + 7001, "n1", "x", extra: ",\"skipRun\":true"),
            WorkBuddyLines.ToolResult(T + 9000, "r3", "m2", "call_3", skipRun: true));
        Assert.Equal((AgentPhase.Idle, (long?)null), (parser.Phase, parser.InFlightStartMs));
        Assert.Equal([(long?)T, T + 5004], _sink.Latest.Select(r => r.StartMs));
    }

    [Fact]
    public void ACompactionCallStartsTheRequestItMadeRoomFor()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 8000, "a1", "m1", WorkBuddyLines.Usage(60_000, 2000), extra: ",\"agent\":\"compact\""));
        Assert.Equal((AgentPhase.Generating, (long?)(T + 8000)), (parser.Phase, parser.InFlightStartMs));

        Feed.Lines(parser, _sink, WorkBuddyLines.Say(T + 11_000, "a2", "m2", WorkBuddyLines.Usage(3000, 200)));
        Assert.Equal([(long?)T, T + 8000], _sink.Latest.Select(r => r.StartMs));
    }

    [Fact]
    public void InterruptedDraftsAndErrorNoticesEndTheTurnWithoutAResponse()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 2000, "a1", "m1", WorkBuddyLines.Usage(900, 12), extra: ",\"isPartialAborted\":true"));
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, _sink, WorkBuddyLines.Say(T + 2010, "n1", "n", extra: ",\"skipRun\":true,\"error\":{\"code\":\"aborted\"}"));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Empty(_sink.Emissions);
    }

    [Fact]
    public void ALaterDuplicateKeyReplacesTheEarlierOne()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            """{"id":"a1","timestamp":1792576801000,"type":"message","role":"assistant","providerData":{"messageId":"m1",""" + WorkBuddyLines.Usage(10, 2) + """},"providerData":{"messageId":"m1"}}""",
            """{"id":"a2","timestamp":1792576802000,"timestamp":"never","type":"message","role":"user"}""");

        Assert.Empty(_sink.Emissions);
        Assert.Equal((AgentPhase.Idle, (long?)(T + 1000)), (parser.Phase, parser.LastLineMs));
    }

    [Fact]
    public void MalformedLinesAreCountedAndBlankLinesAreNot()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink, "{not json", "[1,2]", "   ", "\"text\"");
        parser.Feed([(byte)'{', (byte)'"', 0xC3, (byte)'"', (byte)':', (byte)'1', (byte)'}'], _sink);

        Assert.Equal(4, parser.MalformedLines);
    }

    [Fact]
    public void ProjectFollowsTheLatestWorkingDirectoryOnEitherPlatform()
    {
        var parser = Parser();
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1", cwd: @"C:\\Users\\dev\\projects\\orbit"),
            WorkBuddyLines.User(T + 1, "u2", cwd: "   "));

        Assert.Equal("orbit", parser.Project);
    }

    [Fact]
    public void SubAgentsAreFilesInASubagentsFolderAndAttachWithinTheirEditionOnly()
    {
        var main = "/w/.workbuddy/projects/atlas/s1.jsonl";
        var sub = "/w/.workbuddy/projects/atlas/s1/subagents/agent-1.jsonl";
        var other = "/w/.workbuddy/projects/atlas/s1/subagents/agent-2.jsonl";
        var deduper = new Deduper();
        var parsers = new TranscriptParser[]
        {
            new WorkBuddyParser(main, deduper, SourceKind.WorkBuddy, "/w/.workbuddy/projects"),
            new WorkBuddyParser(sub, deduper, SourceKind.WorkBuddy, "/w/.workbuddy/projects"),
            new WorkBuddyParser(other, deduper, SourceKind.WorkBuddyAi, "/w/.workbuddy/projects"),
        };

        var parents = ParentLinks.Resolve([.. parsers.Select(AgentLink.Of)], StringComparer.Ordinal);

        Assert.Equal([false, true, true], parsers.Select(p => p.IsSubagent));
        Assert.Equal(main, parents.GetValueOrDefault(sub));
        Assert.False(parents.ContainsKey(other));
        Assert.False(parents.ContainsKey(main));
    }

    [Fact]
    public void OnlyNumbersFlagsIdsAndTheModelAreKept()
    {
        var line = WorkBuddyLineReader.Read(Encoding.UTF8.GetBytes(
            """{"id":"a1","timestamp":1792576801000,"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"SECRET"}],"providerData":{"messageId":"m1","model":"glm-5.3","toolResult":{"content":"SECRET"},"usage":{"inputTokens":1,"outputTokens":1}},"cwd":"/w/atlas","message":{"usage":{"cache_creation_input_tokens":999}}}"""));

        Assert.Equal((LineStatus.Valid, WorkBuddyItem.AssistantMessage, "m1", "glm-5.3"), (line.Status, line.Item, line.MessageId, line.Model));
        Assert.Equal(new TokenCounts(1, 0, 0, 1, 0), line.Tokens);
        Assert.DoesNotContain(typeof(WorkBuddyLine).GetFields(), f => f.Name.Contains("Content", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/w/subagents/.workbuddy/projects", "/w/subagents/.workbuddy/projects/atlas/s1.jsonl", false)]
    [InlineData("/w/.workbuddy/projects", "/w/.workbuddy/projects/subagents/s1.jsonl", false)]
    [InlineData("/w/.workbuddy/projects", "/w/.workbuddy/projects/atlas/s1/subagents/agent-1.jsonl", true)]
    public void SubAgentsAreDecidedByTheFoldersBelowTheRootOnly(string root, string path, bool subagent) =>
        Assert.Equal(subagent, new WorkBuddyParser(path, new Deduper(), SourceKind.WorkBuddy, root).IsSubagent);

    private WorkBuddyParser Parser() => new(Session, new Deduper(), SourceKind.WorkBuddy, Root);

    private ParsedResponse Respond(string usage)
    {
        var parser = Parser();
        Feed.Lines(parser, _sink, WorkBuddyLines.User(T, "u1"), WorkBuddyLines.Say(T + 3000, "a1", "m1", usage));
        return Assert.Single(_sink.Latest);
    }
}
