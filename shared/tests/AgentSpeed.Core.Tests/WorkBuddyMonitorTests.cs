using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Both WorkBuddy editions in the monitor (METRICS 2, 4.9, 5, 6.7): roots, detection, sessions, live reads.</summary>
public sealed class WorkBuddyMonitorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public WorkBuddyMonitorTests()
    {
        _todayStart = _now - 3_600_000;
    }

    private string ChinaData => _temp.Combine("home", ".workbuddy");

    private string IntlData => _temp.Combine("home", ".workbuddy-ai");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BothEditionsAreSeededAndSubAgentsAttachToTheirMainSession()
    {
        var t = _now - 200_000;
        var main = Write(ChinaData, "projects/Users-me-atlas/s1.jsonl",
            WorkBuddyLines.User(t, "u1"),
            WorkBuddyLines.ToolCall(t + 2000, "c1", "m1", "call_task", WorkBuddyLines.Usage(4000, 70)));
        var sub = Write(ChinaData, "projects/Users-me-atlas/s1/subagents/agent-1.jsonl",
            WorkBuddyLines.User(t + 2100, "s1"),
            WorkBuddyLines.Say(t + 5000, "s2", "m2", WorkBuddyLines.Usage(3000, 120), model: "glm-5.3-flash"));
        var intl = Write(IntlData, "projects/C-Users-me-orbit/s2.jsonl",
            WorkBuddyLines.User(t, "u2", cwd: "C:/Users/me/orbit"),
            WorkBuddyLines.Say(t + 3000, "a2", "m3", WorkBuddyLines.Usage(9000, 300), model: "custom:gpt-5.5", cwd: "C:/Users/me/orbit"));
        Write(ChinaData, "projects/cloud/user-c1/c1.jsonl", WorkBuddyLines.Say(t, "x", "m9", WorkBuddyLines.Usage(1, 1)));
        Write(ChinaData, "projects/Users-me-atlas/s1/tool-results/call_task.jsonl", WorkBuddyLines.Say(t, "x", "m8", WorkBuddyLines.Usage(1, 1)));
        Write(ChinaData, "traces/4242/t.jsonl", WorkBuddyLines.Say(t, "x", "m7", WorkBuddyLines.Usage(1, 1)));
        var observer = new Observer();
        using var core = Core(Both, observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal((3, 3), (report.FilesDiscovered, core.TrackedFileCount));
        var snapshot = Build(core);
        var agents = snapshot.Agents.ToDictionary(a => a.Id);
        Assert.Equal((main, (string?)null), (agents[sub].ParentId, agents[main].ParentId));
        Assert.Equal((false, true, AgentPhase.Tools), (agents[main].IsSubagent, agents[sub].IsSubagent, agents[main].Phase));
        Assert.Equal(("atlas", "orbit", (string?)"gpt-5.5"), (agents[main].Project, agents[intl].Project, agents[intl].Model));
        var china = snapshot.Sources.Single(s => s.Kind == SourceKind.WorkBuddy);
        var international = snapshot.Sources.Single(s => s.Kind == SourceKind.WorkBuddyAi);
        Assert.Equal((true, 1, 190L), (china.Available, china.SessionCount, china.Today.Output));
        Assert.Equal((true, 1, 300L), (international.Available, international.SessionCount, international.Today.Output));
        Assert.Equal(["m1", "m2", "m3"], observer.Seen.Select(s => s.Response.Key).Order(StringComparer.Ordinal));
        Assert.Equal("custom", observer.Seen.Single(s => s.Response.Key == "m3").Response.Provider);
    }

    [Fact]
    public void AnInstalledButUnusedEditionIsDetectedWithNoSessionsUntilItsFirstSession()
    {
        Directory.CreateDirectory(IntlData);
        var observer = new Observer();
        using var core = Core(Both, observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        var before = Build(core).Sources;
        Assert.Equal((true, 0), (before.Single(s => s.Kind == SourceKind.WorkBuddyAi).Available, before.Single(s => s.Kind == SourceKind.WorkBuddyAi).SessionCount));
        Assert.False(before.Single(s => s.Kind == SourceKind.WorkBuddy).Available);
        Assert.True(core.AnyEnabledRootAvailable);

        Write(IntlData, "projects/C-Users-me-orbit/s1.jsonl",
            WorkBuddyLines.User(_now - 6000, "u1"), WorkBuddyLines.Say(_now - 2000, "a1", "m1", WorkBuddyLines.Usage(800, 64)));
        Assert.True(core.Rediscover(_now, _todayStart, CancellationToken.None));

        var after = Build(core).Sources.Single(s => s.Kind == SourceKind.WorkBuddyAi);
        Assert.Equal((true, 1, 64L), (after.Available, after.SessionCount, after.Today.Output));
        Assert.Equal((long?)(_now - 6000), Assert.Single(observer.Seen).Response.StartMs);
    }

    [Fact]
    public void AnAppendedCallIsALiveCompletionThatStartsAtTheLatestInput()
    {
        var path = Write(ChinaData, "projects/Users-me-atlas/s1.jsonl",
            WorkBuddyLines.User(_now - 9000, "u1"),
            WorkBuddyLines.ToolCall(_now - 8000, "c1", "m1", "call_1", WorkBuddyLines.Usage(500, 20)));
        var observer = new Observer();
        using var core = Core(Both, observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Equal(AgentPhase.Tools, Assert.Single(Build(core).Agents).Phase);

        File.AppendAllText(path, WorkBuddyLines.ToolResult(_now - 6000, "r1", "m1", "call_1") + "\n");
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        Assert.Equal(AgentPhase.Generating, Assert.Single(Build(core).Agents).Phase);

        File.AppendAllText(path, WorkBuddyLines.Say(_now - 1000, "a2", "m2", WorkBuddyLines.Usage(900, 150)) + "\n");
        Assert.True(core.ProcessPaths([path], _now, _todayStart));

        var live = observer.Seen.Last();
        Assert.Equal(("m2", (long?)(_now - 6000), _now - 1000, true, false),
            (live.Response.Key, live.Response.StartMs, live.Response.EndMs, live.IsCompletion, live.Seeded));
        Assert.Equal(AgentPhase.Idle, Assert.Single(Build(core).Agents).Phase);
    }

    [Fact]
    public void ADisabledEditionIsNotRead()
    {
        Write(ChinaData, "projects/Users-me-atlas/s1.jsonl",
            WorkBuddyLines.User(_now - 6000, "u1"), WorkBuddyLines.Say(_now - 2000, "a1", "m1", WorkBuddyLines.Usage(800, 64)));
        Write(IntlData, "projects/C-Users-me-orbit/s2.jsonl",
            WorkBuddyLines.User(_now - 6000, "u2"), WorkBuddyLines.Say(_now - 2000, "a2", "m2", WorkBuddyLines.Usage(800, 32)));
        var observer = new Observer();
        using var core = Core([SourceKind.WorkBuddyAi], observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(1, report.FilesDiscovered);
        Assert.Equal("m2", Assert.Single(observer.Seen).Response.Key);
        var china = Build(core).Sources.Single(s => s.Kind == SourceKind.WorkBuddy);
        Assert.Equal((false, 0L), (china.Enabled, china.Today.Output));
    }

    [Fact]
    public void ReadingTheFixturesChangesNothingInEitherDataFolder()
    {
        var copy = FixtureCopy.WithModificationTimes(_temp.Combine("fixtures"));
        var home = Path.Combine(copy, "workbuddy");
        var before = Listing(home);
        var locations = new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [],
            WorkBuddyRoot: SourceLocations.WorkBuddyRootOf(Path.Combine(home, ".workbuddy")),
            WorkBuddyAiRoot: SourceLocations.WorkBuddyRootOf(Path.Combine(home, ".workbuddy-ai")));
        var enabled = new HashSet<SourceKind>(Both);

        using (var core = new MonitorCore(new MonitorConfiguration(enabled), locations, 0))
        {
            Assert.Equal(8, core.Seed(_now, 0, CancellationToken.None).FilesRead);
        }

        using (var store = HistoryStore.Open(_temp.Combine("store", "history.sqlite")))
        {
            var report = new HistoryAnalyzer(store).Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal((8, 0), (report.FilesAnalyzed, report.FilesUnreadable));
        }

        Assert.Equal(before, Listing(home));
    }

    private static SourceKind[] Both => [SourceKind.WorkBuddy, SourceKind.WorkBuddyAi];

    private MonitorCore Core(SourceKind[] enabled, IResponseObserver? observer = null) => new(
        new MonitorConfiguration(new HashSet<SourceKind>(enabled)),
        new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [],
            WorkBuddyRoot: SourceLocations.WorkBuddyRootOf(ChinaData),
            WorkBuddyAiRoot: SourceLocations.WorkBuddyRootOf(IntlData)),
        _todayStart,
        observer);

    private SpeedSnapshot Build(MonitorCore core) => core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false).Snapshot;

    private static string Write(string data, string relative, params string[] lines)
    {
        var path = Path.Combine(data, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return Path.GetFullPath(path);
    }

    private static List<string> Listing(string folder) =>
    [
        .. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Select(f => $"{Path.GetRelativePath(folder, f.FullName)} {(f.Exists ? f.Length : -1)} {f.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal),
    ];

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) => Seen.Add((response, isCompletion, seeded));
    }
}
