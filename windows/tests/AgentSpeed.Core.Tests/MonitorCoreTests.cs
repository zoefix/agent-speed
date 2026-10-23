using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class MonitorCoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public MonitorCoreTests()
    {
        _todayStart = _now - 3_600_000;
        Directory.CreateDirectory(CodexRoot);
        Directory.CreateDirectory(ClaudeRoot);
    }

    private string CodexRoot => _temp.Combine("codex", "sessions");

    private string ClaudeRoot => _temp.Combine("claude", "projects");

    private string DshRoot => _temp.Combine("dsh", "sessions");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SeedingOrderByModificationTimeDecidesTheOwnerOfADuplicatedKey()
    {
        var t = _now - 600_000;
        var a = WriteClaude("p/a.jsonl", ClaudeLines.UserText(t, "hi"), ClaudeLines.Assistant(t + 2000, "msg_X", 100));
        var b = WriteClaude("p/b.jsonl", ClaudeLines.UserText(t + 500, "hi"), ClaudeLines.Assistant(t + 2000, "msg_X", 100));

        SetModified(a, _now - 20_000);
        SetModified(b, _now - 10_000);
        Assert.Equal(a, SeedAndCollect()["msg_X"].AgentId);

        SetModified(a, _now - 10_000);
        SetModified(b, _now - 20_000);
        Assert.Equal(b, SeedAndCollect()["msg_X"].AgentId);
    }

    [Fact]
    public void EqualModificationTimesAreBrokenByOrdinalPath()
    {
        var t = _now - 600_000;
        var upper = WriteClaude("p/B.jsonl", ClaudeLines.UserText(t, "hi"), ClaudeLines.Assistant(t + 2000, "msg_Y", 10));
        var lower = WriteClaude("p/a.jsonl", ClaudeLines.UserText(t + 100, "hi"), ClaudeLines.Assistant(t + 2000, "msg_Y", 10));
        SetModified(upper, _now - 10_000);
        SetModified(lower, _now - 10_000);

        var owner = SeedAndCollect()["msg_Y"].AgentId;

        Assert.Equal(string.CompareOrdinal(upper, lower) < 0 ? upper : lower, owner);
    }

    [Fact]
    public void FilesOlderThanTheBacklogWindowStartAtTheirSizeAndAppendsAreLive()
    {
        var t = _now - 600_000;
        var path = WriteClaude("p/old.jsonl", ClaudeLines.UserText(t, "hi"), ClaudeLines.Assistant(t + 1000, "msg_old", 50));
        SetModified(path, _todayStart - MonitorCore.BacklogWindowMs - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);

        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Empty(observer.Seen);

        File.AppendAllText(path,
            ClaudeLines.UserText(_now - 4000, "again") + "\n" + ClaudeLines.Assistant(_now - 1000, "msg_new", 80) + "\n");
        Assert.True(core.ProcessPaths([path], _now, _todayStart));

        var seen = Assert.Single(observer.Seen);
        Assert.Equal(("msg_new", true, false), (seen.Response.Key, seen.IsCompletion, seen.Seeded));
        Assert.Equal(_now - 4000, seen.Response.StartMs);
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);
        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(events));
        Assert.Equal(80, completed.Response.OutputTokens);
        Assert.Equal(1, snapshot.Today.Requests);
    }

    [Fact]
    public void SeededResponsesNeverProduceEventsButCountToday()
    {
        var t = _now - 120_000;
        WriteCodex("2026/10/20/rollout-a.jsonl",
            CodexLines.SessionMeta(t),
            CodexLines.TurnContext(t + 10),
            CodexLines.TaskStarted(t + 1000),
            CodexLines.Envelope(t + 5000, "token_usage_record",
                """{"thread_id":"thread-1","response_id":"resp-a","usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":200,"reasoning_output_tokens":20}}"""));
        var core = NewCore();

        var report = core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Equal(1, report.FilesRead);
        Assert.Empty(events);
        Assert.Equal(new TokenTotals(1, 1000, 800, 0, 200, 20), snapshot.Today);
        Assert.Equal(50.0, snapshot.AverageToday!.Value, 6);
        Assert.Equal(Enum.GetValues<SourceKind>().Length, snapshot.Sources.Count);
        Assert.Equal(Enum.GetValues<SourceKind>(), snapshot.Sources.Select(s => s.Kind));
        Assert.Equal(1, snapshot.Sources[0].SessionCount);
    }

    [Fact]
    public void NewFileDiscoveredWhileRunningIsReadFromTheStartAndRaisesEvents()
    {
        var core = NewCore();
        core.Seed(_now, _todayStart, CancellationToken.None);
        var path = WriteClaude("q/new.jsonl", ClaudeLines.UserText(_now - 3000, "go"), ClaudeLines.Assistant(_now - 1000, "msg_n", 40));

        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        Assert.Single(events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal(1, snapshot.Today.Requests);
        Assert.Single(snapshot.Agents);
    }

    [Fact]
    public void TruncationResetsTheParserButKeepsDedupe()
    {
        var t = _now - 60_000;
        var path = WriteClaude("p/s.jsonl",
            ClaudeLines.UserText(t, "a"), ClaudeLines.Assistant(t + 1000, "msg_1", 10),
            ClaudeLines.UserText(t + 2000, "b"), ClaudeLines.Assistant(t + 3000, "msg_2", 20));
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        observer.Seen.Clear();

        File.WriteAllText(path, ClaudeLines.UserText(t, "a") + "\n" + ClaudeLines.Assistant(t + 1000, "msg_1", 10) + "\n");
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var (snapshot, events) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

        var replay = Assert.Single(observer.Seen);
        Assert.Equal(("msg_1", false), (replay.Response.Key, replay.IsCompletion));
        Assert.Empty(events);
        Assert.Equal(2, snapshot.Today.Requests);
        Assert.Equal(30, snapshot.Today.Output);
    }

    [Fact]
    public void DisabledSourcesAreExcludedFromTotalsAndAgents()
    {
        var t = _now - 60_000;
        WriteClaude("p/desk.jsonl",
            ClaudeLines.User(t, "\"hi\"", extra: ",\"entrypoint\":\"claude-desktop\""),
            ClaudeLines.Assistant(t + 1000, "msg_d", 10, extra: ",\"entrypoint\":\"claude-desktop\""));
        WriteClaude("p/cli.jsonl",
            ClaudeLines.User(t, "\"hi\"", extra: ",\"entrypoint\":\"cli\""),
            ClaudeLines.Assistant(t + 1000, "msg_c", 20, extra: ",\"entrypoint\":\"cli\""));
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.ClaudeCode });
        var core = new MonitorCore(configuration, CodexRoot, ClaudeRoot, DshRoot, _todayStart);

        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false);

        Assert.Equal(20, snapshot.Today.Output);
        Assert.All(snapshot.Agents, a => Assert.Equal(SourceKind.ClaudeCode, a.Source));
        var desktop = snapshot.Sources.Single(s => s.Kind == SourceKind.ClaudeDesktop);
        Assert.False(desktop.Enabled);
        Assert.Equal(TokenTotals.Zero, desktop.Today);
        Assert.False(snapshot.Sources.Single(s => s.Kind == SourceKind.Codex).Enabled);
    }

    [Fact]
    public void SessionCountsAreFilesOfThatSourceWithALineToday()
    {
        WriteClaude("p/desk.jsonl", ClaudeLines.User(_now - 5000, "\"hi\"", extra: ",\"entrypoint\":\"claude-desktop\""));
        WriteClaude("p/cli.jsonl", ClaudeLines.User(_now - 5000, "\"hi\"", extra: ",\"entrypoint\":\"cli\""));
        WriteClaude("p/yesterday.jsonl", ClaudeLines.User(_todayStart - 60_000, "\"hi\"", extra: ",\"entrypoint\":\"cli\""));
        var old = WriteClaude("p/old.jsonl", ClaudeLines.User(_todayStart - 86_400_000, "\"hi\"", extra: ",\"entrypoint\":\"claude-desktop\""));
        SetModified(old, _todayStart - 86_400_000);

        var core = NewCore();
        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false);

        Assert.Equal(4, core.TrackedFileCount);
        Assert.Equal(1, core.SessionCount(SourceKind.ClaudeDesktop, _todayStart));
        Assert.Equal(1, core.SessionCount(SourceKind.ClaudeCode, _todayStart));
        Assert.Equal([0, 1, 1, 0, 0, 0, 0, 0], snapshot.Sources.Select(s => s.SessionCount));
    }

    [Fact]
    public void OldFilesSkipTheFragmentBeforeTheFirstNewlineTheyRead()
    {
        var path = Write(Path.Combine(ClaudeRoot, "p/resumed.jsonl"), [ClaudeLines.UserText(_now - 600_000, "hi")]);
        File.AppendAllText(path, "{\"partial\":");
        SetModified(path, _todayStart - 86_400_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        File.AppendAllText(path, "1}\n" + ClaudeLines.UserText(_now - 3000, "go") + "\n"
            + ClaudeLines.Assistant(_now - 1000, "msg_r", 30) + "\n");
        core.ProcessPaths([path], _now, _todayStart);

        Assert.Equal(0, core.MalformedLineCount);
        Assert.Equal(_now - 3000, Assert.Single(observer.Seen).Response.StartMs);
    }

    [Fact]
    public void MissingRootsMeanNoSources()
    {
        var core = new MonitorCore(
            MonitorConfiguration.Default, _temp.Combine("nope1"), _temp.Combine("nope2"), _temp.Combine("nope3"), _todayStart);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.False(core.AnyEnabledRootAvailable);
        Assert.Equal(0, report.FilesDiscovered);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.NoSources, observeLive: false);
        Assert.All(snapshot.Sources, s => Assert.False(s.Available));
    }

    [Fact]
    public void MalformedLinesAreReportedAsAWarning()
    {
        WriteClaude("p/bad.jsonl", "not json", ClaudeLines.UserText(_now - 1000, "ok"), "{\"broken\":");
        var core = NewCore();

        core.Seed(_now, _todayStart, CancellationToken.None);

        var warning = Assert.Single(core.Warnings().OfType<MonitorWarning.MalformedLines>());
        Assert.Equal(2, warning.Count);
    }

    [Fact]
    public void DeletedFilesAreDroppedOnRediscovery()
    {
        var path = WriteClaude("p/gone.jsonl", ClaudeLines.UserText(_now - 1000, "ok"));
        var core = NewCore();
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Equal(1, core.TrackedFileCount);

        File.Delete(path);
        core.Rediscover(_now, _todayStart, CancellationToken.None);

        Assert.Equal(0, core.TrackedFileCount);
    }

    [Fact]
    public void DshSessionsAreADeepSeekSourceWithItsOwnRoot()
    {
        var path = WriteDsh("--w-quasar--/session-a/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_now - 600_000, id: "a", cwd: "/w/quasar")),
            ZstdFrames.Lines(DshLines.StepStart(1, _now - 5000)),
            ZstdFrames.Lines(DshLines.Message(2, _now - 2000, "dsm-1", DshLines.Usage(100, 60, cacheRead: 900))));
        var observer = new Observer();
        var core = NewCore(observer);

        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false);

        var source = snapshot.Sources.Single(s => s.Kind == SourceKind.DeepSeek);
        Assert.True(source.Enabled);
        Assert.True(source.Available);
        Assert.Equal(DshRoot, source.RootPath);
        Assert.Equal(1, source.SessionCount);
        Assert.Equal(60, source.Today.Output);
        Assert.Equal(1000, source.Today.Input);
        var agent = Assert.Single(snapshot.Agents);
        Assert.Equal((path, SourceKind.DeepSeek, "quasar", "deepseek-flash"), (agent.Id, agent.Source, agent.Project, agent.Model));
        Assert.True(Assert.Single(observer.Seen).Seeded);
    }

    [Fact]
    public void DisabledDeepSeekIsNotRead()
    {
        WriteDsh("--w--/session-a/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_now - 600_000), DshLines.StepStart(1, _now - 5000),
                DshLines.Message(2, _now - 2000, "dsm-1", DshLines.Usage(1, 2))));
        var core = new MonitorCore(
            new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex }), CodexRoot, ClaudeRoot, DshRoot, _todayStart);

        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false);

        Assert.Equal(0, core.TrackedFileCount);
        Assert.False(snapshot.Sources.Single(s => s.Kind == SourceKind.DeepSeek).Enabled);
        Assert.Empty(snapshot.Agents);
    }

    [Fact]
    public void OldDshFilesStartAtTheirEndAndAppendedFramesAreLive()
    {
        var path = WriteDsh("--w--/session-a/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_todayStart - 86_400_000, id: "a")),
            ZstdFrames.Lines(DshLines.StepStart(1, _todayStart - 86_000_000),
                DshLines.Message(2, _todayStart - 85_000_000, "dsm-old", DshLines.Usage(1, 2))));
        SetModified(path, _todayStart - 86_000_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Empty(observer.Seen);

        ZstdFrames.Append(path, ZstdFrames.Lines(DshLines.StepStart(3, _now - 3000)),
            ZstdFrames.Lines(DshLines.Message(4, _now - 1000, "dsm-new", DshLines.Usage(10, 20))));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));

        var seen = Assert.Single(observer.Seen);
        Assert.Equal(("dsm-new", _now - 3000, false), (seen.Response.Key, seen.Response.StartMs, seen.Seeded));
    }

    [Fact]
    public void HugeDshFilesAreNotSeededButStillFollowed()
    {
        var path = WriteDsh("--w--/session-a/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_now - 600_000), DshLines.StepStart(1, _now - 500_000),
                DshLines.Message(2, _now - 400_000, "dsm-seed", DshLines.Usage(1, 2))));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(MonitorCore.LargeFileBytes + 1);
        }

        var observer = new Observer();
        var core = NewCore(observer);
        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(0, report.FilesRead);
        Assert.True(core.PartialHistory);
        Assert.Contains(core.Warnings(), w => w is MonitorWarning.PartialHistory);

        ZstdFrames.Append(path, ZstdFrames.Lines(DshLines.StepStart(3, _now - 3000), DshLines.Message(4, _now - 1000, "dsm-tail", DshLines.Usage(5, 6))));
        core.ProcessPaths([path], _now, _todayStart);

        Assert.Equal("dsm-tail", Assert.Single(observer.Seen).Response.Key);
        Assert.Equal(0, core.MalformedLineCount);
    }

    [Fact]
    public void CorruptDshFramesAreReportedAsMalformedLines()
    {
        var corrupt = ZstdFrames.Lines(DshLines.StepStart(1, _now - 5000));
        corrupt[^6] ^= 0xFF;
        WriteDsh("--w--/session-a/session.v4.jsonl.zstd", ZstdFrames.Lines(DshLines.Session(_now - 600_000)), corrupt,
            ZstdFrames.Lines("not json"));
        var core = NewCore();

        core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(2, Assert.Single(core.Warnings().OfType<MonitorWarning.MalformedLines>()).Count);
    }

    [Fact]
    public void NewDshSessionsAreFoundByPathAndByRediscovery()
    {
        Directory.CreateDirectory(DshRoot);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        var first = WriteDsh("--w--/session-a/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_now - 10_000, id: "a"), DshLines.StepStart(1, _now - 5000),
                DshLines.Message(2, _now - 2000, "dsm-a", DshLines.Usage(1, 2))));
        var lockFile = Path.Combine(Path.GetDirectoryName(first)!, "session.lock");
        File.WriteAllText(lockFile, "1");
        core.ProcessPaths([first, lockFile], _now, _todayStart);
        WriteDsh("--w--/session-b/session.v4.jsonl.zstd",
            ZstdFrames.Lines(DshLines.Session(_now - 10_000, id: "b"), DshLines.StepStart(1, _now - 5000),
                DshLines.Message(2, _now - 2000, "dsm-b", DshLines.Usage(1, 2))));
        core.Rediscover(_now, _todayStart, CancellationToken.None);

        Assert.Equal(["dsm-a", "dsm-b"], observer.Seen.Select(s => s.Response.Key));
        Assert.Equal(2, core.TrackedFileCount);
    }

    private MonitorCore NewCore(IResponseObserver? observer = null) =>
        new(MonitorConfiguration.Default, CodexRoot, ClaudeRoot, DshRoot, _todayStart, observer);

    private Dictionary<string, ParsedResponse> SeedAndCollect()
    {
        var observer = new Observer();
        NewCore(observer).Seed(_now, _todayStart, CancellationToken.None);
        return observer.Seen.ToDictionary(s => s.Response.Key, s => s.Response);
    }

    private string WriteClaude(string relative, params string[] lines) =>
        Write(Path.Combine(ClaudeRoot, relative), lines);

    private string WriteDsh(string relative, params byte[][] frames) =>
        ZstdFrames.Write(Path.Combine(DshRoot, relative), frames);

    private string WriteCodex(string relative, params string[] lines) =>
        Write(Path.Combine(CodexRoot, relative), lines);

    private static string Write(string path, string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return Path.GetFullPath(path);
    }

    private static void SetModified(string path, long unixMs) =>
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) =>
            Seen.Add((response, isCompletion, seeded));
    }
}
