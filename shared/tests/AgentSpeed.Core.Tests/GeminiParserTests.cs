using System.Text;
using AgentSpeed.Core.Gemini;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.GeminiRecords;

namespace AgentSpeed.Core.Tests;

public sealed class GeminiParserTests
{
    private const string Chats = "/home/me/.gemini/tmp/proj/chats/";
    private const string Main = Chats + "session-2026-10-20T10-00-abcdef12.jsonl";
    private const string Other = Chats + "session-2026-10-20T11-00-12345678.jsonl";
    private const string Nested = Chats + "abcdef12-0000/sub-1.jsonl";
    private const string Legacy = Chats + "session-2026-10-20T09-00-abcdef12.json";

    [Fact]
    public void UserToGeminiIsOneMeasuredResponse()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header(), User("u1", T0 + 1000));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 1000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, Reply("g1", T0 + 5000, Tokens(100, 20, cached: 50, thoughts: 10, tool: 5), "\"Answer\""));

        Assert.Equal(
            new ParsedResponse("g1", SourceKind.Gemini, Main, "gemini-2.5-pro", T0 + 1000, T0 + 5000,
                new TokenCounts(Input: 105, CachedInput: 50, CacheWriteInput: 0, Output: 30, Reasoning: 10)),
            Assert.Single(sink.Latest));
        Assert.True(Assert.Single(sink.Emissions).IsCompletion);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
        Assert.Equal("gemini-2.5-pro", parser.Model);
        Assert.Equal(T0 + 5000, parser.LastLineMs);
        Assert.Equal(SourceKind.Gemini, parser.Source);
        Assert.True(parser.SourceDecided);
    }

    [Theory]
    [InlineData("\"\"", AgentPhase.Tools)]
    [InlineData("\" \\n\\t\\r\\f\"", AgentPhase.Tools)]
    [InlineData("\"\\u001c\\u001f\\u00a0\\u3000\"", AgentPhase.Tools)]
    [InlineData("\"\\u0008\"", AgentPhase.Idle)]
    [InlineData("\"Answer\"", AgentPhase.Idle)]
    [InlineData("[{\"text\":\"Answer\"}]", AgentPhase.Idle)]
    [InlineData("{\"text\":\"Answer\"}", AgentPhase.Idle)]
    [InlineData("[{\"text\":\"Plan\",\"thought\":true}]", AgentPhase.Tools)]
    [InlineData("[{\"text\":\"Plan\",\"thought\":\"yes\"}]", AgentPhase.Idle)]
    [InlineData("[{\"text\":\"Answer\"},{\"functionCall\":{\"name\":\"read_file\"}}]", AgentPhase.Tools)]
    [InlineData("[{\"functionCall\":\"read_file\"},{\"text\":\"Answer\"}]", AgentPhase.Idle)]
    [InlineData("[\"  \",\"Answer\"]", AgentPhase.Idle)]
    [InlineData("[\"  \",{\"text\":\" \"}]", AgentPhase.Tools)]
    [InlineData("42", AgentPhase.Tools)]
    [InlineData("null", AgentPhase.Tools)]
    public void VisibleTextAndFunctionCallsDecideTheNewResponsePhase(string content, AgentPhase expected)
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), User("u1", T0), Reply("g1", T0 + 1000, Tokens(1, 1), content));

        Assert.Equal(expected, parser.Phase);
    }

    [Fact]
    public void NewToolCallsStartTheNextRequestAtTheLatestCallTimestamp()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(100, 20)));
        Assert.Equal(AgentPhase.Tools, parser.Phase);

        Feed.Lines(parser, sink, Reply("g1", T0 + 5000, Tokens(100, 20), calls: Calls(Call("c1", T0 + 7200), Call("c2", T0 + 7000))));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 7200, parser.InFlightStartMs);
        Assert.Equal(T0 + 7200, parser.LastLineMs);
        Assert.Single(sink.Emissions);

        Feed.Lines(parser, sink, Reply("g2", T0 + 9000, Tokens(200, 30), "\"Done\""));
        Assert.Equal(T0 + 7200, sink.Latest[1].StartMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        // Re-appended with the same calls (tool results masked): nothing is new.
        Feed.Lines(parser, sink, Reply("g1", T0 + 5000, Tokens(100, 20), calls: Calls(Call("c1", T0 + 7200), Call("c2", T0 + 7000))));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(2, sink.Emissions.Count);
    }

    [Fact]
    public void NewToolCallsWithoutTimestampsStartAtTheRecord()
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Reply("g1", T0 + 5000, null, calls: Calls(Call("c1", null))));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 5000, parser.InFlightStartMs);
    }

    [Theory]
    [InlineData("complete_task", "success", "read_file", "success", AgentPhase.Idle)]
    [InlineData("complete_task", "error", "read_file", "success", AgentPhase.Generating)]
    [InlineData("read_file", "cancelled", "write_file", "cancelled", AgentPhase.Idle)]
    [InlineData("read_file", "cancelled", "write_file", "success", AgentPhase.Generating)]
    [InlineData("read_file", "error", "write_file", "error", AgentPhase.Generating)]
    public void NewCallStatusesDecideWhetherAnotherRequestFollows(
        string firstName, string firstStatus, string secondName, string secondStatus, AgentPhase expected)
    {
        var parser = new GeminiParser(Nested, new Deduper());

        Feed.Lines(parser, new RecordingSink(), User("u1", T0), Reply("g1", T0 + 1000, Tokens(1, 1)),
            Reply("g1", T0 + 1000, Tokens(1, 1), calls: Calls(
                Call("c1", T0 + 2000, firstStatus, firstName), Call("c2", T0 + 2100, secondStatus, secondName))));

        Assert.Equal(expected, parser.Phase);
        Assert.Equal(expected == AgentPhase.Generating ? T0 + 2100 : (long?)null, parser.InFlightStartMs);
    }

    [Fact]
    public void OnlyNewCallsCountTowardAllCancelled()
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Reply("g1", T0, Tokens(1, 1), calls: Calls(Call("c1", T0 + 100))));
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, new RecordingSink(), Reply("g1", T0, Tokens(1, 1),
            calls: Calls(Call("c1", T0 + 100), Call("c2", T0 + 200, "cancelled"))));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Theory]
    [InlineData("""{"id":"e1","timestamp":"2026-10-20T10:00:05Z","type":"error","content":"[API Error: 500]"}""", AgentPhase.Idle)]
    [InlineData("""{"id":"i1","timestamp":"2026-10-20T10:00:05Z","type":"info","content":"Request cancelled."}""", AgentPhase.Idle)]
    [InlineData("""{"id":"i1","timestamp":"2026-10-20T10:00:05Z","type":"info","content":"Request cancelled"}""", AgentPhase.Generating)]
    [InlineData("""{"id":"i1","timestamp":"2026-10-20T10:00:05Z","type":"info","content":[{"text":"Request cancelled."}]}""", AgentPhase.Generating)]
    [InlineData("""{"id":"w1","timestamp":"2026-10-20T10:00:05Z","type":"warning","content":"Request cancelled."}""", AgentPhase.Generating)]
    [InlineData("""{"id":"e1","type":"error","content":"no timestamp"}""", AgentPhase.Generating)]
    public void ErrorsAndCancelledInfoEndTheRequest(string record, AgentPhase expected)
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), User("u1", T0), record);

        Assert.Equal(expected, parser.Phase);
    }

    [Fact]
    public void RewindEndsTheRequestButKeepsResponses()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0), Reply("g1", T0 + 1000, Tokens(1, 1), "\"Answer\""), User("u2", T0 + 2000));
        Feed.Lines(parser, sink, """{"$rewindTo":5}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, sink, """{"$rewindTo":"u1"}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
        Assert.Single(sink.Latest);

        Feed.Lines(parser, sink, Reply("g2", T0 + 3000, Tokens(1, 1)));
        Assert.Null(sink.Latest[1].StartMs);
    }

    [Fact]
    public void PatchEndsTheRequestOnlyWhenItRemovesThePendingRecord()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0),
            """{"$patch":{"updates":[{"id":"u0","content":[]}]}}""",
            """{"$patch":{"removeIds":["u0"]}}""",
            """{"$patch":{"removeIds":"u1"}}""",
            """{"$patch":5,"id":"x"}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);

        Feed.Lines(parser, sink, """{"$patch":{"removeIds":[1,"u1"]}}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        // Tool calls make their message the pending record.
        Feed.Lines(parser, sink, Reply("g1", T0 + 1000, Tokens(1, 1), calls: Calls(Call("c1", T0 + 2000))));
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Feed.Lines(parser, sink, """{"$patch":{"removeIds":["g1"]}}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void CheckpointUpdatesKnownResponsesAndEmitsUnknownOnesWithoutAStart()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(100, 20), "\"Answer\"", model: "m1"),
            User("u2", T0 + 6000));
        Feed.Lines(parser, sink, Checkpoint(
            User("u1", T0 + 1000),
            Reply("g1", T0 + 5500, Tokens(120, 25), model: null),
            Reply("g0", T0 + 9000, Tokens(10, 2), model: "m0"),
            Reply("g-none", T0 + 9000, null),
            """{"id":"g-bad","type":"gemini","timestamp":"bad","tokens":{"input":1}}""",
            User("u2", T0 + 6000)));

        Assert.Equal(3, sink.Emissions.Count);
        var (updated, updateIsCompletion) = sink.Emissions[1];
        Assert.False(updateIsCompletion);
        Assert.Equal(new ParsedResponse("g1", SourceKind.Gemini, Main, "m1", T0 + 1000, T0 + 5500,
            new TokenCounts(Input: 120, Output: 25)), updated);
        var (unknown, unknownIsCompletion) = sink.Emissions[2];
        Assert.True(unknownIsCompletion);
        Assert.Equal(new ParsedResponse("g0", SourceKind.Gemini, Main, "m0", null, T0 + 9000,
            new TokenCounts(Input: 10, Output: 2)), unknown);
        Assert.Equal("m0", parser.Model);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);
        Assert.Equal(T0 + 6000, parser.LastLineMs);

        // The in-flight request is missing from the next checkpoint: it was rolled back.
        Feed.Lines(parser, sink, Checkpoint(User("u1", T0 + 1000), Reply("g1", T0 + 5500, Tokens(120, 25), model: null)));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(3, sink.Emissions.Count);
    }

    [Fact]
    public void CheckpointSkipsResponsesOwnedByAnotherFile()
    {
        var deduper = new Deduper();
        var first = new GeminiParser(Main, deduper);
        var second = new GeminiParser(Other, deduper);
        var sink = new RecordingSink();

        Feed.Lines(first, new RecordingSink(), User("u1", T0), Reply("g1", T0 + 1000, Tokens(1, 1)));
        Feed.Lines(second, sink, User("u1", T0), Checkpoint(User("u1", T0), Reply("g1", T0 + 1000, Tokens(5, 5))));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, second.Phase);
    }

    [Fact]
    public void NonEmptySummaryEndsTheSession()
    {
        var parser = new GeminiParser(Nested, new Deduper());

        Feed.Lines(parser, new RecordingSink(), User("u1", T0), """{"$set":{"summary":""}}""", """{"$set":{"summary":5}}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, new RecordingSink(), """{"$set":{"summary":" "}}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void KnownKeyUpdateReplacesTokensAndKeepsThePhase()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(100, 20), model: "m1"), User("u2", T0 + 6000));
        Feed.Lines(parser, sink, Reply("g1", T0 + 4000, Tokens(90, 10), "\"Answer\"", model: null));

        var (response, isCompletion) = sink.Emissions[^1];
        Assert.False(isCompletion);
        Assert.Equal(new ParsedResponse("g1", SourceKind.Gemini, Main, "m1", T0 + 1000, T0 + 5000,
            new TokenCounts(Input: 90, Output: 10)), response);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);
    }

    [Fact]
    public void ModelFollowsTheLatestCreatedOrUpdatedResponse()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Reply("g1", T0, Tokens(1, 1), model: "m1"), Reply("g2", T0 + 1000, Tokens(1, 1), model: "m2"));
        Assert.Equal("m2", parser.Model);

        Feed.Lines(parser, sink, Reply("g1", T0, Tokens(1, 1), model: null));
        Assert.Equal("m1", parser.Model);
        Assert.Equal(2, sink.Emissions.Count);

        Feed.Lines(parser, sink, Reply("g3", T0 + 2000, Tokens(1, 1), model: ""));
        Assert.Null(sink.Latest[^1].Model);
        Assert.Equal("m1", parser.Model);
    }

    [Fact]
    public void MessageCreatedWhenItsToolsFinishedHasNoStart()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        // <= v0.31: the first record of a response already carries its finished tool calls.
        Feed.Lines(parser, sink, User("u1", T0 + 1000),
            Reply("g1", T0 + 8000, Tokens(1, 1), calls: Calls(Call("c1", T0 + 7995), Call("c2", T0 + 8000))));

        Assert.Null(Assert.Single(sink.Latest).StartMs);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 8000, parser.InFlightStartMs);

        Feed.Lines(parser, sink, User("u2", T0 + 9000),
            Reply("g2", T0 + 10_000, Tokens(1, 1), calls: Calls(Call("c3", T0 + 10_001), Call("c4", null))));
        Assert.Equal(T0 + 9000, sink.Latest[1].StartMs);

        Feed.Lines(parser, sink, User("u3", T0 + 11_000),
            Reply("g3", T0 + 12_000, Tokens(1, 1), calls: Calls(Call("c5", null))));
        Assert.Equal(T0 + 11_000, sink.Latest[2].StartMs);
    }

    [Fact]
    public void RecordOwnedByAnotherFileIsIgnoredButStillTouchesLastLine()
    {
        var deduper = new Deduper();
        var owner = new GeminiParser(Main, deduper);
        var copy = new GeminiParser(Other, deduper);
        var sink = new RecordingSink();

        Feed.Lines(owner, new RecordingSink(), User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(1, 1)));
        Feed.Lines(copy, sink, User("u9", T0 + 100),
            Reply("g1", T0 + 5000, Tokens(1, 1), calls: Calls(Call("c1", T0 + 7000)), thoughts: """[{"timestamp":"2026-10-20T10:00:07.100Z"}]"""));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, copy.Phase);
        Assert.Equal(T0 + 100, copy.InFlightStartMs);
        Assert.Equal(T0 + 7100, copy.LastLineMs);

        // The ignored call was never seen by this file.
        Feed.Lines(copy, sink, Reply("g2", T0 + 8000, Tokens(1, 1)));
        Assert.Equal(T0 + 100, Assert.Single(sink.Latest).StartMs);
        Feed.Lines(copy, sink, Reply("g2", T0 + 8000, Tokens(1, 1), calls: Calls(Call("c1", T0 + 9000))));
        Assert.Equal(AgentPhase.Generating, copy.Phase);
        Assert.Equal(T0 + 9000, copy.InFlightStartMs);
    }

    [Fact]
    public void RecordsWithoutTokensAreNeverClaimed()
    {
        var deduper = new Deduper();
        var first = new GeminiParser(Main, deduper);
        var second = new GeminiParser(Other, deduper);
        var sink = new RecordingSink();

        Feed.Lines(first, new RecordingSink(), Reply("g1", T0, null, calls: Calls(Call("c1", T0 + 100))));
        Feed.Lines(second, sink, Reply("g1", T0, Tokens(1, 1)));

        Assert.True(Assert.Single(sink.Emissions).IsCompletion);
    }

    [Fact]
    public void GeminiRecordWithoutTokensOrNewCallsChangesNothing()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, User("u1", T0), Reply("g1", T0 + 1000, null, "\"Compression acknowledged\""));

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);
        Assert.Equal(T0 + 1000, parser.LastLineMs);
    }

    [Fact]
    public void MessagesWithoutAParseableTimestampAreIgnored()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink,
            """{"id":"u1","type":"user"}""",
            """{"id":"u1","type":"user","timestamp":"2026-10-20 10:00"}""",
            """{"id":"g1","type":"gemini","timestamp":5,"tokens":{"input":1,"output":1},"toolCalls":[{"id":"c1","timestamp":"2026-10-20T10:00:00Z"}]}""");

        Assert.Empty(sink.Emissions);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.LastLineMs);
    }

    [Fact]
    public void EmptyStringIdsAreIds()
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), """{"id":5,"timestamp":"2026-10-20T10:00:00Z","type":"user"}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);

        Feed.Lines(parser, new RecordingSink(), """{"id":"","timestamp":"2026-10-20T10:00:00Z","type":"user"}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, new RecordingSink(), """{"$patch":{"removeIds":[""]}}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void ToolCallsCountOnlyAsObjectsWithStringIds()
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Reply("g1", T0 + 5000, Tokens(1, 1)),
            Reply("g1", T0 + 5000, Tokens(1, 1), calls: """
                [{"name":"read_file","timestamp":"2026-10-20T10:00:09Z","status":"success"},"c1",
                 {"id":5,"timestamp":"2026-10-20T10:00:09Z"},{"id":"","timestamp":"2026-10-20T10:00:07Z"}]
                """.ReplaceLineEndings(string.Empty)));

        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 7000, parser.InFlightStartMs);
        Assert.Equal(T0 + 7000, parser.LastLineMs);
    }

    [Fact]
    public void ThoughtTimestampsCountForLastLine()
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), Reply("g1", T0 + 5000, null,
            thoughts: """[{"timestamp":"2026-10-20T10:00:06Z"},"x",{"timestamp":"bad"},{"subject":"s"}]"""));

        Assert.Equal(T0 + 6000, parser.LastLineMs);
    }

    [Fact]
    public void HeadersAndSetRecordsCarrySessionMetadata()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, """{"sessionId":"s-0","projectHash":"h","startTime":"bad"}""");
        Assert.Equal("s-0", parser.SessionId);
        Assert.Null(parser.StartTimeMs);
        Assert.Null(parser.LastLineMs);

        Feed.Lines(parser, sink, Header("s-1", T0 + 10, lastUpdated: T0 + 20));
        Assert.Equal("s-1", parser.SessionId);
        Assert.Equal(T0 + 10, parser.StartTimeMs);
        Assert.Equal(T0 + 20, parser.LastLineMs);
        Assert.False(parser.IsSubagent);

        Feed.Lines(parser, sink, Header("s-2", T0 + 500));
        Assert.Equal("s-2", parser.SessionId);
        Assert.Equal(T0 + 10, parser.StartTimeMs);
        Assert.Equal(T0 + 500, parser.LastLineMs);

        Feed.Lines(parser, sink, """{"$set":{"sessionId":"s-3","kind":"subagent","lastUpdated":"2026-10-20T10:00:00.900Z"}}""");
        Assert.Equal("s-3", parser.SessionId);
        Assert.True(parser.IsSubagent);
        Assert.Equal(T0 + 900, parser.LastLineMs);

        Feed.Lines(parser, sink, """{"$set":{"startTime":"2026-10-20T10:00:02Z","sessionId":7}}""");
        Assert.Equal(T0 + 900, parser.LastLineMs);
        Assert.Equal("s-3", parser.SessionId);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Empty(sink.Emissions);
    }

    [Fact]
    public void HeaderOnOneLineWithMessagesIsReplayed()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, Header("s-1", T0, messages:
            $"[{User("u1", T0 + 1000)},{Reply("g1", T0 + 5000, Tokens(1, 1), "\"Answer\"")},5,{{\"type\":\"user\"}}]"));

        Assert.Equal(T0 + 1000, Assert.Single(sink.Latest).StartMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 5000, parser.LastLineMs);
    }

    [Fact]
    public void RecordsAreClassifiedInGeminiLoaderOrder()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();
        Feed.Lines(parser, sink, User("u1", T0));

        Feed.Lines(parser, sink, """{"$patch":{"removeIds":[]},"id":"e1","timestamp":"2026-10-20T10:00:01Z","type":"error"}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, sink, """{"id":"w1","timestamp":"2026-10-20T10:00:01Z","type":"warning","$set":{"summary":"Done"}}""");
        Assert.Equal(AgentPhase.Generating, parser.Phase);

        Feed.Lines(parser, sink, """{"sessionId":"s-1"}""", """{"sessionId":"s-1","projectHash":5}""");
        Assert.Null(parser.SessionId);

        Feed.Lines(parser, sink, """{"$set":{"lastUpdated":"2026-10-20T10:00:03Z"},"sessionId":"s-1","projectHash":"h"}""");
        Assert.Null(parser.SessionId);
        Assert.Equal(T0 + 3000, parser.LastLineMs);

        Feed.Lines(parser, sink, """{"$rewindTo":"u0","id":"u2","timestamp":"2026-10-20T10:00:04Z","type":"user"}""");
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 3000, parser.LastLineMs);
    }

    [Fact]
    public void DuplicateKeysKeepTheirLastValue()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink,
            """{"id":"u1","id":5,"timestamp":"2026-10-20T10:00:00Z","type":"user"}""",
            """{"id":"g1","type":"user","type":"gemini","timestamp":"2026-10-20T10:00:01Z","tokens":{"input":1,"input":2},"content":"Answer","content":""}""",
            """{"id":"g2","type":"gemini","timestamp":"2026-10-20T10:00:02Z","tokens":{"input":1},"tokens":null}""");

        var response = Assert.Single(sink.Latest);
        Assert.Equal("g1", response.Key);
        Assert.Null(response.StartMs);
        Assert.Equal(2, response.Tokens.Input);
        Assert.Equal(AgentPhase.Tools, parser.Phase);
    }

    [Fact]
    public void TokenCountsFollowTheReferenceNumberRules()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink,
            """{"id":"g1","type":"gemini","timestamp":"2026-10-20T10:00:00Z","tokens":{"input":true,"output":12.9,"cached":-5,"thoughts":"7","tool":3}}""",
            """{"id":"g2","type":"gemini","timestamp":"2026-10-20T10:00:01Z","tokens":{}}""",
            """{"id":"g3","type":"gemini","timestamp":"2026-10-20T10:00:02Z","tokens":[1,2]}""");

        Assert.Equal(2, sink.Latest.Count);
        Assert.Equal(new TokenCounts(Input: 3, CachedInput: 0, CacheWriteInput: 0, Output: 12, Reasoning: 0), sink.Latest[0].Tokens);
        Assert.Equal(default, sink.Latest[1].Tokens);
    }

    [Fact]
    public void PathLayoutDecidesSubagentAndParentFolderAndSurvivesReset()
    {
        var nested = new GeminiParser(Nested, new Deduper());
        Assert.True(nested.IsSubagent);
        Assert.Equal("abcdef12-0000", nested.ParentDirectory);
        Assert.False(nested.IsMainSession);
        Assert.False(nested.IsDocument);

        var windows = new GeminiParser(@"C:\Users\me\.gemini\tmp\proj\chats\abcdef12\sub.jsonl", new Deduper());
        Assert.True(windows.IsSubagent);
        Assert.Equal("abcdef12", windows.ParentDirectory);

        var main = new GeminiParser(Main, new Deduper());
        Assert.False(main.IsSubagent);
        Assert.Null(main.ParentDirectory);
        Assert.True(main.IsMainSession);

        Feed.Lines(main, new RecordingSink(), Header("s-1", T0, kind: "subagent"), User("u1", T0 + 1000));
        Assert.True(main.IsSubagent);
        var version = main.Version;

        main.Reset();

        Assert.NotEqual(version, main.Version);
        Assert.False(main.IsSubagent);
        Assert.Null(main.SessionId);
        Assert.Null(main.StartTimeMs);
        Assert.Null(main.LastLineMs);
        Assert.Equal(AgentPhase.Idle, main.Phase);
        Assert.Equal(SourceKind.Gemini, main.Source);
    }

    [Fact]
    public void ReplayAfterResetEmitsUpdatesWithTheSameValues()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();
        string[] lines = [User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(100, 20), "\"Answer\"")];

        Feed.Lines(parser, sink, lines);
        parser.Reset();
        Feed.Lines(parser, sink, lines);

        Assert.Equal(new[] { true, false }, sink.Emissions.Select(e => e.IsCompletion));
        Assert.Equal(sink.Emissions[0].Response, sink.Emissions[1].Response);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    [Fact]
    public void LegacyDocumentReplaysFromAResetAndKeepsStateWhenMalformed()
    {
        var parser = new GeminiParser(Legacy, new Deduper());
        var sink = new RecordingSink();
        Assert.True(parser.IsDocument);
        string[] first = [User("u1", T0 + 1000), Reply("g1", T0 + 5000, Tokens(100, 20), "\"Answer\""), User("u2", T0 + 6000)];
        string[] second = [.. first, Reply("g2", T0 + 8000, Tokens(200, 30), "\"Done\"")];

        parser.Feed(Document(first), sink);
        Assert.Equal("s-legacy", parser.SessionId);
        Assert.Equal(T0, parser.StartTimeMs);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0 + 6000, parser.InFlightStartMs);

        parser.Feed(Document(second), sink);
        Assert.Equal(new[] { ("g1", true), ("g1", false), ("g2", true) }, sink.Emissions.Select(e => (e.Response.Key, e.IsCompletion)));
        Assert.Equal(sink.Emissions[0].Response, sink.Emissions[1].Response);
        Assert.Equal(T0 + 6000, sink.Emissions[2].Response.StartMs);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 8000, parser.LastLineMs);

        var whole = Document(second);
        parser.Feed(whole.AsSpan(0, whole.Length / 2), sink);
        parser.Feed("[1,2]"u8, sink);
        parser.Feed("""{"projectHash":"h","messages":[]}"""u8, sink);
        parser.Feed(""u8, sink);
        parser.Feed(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(whole) + " {}"), sink);
        Assert.Equal(5, parser.MalformedLines);
        Assert.Equal(3, sink.Emissions.Count);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(T0 + 8000, parser.LastLineMs);
        Assert.Equal("s-legacy", parser.SessionId);

        // Rewound: g2 is missing from the new snapshot but stays recorded downstream.
        parser.Feed(Document(first), sink);
        Assert.Equal(("g1", false), (sink.Emissions[^1].Response.Key, sink.Emissions[^1].IsCompletion));
        Assert.Equal(2, sink.Latest.Count);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(5, parser.MalformedLines);
    }

    [Fact]
    public void LegacyDocumentNeedsOnlyAStringSessionId()
    {
        var parser = new GeminiParser(Legacy, new Deduper());
        var sink = new RecordingSink();

        parser.Feed("""{"sessionId":"","kind":"subagent","messages":[{"id":"u1","timestamp":"2026-10-20T10:00:00Z","type":"user"}]}"""u8, sink);

        Assert.Equal(string.Empty, parser.SessionId);
        Assert.True(parser.IsSubagent);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(0, parser.MalformedLines);
    }

    [Theory]
    [MemberData(nameof(ParserCommonTests.MalformedLines), MemberType = typeof(ParserCommonTests))]
    public void MalformedLinesAreCounted(string line)
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, line, line);

        Assert.Equal(2, parser.MalformedLines);
        Assert.Null(parser.LastLineMs);
        Assert.Empty(sink.Emissions);
    }

    [Theory]
    [MemberData(nameof(ParserCommonTests.BlankLines), MemberType = typeof(ParserCommonTests))]
    public void BlankLinesAreNotMalformed(string line)
    {
        var parser = new GeminiParser(Main, new Deduper());

        Feed.Lines(parser, new RecordingSink(), line);

        Assert.Equal(0, parser.MalformedLines);
    }

    [Fact]
    public void BatchesExtractOffThreadAndApplyInOrder()
    {
        var parser = new GeminiParser(Main, new Deduper());
        var sink = new RecordingSink();
        var batch = parser.NewBatch();

        foreach (var line in new[] { User("u1", T0), "not json", Reply("g1", T0 + 1000, Tokens(1, 1), "\"Answer\"") })
        {
            batch.OnLine(Encoding.UTF8.GetBytes(line));
        }

        Assert.Null(parser.LastLineMs);
        batch.ApplyTo(sink);

        Assert.Equal(T0, Assert.Single(sink.Latest).StartMs);
        Assert.Equal(1, parser.MalformedLines);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    private static byte[] Document(string[] messages) => Encoding.UTF8.GetBytes(
        "{\n  \"sessionId\": \"s-legacy\",\n  \"projectHash\": \"h\",\n  \"startTime\": \"" + Ts.Iso(T0) + "\",\n  \"messages\": [\n    "
        + string.Join(",\n    ", messages) + "\n  ]\n}\n");
}

/// <summary>Gemini CLI records as written by the CLI (METRICS 4.6).</summary>
internal static class GeminiRecords
{
    public static readonly long T0 = Ts.Ms("2026-10-20T10:00:00Z");

    public static string Header(
        string sessionId = "s-1", long? start = null, long? lastUpdated = null, string? kind = null, string? messages = null)
    {
        var parts = new List<string>
        {
            $"\"sessionId\":\"{sessionId}\"",
            "\"projectHash\":\"hash\"",
            $"\"startTime\":\"{Ts.Iso(start ?? T0)}\"",
            $"\"lastUpdated\":\"{Ts.Iso(lastUpdated ?? start ?? T0)}\"",
        };
        if (kind is not null)
        {
            parts.Add($"\"kind\":\"{kind}\"");
        }

        if (messages is not null)
        {
            parts.Add($"\"messages\":{messages}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    public static string User(string id, long ts) =>
        $$"""{"id":"{{id}}","timestamp":"{{Ts.Iso(ts)}}","type":"user","content":[{"text":"Go ahead"}]}""";

    public static string Reply(
        string id, long ts, string? tokens, string content = "\"\"", string? model = "gemini-2.5-pro", string? calls = null,
        string? thoughts = null)
    {
        var parts = new List<string>
        {
            $"\"id\":\"{id}\"",
            $"\"timestamp\":\"{Ts.Iso(ts)}\"",
            "\"type\":\"gemini\"",
            $"\"content\":{content}",
        };
        if (thoughts is not null)
        {
            parts.Add($"\"thoughts\":{thoughts}");
        }

        if (tokens is not null)
        {
            parts.Add($"\"tokens\":{tokens}");
        }

        if (model is not null)
        {
            parts.Add($"\"model\":\"{model}\"");
        }

        if (calls is not null)
        {
            parts.Add($"\"toolCalls\":{calls}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    public static string Tokens(long input, long output, long cached = 0, long thoughts = 0, long tool = 0) =>
        $$"""{"input":{{input}},"output":{{output}},"cached":{{cached}},"thoughts":{{thoughts}},"tool":{{tool}},"total":{{input + output + thoughts + tool}}}""";

    public static string Calls(params string[] calls) => "[" + string.Join(",", calls) + "]";

    public static string Call(string id, long? ts, string status = "success", string name = "read_file")
    {
        var timestamp = ts is { } ms ? $",\"timestamp\":\"{Ts.Iso(ms)}\"" : string.Empty;
        var result = "[{\"functionResponse\":{\"id\":\"" + id + "\",\"response\":{\"output\":\"ok\"}}}]";
        return $$"""{"id":"{{id}}","name":"{{name}}","args":{},"result":{{result}},"status":"{{status}}"{{timestamp}}}""";
    }

    public static string Checkpoint(params string[] messages) =>
        "{\"$set\":{\"messages\":[" + string.Join(",", messages) + "]}}";
}
