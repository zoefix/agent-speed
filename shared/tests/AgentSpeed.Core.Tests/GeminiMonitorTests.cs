using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.GeminiRecords;

namespace AgentSpeed.Core.Tests;

/// <summary>Gemini CLI roots through the monitor: legacy documents, live JSONL and parent links (METRICS 4.6, 6.7).</summary>
public sealed class GeminiMonitorTests : IDisposable
{
    // Sanitized, the sub-agent folder name is "orbit_main_1".
    private const string MainId = "orbit.main:1";

    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public GeminiMonitorTests()
    {
        _todayStart = _now - 3_600_000;
        _temp.WriteFile("gemini/tmp/proj/.project_root", "/work/rocket\n");
    }

    private string GeminiRoot => _temp.Combine("gemini", "tmp");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LegacyDocumentIsSeededAndARewriteAddsOnlyTheNewResponse()
    {
        var t = _now - 90_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""));
        SetModified(path, _now - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, events) = Build(core);

        Assert.Equal(1, report.FilesRead);
        Assert.Equal([("g1", true, true)], observer.Keys());
        Assert.Empty(events);
        Assert.Equal(new TokenTotals(1, 1000, 0, 0, 100, 0), snapshot.Today);
        var agent = Assert.Single(snapshot.Agents);
        Assert.Equal((SourceKind.Gemini, "rocket", "gemini-2.5-pro", AgentPhase.Idle, false),
            (agent.Source, agent.Project, agent.Model, agent.Phase, agent.IsSubagent));
        Assert.Equal(1, snapshot.Sources.Single(s => s.Kind == SourceKind.Gemini).SessionCount);
        observer.Seen.Clear();

        WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""),
            User("u2", _now - 9000), Reply("g2", _now - 5000, Tokens(2000, 300), "\"Done\""));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        (snapshot, events) = Build(core);

        Assert.Equal([("g1", false, false), ("g2", true, false)], observer.Keys());
        Assert.Equal(_now - 9000, observer.Seen[1].Response.StartMs);
        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(events));
        Assert.Equal((SourceKind.Gemini, "rocket", 300L), (completed.Response.Source, completed.Response.Project, completed.Response.OutputTokens));
        Assert.Equal(new TokenTotals(2, 3000, 0, 0, 400, 0), snapshot.Today);
        Assert.Equal(0, core.MalformedLineCount);
    }

    [Fact]
    public void LegacyDocumentRewritesAreFoundByTheRecentFilePoll()
    {
        var t = _now - 90_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""));
        SetModified(path, _now - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        observer.Seen.Clear();

        WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""), User("u2", _now - 2000));

        Assert.True(core.PollRecent(_now));
        Assert.Equal([("g1", false, false)], observer.Keys());
        Assert.Equal(AgentPhase.Generating, Assert.Single(Build(core).Snapshot.Agents).Phase);
    }

    [Fact]
    public void TornRewriteCountsOneMalformedLineAndKeepsStateAndTotals()
    {
        var t = _now - 90_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""), User("u2", _now - 5000));
        SetModified(path, _now - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        observer.Seen.Clear();
        var whole = File.ReadAllBytes(path);

        File.WriteAllBytes(path, whole[..(whole.Length / 2)]);
        core.ProcessPaths([path], _now, _todayStart);
        var (snapshot, events) = Build(core);

        Assert.Equal(1, core.MalformedLineCount);
        Assert.Contains(new MonitorWarning.MalformedLines(1), snapshot.Warnings);
        Assert.Empty(observer.Seen);
        Assert.Empty(events);
        Assert.Equal(new TokenTotals(1, 1000, 0, 0, 100, 0), snapshot.Today);
        Assert.Equal(AgentPhase.Generating, Assert.Single(snapshot.Agents).Phase);

        File.WriteAllBytes(path, whole);
        core.ProcessPaths([path], _now, _todayStart);
        (snapshot, events) = Build(core);

        Assert.Equal([("g1", false, false)], observer.Keys());
        Assert.Empty(events);
        Assert.Equal(1, core.MalformedLineCount);
        Assert.Equal(1, snapshot.Today.Requests);
    }

    [Fact]
    public void OversizedDocumentIsNotReadAndMarksPartialHistory()
    {
        var big = _temp.WriteFile("gemini/tmp/proj/chats/session-2026-10-20T09-00-bigbig12.json", "{}");
        WholeFileTailerTests.SetLength(big, DocumentTailer.MaxBytes + 1L);
        SetModified(big, _now - 30_000);
        WriteJsonl("session-2026-10-20T10-00-abcdef12.jsonl", Header("abcdef12-s", _now - 20_000), User("u1", _now - 10_000));
        var observer = new Observer();
        var core = NewCore(observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = Build(core);

        Assert.Equal(2, report.FilesDiscovered);
        Assert.Equal(1, report.FilesRead);
        Assert.True(core.PartialHistory);
        Assert.Contains(new MonitorWarning.PartialHistory(), snapshot.Warnings);
        Assert.Empty(observer.Seen);
        Assert.Equal(0, core.MalformedLineCount);
        Assert.Equal(2, core.TrackedFileCount);
    }

    [Fact]
    public void DocumentGrowingPastTheCapWhileRunningMarksPartialHistoryAndKeepsTotals()
    {
        var t = _now - 90_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""));
        SetModified(path, _now - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.False(core.PartialHistory);
        observer.Seen.Clear();

        WholeFileTailerTests.SetLength(path, DocumentTailer.MaxBytes + 1L);
        core.ProcessPaths([path], _now, _todayStart);
        var (snapshot, _) = Build(core);

        Assert.True(core.PartialHistory);
        Assert.Empty(observer.Seen);
        Assert.Equal(0, core.MalformedLineCount);
        Assert.Equal(1, snapshot.Today.Requests);
    }

    [Fact]
    public void LiveJsonlAppendRaisesACompletionEvent()
    {
        var path = WriteJsonl("session-2026-10-20T10-00-abcdef12.jsonl", Header("abcdef12-s", _now - 20_000), User("u1", _now - 10_000));
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, events) = Build(core);
        Assert.Empty(events);
        Assert.Equal(AgentPhase.Generating, Assert.Single(snapshot.Agents).Phase);

        File.AppendAllText(path, Reply("g1", _now - 2000, Tokens(500, 80, cached: 400), "\"Answer\"") + "\n");
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        (snapshot, events) = Build(core);

        Assert.Equal([("g1", true, false)], observer.Keys());
        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(events));
        Assert.Equal((SourceKind.Gemini, "rocket", "gemini-2.5-pro", 80L, 580L),
            (completed.Response.Source, completed.Response.Project, completed.Response.Model,
                completed.Response.OutputTokens, completed.Response.TotalTokens));
        Assert.Equal(TimeSpan.FromSeconds(8), completed.Response.Duration);
        Assert.Equal(new TokenTotals(1, 500, 400, 0, 80, 0), snapshot.Today);
        Assert.Equal(AgentPhase.Idle, Assert.Single(snapshot.Agents).Phase);
    }

    [Fact]
    public void SubagentsAttachToTheirMainSessionPreferringJsonl()
    {
        var main = WriteJsonl("session-2026-10-20T10-00-orbitmai.jsonl", Header(MainId, _now - 60_000), User("m1", _now - 50_000));
        var legacyMain = WriteDocument("session-2026-10-20T09-00-orbitmai.json", MainId, _now - 120_000, User("l1", _now - 110_000));
        var sub = WriteJsonl("orbit_main_1/sub-1.jsonl", Header("sub-1", _now - 40_000, kind: "subagent"), User("s1", _now - 30_000));
        var nested = WriteJsonl("orbit_main_1/sub-2.jsonl", Header("sub-2", _now - 35_000, kind: "subagent"), User("s2", _now - 25_000));
        var stray = WriteJsonl("someone_else/sub-3.jsonl", Header("sub-3", _now - 35_000, kind: "subagent"), User("s3", _now - 25_000));
        var core = NewCore();

        core.Seed(_now, _todayStart, CancellationToken.None);
        var agents = Build(core).Snapshot.Agents.ToDictionary(a => a.Id);

        Assert.Equal(5, agents.Count);
        Assert.Equal(main, agents[sub].ParentId);
        Assert.Equal(main, agents[nested].ParentId);
        Assert.Null(agents[main].ParentId);
        Assert.Null(agents[legacyMain].ParentId);
        Assert.Null(agents[stray].ParentId);
        Assert.True(agents[sub].IsSubagent);
        Assert.True(agents[stray].IsSubagent);
        Assert.False(agents[main].IsSubagent);
        Assert.All(agents.Values, a => Assert.Equal("rocket", a.Project));
    }

    [Fact]
    public void WatcherPathsAreAcceptedOnlyInTheSessionLayout()
    {
        var core = NewCore();
        core.Seed(_now, _todayStart, CancellationToken.None);
        var logs = _temp.WriteFile("gemini/tmp/proj/logs.json", "[]");
        var checkpoint = _temp.WriteFile("gemini/tmp/proj/checkpoint-release.json", "[]");
        var temporary = _temp.WriteFile("gemini/tmp/proj/chats/session-x.jsonl.tmp-42", User("u0", _now) + "\n");
        var deep = WriteJsonl("a/b/deep.jsonl", User("u0", _now));

        Assert.False(core.ProcessPaths([logs, checkpoint, temporary, deep], _now, _todayStart));
        Assert.Equal(0, core.TrackedFileCount);

        var session = WriteJsonl("session-2026-10-20T10-05-newnew12.jsonl", Header("newnew12-s", _now - 3000), User("u1", _now - 1000));
        var legacy = WriteDocument("session-2026-10-20T10-06-legacy12.json", "legacy12-s", _now - 3000, User("u2", _now - 1000));

        Assert.True(core.ProcessPaths([session, legacy], _now, _todayStart));
        Assert.Equal(2, core.TrackedFileCount);
        Assert.Equal(2, Build(core).Snapshot.GeneratingCount);
    }

    [Fact]
    public void OldLegacyDocumentIsReadOnceItChangesAndItsOldResponseRaisesNoEvent()
    {
        var t = _now - 30 * 60_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""));
        SetModified(path, _todayStart - MonitorCore.BacklogWindowMs - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);

        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Empty(observer.Seen);

        WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""), User("u2", _now - 1000));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var (snapshot, events) = Build(core);

        Assert.Equal([("g1", true, false)], observer.Keys());
        Assert.Empty(events);
        Assert.Equal(1, snapshot.Today.Requests);
        Assert.Equal(AgentPhase.Generating, Assert.Single(snapshot.Agents).Phase);
    }

    [Fact]
    public void OldLegacyDocumentRewrittenWithTheSameLengthIsRead()
    {
        var t = _now - 30 * 60_000;
        var path = WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 100), "\"Answer\""));
        SetModified(path, _todayStart - MonitorCore.BacklogWindowMs - 60_000);
        var observer = new Observer();
        var core = NewCore(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        WriteDocument("session-2026-10-20T10-00-abcdef12.json", "abcdef12-s", t,
            User("u1", t), Reply("g1", t + 4000, Tokens(1000, 200), "\"Answer\""));
        core.ProcessPaths([path], _now, _todayStart);

        Assert.Equal([("g1", true, false)], observer.Keys());
        Assert.Equal(200, Build(core).Snapshot.Today.Output);
    }

    [Fact]
    public void DisabledGeminiSourceIsNotRead()
    {
        WriteJsonl("session-2026-10-20T10-00-abcdef12.jsonl", Header("abcdef12-s", _now - 20_000), User("u1", _now - 10_000));
        var enabled = new HashSet<SourceKind>(Enum.GetValues<SourceKind>().Where(s => s != SourceKind.Gemini));
        using var core = new MonitorCore(
            new MonitorConfiguration(enabled),
            new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [GeminiRoot]),
            _todayStart);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(0, report.FilesDiscovered);
        Assert.Empty(Build(core).Snapshot.Agents);
    }

    private (SpeedSnapshot Snapshot, IReadOnlyList<SpeedEvent> Events) Build(MonitorCore core) =>
        core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

    private MonitorCore NewCore(IResponseObserver? observer = null) => new(
        MonitorConfiguration.Default,
        new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [GeminiRoot]),
        _todayStart,
        observer);

    private string WriteJsonl(string relativeToChats, params string[] lines) =>
        Path.GetFullPath(_temp.WriteFile("gemini/tmp/proj/chats/" + relativeToChats, string.Concat(lines.Select(l => l + "\n"))));

    // A pretty-printed legacy session document, as Gemini CLI <= v0.38 wrote it.
    private string WriteDocument(string relativeToChats, string sessionId, long startMs, params string[] messages) =>
        Path.GetFullPath(_temp.WriteFile("gemini/tmp/proj/chats/" + relativeToChats,
            "{\n  \"sessionId\": \"" + sessionId + "\",\n  \"projectHash\": \"hash\",\n  \"startTime\": \"" + Ts.Iso(startMs)
            + "\",\n  \"lastUpdated\": \"" + Ts.Iso(startMs) + "\",\n  \"messages\": [\n    "
            + string.Join(",\n    ", messages) + "\n  ]\n}"));

    private static void SetModified(string path, long unixMs) =>
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(unixMs));

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) =>
            Seen.Add((response, isCompletion, seeded));

        public List<(string Key, bool IsCompletion, bool Seeded)> Keys() =>
            Seen.Select(s => (s.Response.Key, s.IsCompletion, s.Seeded)).ToList();
    }
}
