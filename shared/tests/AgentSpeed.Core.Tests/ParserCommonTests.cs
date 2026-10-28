using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class ParserCommonTests
{
    private const string ClaudeAgent = "/home/me/.claude/projects/-home-me-proj/s.jsonl";
    private const string CodexAgent = "/home/me/.codex/sessions/2026/10/20/rollout.jsonl";

    public static TheoryData<string> MalformedLines() =>
    [
        "this line is not json",
        "[1,2,3]",
        "[{\"type\":\"user\",\"timestamp\":\"2026-10-20T10:00:00Z\"}]",
        "{\"type\":\"user\",\"timestamp\":\"2026-10-20T10:00:00Z\"",
        "{\"type\":\"user\",\"timestamp\":\"2026-10-20T10:00:00Z\"} trailing",
        "{\"type\":\"user\"}{\"type\":\"user\"}",
        "{\"type\":\"user\",}",
        "42",
        "\"text\"",
        "null",
        "{\"message\":{\"content\":[1,2}}",
    ];

    public static TheoryData<string> BlankLines() => ["", " ", "   \t ", "\r"];

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void ClaudeCountsMalformedLines(string line)
    {
        var parser = new ClaudeParser(ClaudeAgent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, line, line);

        Assert.Equal(2, parser.MalformedLines);
        Assert.Null(parser.LastLineMs);
        Assert.Empty(sink.Emissions);
    }

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void CodexCountsMalformedLines(string line)
    {
        var parser = new CodexParser(CodexAgent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, line, line);

        Assert.Equal(2, parser.MalformedLines);
        Assert.Null(parser.LastLineMs);
        Assert.Empty(sink.Emissions);
    }

    [Theory]
    [MemberData(nameof(BlankLines))]
    public void BlankLinesAreNotMalformed(string line)
    {
        var claude = new ClaudeParser(ClaudeAgent, new Deduper());
        var codex = new CodexParser(CodexAgent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(claude, sink, line);
        Feed.Lines(codex, sink, line);

        Assert.Equal(0, claude.MalformedLines);
        Assert.Equal(0, codex.MalformedLines);
    }

    [Fact]
    public void ValidObjectsWithTrailingWhitespaceAreNotMalformed()
    {
        var claude = new ClaudeParser(ClaudeAgent, new Deduper());
        var codex = new CodexParser(CodexAgent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(claude, sink, "{\"type\":\"system\",\"timestamp\":\"2026-10-20T10:00:00Z\"}  \t", "{}");
        Feed.Lines(codex, sink, "  {\"type\":\"other\",\"timestamp\":\"2026-10-20T10:00:00Z\",\"payload\":{}} ", "{}");

        Assert.Equal(0, claude.MalformedLines);
        Assert.Equal(0, codex.MalformedLines);
        Assert.Equal(ClaudeLines.T0, claude.LastLineMs);
        Assert.Equal(CodexLines.T0, codex.LastLineMs);
    }

    [Fact]
    public void LinesWithoutParseableTimestampAreIgnored()
    {
        var claude = new ClaudeParser(ClaudeAgent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(
            claude,
            sink,
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
            "{\"type\":\"user\",\"timestamp\":\"yesterday\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
            "{\"type\":\"user\",\"timestamp\":12345,\"message\":{\"role\":\"user\",\"content\":\"hi\"}}");

        Assert.Equal(0, claude.MalformedLines);
        Assert.Null(claude.LastLineMs);
        Assert.Equal(AgentPhase.Idle, claude.Phase);
    }

    [Fact]
    public void DeduperRemembersFirstOwner()
    {
        var deduper = new Deduper();

        Assert.True(deduper.TryClaim("k", "file-a", out var first));
        Assert.Equal("file-a", first);
        Assert.False(deduper.TryClaim("k", "file-b", out var owner));
        Assert.Equal("file-a", owner);
        Assert.False(deduper.TryClaim("k", "file-a", out owner));
        Assert.Equal("file-a", owner);
        Assert.Equal(1, deduper.Count);
    }

    [Fact]
    public void DeduperEvictsOldestFirstAtCapacity()
    {
        var deduper = new Deduper(capacity: 2);

        Assert.True(deduper.TryClaim("a", "f1", out _));
        Assert.True(deduper.TryClaim("b", "f1", out _));
        Assert.False(deduper.TryClaim("a", "f2", out _));

        // Insertion order, not recency: re-checking "a" did not refresh it.
        Assert.True(deduper.TryClaim("c", "f1", out _));
        Assert.Equal(2, deduper.Count);
        Assert.True(deduper.TryClaim("a", "f2", out var owner));
        Assert.Equal("f2", owner);
        Assert.False(deduper.TryClaim("c", "f2", out owner));
        Assert.Equal("f1", owner);
        Assert.True(deduper.TryClaim("b", "f2", out _));
        Assert.Equal(2, deduper.Count);
    }

    [Fact]
    public void DeduperDefaultCapacityIs65536()
    {
        Assert.Equal(65_536, Deduper.DefaultCapacity);
        var deduper = new Deduper();
        for (var i = 0; i < Deduper.DefaultCapacity; i++)
        {
            Assert.True(deduper.TryClaim("k" + i, "f", out _));
        }

        Assert.False(deduper.TryClaim("k0", "g", out _));
        Assert.True(deduper.TryClaim("overflow", "f", out _));
        Assert.Equal(Deduper.DefaultCapacity, deduper.Count);
        Assert.True(deduper.TryClaim("k0", "g", out _));
        Assert.False(deduper.TryClaim("k2", "g", out _));
    }

    [Fact]
    public void EvictedKeyIsTreatedAsNewByParsers()
    {
        var deduper = new Deduper(capacity: 1);
        var a = new CodexParser(CodexAgent, deduper);
        var b = new CodexParser(CodexAgent + ".b", deduper);
        var sink = new RecordingSink();
        var usage = CodexLines.Usage(10, 0, 1, 0);

        Feed.Lines(a, sink, CodexLines.UsageRecord(CodexLines.T0, "r1", usage), CodexLines.UsageRecord(CodexLines.T0 + 1, "r2", usage));
        Feed.Lines(b, sink, CodexLines.UsageRecord(CodexLines.T0 + 2, "r1", usage), CodexLines.UsageRecord(CodexLines.T0 + 3, "r1", usage));

        Assert.Equal(["r1", "r2", "r1"], sink.Emissions.Select(e => e.Response.Key));
    }

    [Theory]
    [InlineData(@"C:\Users\me\proj\", "proj")]
    [InlineData(@"C:\Users\me\proj\\", "proj")]
    [InlineData("/Users/dev/projects/rocket//", "rocket")]
    [InlineData("/Users/dev/projects/rocket", "rocket")]
    [InlineData(@"/mixed\separators/last", "last")]
    [InlineData("proj", "proj")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void BasenameIgnoresTrailingSeparators(string path, string expected)
    {
        Assert.Equal(expected, PathText.Basename(path));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude\projects\-C-Users-me-proj\s1\subagents\agent-1.jsonl", true)]
    [InlineData(@"C:\Users\me\.claude\projects\-C-Users-me-proj\subagents.jsonl", false)]
    [InlineData("/a/subagents/x.jsonl", true)]
    [InlineData("subagents/x.jsonl", true)]
    [InlineData("/a/subagents", false)]
    [InlineData("/a/my-subagents/x.jsonl", false)]
    [InlineData("subagents", false)]
    public void DirectoryComponentExcludesTheFileName(string path, bool expected)
    {
        Assert.Equal(expected, PathText.HasDirectoryComponent(path, "subagents"));
    }
}
