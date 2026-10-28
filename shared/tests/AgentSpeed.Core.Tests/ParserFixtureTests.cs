using System.Text.Json;
using AgentSpeed.Core.Gemini;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Omp;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

public sealed class ParserFixtureTests
{
    private static readonly Lazy<JsonDocument> ExpectedDocument = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureLocator.FixturesDirectory, "parser-expected.json"))));

    private static readonly Lazy<Dictionary<string, FileResult>> Results = new(RunAll);

    internal static JsonElement Expected => ExpectedDocument.Value.RootElement;

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expected.GetProperty("order").EnumerateArray())
        {
            data.Add(name.GetString()!);
        }

        return data;
    }

    [Fact]
    public void EveryFixtureFileIsListedInOrder()
    {
        var listed = Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        var expectedFiles = Expected.GetProperty("files").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(expectedFiles, listed);
        foreach (var name in listed)
        {
            Assert.True(File.Exists(FixtureLocator.PathOf(name)), name);
        }
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void ParsedFileMatchesExpectation(string relative)
    {
        var expected = Expected.GetProperty("files").GetProperty(relative);
        var actual = Results.Value[relative];
        var parser = actual.Parser;

        Assert.Equal(expected.GetProperty("source").GetString(), SourceName(parser.Source));
        Assert.Equal(expected.GetProperty("project").GetString(), parser.Project);
        Assert.Equal(expected.GetProperty("model").GetString(), parser.Model);
        Assert.Equal(expected.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
        Assert.Equal(expected.GetProperty("phase").GetString(), PhaseName(parser.Phase));
        Assert.Equal(NullableLong(expected.GetProperty("inFlightStartMs")), parser.InFlightStartMs);
        Assert.Equal(NullableLong(expected.GetProperty("lastLineMs")), parser.LastLineMs);
        Assert.Equal(expected.GetProperty("malformedLines").GetInt64(), parser.MalformedLines);

        var expectedResponses = expected.GetProperty("responses").EnumerateArray().ToList();
        var responses = actual.Sink.Latest;
        Assert.Equal(
            expectedResponses.Select(r => r.GetProperty("key").GetString()!).ToList(),
            responses.Select(r => r.Key).ToList());

        for (var i = 0; i < expectedResponses.Count; i++)
        {
            var e = expectedResponses[i];
            var r = responses[i];
            var label = relative + " " + r.Key;
            Assert.True(NullableLong(e.GetProperty("startMs")) == r.StartMs, $"{label} startMs: expected {e.GetProperty("startMs")}, got {r.StartMs}");
            Assert.True(e.GetProperty("endMs").GetInt64() == r.EndMs, $"{label} endMs: expected {e.GetProperty("endMs")}, got {r.EndMs}");
            Assert.True(e.GetProperty("input").GetInt64() == r.Tokens.Input, $"{label} input: got {r.Tokens.Input}");
            Assert.True(e.GetProperty("cachedInput").GetInt64() == r.Tokens.CachedInput, $"{label} cachedInput: got {r.Tokens.CachedInput}");
            Assert.True(e.GetProperty("cacheWriteInput").GetInt64() == r.Tokens.CacheWriteInput, $"{label} cacheWriteInput: got {r.Tokens.CacheWriteInput}");
            Assert.True(e.GetProperty("output").GetInt64() == r.Tokens.Output, $"{label} output: got {r.Tokens.Output}");
            Assert.True(e.GetProperty("reasoning").GetInt64() == r.Tokens.Reasoning, $"{label} reasoning: got {r.Tokens.Reasoning}");
            Assert.True(e.GetProperty("model").GetString() == r.Model, $"{label} model: got {r.Model}");
            Assert.True(e.GetProperty("final").GetBoolean() == r.Final, $"{label} final: got {r.Final}");
            Assert.True(e.GetProperty("cacheWrite1hInput").GetInt64() == r.Tokens.CacheWrite1hInput,
                $"{label} cacheWrite1hInput: got {r.Tokens.CacheWrite1hInput}");
            Assert.True(e.GetProperty("provider").GetString() == r.Provider, $"{label} provider: got {r.Provider}");
            Assert.True(e.GetProperty("fast").GetBoolean() == r.Fast, $"{label} fast: got {r.Fast}");
            Assert.Equal(parser.Source, r.Source);
            Assert.Equal(parser.AgentId, r.AgentId);
        }
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void ParentFileMatchesExpectation(string relative)
    {
        var parents = ParentLinks.Resolve(
            Results.Value.Values.Select(r => AgentLink.Of(r.Parser)).ToList(), StringComparer.Ordinal);
        var expected = Expected.GetProperty("files").GetProperty(relative).GetProperty("parentFile").GetString();

        var parent = parents.GetValueOrDefault(Results.Value[relative].Parser.AgentId);
        var actual = parent is null
            ? null
            : Path.GetRelativePath(FixtureLocator.FixturesDirectory, parent).Replace(Path.DirectorySeparatorChar, '/');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FirstEmissionOfEachKeyIsTheOnlyCompletion()
    {
        foreach (var (_, result) in Results.Value)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (response, isCompletion) in result.Sink.Emissions)
            {
                Assert.Equal(seen.Add(response.Key), isCompletion);
            }
        }
    }

    internal static string SourceName(SourceKind source) => source switch
    {
        SourceKind.Codex => "codex",
        SourceKind.ClaudeCode => "claudeCode",
        SourceKind.ClaudeDesktop => "claudeDesktop",
        SourceKind.DeepSeek => "deepseek",
        SourceKind.OpenCode => "opencode",
        SourceKind.Gemini => "gemini",
        SourceKind.Omp => "omp",
        SourceKind.Antigravity => "antigravity",
        SourceKind.WorkBuddy => "workbuddy",
        SourceKind.WorkBuddyAi => "workbuddyAi",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    internal static string PhaseName(AgentPhase phase) => phase switch
    {
        AgentPhase.Generating => "generating",
        AgentPhase.Tools => "tools",
        AgentPhase.Idle => "idle",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    internal static long? NullableLong(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt64();

    /// <summary>Parses one fixture file completely, or its first <paramref name="maxLines"/> physical lines.</summary>
    internal static TranscriptParser ParseFixture(string relative, IDeduper deduper, IResponseSink sink, int? maxLines = null)
    {
        var path = FixtureLocator.PathOf(relative);
        var content = File.ReadAllBytes(path);
        if (relative.StartsWith("claude/", StringComparison.Ordinal))
        {
            return FeedLines(new ClaudeParser(path, deduper), content, sink, maxLines);
        }

        if (relative.StartsWith("codex/", StringComparison.Ordinal))
        {
            var codex = new CodexParser(path, deduper);
            if (!relative.EndsWith(".jsonl.zst", StringComparison.Ordinal))
            {
                return FeedLines(codex, content, sink, maxLines);
            }

            var batch = codex.NewBatch();
            new ZstStreamTailer(path).ReadAppended(batch);
            batch.ApplyTo(sink);
            return codex;
        }

        if (relative.StartsWith("dsh/", StringComparison.Ordinal))
        {
            var dsh = new DshParser(path, deduper);
            var batch = dsh.NewBatch();
            new ZstdFrameTailer(path).ReadAppended(batch);
            batch.ApplyTo(sink);
            return dsh;
        }

        if (relative.StartsWith("gemini/", StringComparison.Ordinal))
        {
            var gemini = new GeminiParser(path, deduper);
            if (!gemini.IsDocument)
            {
                return FeedLines(gemini, content, sink, maxLines);
            }

            gemini.Feed(content, sink);
            return gemini;
        }

        if (relative.StartsWith("omp/", StringComparison.Ordinal))
        {
            return FeedLines(new OmpParser(path, deduper, OmpFixture.RootOf(path)), content, sink, maxLines);
        }

        if (relative.StartsWith("workbuddy/", StringComparison.Ordinal))
        {
            var source = WorkBuddyFixture.SourceOf(relative);
            var workBuddy = new WorkBuddyParser(path, deduper, source, WorkBuddyFixture.RootOf(source));
            if (maxLines is not null)
            {
                return FeedLines(workBuddy, content, sink, maxLines);
            }

            // Through the tailer, like the app: a line still being written (no newline yet) is not read.
            var batch = workBuddy.NewBatch();
            new FileTailer(path).ReadAppended(batch);
            batch.ApplyTo(sink);
            return workBuddy;
        }

        throw new InvalidOperationException("Unknown fixture source: " + relative);
    }

    private static TranscriptParser FeedLines<TLine>(TranscriptParser<TLine> parser, byte[] content, IResponseSink sink, int? maxLines)
        where TLine : struct
    {
        var lines = maxLines is { } count ? PhysicalLines(content).Take(count) : Feed.SplitJsonl(content);
        foreach (var line in lines)
        {
            parser.Feed(line, sink);
        }

        return parser;
    }

    /// <summary>Newline-terminated lines, blank ones included, as the reference's <c>read_lines</c> counts them.</summary>
    private static IEnumerable<byte[]> PhysicalLines(byte[] content)
    {
        var start = 0;
        int newline;
        while ((newline = Array.IndexOf(content, (byte)'\n', start)) >= 0)
        {
            yield return content.AsSpan(start, newline - start).ToArray();
            start = newline + 1;
        }
    }

    private static Dictionary<string, FileResult> RunAll()
    {
        var deduper = new Deduper();
        var results = new Dictionary<string, FileResult>(StringComparer.Ordinal);
        foreach (var element in Expected.GetProperty("order").EnumerateArray())
        {
            var relative = element.GetString()!;
            var sink = new RecordingSink();
            results[relative] = new FileResult(ParseFixture(relative, deduper, sink), sink);
        }

        return results;
    }

    private sealed record FileResult(TranscriptParser Parser, RecordingSink Sink);
}
