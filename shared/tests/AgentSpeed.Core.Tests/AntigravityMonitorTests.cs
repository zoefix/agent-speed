using System.Diagnostics;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Antigravity databases in the monitor (METRICS 5.4) and the daily history (8).</summary>
public sealed class AntigravityMonitorTests : IDisposable
{
    private const string Main = "11111111-2222-4333-8444-555555555555";
    private const string Sub = "66666666-7777-4888-9999-aaaaaaaaaaaa";

    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public AntigravityMonitorTests()
    {
        _todayStart = _now - 3_600_000;
    }

    private string Store => _temp.Combine("gemini", "antigravity-cli");

    private string Conversations => Path.Combine(Store, "conversations");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void RecentDatabasesAreSeededAndOlderOnesAreReadCompletelyOnTheirFirstChange()
    {
        var recent = Conversation(Main, _now - 120_000, ("r-recent", _now - 60_000, 40));
        var old = Conversation(Sub, _todayStart - 86_400_000, ("r-old", _todayStart - 86_000_000, 25));
        SetModified(old, _todayStart - 86_000_000);
        var observer = new Observer();
        using var core = Core(observer);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal((2, 1), (report.FilesDiscovered, report.FilesRead));
        Assert.Equal(["r-recent"], observer.Seen.Select(s => s.Response.Key));
        Assert.True(observer.Seen.All(s => s.Seeded && s.IsCompletion));
        Assert.Equal(0, core.OpenDatabaseConnections);
        var (snapshot, _) = Build(core);
        Assert.Equal(new[] { recent }, snapshot.Agents.Select(a => a.Id));

        using (var writer = new AgWriter(old, schema: false))
        {
            writer.Step(9, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_now - 3000, _now - 1000, usage: AgBlobs.Usage(100, 30, responseId: "r-new")));
            Assert.True(core.ProcessPaths([old + "-wal"], _now, _todayStart));
        }

        Assert.Equal(["r-recent", "r-old", "r-new"], observer.Seen.Select(s => s.Response.Key));
        Assert.All(observer.Seen.Skip(1), s => Assert.False(s.Seeded));
        var (after, events) = Build(core);
        var completed = Assert.Single(events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal((SourceKind.Antigravity, 30L), (completed.Response.Source, completed.Response.OutputTokens));
        Assert.Equal(70, after.Sources.Single(s => s.Kind == SourceKind.Antigravity).Today.Output);
    }

    [Fact]
    public void ADatabaseThatKeepsChangingIsReadAtMostEveryHalfSecond()
    {
        using var writer = new AgWriter(AgWriter.ConversationPath(Conversations, Main)).Trajectory(AgBlobs.Trajectory(_now - 10_000, "/w/nova"));
        writer.Step(0, AgWriter.UserInput, AgWriter.Done, AgBlobs.Step(created: _now - 9000));
        var observer = new Observer();
        using var core = Core(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        writer.Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_now - 8000, _now - 6000, usage: AgBlobs.Usage(10, 4, responseId: "a")));
        Assert.False(core.ProcessPaths([writer.Path + "-wal"], _now, _todayStart));
        Assert.Empty(observer.Seen);

        Thread.Sleep(MonitorCore.DatabaseReadInterval + TimeSpan.FromMilliseconds(50));
        Assert.True(core.PollRecent(_now));
        Assert.Equal(["a"], observer.Seen.Select(s => s.Response.Key));
        Assert.False(core.PollRecent(_now));
    }

    [Fact]
    public void NoConnectionStaysOpenBetweenReads()
    {
        for (var i = 0; i < 20; i++)
        {
            Conversation($"{i:x8}-0000-4000-8000-000000000000", _now - 300_000, ($"r{i}", _now - 200_000 + i, 10));
        }

        using var core = Core();
        core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(20, Build(core).Snapshot.Agents.Count);
        Assert.Equal(0, core.OpenDatabaseConnections);
    }

    [Fact]
    public void SubAgentsLinkToTheirParentConversationAndDeletedDatabasesLeaveTheList()
    {
        var main = Conversation(Main, _now - 120_000, ("m", _now - 100_000, 10));
        var sub = Conversation(Sub, _now - 90_000, ("s", _now - 80_000, 20), parent: Main.ToUpperInvariant());
        using var core = Core();
        core.Seed(_now, _todayStart, CancellationToken.None);

        var agents = Build(core).Snapshot.Agents;
        Assert.Equal(main, agents.Single(a => a.Id == sub).ParentId);
        Assert.True(agents.Single(a => a.Id == sub).IsSubagent);

        File.Delete(sub);
        Assert.True(core.ProcessPaths([sub], _now, _todayStart));
        var (after, _) = Build(core);
        Assert.Equal([main], after.Agents.Select(a => a.Id));
        Assert.Equal(30, after.Today.Output);
    }

    [Fact]
    public void AConversationWithoutAWorkspaceShowsItsStore()
    {
        using (var writer = new AgWriter(AgWriter.ConversationPath(Conversations, Main)))
        {
            writer.Trajectory(Pb.Message(Pb.Timestamp(2, _now - 60_000)))
                .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_now - 50_000, _now - 49_000, usage: AgBlobs.Usage(10, 5, responseId: "w")));
        }

        var observer = new Observer();
        using var core = Core(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal("antigravity-cli", Assert.Single(Build(core).Snapshot.Agents).Project);
        Assert.Equal("antigravity-cli", Assert.Single(observer.Seen).Response.AgentId.Split(Path.DirectorySeparatorChar)[^3]);
    }

    [Fact]
    public void UnsupportedDatabasesAreAWarningAndNoAgent()
    {
        using (var writer = new AgWriter(AgWriter.ConversationPath(Conversations, Main), schema: false))
        {
            writer.Sql("CREATE TABLE trajectory_steps(idx INTEGER, payload BLOB)");
        }

        using var core = Core();
        core.Seed(_now, _todayStart, CancellationToken.None);

        var (snapshot, _) = Build(core);
        Assert.Empty(snapshot.Agents);
        Assert.Contains(new MonitorWarning.UnsupportedFormat(SourceKind.Antigravity), snapshot.Warnings);
    }

    [Fact]
    public void ABackupStoreIsNeverReadAndTheSourceShowsNoFolder()
    {
        var backup = _temp.Combine("gemini", "antigravity-backup");
        Conversation(Main, _now - 60_000, ("b", _now - 50_000, 10), conversations: Path.Combine(backup, "conversations"));
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity }, AntigravityRoot: backup);
        using var core = new MonitorCore(configuration, SourceLocations.Resolve(configuration) with
        {
            CodexRoot = string.Empty, ClaudeRoot = string.Empty, DshRoot = string.Empty, OpenCodeRoot = string.Empty, GeminiRoots = [], OmpRoots = [],
        }, _todayStart);

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(0, report.FilesDiscovered);
        var source = Build(core).Snapshot.Sources.Single(s => s.Kind == SourceKind.Antigravity);
        Assert.False(source.Available);
        Assert.False(core.AnyEnabledRootAvailable);
    }

    [Fact]
    public void TheHistoryReadsEveryDatabaseOnceWithoutCreatingFiles()
    {
        using (var writer = new AgWriter(AgWriter.ConversationPath(Conversations, Main)))
        {
            writer.Trajectory(AgBlobs.Trajectory(_now - 200_000_000, "/w/nova"))
                .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_now - 190_000_000, _now - 189_999_000, usage: AgBlobs.Usage(10, 5, responseId: "x")))
                .Generation(0, AgBlobs.Generation("gemini-3.1-pro", 0));
        }

        Conversation(Sub, _now - 100_000, ("x", _now - 90_000, 99));
        var before = Directory.GetFiles(Conversations).Order(StringComparer.Ordinal).ToList();
        using var store = HistoryStore.Open(_temp.Combine("store", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var locations = new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [], AntigravityStores: [Store]);
        var enabled = new HashSet<SourceKind> { SourceKind.Antigravity };

        var report = analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        var again = analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal((2, 2, 0), (report.FilesAnalyzed, report.FilesDiscovered, again.FilesAnalyzed));
        Assert.Equal(before, Directory.GetFiles(Conversations).Order(StringComparer.Ordinal).ToList());
        var day = Assert.Single(analyzer.Days());
        Assert.Equal((SourceKind.Antigravity, 1L, 5L, 1), (day.Source, day.Totals.Requests, day.Totals.Output, day.Agents));
        Assert.Equal("gemini-3.1-pro", Assert.Single(day.Models).Model);
    }

    [Fact]
    public async Task TheLiveMonitorFollowsANewConversationThroughTheWatcher()
    {
        Directory.CreateDirectory(Conversations);
        var hub = await Task.Run(() => new SpeedHub());
        var events = new System.Collections.Concurrent.ConcurrentQueue<SpeedEvent>();
        var running = new TaskCompletionSource();
        hub.EventRaised += (_, e) => events.Enqueue(e);
        hub.SnapshotChanged += (_, s) =>
        {
            if (s.State.Kind == MonitorStateKind.Running)
            {
                running.TrySetResult();
            }
        };
        var configuration = new MonitorConfiguration(
            new HashSet<SourceKind> { SourceKind.Antigravity }, _temp.Combine("x"), _temp.Combine("x"), _temp.Combine("x"),
            _temp.Combine("x"), _temp.Combine("x"), _temp.Combine("x"), Store);
        using var monitor = new LiveMonitor(hub, configuration);
        monitor.Start();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var writer = new AgWriter(AgWriter.ConversationPath(Conversations, Main)).Trajectory(AgBlobs.Trajectory(start - 1000, "/w/nova"));
        writer.Step(0, AgWriter.UserInput, AgWriter.Done, AgBlobs.Step(created: start - 900))
            .Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(start - 800, start, usage: AgBlobs.Usage(100, 64, responseId: "live")));

        var watch = Stopwatch.StartNew();
        while (!events.OfType<SpeedEvent.ResponseCompleted>().Any() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(25);
        }

        var completed = Assert.Single(events.OfType<SpeedEvent.ResponseCompleted>());
        Assert.Equal((SourceKind.Antigravity, "nova", 64L), (completed.Response.Source, completed.Response.Project, completed.Response.OutputTokens));
    }

    private MonitorCore Core(IResponseObserver? observer = null) => new(
        new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity }),
        new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [], AntigravityStores: [Store]),
        _todayStart,
        observer);

    private (SpeedSnapshot Snapshot, IReadOnlyList<SpeedEvent> Events) Build(MonitorCore core) =>
        core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true);

    /// <summary>A closed conversation with one planner response per entry.</summary>
    private string Conversation(
        string id, long createdMs, (string Key, long EndMs, long Output) response, string? parent = null, string? conversations = null)
    {
        var path = AgWriter.ConversationPath(conversations ?? Conversations, id);
        using (var writer = new AgWriter(path))
        {
            writer.Trajectory(AgBlobs.Trajectory(createdMs, "file:///w/nova", parent))
                .Step(0, AgWriter.UserInput, AgWriter.Done, AgBlobs.Step(created: createdMs + 10))
                .Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(
                    response.EndMs - 1500, response.EndMs, usage: AgBlobs.Usage(1000, response.Output, responseId: response.Key)));
        }

        return Path.GetFullPath(path);
    }

    private static void SetModified(string path, long unixMs) =>
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) => Seen.Add((response, isCompletion, seeded));
    }
}
