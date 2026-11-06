using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests.Locations;

/// <summary>
/// METRICS 2.1 in the live monitor and the daily history: several locations of one source are read together, a file or
/// database reachable twice counts once, and every database keeps its own identity (issue #45).
/// </summary>
public sealed class MultiLocationTests : IDisposable
{
    private const string Modern = "rollout-2026-10-20T10-00-00-modern.jsonl";
    private const string Legacy = "rollout-2026-10-20T10-01-00-legacy.jsonl";

    private static readonly long FixtureDay = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long FixtureNoon = FixtureDay + 12 * 3_600_000;

    /// <summary>The last row of the OpenCode fixture database.</summary>
    private const long OpenCodeLast = 1_792_490_748_000;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DataLocation At(string path) => DataLocation.At(path);

    /// <summary>Copies Codex fixture rollouts into <c>folder/2026/10/20/</c>; written now, so they are read at startup.</summary>
    private static void CopyCodex(string folder, params string[] names)
    {
        var day = Directory.CreateDirectory(Path.Combine(folder, "2026", "10", "20")).FullName;
        foreach (var name in names)
        {
            File.Copy(FixtureLocator.PathOf("codex/sessions/2026/10/20/" + name), Path.Combine(day, name));
        }
    }

    private MonitorConfiguration Configuration(SourceKind source, LocationFolder folder, params DataLocation[] list) =>
        new(new HashSet<SourceKind> { source })
        {
            Locations = LocationLists.Automatic.With(folder, list)
                .With(LocationFolder.OpenClaw, [At(_temp.Combine("no-claw"))]),
        };

    private static MonitorCore Core(MonitorConfiguration configuration, long todayStart, Observer? observer = null, TimeZoneInfo? zone = null) =>
        new(configuration, SourceLocations.Resolve(configuration), todayStart, observer, zone: zone);

    private static SourceStatus Status(MonitorCore core, SourceKind kind, long now, long todayStart) =>
        core.BuildSnapshot(now, todayStart, MonitorState.Running, observeLive: false).Snapshot.Sources.Single(s => s.Kind == kind);

    // Files

    [Fact]
    public void TwoCodexFoldersAreReadTogether()
    {
        var official = _temp.Combine("official", "sessions");
        var company = _temp.Combine("company", "sessions");
        CopyCodex(official, Modern);
        CopyCodex(company, Legacy);
        HashSet<string> KeysOf(params DataLocation[] list)
        {
            var observer = new Observer();
            using var core = Core(Configuration(SourceKind.Codex, LocationFolder.Codex, list), FixtureDay, observer);
            core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
            return [.. observer.Keys];
        }

        var observer = new Observer();
        using var core = Core(Configuration(SourceKind.Codex, LocationFolder.Codex, At(official), At(company)), FixtureDay, observer);
        var report = core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
        Assert.Equal(2, report.FilesDiscovered);
        var first = KeysOf(At(official));
        var second = KeysOf(At(company));
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.True(observer.Keys.SetEquals(first.Union(second)), "both folders' responses");
        Assert.Equal(2, core.SessionCount(SourceKind.Codex, FixtureDay));
        var status = Status(core, SourceKind.Codex, FixtureNoon, FixtureDay);
        Assert.True(status.Available);
        Assert.Equal(official, status.RootPath);
    }

    [Fact]
    public void TheFirstDetectedRootNamesTheSourceAndAnyRootDetectsIt()
    {
        var company = _temp.Combine("company", "sessions");
        CopyCodex(company, Legacy);
        using var core = Core(Configuration(SourceKind.Codex, LocationFolder.Codex, At(_temp.Combine("missing")), At(company)), FixtureDay);
        core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
        var status = Status(core, SourceKind.Codex, FixtureNoon, FixtureDay);
        Assert.True(status.Available);
        Assert.Equal(company, status.RootPath);
        Assert.Equal(1, status.SessionCount);
    }

    [Fact]
    public void AFileUnderTwoNestedRootsIsReadOnce()
    {
        var root = _temp.Combine("codex", "sessions");
        CopyCodex(root, Modern, Legacy);
        var nested = Path.Combine(root, "2026", "10");
        using var core = Core(Configuration(SourceKind.Codex, LocationFolder.Codex, At(root), At(nested)), FixtureDay);
        var report = core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
        Assert.Equal(2, report.FilesDiscovered);
        Assert.Equal(2, core.SessionCount(SourceKind.Codex, FixtureDay));
        var snapshot = core.BuildSnapshot(FixtureNoon, FixtureDay, MonitorState.Running, observeLive: false).Snapshot;
        Assert.All(snapshot.Agents, agent => Assert.StartsWith(root + Path.DirectorySeparatorChar, agent.Id, StringComparison.Ordinal));
        Assert.False(core.Rediscover(FixtureNoon, FixtureDay, CancellationToken.None), "the nested root finds nothing new");
        Assert.Equal(2, core.TrackedFileCount);
    }

    [Fact]
    public void ARootThatResolvesToAnEarlierRootIsReadOnce()
    {
        var root = _temp.Combine("codex", "sessions");
        CopyCodex(root, Modern);
        Directory.CreateSymbolicLink(_temp.Combine("link"), root);
        using var core = Core(Configuration(SourceKind.Codex, LocationFolder.Codex, At(root), At(_temp.Combine("link"))), FixtureDay);
        var report = core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
        Assert.Equal(1, report.FilesDiscovered);
        Assert.Equal(1, core.SessionCount(SourceKind.Codex, FixtureDay));
        Assert.Single(core.WatchRoots, r => r.Family == RootFamily.Codex);
    }

    // Databases

    /// <summary>A copy of the OpenCode fixture database whose ids carry <paramref name="suffix"/>, so it holds other sessions and rows.</summary>
    private string OpenCodeCopy(string name, string? suffix = null)
    {
        var folder = Directory.CreateDirectory(_temp.Combine(name)).FullName;
        var target = Path.Combine(folder, "opencode.db");
        File.Copy(FixtureLocator.PathOf("opencode/opencode.db"), target);
        if (suffix is not null)
        {
            using var writer = SqliteConnection.Open(target, SqliteOpenMode.ReadWriteCreate);
            writer.Execute($"""
                UPDATE session_v2 SET id = id || '{suffix}', parent_id = parent_id || '{suffix}';
                UPDATE session_message SET id = id || '{suffix}', session_id = session_id || '{suffix}';
                """);
        }

        return folder;
    }

    [Fact]
    public void EveryOpenCodeDatabaseIsRead()
    {
        var a = OpenCodeCopy("a");
        var b = OpenCodeCopy("b", "_b");
        var observer = new Observer();
        using var core = Core(Configuration(SourceKind.OpenCode, LocationFolder.OpenCode, At(a), At(Path.Combine(b, "opencode.db"))), 0, observer);
        var report = core.Seed(FixtureNoon, 0, CancellationToken.None);
        Assert.Equal(2, report.FilesDiscovered);
        Assert.Equal(8, observer.Keys.Count);
        Assert.Equal(4, observer.Keys.Count(k => k.EndsWith("_b", StringComparison.Ordinal)));
        var snapshot = core.BuildSnapshot(OpenCodeLast + 1000, 0, MonitorState.Running, observeLive: false).Snapshot;
        Assert.Equal("opencode:ses_main_b", snapshot.Agents.Single(x => x.Id == "opencode:ses_sub_b").ParentId);
        Assert.Equal("opencode:ses_main", snapshot.Agents.Single(x => x.Id == "opencode:ses_sub").ParentId);
        Assert.Equal(4, snapshot.Sources.Single(s => s.Kind == SourceKind.OpenCode).SessionCount);
        Assert.Equal(2, core.WatchRoots.Count(r => r.Family == RootFamily.OpenCode));
    }

    [Fact]
    public void ACopiedOpenCodeDatabaseAddsNothing()
    {
        var live = OpenCodeCopy("live");
        var backup = OpenCodeCopy("backup");
        var observer = new Observer();
        using var core = Core(Configuration(SourceKind.OpenCode, LocationFolder.OpenCode, At(live), At(backup)), 0, observer);
        core.Seed(FixtureNoon, 0, CancellationToken.None);
        Assert.Equal(4, observer.Keys.Count);
        Assert.Equal(2, Status(core, SourceKind.OpenCode, FixtureNoon, 0).SessionCount);
        Assert.Equal(0, core.OpenCode!.Sources[1].ActiveSessionCount(0));
        core.Rediscover(FixtureNoon, 0, CancellationToken.None);
        Assert.Equal(2, Status(core, SourceKind.OpenCode, FixtureNoon, 0).SessionCount);
        Assert.Equal(4, observer.Keys.Count);
    }

    [Fact]
    public void ACopiedZCodeDatabaseAddsNothing()
    {
        foreach (var name in new[] { "a", "b" })
        {
            Directory.CreateDirectory(_temp.Combine(name));
            File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), _temp.Combine(name, "db.sqlite"));
        }

        (MonitorCore Core, Observer Observer) Seeded(params string[] names)
        {
            var observer = new Observer();
            var core = Core(Configuration(SourceKind.ZCode, LocationFolder.ZCode, [.. names.Select(n => At(_temp.Combine(n)))]), FixtureDay, observer);
            core.Seed(FixtureNoon, FixtureDay, CancellationToken.None);
            return (core, observer);
        }

        var (one, oneSeen) = Seeded("a");
        var (two, twoSeen) = Seeded("a", "b");
        using (one)
        using (two)
        {
            var count = one.SessionCount(SourceKind.ZCode, FixtureDay);
            Assert.True(count > 0);
            Assert.Equal(count, two.SessionCount(SourceKind.ZCode, FixtureDay));
            Assert.True(oneSeen.Keys.SetEquals(twoSeen.Keys));
            Assert.Equal(0, two.ZCode!.Sources[1].ActiveSessionCount(FixtureDay));
            Assert.Equal(
                one.BuildSnapshot(FixtureNoon, FixtureDay, MonitorState.Running, false).Snapshot.Agents.Count,
                two.BuildSnapshot(FixtureNoon, FixtureDay, MonitorState.Running, false).Snapshot.Agents.Count);
        }
    }

    // Hermes Agent

    [Fact]
    public void HermesReadsAHomeAndAnAddedLogsFolder()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000 * 1000;
        var t = now - 300_000;
        var zone = TimeZoneInfo.Utc;
        var home = _temp.Combine("home", ".hermes");
        var other = _temp.Combine("other", "logs");
        HermesLines.Append(Path.Combine(home, "logs", "agent.log"), HermesLines.Turn(t, zone, "A"), HermesLines.Call(t + 2000, zone, "A", 1, 100, 10));
        HermesLines.Append(Path.Combine(other, "agent.log"), HermesLines.Turn(t, zone, "B"), HermesLines.Call(t + 3000, zone, "B", 1, 200, 20));
        var observer = new Observer();
        var todayStart = now - 3_600_000;
        using var core = Core(Configuration(SourceKind.Hermes, LocationFolder.Hermes, At(home), At(other)), todayStart, observer, zone);
        core.Seed(now, todayStart, CancellationToken.None);
        Assert.Equal(2, observer.Keys.Count);
        Assert.Equal([Path.Combine(home, "logs"), other], core.Hermes!.WatchDirectories());
        var status = Status(core, SourceKind.Hermes, now, todayStart);
        Assert.Equal(home, status.RootPath);
        Assert.Equal(2, status.SessionCount);
    }

    // Gemini CLI configured effort

    [Fact]
    public void EachGeminiRootTakesItsOwnSettings()
    {
        const string Settings = """
            {"modelConfigs":{"overrides":[{"match":{"model":"gemini-3-pro"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"LEVEL"}}}}]}}
            """;
        _temp.WriteFile("home/.gemini/settings.json", Settings.Replace("LEVEL", "HIGH", StringComparison.Ordinal));
        _temp.WriteFile("work/.gemini/settings.json", Settings.Replace("LEVEL", "LOW", StringComparison.Ordinal));
        _temp.WriteFile("plain/settings.json", Settings.Replace("LEVEL", "MEDIUM", StringComparison.Ordinal));
        var roots = new[] { _temp.Combine("home", ".gemini", "tmp"), _temp.Combine("work", ".gemini", "tmp"), _temp.Combine("plain") };
        var locations = SourceLocations.Resolve(Configuration(SourceKind.Gemini, LocationFolder.Gemini, [.. roots.Select(At)]));
        var values = new ConfiguredValues();
        var efforts = roots.Select(root => values.Of(SourceKind.Gemini, root, string.Empty, "gemini-3-pro", locations.GeminiSettingsOf(root))).ToList();
        Assert.All(efforts, effort => Assert.False(effort.IsEmpty));
        Assert.Equal(3, efforts.Distinct().Count());
    }

    // History

    [Fact]
    public void HistoryReadsEveryLocationAndEachDatabaseOnce()
    {
        var official = _temp.Combine("official", "sessions");
        var company = _temp.Combine("company", "sessions");
        CopyCodex(official, Modern);
        CopyCodex(company, Legacy);
        var live = OpenCodeCopy("oc-live");
        var copy = OpenCodeCopy("oc-copy");
        var other = OpenCodeCopy("oc-other", "_b");
        var enabled = new HashSet<SourceKind> { SourceKind.Codex, SourceKind.OpenCode };

        (HistoryAnalyzer Analyzer, HistoryReport Report) Run(HistoryStore store, DataLocation[] codex, DataLocation[] opencode)
        {
            var configuration = new MonitorConfiguration(enabled)
            {
                Locations = LocationLists.Automatic.With(LocationFolder.Codex, codex).With(LocationFolder.OpenCode, opencode)
                    .With(LocationFolder.OpenClaw, [At(_temp.Combine("no-claw"))]),
            };
            var analyzer = new HistoryAnalyzer(store);
            return (analyzer, analyzer.Run(SourceLocations.Resolve(configuration), enabled, TimeZoneInfo.Utc, CancellationToken.None));
        }

        static long Requests(HistoryAnalyzer analyzer, SourceKind source) =>
            analyzer.Days(new HashSet<SourceKind> { source }).Sum(day => day.Totals.Requests);

        using var singleStore = HistoryStore.Open(_temp.Combine("single", "history.sqlite"));
        using var otherStore = HistoryStore.Open(_temp.Combine("other", "history.sqlite"));
        using var store = HistoryStore.Open(_temp.Combine("both", "history.sqlite"));
        var single = Run(singleStore, [At(official)], [At(live)]).Analyzer;
        var second = Run(otherStore, [At(company)], [At(other)]).Analyzer;
        DataLocation[] codexList = [At(official), At(company)];
        DataLocation[] opencodeList = [At(live), At(copy), At(other)];
        var (both, first) = Run(store, codexList, opencodeList);
        Assert.Equal(2 + 3, first.FilesDiscovered);
        Assert.True(Requests(single, SourceKind.Codex) > 0 && Requests(second, SourceKind.Codex) > 0);
        Assert.Equal(Requests(single, SourceKind.Codex) + Requests(second, SourceKind.Codex), Requests(both, SourceKind.Codex));
        Assert.Equal(Requests(single, SourceKind.OpenCode) + Requests(second, SourceKind.OpenCode), Requests(both, SourceKind.OpenCode));
        var again = Run(store, codexList, opencodeList).Report;
        Assert.Equal((0, 0), (again.FilesAnalyzed, again.SessionsAnalyzed));
        Assert.NotNull(store.Meta("opencode:" + Path.Combine(other, "opencode.db")));
    }

    private sealed class Observer : IResponseObserver
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) => Keys.Add(response.Key);
    }
}
