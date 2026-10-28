using System.Globalization;
using System.Text;
using AgentSpeed.Core.Omp;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class OmpParserTests
{
    private const string Model = "claude-opus-4-5";
    private const string Guard = "\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-opus-4-5\"";
    private const string SonnetGuard = "\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-sonnet-4-5\"";
    private const string SideGuard = "\"model\":\"gpt-5-mini\",\"provider\":\"openai\",\"api\":\"openai-responses\"";
    private const string TextContent = "[{\"type\":\"text\",\"text\":\"Done\"}]";
    private const string FileName = "2026-10-20T10-00-00-000Z_0199f1a2.jsonl";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "agent-speed-omp-tests", "sessions");
    private static readonly string Bucket = Path.Combine(Root, "--home-me-proj--");
    private static readonly string Main = Path.Combine(Bucket, FileName);
    private static readonly string Other = Path.Combine(Bucket, "2026-10-20T11-00-00-000Z_0199f1b3.jsonl");
    private static readonly string SubAgent = Path.Combine(Bucket, "2026-10-20T10-00-00-000Z_0199f1a2", "Explore.jsonl");
    private static readonly long T0 = Ts.Ms("2026-10-20T10:00:00.000Z");

    public static TheoryData<string> MalformedLines() =>
    [
        "this line is not json",
        "[1,2,3]",
        "42",
        "\"text\"",
        "null",
        "{\"type\":\"session\",\"timestamp\":\"2026-10-20T10:00:00Z\"",
        "{\"type\":\"title\"} trailing",
        "{\"type\":\"title\"}{\"type\":\"title\"}",
        "{\"type\":\"message\",}",
        "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[1,2}}",
    ];

    public static TheoryData<string> UnguardedCalls() =>
    [
        "\"provider\":\"p\",\"api\":\"a\",\"usage\":{\"input\":5}",
        "\"model\":5,\"provider\":\"p\",\"api\":\"a\",\"usage\":{\"input\":5}",
        "\"model\":null,\"provider\":\"p\",\"api\":\"a\",\"usage\":{\"input\":5}",
        "\"model\":\"m\",\"api\":\"a\",\"usage\":{\"input\":5}",
        "\"model\":\"m\",\"provider\":7,\"api\":\"a\",\"usage\":{\"input\":5}",
        "\"model\":\"m\",\"provider\":\"p\",\"usage\":{\"input\":5}",
        "\"model\":\"m\",\"provider\":\"p\",\"api\":[\"a\"],\"usage\":{\"input\":5}",
        "\"model\":\"m\",\"provider\":\"p\",\"api\":\"a\"",
        "\"model\":\"m\",\"provider\":\"p\",\"api\":\"a\",\"usage\":null",
        "\"model\":\"m\",\"provider\":\"p\",\"api\":\"a\",\"usage\":[5]",
        "\"model\":\"m\",\"provider\":\"p\",\"api\":\"a\",\"usage\":\"5\"",
    ];

    [Fact]
    public void AssistantMessageIsOneMeasuredResponseFromItsOwnStartAndDuration()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, TitleSlot(), Header(T0), User("u1", T0 + 1000));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, Reply("a1", T0 + 4600, N(T0 + 1010), duration: "3500.75",
            usage: Usage(100, 20, cacheRead: 50, cacheWrite: 10, reasoning: 7)));

        Assert.Equal(
            new ParsedResponse(Key("a1", T0 + 1010), SourceKind.Omp, Main, Model, T0 + 1010, T0 + 4510,
                new TokenCounts(Input: 160, CachedInput: 50, CacheWriteInput: 10, Output: 20, Reasoning: 7),
                Provider: "anthropic"),
            Assert.Single(sink.Latest));
        Assert.True(Assert.Single(sink.Emissions).IsCompletion);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 4600, parser.PhaseSinceMs);
        Assert.Null(parser.InFlightStartMs);
        Assert.Equal(Model, parser.Model);
        Assert.Equal("proj", parser.Project);
        Assert.Equal(T0 + 4600, parser.LastLineMs);
        Assert.Equal(T0, parser.HeaderMs);
        Assert.True(parser.HeaderSeen);
        Assert.Equal(SourceKind.Omp, parser.Source);
        Assert.True(parser.SourceDecided);
        Assert.False(parser.IsSubagent);
        Assert.Null(parser.ParentPath);
        Assert.Equal(0, parser.MalformedLines);
    }

    [Theory]
    [InlineData("{\"input\":10.9,\"output\":2,\"cacheRead\":-1,\"cacheWrite\":1,\"reasoningTokens\":\"4\"}", 11L, 0L, 1L, 2L, 0L)]
    [InlineData("{\"output\":5}", 0L, 0L, 0L, 5L, 0L)]
    [InlineData("{\"input\":1,\"cacheRead\":30,\"cacheWrite\":4,\"reasoningTokens\":9,\"totalTokens\":999}", 35L, 30L, 4L, 0L, 9L)]
    [InlineData("{\"input\":3,\"output\":true,\"cacheRead\":[5],\"cacheWrite\":{\"n\":5},\"reasoningTokens\":2.9}", 3L, 0L, 0L, 0L, 2L)]
    public void UsageFieldsAreNormalizedTokens(
        string usage, long input, long cachedInput, long cacheWriteInput, long output, long reasoning)
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 3000, N(T0 + 1000), usage: usage));

        Assert.Equal(new TokenCounts(input, cachedInput, cacheWriteInput, output, reasoning), Assert.Single(sink.Latest).Tokens);
    }

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void MalformedLinesAreCountedBeforeAndAfterTheHeader(string line)
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, line, Header(T0), line);

        Assert.Equal(2, parser.MalformedLines);
        Assert.True(parser.HeaderSeen);
        Assert.Equal(T0, parser.LastLineMs);
        Assert.Empty(sink.Emissions);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r")]
    public void BlankLinesAreIgnored(string line)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), line, Header(T0), line, User("u1", T0 + 1000), line);

        Assert.Equal(0, parser.MalformedLines);
        Assert.True(parser.HeaderSeen);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
    }

    [Fact]
    public void InvalidUtf8InAFieldThatIsReadMakesTheLineMalformed()
    {
        var parser = Parser();
        var sink = new RecordingSink();
        Feed.Lines(parser, sink, Header(T0));

        byte[] line =
        [
            .. "{\"type\":\"model_change\",\"id\":\"c"u8,
            0xFF,
            .. Encoding.UTF8.GetBytes("\",\"timestamp\":\"" + Ts.Iso(T0 + 1000) + "\"}"),
        ];
        parser.Feed(line, sink);

        Assert.Equal(1, parser.MalformedLines);
        Assert.Equal(T0, parser.LastLineMs);
    }

    [Fact]
    public void InvalidUtf8AnywhereMakesTheLineMalformed()
    {
        var parser = Parser();
        var sink = new RecordingSink();
        Feed.Lines(parser, sink, Header(T0));

        byte[] line =
        [
            .. Encoding.UTF8.GetBytes("{\"type\":\"model_change\",\"id\":\"c1\",\"timestamp\":\"" + Ts.Iso(T0 + 1000) + "\",\"note\":\""),
            0xFF,
            .. "\"}"u8,
        ];
        parser.Feed(line, sink);

        Assert.Equal(1, parser.MalformedLines);
        Assert.Equal(T0, parser.LastLineMs);
    }

    [Fact]
    public void TitleLinesAreIgnoredEvenBeforeTheHeader()
    {
        var parser = Parser();
        var sink = new RecordingSink();
        var stampedTitle = Obj(F("type", Q("title")), F("id", Q("t1")), F("timestamp", Q(Ts.Iso(T0 + 90_000))), F("title", Q("Fix")));

        Feed.Lines(parser, sink, stampedTitle, Reply("a0", T0 + 2000, N(T0 + 1000)));
        Assert.False(parser.HeaderSeen);
        Assert.Null(parser.LastLineMs);
        Assert.Empty(sink.Emissions);

        Feed.Lines(parser, sink, Header(T0), stampedTitle, TitleSlot(), Reply("a1", T0 + 3000, N(T0 + 1000)));

        Assert.True(parser.HeaderSeen);
        Assert.Equal(T0, parser.HeaderMs);
        Assert.Equal(Key("a1", T0 + 1000), Assert.Single(sink.Latest).Key);
        Assert.Equal(T0 + 3000, parser.LastLineMs);
        Assert.Equal(0, parser.MalformedLines);
    }

    [Fact]
    public void EntriesBeforeTheHeaderAreIgnored()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink,
            User("u0", T0 + 1000),
            Reply("a0", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c0")),
            SideCall("m0", T0 + 3100),
            SessionExit("x0", T0 + 3200));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.LastLineMs);
        Assert.Null(parser.Model);
        Assert.Null(parser.Project);
        Assert.False(parser.HeaderSeen);

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 5000, N(T0 + 4000)));

        Assert.Equal(Key("a1", T0 + 4000), Assert.Single(sink.Latest).Key);
    }

    [Fact]
    public void OnlyTheFirstHeaderCounts()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0, "/home/me/first"), Header(T0 + 60_000, "/home/me/second"));

        Assert.Equal(T0, parser.HeaderMs);
        Assert.Equal("first", parser.Project);
        Assert.Equal(T0, parser.LastLineMs);

        Feed.Lines(parser, sink, User("u1", T0 - 1500));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 - 1500, parser.InFlightStartMs);
        Assert.Equal(T0, parser.LastLineMs);
    }

    [Theory]
    [InlineData("\"/home/me/proj\"", "proj")]
    [InlineData("\"C:\\\\Users\\\\me\\\\proj\"", "proj")]
    [InlineData("\"/home/me/proj/\"", "proj")]
    [InlineData("\"/\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("5", null)]
    [InlineData(null, null)]
    public void ProjectIsTheBasenameOfTheHeaderCwd(string? cwd, string? expected)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Obj(
            F("type", Q("session")), F("version", "3"), F("id", Q("s1")), F("timestamp", Q(Ts.Iso(T0))), F("cwd", cwd),
            F("parentSession", Q("/home/me/.omp/agent/sessions/b/parent.jsonl"))));

        Assert.True(parser.HeaderSeen);
        Assert.Equal(expected, parser.Project);
        Assert.False(parser.IsSubagent);
    }

    [Fact]
    public void AHeaderWithoutATimestampDisablesTheCopiedHistoryRule()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Obj(F("type", Q("session")), F("id", Q("s1")), F("timestamp", N(T0)), F("cwd", Q("/home/me/proj"))));

        Assert.True(parser.HeaderSeen);
        Assert.Null(parser.HeaderMs);
        Assert.Null(parser.LastLineMs);
        Assert.Equal("proj", parser.Project);

        Feed.Lines(parser, sink, Reply("a1", T0 - 86_400_000, N(T0 - 86_401_000)));

        Assert.Single(sink.Latest);
        Assert.Equal(T0 - 86_400_000, parser.LastLineMs);
    }

    [Theory]
    [InlineData(null, "\"{TS}\"")]
    [InlineData("5", "\"{TS}\"")]
    [InlineData("null", "\"{TS}\"")]
    [InlineData("[\"a1\"]", "\"{TS}\"")]
    [InlineData("\"a1\"", null)]
    [InlineData("\"a1\"", "1760954405000")]
    [InlineData("\"a1\"", "\"yesterday\"")]
    [InlineData("\"a1\"", "null")]
    [InlineData("\"a1\"", "\"2026-13-45T10:00:05Z\"")]
    public void EntriesWithoutAStringIdOrAParseableTimestampAreIgnoredEntirely(string? id, string? timestamp)
    {
        var parser = Parser();
        var sink = new RecordingSink();
        var stamp = timestamp?.Replace("{TS}", Ts.Iso(T0 + 5000), StringComparison.Ordinal);
        string Line(string type, params string?[] fields) => Obj([F("type", Q(type)), F("id", id), F("timestamp", stamp), .. fields]);

        Feed.Lines(parser, sink, Header(T0),
            Line("message", F("message", UserBody(T0 + 5000))),
            Line("message", F("message", ReplyBody(N(T0 + 4000)))),
            Line("model_usage", SideGuard, F("usage", Usage())));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0, parser.LastLineMs);
        Assert.Equal(0, parser.MalformedLines);

        Feed.Lines(parser, sink, User("u1", T0 + 6000), Line("custom", F("customType", Q("session_exit"))));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.LastLineMs);
    }

    [Fact]
    public void CopiedHistoryIsIgnoredCompletely()
    {
        var deduper = new Deduper();
        var parser = Parser(Main, deduper);
        var sink = new RecordingSink();
        var copied = Reply("a0", T0 + 7_999, N(T0 + 5_000));

        Feed.Lines(parser, sink, Header(T0 + 10_000), User("u0", T0 + 7_999), copied, SideCall("m0", T0 + 7_999));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 10_000, parser.LastLineMs);

        Feed.Lines(parser, sink, User("u1", T0 + 8_000), SessionExit("x0", T0 + 7_000));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 8_000, parser.InFlightStartMs);
        Assert.Equal(T0 + 10_000, parser.LastLineMs);

        Feed.Lines(parser, sink, Reply("a1", T0 + 12_000, N(T0 + 1)));

        var response = Assert.Single(sink.Latest);
        Assert.Equal(Key("a1", T0 + 1), response.Key);
        Assert.Equal(T0 + 1, response.StartMs);
        Assert.Equal(T0 + 12_000, parser.LastLineMs);

        var source = Parser(Other, deduper);
        var sourceSink = new RecordingSink();
        Feed.Lines(source, sourceSink, Header(T0), copied);

        Assert.True(Assert.Single(sourceSink.Emissions).IsCompletion);
    }

    [Theory]
    [InlineData("{S}", true)]
    [InlineData("{S}.9", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("-1000", false)]
    [InlineData("\"{S}\"", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("1e400", false)]
    public void KeyUsesTheMessageTimestampElseTheEntryTimestamp(string? started, bool usesStart)
    {
        var parser = Parser();
        var sink = new RecordingSink();
        var start = T0 + 1000;
        var entry = T0 + 5000;

        Feed.Lines(parser, sink, Header(T0), Reply("a1", entry, started?.Replace("{S}", N(start), StringComparison.Ordinal)));

        var response = Assert.Single(sink.Latest);
        Assert.Equal(Key("a1", usesStart ? start : entry), response.Key);
        Assert.Equal(usesStart ? start : (long?)null, response.StartMs);
        Assert.Equal(usesStart ? start + 1500 : entry, response.EndMs);
    }

    [Fact]
    public void AStartBetweenZeroAndOneTruncatesToZero()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 5000, "0.5"));

        Assert.Equal(new ParsedResponse("omp:a1:0", SourceKind.Omp, Main, Model, 0, 1500, new TokenCounts(100, 0, 0, 20, 0), Provider: "anthropic"),
            Assert.Single(sink.Latest));
    }

    [Theory]
    [InlineData("2000", "{C}", 3000L)]
    [InlineData("0", "{C}", 1000L)]
    [InlineData("2000.999", null, 3000L)]
    [InlineData("-1", "{C}", 4200L)]
    [InlineData("-0.5", "{C}", 4200L)]
    [InlineData("\"2000\"", "{C}.7", 4200L)]
    [InlineData("null", "{C}", 4200L)]
    [InlineData(null, "{C}", 4200L)]
    [InlineData("1e400", "{C}", 4200L)]
    [InlineData(null, null, 5000L)]
    [InlineData(null, "0", 5000L)]
    [InlineData(null, "-5", 5000L)]
    [InlineData(null, "\"{C}\"", 5000L)]
    public void EndIsStartPlusDurationElseCompletedAtElseTheEntryTimestamp(string? duration, string? completedAt, long expectedOffset)
    {
        var parser = Parser();
        var sink = new RecordingSink();
        var completed = completedAt?.Replace("{C}", N(T0 + 4200), StringComparison.Ordinal);

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 5000, N(T0 + 1000), duration: duration, extra: F("completedAt", completed)));

        var response = Assert.Single(sink.Latest);
        Assert.Equal(T0 + 1000, response.StartMs);
        Assert.Equal(T0 + expectedOffset, response.EndMs);
    }

    [Fact]
    public void DurationNeedsAKnownStart()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0),
            Reply("a1", T0 + 5000, null, duration: "2000", extra: F("completedAt", N(T0 + 4200))),
            Reply("a2", T0 + 6000, null, duration: "2000"));

        Assert.Equal(
            [(Key("a1", T0 + 5000), (long?)null, T0 + 4200), (Key("a2", T0 + 6000), null, T0 + 6000)],
            sink.Latest.Select(r => (r.Key, r.StartMs, r.EndMs)));
    }

    [Theory]
    [InlineData("\"error\"")]
    [InlineData("\"aborted\"")]
    [InlineData(null)]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"reason\":\"stop\"}")]
    public void ErrorAbortedOrMissingStopReasonCountsInTotalsOnly(string? stop)
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 500),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: stop, content: ToolCalls("c1")));

        var response = Assert.Single(sink.Latest);
        Assert.Null(response.StartMs);
        Assert.Equal(T0 + 2500, response.EndMs);
        Assert.True(response.Final);
        Assert.Equal(Model, parser.Model);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);

        Feed.Lines(parser, sink, ToolResult("r1", T0 + 3500, "c1"));

        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void AnUnknownStopReasonKeepsTheStartAndEndsTheTurn()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 500), Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"refusal\""));

        Assert.Equal(T0 + 1000, Assert.Single(sink.Latest).StartMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void AllZeroUsageEmitsNothingButStillChangesThePhase()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 1000),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", usage: Usage(0, 0, reasoning: 9), content: ToolCalls("c1")));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Null(parser.Model);
        Assert.Equal(T0 + 3000, parser.LastLineMs);

        Feed.Lines(parser, sink, User("u2", T0 + 4000), Reply("a2", T0 + 5000, N(T0 + 4000), stop: "\"aborted\"", usage: Usage(0, 0)));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(parser, sink, User("u3", T0 + 6000), Reply("a3", T0 + 7000, N(T0 + 6000),
            usage: "{\"input\":-5,\"output\":\"7\",\"cacheRead\":true,\"cacheWrite\":0.5,\"reasoningTokens\":3}",
            extra: F("stopDetails", "{\"type\":\"pause_turn\"}")));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 7500, parser.InFlightStartMs);
        Assert.Null(parser.Model);
    }

    [Theory]
    [MemberData(nameof(UnguardedCalls))]
    public void CallsFailingTheGuardOnlyCountForLastLineMs(string fields)
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 1000),
            Assistant("a1", T0 + 3000, fields, F("content", ToolCalls("c1")), F("stopReason", Q("toolUse")),
                F("timestamp", N(T0 + 1000)), F("duration", "1500")),
            Entry("model_usage", "m1", T0 + 3500, fields));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
        Assert.Equal(T0 + 3500, parser.LastLineMs);
        Assert.Null(parser.Model);
    }

    [Fact]
    public void AnEmptyModelStringPassesTheGuard()
    {
        const string fields = "\"model\":\"\",\"provider\":\"p\",\"api\":\"a\",\"usage\":{\"input\":5}";
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 500),
            Assistant("a1", T0 + 3000, fields, F("stopReason", Q("stop")), F("timestamp", N(T0 + 1000))),
            Entry("model_usage", "m1", T0 + 3500, fields));

        Assert.Equal(["", ""], sink.Latest.Select(r => r.Model));
        Assert.Equal(string.Empty, parser.Model);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void SideCallsCountInTotalsOnlyAndNeverSetTheModelOrPhase()
    {
        var deduper = new Deduper();
        var parser = Parser(Main, deduper);
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 900, N(T0 + 100), duration: "700"), User("u1", T0 + 1000),
            SideCall("m1", T0 + 2000, Usage(40, 8, cacheRead: 30, cacheWrite: 2, reasoning: 3)));

        Assert.Equal(
            new ParsedResponse(Key("m1", T0 + 2000), SourceKind.Omp, Main, "gpt-5-mini", null, T0 + 2000,
                new TokenCounts(Input: 72, CachedInput: 30, CacheWriteInput: 2, Output: 8, Reasoning: 3),
                Provider: "openai"),
            sink.Latest[1]);
        Assert.True(sink.Emissions[1].IsCompletion);
        Assert.Equal(Model, parser.Model);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
        Assert.Equal(T0 + 2000, parser.LastLineMs);

        Feed.Lines(parser, sink, SideCall("m1", T0 + 2000, Usage(50, 8)));

        Assert.Equal(new TokenCounts(50, 0, 0, 8, 0), sink.Latest[1].Tokens);
        Assert.False(sink.Emissions[^1].IsCompletion);

        Feed.Lines(parser, sink, SideCall("m2", T0 + 2500, Usage(0, 0, reasoning: 4)));

        Assert.Equal(2, sink.Latest.Count);
        Assert.Equal(T0 + 2500, parser.LastLineMs);

        var other = Parser(Other, deduper);
        var otherSink = new RecordingSink();
        Feed.Lines(other, otherSink, Header(T0), User("u9", T0 + 1500), SideCall("m1", T0 + 2000), SideCall("m3", T0 + 2600));

        Assert.Equal([Key("m3", T0 + 2600)], otherSink.Latest.Select(r => r.Key));
        Assert.Equal(T0 + 2600, other.LastLineMs);
        Assert.Equal(AgentPhase.Generating, other.Phase);
        Assert.Null(other.Model);
    }

    [Fact]
    public void AKeyFirstSeenInAnotherFileIsIgnoredButStillCountsForLastLineMs()
    {
        var deduper = new Deduper();
        var first = Parser(Main, deduper);
        var firstSink = new RecordingSink();
        var reply = Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1"));

        Feed.Lines(first, firstSink, Header(T0), reply, Reply("a2", T0 + 4000, N(T0 + 3500), usage: Usage(0, 0)));
        Assert.Single(firstSink.Emissions);

        var second = Parser(Other, deduper);
        var secondSink = new RecordingSink();
        Feed.Lines(second, secondSink, Header(T0 + 500), User("u1", T0 + 900), reply);

        Assert.Empty(secondSink.Emissions);
        Assert.Equal(AgentPhase.Generating, second.Phase);
        Assert.Equal(T0 + 900, second.InFlightStartMs);
        Assert.Equal(T0 + 3000, second.LastLineMs);
        Assert.Null(second.Model);

        Feed.Lines(second, secondSink, Reply("a2", T0 + 4000, N(T0 + 3500)));

        Assert.True(Assert.Single(secondSink.Emissions).IsCompletion);

        Feed.Lines(first, firstSink, Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", usage: Usage(100, 30), content: ToolCalls("c1")));

        Assert.Equal(2, firstSink.Emissions.Count);
        Assert.False(firstSink.Emissions[1].IsCompletion);
    }

    [Fact]
    public void ARepeatInTheSameFileIsAnUpdate()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 3000, N(T0 + 1000)), Reply("a1", T0 + 3000, N(T0 + 1000)));
        Feed.Lines(parser, sink, Reply("a1", T0 + 3100, N(T0 + 1000), duration: "1800", usage: Usage(100, 25), guard: SonnetGuard));

        Assert.True(sink.Emissions[0].IsCompletion);
        Assert.All(sink.Emissions.Skip(1), emission => Assert.False(emission.IsCompletion));
        var response = Assert.Single(sink.Latest);
        Assert.Equal(T0 + 2800, response.EndMs);
        Assert.Equal(25, response.Tokens.Output);
        Assert.Equal("claude-sonnet-4-5", response.Model);
        Assert.Equal("claude-sonnet-4-5", parser.Model);
        Assert.Equal(T0 + 3100, parser.LastLineMs);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("developer")]
    public void InputMessagesStartTheInFlightEstimate(string role)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0),
            Reply("a1", T0 + 1000, N(T0 + 500), stop: "\"toolUse\"", content: ToolCalls("c1")),
            User("u1", T0 + 2000, role));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 2000, parser.InFlightStartMs);
        Assert.Equal(T0 + 2000, parser.PhaseSinceMs);
    }

    [Fact]
    public void AToolBatchEndsWhenEveryCallHasAResult()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 1000),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1", "c2", "c1")));

        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
        Assert.Equal(T0 + 3000, parser.PhaseSinceMs);

        Feed.Lines(parser, sink, ToolResult("r0", T0 + 3500, "unknown"), ToolResult("r1", T0 + 4000, "c2"));

        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Equal(T0 + 4000, parser.LastLineMs);

        Feed.Lines(parser, sink, ToolResult("r2", T0 + 4500, "c1"));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 4500, parser.InFlightStartMs);
        Assert.Equal(T0 + 4500, parser.PhaseSinceMs);

        Feed.Lines(parser, sink, ToolResult("r3", T0 + 4600, "c1"));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 4500, parser.InFlightStartMs);
    }

    [Fact]
    public void ANewToolBatchReplacesThePendingCalls()
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1")),
            Reply("a2", T0 + 5000, N(T0 + 3500), stop: "\"toolUse\"", content: ToolCalls("c2")),
            ToolResult("r2", T0 + 6000, "c2"));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);
    }

    [Theory]
    [InlineData("\"toolUse\"", AgentPhase.Tools)]
    [InlineData("\"stop\"", AgentPhase.Tools)]
    [InlineData("\"length\"", AgentPhase.Tools)]
    [InlineData("\"refusal\"", AgentPhase.Idle)]
    [InlineData("\"error\"", AgentPhase.Idle)]
    [InlineData("\"aborted\"", AgentPhase.Idle)]
    public void ToolCallsStartToolsUnderToolUseStopAndLength(string stop, AgentPhase expected)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0), User("u1", T0 + 500),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: stop, content: ToolCalls("c1")));

        Assert.Equal(expected, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
    }

    [Theory]
    [InlineData("[{\"type\":\"toolCall\",\"id\":5},{\"type\":\"toolCall\"},{\"type\":\"text\",\"id\":\"t1\"},\"x\",5,null]")]
    [InlineData("\"toolCall\"")]
    [InlineData("{\"type\":\"toolCall\",\"id\":\"c1\"}")]
    [InlineData("[{\"type\":\"toolCall\",\"id\":\"c1\",\"type\":\"text\"}]")]
    [InlineData("[{\"type\":\"toolCall\",\"id\":\"c1\",\"id\":7}]")]
    public void ToolCallsNeedAStringIdInsideTheContentArray(string content)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0), User("u1", T0 + 500),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: content));

        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Theory]
    [InlineData("false", AgentPhase.Idle)]
    [InlineData(null, AgentPhase.Idle)]
    [InlineData("null", AgentPhase.Idle)]
    [InlineData("\"true\"", AgentPhase.Idle)]
    [InlineData("1", AgentPhase.Idle)]
    [InlineData("true", AgentPhase.Tools)]
    public void AYieldResultEndsTheRunUnlessItIsAnError(string? isError, AgentPhase expected)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0), User("u1", T0 + 500),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1", "c2")),
            ToolResult("r1", T0 + 4000, "c1", toolName: "yield", isError: isError));

        Assert.Equal(expected, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
    }

    [Fact]
    public void AFailedYieldThatCompletesTheBatchStartsTheNextRequest()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1")),
            ToolResult("r1", T0 + 4000, "c1", toolName: "yield", isError: "true"));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 4000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, User("u2", T0 + 5000), ToolResult("r2", T0 + 6000, "elsewhere", toolName: "yield"));

        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 6000, parser.PhaseSinceMs);
    }

    [Theory]
    [InlineData("\"stop\"", "{\"type\":\"pause_turn\"}", null, AgentPhase.Generating)]
    [InlineData("\"stop\"", "{\"type\":\"pause_turn\",\"type\":\"end_turn\"}", null, AgentPhase.Idle)]
    [InlineData("\"stop\"", "{\"type\":\"end_turn\",\"type\":\"pause_turn\"}", null, AgentPhase.Generating)]
    [InlineData("\"stop\"", "{\"type\":\"end_turn\"}", null, AgentPhase.Idle)]
    [InlineData("\"stop\"", "\"pause_turn\"", null, AgentPhase.Idle)]
    [InlineData("\"toolUse\"", "{\"type\":\"pause_turn\"}", null, AgentPhase.Idle)]
    [InlineData("\"length\"", "{\"type\":\"pause_turn\"}", null, AgentPhase.Idle)]
    [InlineData("\"stop\"", "{\"type\":\"pause_turn\"}", "c1", AgentPhase.Tools)]
    public void PauseTurnSamplesAgainFromTheEndOfTheResponse(string stop, string details, string? call, AgentPhase expected)
    {
        var parser = Parser();

        Feed.Lines(parser, new RecordingSink(), Header(T0), User("u1", T0 + 500),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: stop, content: call is null ? null : ToolCalls(call), extra: F("stopDetails", details)));

        Assert.Equal(expected, parser.Phase);
        Assert.Equal(expected == AgentPhase.Generating ? T0 + 2500 : (long?)null, parser.InFlightStartMs);
    }

    [Fact]
    public void PauseTurnWithoutADurationSamplesAgainFromTheFallbackEnd()
    {
        const string pause = "\"stopDetails\":{\"type\":\"pause_turn\"}";
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), Reply("a1", T0 + 3000, N(T0 + 1000), duration: null, extra: pause + "," + F("completedAt", N(T0 + 2800))));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 2800, parser.InFlightStartMs);

        Feed.Lines(parser, sink, Reply("a2", T0 + 5000, N(T0 + 4000), duration: null, extra: pause));

        Assert.Equal(T0 + 5000, parser.InFlightStartMs);
    }

    [Fact]
    public void SessionExitEndsTheRun()
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 1000),
            Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1")),
            SessionExit("x1", T0 + 3500));

        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 3500, parser.PhaseSinceMs);
        Assert.Equal(T0 + 3500, parser.LastLineMs);

        Feed.Lines(parser, sink, ToolResult("r1", T0 + 4000, "c1"));

        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 4000, parser.LastLineMs);
    }

    [Theory]
    [InlineData("message", "\"message\":{\"role\":\"bashExecution\",\"command\":\"ls\"}")]
    [InlineData("message", "\"message\":{\"role\":\"custom\"}")]
    [InlineData("message", "\"message\":{\"role\":5}")]
    [InlineData("message", "\"message\":{\"content\":\"no role\"}")]
    [InlineData("message", "\"message\":\"text\"")]
    [InlineData("message", null)]
    [InlineData("model_change", "\"provider\":\"openai\",\"modelId\":\"gpt-5\"")]
    [InlineData("compaction", "\"summary\":\"short\",\"tokensBefore\":5000")]
    [InlineData("custom", "\"customType\":\"other\"")]
    [InlineData("custom", "\"customType\":5")]
    [InlineData("label", "\"customType\":\"session_exit\"")]
    public void OtherRecordsOnlyCountForLastLineMs(string type, string? fields)
    {
        var parser = Parser();
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 1000), Entry(type, "e1", T0 + 2000, fields));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);
        Assert.Equal(T0 + 2000, parser.LastLineMs);
    }

    [Fact]
    public void DuplicateJsonKeysResolveToTheLastOccurrence()
    {
        var ts = Q(Ts.Iso(T0 + 3000));
        var reply = ReplyBody(N(T0 + 1000));

        Assert.Single(Run(Obj(F("type", Q("title")), F("type", Q("message")), F("id", Q("a1")), F("timestamp", ts), F("message", reply))).Sink.Latest);
        Assert.Empty(Run(Obj(F("type", Q("message")), F("type", Q("title")), F("id", Q("a1")), F("timestamp", ts), F("message", reply))).Sink.Latest);
        Assert.Empty(Run(Obj(F("type", Q("message")), F("id", Q("a1")), F("id", "7"), F("timestamp", ts), F("message", reply))).Sink.Latest);
        Assert.Equal(Key("a2", T0 + 1000), Assert.Single(Run(Obj(F("type", Q("message")), F("id", "7"), F("id", Q("a2")), F("timestamp", ts), F("message", reply))).Sink.Latest).Key);
        Assert.Empty(Run(Obj(F("type", Q("message")), F("id", Q("a1")), F("timestamp", ts), F("timestamp", Q("bad")), F("message", reply))).Sink.Latest);
        Assert.Single(Run(Obj(F("type", Q("message")), F("id", Q("a1")), F("timestamp", Q("bad")), F("timestamp", ts), F("message", reply))).Sink.Latest);

        var replaced = Run(Obj(F("type", Q("message")), F("id", Q("a1")), F("timestamp", ts), F("message", reply), F("message", Q("x"))));
        Assert.Empty(replaced.Sink.Latest);
        Assert.Equal(AgentPhase.Generating, replaced.Parser.Phase);
        Assert.Equal(T0 + 3000, replaced.Parser.LastLineMs);

        Assert.NotNull(Assert.Single(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), stop: "\"error\"", extra: F("stopReason", Q("stop"))))).Sink.Latest).StartMs);
        Assert.Null(Assert.Single(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("stopReason", Q("error"))))).Sink.Latest).StartMs);
        Assert.Empty(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("usage", "null")))).Sink.Latest);
        Assert.Equal("claude-sonnet-4-5", Assert.Single(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("model", Q("claude-sonnet-4-5"))))).Sink.Latest).Model);
        Assert.Empty(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("model", "5")))).Sink.Latest);
        Assert.Equal(7, Assert.Single(Run(Reply("a1", T0 + 3000, N(T0 + 1000), usage: "{\"input\":5,\"output\":1,\"input\":7}")).Sink.Latest).Tokens.Input);
        Assert.Equal(Key("a1", T0 + 3000), Assert.Single(Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("timestamp", Q("x"))))).Sink.Latest).Key);

        var toolsReplaced = Run(Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1"), extra: F("content", "[]")));
        Assert.Equal(AgentPhase.Idle, toolsReplaced.Parser.Phase);

        var roleReplaced = Run(Message("a1", T0 + 3000, ReplyBody(N(T0 + 1000), extra: F("role", Q("user")))));
        Assert.Empty(roleReplaced.Sink.Latest);
        Assert.Equal(T0 + 3000, roleReplaced.Parser.InFlightStartMs);

        var exitReplaced = Run(Entry("custom", "x1", T0 + 3000, F("customType", Q("session_exit")), F("customType", Q("other"))));
        Assert.Equal(AgentPhase.Generating, exitReplaced.Parser.Phase);

        var header = Parser();
        Feed.Lines(header, new RecordingSink(), Obj(
            F("type", Q("message")), F("type", Q("session")), F("id", Q("s1")), F("timestamp", Q(Ts.Iso(T0))),
            F("cwd", Q("/home/me/x")), F("cwd", Q("/home/me/y"))));
        Assert.True(header.HeaderSeen);
        Assert.Equal("y", header.Project);
    }

    [Fact]
    public void ResetClearsPerFileStateButKeepsTheDeduper()
    {
        var deduper = new Deduper();
        var parser = Parser(SubAgent, deduper);
        var sink = new RecordingSink();
        var reply = Reply("a1", T0 + 3000, N(T0 + 1000), stop: "\"toolUse\"", content: ToolCalls("c1"));

        Feed.Lines(parser, sink, Header(T0), User("u1", T0 + 500), reply);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Equal(Model, parser.Model);

        parser.Reset();

        Assert.False(parser.HeaderSeen);
        Assert.Null(parser.HeaderMs);
        Assert.Null(parser.Project);
        Assert.Null(parser.Model);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.PhaseSinceMs);
        Assert.Null(parser.InFlightStartMs);
        Assert.Null(parser.LastLineMs);
        Assert.True(parser.IsSubagent);
        Assert.Equal(SourceKind.Omp, parser.Source);
        Assert.True(parser.SourceDecided);

        Feed.Lines(parser, sink, ToolResult("r1", T0 + 3500, "c1"));

        Assert.Null(parser.LastLineMs);

        Feed.Lines(parser, sink, Header(T0), reply);

        Assert.Equal(2, sink.Emissions.Count);
        Assert.False(sink.Emissions[1].IsCompletion);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
        Assert.Equal("proj", parser.Project);

        var other = Parser(Other, deduper);
        var otherSink = new RecordingSink();
        Feed.Lines(other, otherSink, Header(T0), reply);

        Assert.Empty(otherSink.Emissions);
    }

    [Theory]
    [InlineData("bucket/s.jsonl", false, null)]
    [InlineData("bucket/stem/Task.jsonl", true, "bucket/stem.jsonl")]
    [InlineData("bucket/stem/__advisor.jsonl", true, "bucket/stem.jsonl")]
    [InlineData("bucket/stem/Task/Child.jsonl", true, "bucket/stem/Task.jsonl")]
    [InlineData("s.jsonl", false, null)]
    public void SubAgentsLiveThreeOrMoreComponentsBelowTheRoot(string relative, bool expected, string? parent)
    {
        var path = Path.Combine([Root, .. relative.Split('/')]);
        var parser = new OmpParser(path, new Deduper(), Root);

        Assert.Equal(expected, parser.IsSubagent);
        Assert.Equal(expected, parser.PathIsSubagent);
        Assert.Equal(parent is null ? null : Path.Combine([Root, .. parent.Split('/')]), parser.ParentPath);

        Feed.Lines(parser, new RecordingSink(), Header(T0));

        Assert.Equal(expected, parser.IsSubagent);
        Assert.Equal(expected, new OmpParser(path, new Deduper(), Root + Path.DirectorySeparatorChar).IsSubagent);
    }

    [Fact]
    public void FilesOutsideTheRootAreMainSessions()
    {
        var outside = Path.Combine(Path.GetTempPath(), "agent-speed-omp-elsewhere", "a", "b", "c.jsonl");

        Assert.False(new OmpParser(outside, new Deduper(), Root).IsSubagent);
        Assert.False(new OmpParser(SubAgent, new Deduper(), string.Empty).IsSubagent);
        Assert.Null(new OmpParser(outside, new Deduper(), Root).ParentPath);
    }

    [Theory]
    [InlineData("title,header", true)]
    [InlineData("header", true)]
    [InlineData("garbage,header", true)]
    [InlineData("blank,header", true)]
    [InlineData("user,header", true)]
    [InlineData("title,user,header", false)]
    [InlineData("title,title,header", false)]
    [InlineData("title", false)]
    [InlineData("", false)]
    public void ReadingFromAnOffsetFirstTakesTheHeaderFromTheFirstTwoLines(string layout, bool found)
    {
        using var temp = new TempDirectory();
        var lines = layout.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(token => token switch
        {
            "title" => TitleSlot(),
            "header" => Header(T0),
            "garbage" => "{\"type\":\"session\"",
            "blank" => string.Empty,
            "user" => User("u0", T0 + 100),
            _ => throw new ArgumentException(token, nameof(layout)),
        });
        var path = temp.WriteFile("sessions/bucket/" + FileName, string.Concat(lines.Select(line => line + "\n")));
        var parser = new OmpParser(path, new Deduper(), temp.Combine("sessions"));
        var sink = new RecordingSink();
        var batch = parser.NewBatch();

        parser.PrepareBatch(batch, Math.Max(1, new FileInfo(path).Length));
        Assert.False(parser.HeaderSeen);

        batch.OnLine(Encoding.UTF8.GetBytes(Reply("a1", T0 + 3000, N(T0 + 1000))));
        batch.OnLine(Encoding.UTF8.GetBytes(Reply("a0", T0 - 2001, N(T0 - 3000))));
        Assert.Equal(2, batch.Count);
        batch.ApplyTo(sink);

        Assert.Equal(found, parser.HeaderSeen);
        Assert.Equal(0, parser.MalformedLines);
        if (found)
        {
            Assert.Equal(T0, parser.HeaderMs);
            Assert.Equal("proj", parser.Project);
            Assert.Equal(Key("a1", T0 + 1000), Assert.Single(sink.Latest).Key);
            Assert.Equal(T0 + 3000, parser.LastLineMs);
        }
        else
        {
            Assert.Empty(sink.Emissions);
            Assert.Null(parser.LastLineMs);
            Assert.Null(parser.Project);
        }
    }

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("", false)]
    public void TheHeaderProbeAcceptsCrlfAndAnUnterminatedSecondLine(string separator, bool terminated)
    {
        using var temp = new TempDirectory();
        var content = TitleSlot() + (separator.Length > 0 ? separator : "\n") + Header(T0) + (terminated ? separator : string.Empty);
        var path = temp.WriteFile("sessions/bucket/" + FileName, content);
        var parser = new OmpParser(path, new Deduper(), temp.Combine("sessions"));
        var batch = parser.NewBatch();

        parser.PrepareBatch(batch, new FileInfo(path).Length);
        batch.ApplyTo(new RecordingSink());

        Assert.True(parser.HeaderSeen);
        Assert.Equal(T0, parser.HeaderMs);
        Assert.Equal("proj", parser.Project);
    }

    [Fact]
    public void ReadingFromTheStartDoesNotProbeTheHeader()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("sessions/bucket/" + FileName, TitleSlot() + "\n" + Header(T0) + "\n");
        var parser = new OmpParser(path, new Deduper(), temp.Combine("sessions"));
        var sink = new RecordingSink();
        var batch = parser.NewBatch();

        parser.PrepareBatch(batch, 0);
        batch.OnLine(Encoding.UTF8.GetBytes(Reply("a1", T0 + 3000, N(T0 + 1000))));
        batch.ApplyTo(sink);

        Assert.False(parser.HeaderSeen);
        Assert.Empty(sink.Emissions);

        batch.Clear();
        parser.PrepareBatch(batch, 0);
        batch.OnLine(Encoding.UTF8.GetBytes(TitleSlot()));
        batch.OnLine(Encoding.UTF8.GetBytes(Header(T0)));
        batch.OnLine(Encoding.UTF8.GetBytes(Reply("a1", T0 + 3000, N(T0 + 1000))));
        batch.ApplyTo(sink);

        Assert.True(parser.HeaderSeen);
        Assert.Single(sink.Latest);
    }

    [Fact]
    public void TheHeaderIsProbedOnceUntilReset()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("sessions/bucket/" + FileName, TitleSlot() + "\n" + Header(T0) + "\n");
        var parser = new OmpParser(path, new Deduper(), temp.Combine("sessions"));
        var sink = new RecordingSink();
        var batch = parser.NewBatch();

        parser.PrepareBatch(batch, 10);
        batch.ApplyTo(sink);

        Assert.True(parser.HeaderSeen);
        Assert.Equal(T0, parser.HeaderMs);
        Assert.Equal(T0, parser.LastLineMs);

        File.WriteAllText(path, TitleSlot() + "\n" + Header(T0 + 50_000, "/home/me/other") + "\n");
        batch.Clear();
        parser.PrepareBatch(batch, 10);
        batch.ApplyTo(sink);

        Assert.Equal(T0, parser.HeaderMs);
        Assert.Equal("proj", parser.Project);

        parser.Reset();
        batch.Clear();
        parser.PrepareBatch(batch, 10);
        batch.OnMalformed();
        batch.ApplyTo(sink);

        Assert.Equal(T0 + 50_000, parser.HeaderMs);
        Assert.Equal("other", parser.Project);
        Assert.Equal(1, parser.MalformedLines);
    }

    [Fact]
    public void AMissingFileHasNoHeaderToProbe()
    {
        using var temp = new TempDirectory();
        var parser = new OmpParser(temp.Combine("sessions", "bucket", FileName), new Deduper(), temp.Combine("sessions"));
        var sink = new RecordingSink();
        var batch = parser.NewBatch();

        parser.PrepareBatch(batch, 10);
        batch.OnLine(Encoding.UTF8.GetBytes(Reply("a1", T0 + 3000, N(T0 + 1000))));
        batch.ApplyTo(sink);

        Assert.False(parser.HeaderSeen);
        Assert.Empty(sink.Emissions);
    }

    [Fact]
    public void HugeNumbersSaturateInsteadOfOverflowing()
    {
        var (_, sink) = Run(Reply("a1", T0 + 2000, "1e19", duration: "1e300"));

        var response = Assert.Single(sink.Latest);
        Assert.Equal((Key("a1", long.MaxValue), (long?)long.MaxValue, long.MaxValue), (response.Key, response.StartMs, response.EndMs));
    }

    private static (OmpParser Parser, RecordingSink Sink) Run(params string[] lines)
    {
        var parser = Parser();
        var sink = new RecordingSink();
        Feed.Lines(parser, sink, [Header(T0), User("u0", T0 + 500), .. lines]);
        return (parser, sink);
    }

    private static OmpParser Parser(string? path = null, IDeduper? deduper = null) =>
        new(path ?? Main, deduper ?? new Deduper(), Root);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Q(string text) => "\"" + text + "\"";

    private static string? F(string name, string? raw) => raw is null ? null : Q(name) + ":" + raw;

    private static string Obj(params string?[] fields) => "{" + string.Join(",", fields.Where(field => field is not null)) + "}";

    private static string Key(string id, long stamp) => "omp:" + id + ":" + N(stamp);

    private static string Header(long ms, string cwd = "/home/me/proj") =>
        Obj(F("type", Q("session")), F("version", "3"), F("id", Q("0199f1a2")), F("timestamp", Q(Ts.Iso(ms))), F("cwd", Q(cwd)));

    private static string TitleSlot() => Obj(F("type", Q("title")), F("v", "1"), F("title", Q("Fix the build"))).PadRight(255);

    private static string Entry(string type, string id, long ms, params string?[] fields) =>
        Obj([F("type", Q(type)), F("id", Q(id)), F("parentId", "null"), F("timestamp", Q(Ts.Iso(ms))), .. fields]);

    private static string Message(string id, long ms, string body) => Entry("message", id, ms, F("message", body));

    private static string UserBody(long ms, string role = "user") =>
        Obj(F("role", Q(role)), F("content", "[{\"type\":\"text\",\"text\":\"Go\"}]"), F("timestamp", N(ms)));

    private static string User(string id, long ms, string role = "user") => Message(id, ms, UserBody(ms, role));

    private static string Usage(long input = 100, long output = 20, long cacheRead = 0, long cacheWrite = 0, long reasoning = 0) =>
        Obj(F("input", N(input)), F("output", N(output)), F("cacheRead", N(cacheRead)), F("cacheWrite", N(cacheWrite)),
            F("reasoningTokens", N(reasoning)), F("totalTokens", N(input + output + cacheRead + cacheWrite)),
            F("cost", "{\"input\":0.1,\"output\":0.2,\"total\":0.3}"));

    private static string ReplyBody(
        string? started, string? duration = "1500", string? stop = "\"stop\"", string? usage = null, string? content = null,
        string? extra = null, string guard = Guard) =>
        Obj(F("role", Q("assistant")), F("content", content ?? TextContent), guard, F("usage", usage ?? Usage()),
            F("stopReason", stop), F("timestamp", started), F("duration", duration), extra);

    private static string Reply(
        string id, long ms, string? started, string? duration = "1500", string? stop = "\"stop\"", string? usage = null,
        string? content = null, string? extra = null, string guard = Guard) =>
        Message(id, ms, ReplyBody(started, duration, stop, usage, content, extra, guard));

    private static string Assistant(string id, long ms, params string?[] fields) => Message(id, ms, Obj([F("role", Q("assistant")), .. fields]));

    private static string ToolCalls(params string[] ids) =>
        "[{\"type\":\"thinking\",\"thinking\":\"Plan\"},"
        + string.Join(",", ids.Select(id => Obj(F("type", Q("toolCall")), F("id", Q(id)), F("name", Q("read")), F("arguments", "{\"path\":\"a.txt\"}"))))
        + "]";

    private static string ToolResult(string id, long ms, string callId, string toolName = "read", string? isError = "false") =>
        Message(id, ms, Obj(F("role", Q("toolResult")), F("toolCallId", Q(callId)), F("toolName", Q(toolName)),
            F("content", "[{\"type\":\"text\",\"text\":\"ok\"}]"), F("isError", isError), F("timestamp", N(ms))));

    private static string SideCall(string id, long ms, string? usage = null) =>
        Entry("model_usage", id, ms, F("purpose", Q("memory")), SideGuard, F("usage", usage ?? Usage(40, 8)));

    private static string SessionExit(string id, long ms) => Entry("custom", id, ms, F("customType", Q("session_exit")), F("data", "{}"));
}
