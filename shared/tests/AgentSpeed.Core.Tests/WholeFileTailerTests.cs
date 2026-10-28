using System.Text;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.ZstdFrames;

namespace AgentSpeed.Core.Tests;

public sealed class WholeFileTailerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ZstSingleFrameIsDecodedWhole()
    {
        var path = Zst("rollout.jsonl.zst", Frame("one\ntwo\n"u8, contentSize: true));
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();

        var result = tailer.ReadAppended(lines);

        Assert.Equal(["one", "two"], lines.Take());
        Assert.Equal(0, lines.Malformed);
        Assert.False(result.WasReset);
        Assert.Equal(new FileInfo(path).Length, result.Length);
        Assert.Equal(result.Length, result.BytesRead);
        Assert.Equal(result.Length, tailer.Offset);
        Assert.Equal(result.Length, tailer.TotalBytesRead);
    }

    [Fact]
    public void ZstLinesMayCrossFrameBoundaries()
    {
        var path = Zst("rollout.jsonl.zst", Text("one\ntw"), Text("o\nthr"), Text("ee\nfour\n"));
        var lines = new LineCollector();

        new ZstStreamTailer(path).ReadAppended(lines);

        Assert.Equal(["one", "two", "three", "four"], lines.Take());
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void ZstLinesLongerThanOneReadChunkStayWhole()
    {
        var big = new string('x', 1_500_000);
        var path = Zst("rollout.jsonl.zst", Frame(Encoding.UTF8.GetBytes("s\n" + big + "\nt\r\n"), contentSize: true));
        var lines = new LineCollector();

        new ZstStreamTailer(path).ReadAppended(lines);

        Assert.Equal(["s", big, "t"], lines.Take());
    }

    [Fact]
    public void ZstSkippableFramesAreSkipped()
    {
        var path = Zst("rollout.jsonl.zst", Skippable(12), Lines("a"), Skippable(16, variant: 7), Lines("b"));
        var lines = new LineCollector();

        new ZstStreamTailer(path).ReadAppended(lines);

        Assert.Equal(["a", "b"], lines.Take());
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void ZstIncompleteTrailingLineIsNotEmitted()
    {
        var path = Zst("rollout.jsonl.zst", Text("a\nb"));
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["a"], lines.Take());

        Write(path, Text("a\nb\n"));
        var reread = tailer.ReadAppended(lines);

        Assert.True(reread.WasReset);
        Assert.Equal(["a", "b"], lines.Take());
    }

    [Fact]
    public void ZstGarbageAfterAFrameCountsOneMalformedLineAndKeepsEarlierLines()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a", "b"), Enumerable.Repeat((byte)0xAB, 32).ToArray(), Lines("c"));
        var lines = new LineCollector();

        var result = new ZstStreamTailer(path).ReadAppended(lines);

        Assert.Equal(["a", "b"], lines.Take());
        Assert.Equal(1, lines.Malformed);
        Assert.Equal(new FileInfo(path).Length, result.BytesRead);
    }

    [Fact]
    public void ZstTruncatedStreamCountsOneMalformedLine()
    {
        var whole = Lines("c", "d", "e");
        var path = Zst("rollout.jsonl.zst", Lines("a", "b"), whole[..^5]);
        var lines = new LineCollector();

        new ZstStreamTailer(path).ReadAppended(lines);

        // Streamed blocks of the cut frame may still be decoded; its incomplete last line never is.
        Assert.Equal(["a", "b"], lines.Lines.Take(2));
        Assert.DoesNotContain("e", lines.Lines);
        Assert.Equal(1, lines.Malformed);
    }

    [Fact]
    public void ZstBadChecksumCountsOneMalformedLine()
    {
        var broken = Lines("x", "y");
        broken[^1] ^= 0xFF;
        var path = Zst("rollout.jsonl.zst", Lines("a"), broken);
        var lines = new LineCollector();

        new ZstStreamTailer(path).ReadAppended(lines);

        Assert.Equal("a", lines.Lines[0]);
        Assert.Equal(1, lines.Malformed);
    }

    [Fact]
    public void UnchangedZstIsNotReadAgain()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a", "b"));
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        lines.Take();

        var again = tailer.ReadAppended(lines);

        Assert.Equal(0, again.BytesRead);
        Assert.False(again.WasReset);
        Assert.Empty(lines.Lines);
        Assert.Equal(new FileInfo(path).Length, tailer.TotalBytesRead);
    }

    [Fact]
    public void RewrittenZstIsReadAgainFromTheStartAndAsksForAReset()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a", "b"));
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        lines.Take();

        Write(path, Lines("a", "b", "c"));
        var reread = tailer.ReadAppended(lines);

        Assert.True(reread.WasReset);
        Assert.Equal(["a", "b", "c"], lines.Take());
        Assert.Equal(new FileInfo(path).Length, reread.BytesRead);
    }

    [Fact]
    public void ZstWithTheSameLengthButANewModificationTimeIsReadAgain()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a"));
        var stamp = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        lines.Take();

        Write(path, Lines("b"));
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        var reread = tailer.ReadAppended(lines);

        Assert.True(reread.WasReset);
        Assert.Equal(["b"], lines.Take());
    }

    [Fact]
    public void ZstStartedAtItsLengthIsNotReadUntilItChanges()
    {
        var path = Zst("rollout.jsonl.zst", Lines("old"));
        var tailer = new ZstStreamTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(new FileInfo(path).Length);
        var first = tailer.ReadAppended(lines);
        Assert.Equal(0, first.BytesRead);
        Assert.Empty(lines.Lines);

        Write(path, Lines("old", "new"));
        var changed = tailer.ReadAppended(lines);

        Assert.Equal(["old", "new"], lines.Take());
        Assert.True(changed.WasReset);
    }

    [Fact]
    public void ZstStartedAtZeroIsReadAndPrimingAtTheLengthIsNot()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a"));
        var lines = new LineCollector();

        var started = new ZstStreamTailer(path);
        started.StartAt(0);
        started.ReadAppended(lines);
        Assert.Equal(["a"], lines.Take());

        var primed = new ZstStreamTailer(path);
        primed.Prime(long.MaxValue);
        Assert.Equal(0, primed.ReadAppended(lines).BytesRead);
        Assert.Empty(lines.Lines);
    }

    [Fact]
    public void ZstReadHonoursCancellation()
    {
        var path = Zst("rollout.jsonl.zst", Lines("a"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new ZstStreamTailer(path).ReadAppended(new LineCollector(), cancelled.Token));
    }

    [Fact]
    public void DocumentIsHandedOverAsOneLine()
    {
        const string Json = "{\n  \"sessionId\": \"s\",\n  \"messages\": []\n}\n";
        var path = _temp.WriteFile("chats/session.json", Json);
        var tailer = new DocumentTailer(path);
        var lines = new LineCollector();

        var result = tailer.ReadAppended(lines);

        Assert.Equal([Json], lines.Take());
        Assert.False(tailer.Oversized);
        Assert.False(result.WasReset);
        Assert.Equal(Json.Length, result.BytesRead);
        Assert.Equal(Json.Length, tailer.Offset);
    }

    [Fact]
    public void DocumentIsReadAgainOnlyWhenItChangesAndNeverAsksForAReset()
    {
        var path = _temp.WriteFile("chats/session.json", "{\"sessionId\":\"a\"}");
        var tailer = new DocumentTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        lines.Take();

        Assert.Equal(0, tailer.ReadAppended(lines).BytesRead);
        Assert.Empty(lines.Lines);

        File.WriteAllText(path, "{\"sessionId\":\"b\",\"messages\":[]}");
        var reread = tailer.ReadAppended(lines);

        Assert.False(reread.WasReset);
        Assert.Equal(["{\"sessionId\":\"b\",\"messages\":[]}"], lines.Take());
    }

    [Fact]
    public void EmptyDocumentIsHandedOverAsAnEmptyLine()
    {
        var path = _temp.WriteFile("chats/session.json", string.Empty);
        var lines = new LineCollector();

        new DocumentTailer(path).ReadAppended(lines);

        Assert.Equal([string.Empty], lines.Take());
    }

    [Fact]
    public void OversizedDocumentIsNotRead()
    {
        var path = _temp.WriteFile("chats/session.json", "{}");
        SetLength(path, DocumentTailer.MaxBytes + 1L);
        var tailer = new DocumentTailer(path);
        var lines = new LineCollector();

        var result = tailer.ReadAppended(lines);

        Assert.True(tailer.Oversized);
        Assert.Equal(0, result.BytesRead);
        Assert.Empty(lines.Lines);
        Assert.Equal(DocumentTailer.MaxBytes + 1L, tailer.Offset);

        File.WriteAllText(path, "{\"sessionId\":\"s\"}");
        tailer.ReadAppended(lines);

        Assert.False(tailer.Oversized);
        Assert.Equal(["{\"sessionId\":\"s\"}"], lines.Take());
    }

    [Theory]
    [InlineData("rollout.jsonl.zst", typeof(ZstStreamTailer))]
    [InlineData("ROLLOUT.JSONL.ZST", typeof(ZstStreamTailer))]
    [InlineData("session.json", typeof(DocumentTailer))]
    [InlineData("session.JSON", typeof(DocumentTailer))]
    [InlineData("session.v4.jsonl.zstd", typeof(ZstdFrameTailer))]
    [InlineData("rollout.jsonl", typeof(FileTailer))]
    [InlineData("session.jsonl", typeof(FileTailer))]
    public void TailerIsChosenByFileName(string name, Type expected) =>
        Assert.IsType(expected, TranscriptTailer.For(Path.Combine(_temp.Path, name)));

    internal static void SetLength(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(length);
    }

    private string Zst(string name, params byte[][] frames) => ZstdFrames.Write(_temp.Combine(name), frames);
}

/// <summary>Compressed Codex rollouts (METRICS 5.3) through the monitor.</summary>
public sealed class CompressedCodexMonitorTests : IDisposable
{
    private const string ThreadId = "thread-z";

    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public CompressedCodexMonitorTests()
    {
        _todayStart = _now - 3_600_000;
        Directory.CreateDirectory(CodexRoot);
    }

    private string CodexRoot => _temp.Combine("codex", "sessions");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void RecentCompressedRolloutIsSeeded()
    {
        var path = WriteZst("rollout-z.jsonl.zst", Turn(_now - 60_000, "resp-z1", output: 150));
        SetModified(path, _now - 10_000);
        var observer = new Observer();
        var core = NewCore(observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Equal(1, report.FilesRead);
        Assert.Equal(("resp-z1", true, true), Single(observer));
        Assert.Empty(events);
        Assert.Equal(new TokenTotals(1, 6000, 5000, 0, 150, 40), snapshot.Today);
        Assert.Equal("comet", Assert.Single(snapshot.Agents).Project);
        Assert.Equal(0, core.MalformedLineCount);
    }

    [Fact]
    public void CompressedRolloutOlderThanTheBacklogWindowIsNotRead()
    {
        var path = WriteZst("rollout-z.jsonl.zst", Turn(_now - 60_000, "resp-z1", output: 150));
        SetModified(path, _todayStart - MonitorCore.BacklogWindowMs - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);
        core.Rediscover(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Equal(0, report.FilesRead);
        Assert.Equal(1, core.TrackedFileCount);
        Assert.Empty(observer.Seen);
        Assert.Equal(0, snapshot.Today.Requests);
    }

    [Fact]
    public void RolloutCompressedWhileTrackedAddsNothing()
    {
        var turn = Turn(_now - 60_000, "resp-z1", output: 150);
        var plain = Path.Combine(CodexRoot, "2026", "10", "20", "rollout-z.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        File.WriteAllText(plain, string.Concat(turn.Select(l => l + "\n")));
        SetModified(plain, _now - 30_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Equal(("resp-z1", true, true), Single(observer));
        observer.Seen.Clear();

        var compressed = WriteZst("rollout-z.jsonl.zst", turn);
        File.Delete(plain);
        core.ProcessPaths([plain, compressed], _now, _todayStart);
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Empty(observer.Seen);
        Assert.Empty(events);
        Assert.Equal(1, core.TrackedFileCount);
        Assert.Equal(1, snapshot.Today.Requests);
        Assert.Equal(150, snapshot.Today.Output);
    }

    [Fact]
    public void RewrittenCompressedRolloutIsReadAgainWithoutDoubleCounting()
    {
        var first = Turn(_now - 60_000, "resp-z1", output: 150);
        var path = WriteZst("rollout-z.jsonl.zst", first);
        SetModified(path, _now - 30_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        observer.Seen.Clear();

        WriteZst("rollout-z.jsonl.zst", [.. first, .. SecondTurn(_now - 10_000, "resp-z2", output: 210)]);
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Equal(("resp-z2", true, false), Single(observer));
        Assert.Equal(_now - 10_000 + 1000, observer.Seen[0].Response.StartMs);
        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(events));
        Assert.Equal(210, completed.Response.OutputTokens);
        Assert.Equal(2, snapshot.Today.Requests);
        Assert.Equal(360, snapshot.Today.Output);
        Assert.Single(snapshot.Agents);
        Assert.Equal(0, core.MalformedLineCount);
    }

    [Fact]
    public void CorruptCompressedRolloutCountsOneMalformedLineAndKeepsWhatWasParsed()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Turn(_now - 60_000, "resp-z1", output: 150).Select(l => l + "\n")));
        var path = ZstdFrames.Write(Path.Combine(CodexRoot, "2026", "10", "20", "rollout-z.jsonl.zst"),
            Frame(text, contentSize: true), Enumerable.Repeat((byte)0xAB, 32).ToArray());
        SetModified(path, _now - 30_000);
        var observer = new Observer();
        var core = NewCore(observer);

        core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(("resp-z1", true, true), Single(observer));
        Assert.Equal(1, core.MalformedLineCount);
    }

    [Fact]
    public void CompressedRolloutAppearingWhileRunningIsAcceptedFromTheWatcher()
    {
        var core = NewCore();
        core.Seed(_now, _todayStart, CancellationToken.None);
        var path = WriteZst("rollout-n.jsonl.zst", Turn(_now - 30_000, "resp-n", output: 90));

        Assert.True(core.ProcessPaths([path, path + ".tmp"], _now, _todayStart));
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Equal(1, core.TrackedFileCount);
        Assert.Equal(1, snapshot.Today.Requests);
        Assert.Single(events.OfType<SpeedEvent.ResponseCompleted>());
    }

    // A turn of thread-z: input 6000 (5000 cached), output as given, 40 reasoning; usage 4 s after the turn starts.
    private static string[] Turn(long t, string responseId, long output) =>
    [
        CodexLines.SessionMeta(t, ThreadId, "/home/me/comet"),
        CodexLines.TurnContext(t + 10, "gpt-6.1-sol-mini"),
        CodexLines.TaskStarted(t + 1000),
        CodexLines.UsageRecord(t + 4000, responseId, CodexLines.Usage(6000, 5000, output, 40), ThreadId),
        CodexLines.TaskComplete(t + 4100),
    ];

    private static string[] SecondTurn(long t, string responseId, long output) =>
    [
        CodexLines.TaskStarted(t + 1000),
        CodexLines.UsageRecord(t + 4000, responseId, CodexLines.Usage(6300, 6000, output, 0), ThreadId),
        CodexLines.TaskComplete(t + 4100),
    ];

    private static (string Key, bool IsCompletion, bool Seeded) Single(Observer observer)
    {
        var seen = Assert.Single(observer.Seen);
        return (seen.Response.Key, seen.IsCompletion, seen.Seeded);
    }

    private static void SetModified(string path, long unixMs) =>
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(unixMs));

    private MonitorCore NewCore(IResponseObserver? observer = null) => new(
        MonitorConfiguration.Default,
        new SourceLocations(CodexRoot, string.Empty, string.Empty, string.Empty, []),
        _todayStart,
        observer);

    private string WriteZst(string name, string[] lines) => ZstdFrames.Write(
        Path.Combine(CodexRoot, "2026", "10", "20", name),
        Frame(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))), contentSize: true));

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) =>
            Seen.Add((response, isCompletion, seeded));
    }
}
