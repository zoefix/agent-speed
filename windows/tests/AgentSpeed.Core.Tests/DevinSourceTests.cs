using System.Text;
using AgentSpeed.Core.Devin;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Devin CLI's rules one by one (METRICS 4.17): what a row says, live reading, several databases and the location list.</summary>
public sealed class DevinSourceTests : IDisposable
{
    private static readonly long NowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A start of day two hours ago: every row these tests write now is today's.</summary>
    private static readonly long TodayStartMs = (NowSeconds - 2 * 3600) * 1000;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DevinMessage? Parse(string text) => DevinMessage.Parse(Encoding.UTF8.GetBytes(text));

    // Rows

    [Fact]
    public void ARowGivesItsCallFromTheMetricsAlone()
    {
        var message = Parse("""
            {"role":"assistant","content":"x","tool_calls":[{}],"metadata":{"request_id":" r1 ","generation_model":"glm-5-2","finish_reason":"stop","metrics":{"input_tokens":67,"output_tokens":103,"cache_read_tokens":32256,"cache_creation_tokens":-4,"total_time_ms":13666,"ttft_ms":13394}}}
            """)!;
        Assert.Equal((DevinRole.Assistant, AgentPhase.Tools, "r1"), (message.Role, message.Phase, message.RequestId));
        var record = DevinCalls.Response(message, "s", 7, 20_000, null, "db#s")!;
        Assert.Equal("devin:s:request:r1", record.Key);
        Assert.Equal(new TokenCounts(32_323, 32_256, 0, 103), record.Tokens);
        Assert.Equal((20_000L - 13_666, 20_000L - 13_666 + 13_394), (record.StartMs, record.FirstMs));
        Assert.Equal(("glm-5-2", (string?)null, true, SourceKind.Devin), (record.Model, record.Provider, record.Final, record.Source));

        // No request id: the row is the call; a session model of `adaptive` is no model; half a millisecond is no duration.
        var plain = Parse("""{"role":"assistant","metadata":{"metrics":{"output_tokens":1,"total_time_ms":0.5}}}""")!;
        var row = DevinCalls.Response(plain, "s", 9, 1_000, DevinCalls.SessionModel(" Adaptive "), "db#s")!;
        Assert.Equal(("devin:s:row:9", (string?)null, (long?)null, (long?)null), (row.Key, row.Model, row.StartMs, row.FirstMs));
        Assert.Equal("kimi-k2-7", DevinCalls.SessionModel(" kimi-k2-7 "));
        Assert.Null(DevinCalls.SessionModel("   "));

        Assert.Equal(AgentPhase.Generating, Parse("""{"role":"tool","content":"…"}""")!.Phase);
        Assert.Equal(AgentPhase.Generating, Parse("""{"role":"user"}""")!.Phase);
        Assert.Equal(AgentPhase.Tools, Parse("""{"role":"assistant","metadata":{"finish_reason":"tool_calls"}}""")!.Phase);
        Assert.Equal(AgentPhase.Idle, Parse("""{"role":"assistant","tool_calls":[],"metadata":{"finish_reason":"stop"}}""")!.Phase);
        Assert.Null(Parse("""{"role":"system"}""")!.Phase);

        // Strict JSON objects only: NaN, trailing text, arrays and truncated text are malformed rows.
        Assert.Null(Parse("""{"role":"assistant","metadata":{"metrics":{"input_tokens":NaN}}}"""));
        Assert.Null(Parse("""{"role":"user"} x"""));
        Assert.Null(Parse("[1]"));
        Assert.Null(Parse("""{"role":"user" """));

        // A later metadata member replaces the earlier one; a non-finite total time gives no start.
        var replaced = Parse("""{"role":"assistant","metadata":{"request_id":"a","metrics":{"output_tokens":5}},"metadata":{"metrics":{"output_tokens":2,"total_time_ms":1e999}}}""")!;
        var last = DevinCalls.Response(replaced, "s", 3, 5_000, "m", "db#s")!;
        Assert.Equal(("devin:s:row:3", 2L, (long?)null, "m"), (last.Key, last.Tokens.Output, last.StartMs, last.Model));

        // Zero tokens: no call (the row still sets the phase).
        var empty = Parse("""{"role":"assistant","metadata":{"metrics":{"input_tokens":0}}}""")!;
        Assert.Null(DevinCalls.Response(empty, "s", 1, 0, null, "a"));
        Assert.Equal(AgentPhase.Idle, empty.Phase);
    }

    // Live

    [Fact]
    public void TheStartupWindowThenNewRowsAsLiveCompletions()
    {
        var path = _temp.Combine("cli", "sessions.db");
        using var database = new DevinCliDatabase(path);
        database.Session("old-pond");
        database.Session("aurora");
        var start = TodayStartMs / 1000;
        database.Call("old-pond", "old", start - 3 * 3600);
        database.Row("aurora", """{"role":"user","content":"go"}""", start + 60);
        database.Call("aurora", "r1", start + 65, tools: true);
        var source = new DevinSource([DevinRoot.Location(_temp.Combine("cli"))], new Deduper());

        var (seeded, discovered, read, _) = source.Seed(TodayStartMs);
        Assert.Equal((1, 1), (discovered, read));
        var seed = Assert.Single(seeded);
        Assert.True(seed.Seeded);
        Assert.Equal(["devin:aurora:request:r1"], seed.Responses.Select(r => r.Response.Key));
        var id = path + "#aurora";
        Assert.Equal((AgentPhase.Tools, "aurora"), (seed.Agents[id].Phase, seed.Agents[id].Project));
        Assert.False(seed.Agents.ContainsKey(path + "#old-pond"), "only rows of the startup window");
        Assert.Equal(1, source.ActiveSessionCount(TodayStartMs));

        // Devin stores the call twice, then the tool result starts the next request.
        database.Call("aurora", "r1", start + 65, tools: true);
        database.Row("aurora", """{"role":"tool","tool_call_id":"c1","content":"…"}""", start + 70);
        var tool = Assert.Single(source.Refresh());
        Assert.Empty(tool.Responses);
        Assert.Equal((AgentPhase.Generating, (start + 70) * 1000), (tool.Agents[id].Phase, tool.Agents[id].InFlightStartMs));

        database.Call("aurora", "r2", start + 80, input: 10, output: 5);
        var next = Assert.Single(source.Refresh());
        Assert.False(next.Seeded);
        var (response, completion) = Assert.Single(next.Responses);
        Assert.Equal(("devin:aurora:request:r2", true), (response.Key, completion));
        Assert.Equal(AgentPhase.Idle, next.Agents[id].Phase);
        Assert.Empty(source.Refresh());
    }

    [Fact]
    public void AReplacedDatabaseIsReadAgainLikeAtStartup()
    {
        var path = _temp.Combine("cli", "sessions.db");
        using (var database = new DevinCliDatabase(path))
        {
            database.Session("aurora");
            database.Call("aurora", "r1", NowSeconds - 60);
        }

        var source = new DevinSource([DevinRoot.Location(_temp.Combine("cli"))], new Deduper());
        source.Seed(TodayStartMs);
        var other = _temp.Combine("other", "sessions.db");
        using (var database = new DevinCliDatabase(other))
        {
            database.Session("aurora");
            database.Call("aurora", "r1", NowSeconds - 60);
            database.Call("aurora", "r2", NowSeconds - 30);
            database.Row("aurora", """{"role":"user"}""", NowSeconds - 20);
        }

        File.Move(other, path, overwrite: true);
        var update = Assert.Single(source.Refresh());
        Assert.True(update.Seeded, "a replaced database is read like at startup");
        Assert.Equal([("devin:aurora:request:r1", false), ("devin:aurora:request:r2", true)],
            update.Responses.Select(r => (r.Response.Key, r.Completion)));
        Assert.Equal(AgentPhase.Generating, update.Agents[path + "#aurora"].Phase);
    }

    [Fact]
    public void ADatabaseSwappedInWithTheSameSizeAndTimeIsStillReadAgain()
    {
        var path = _temp.Combine("cli", "sessions.db");
        using (var database = new DevinCliDatabase(path))
        {
            database.Session("aurora");
            database.Call("aurora", "r1", NowSeconds - 60);
        }

        var source = new DevinSource([DevinRoot.Location(_temp.Combine("cli"))], new Deduper());
        source.Seed(TodayStartMs);
        var other = _temp.Combine("other", "sessions.db");
        using (var database = new DevinCliDatabase(other))
        {
            database.Session("aurora");
            database.Call("aurora", "r2", NowSeconds - 30);
        }

        Assert.Equal(new FileInfo(path).Length, new FileInfo(other).Length);
        File.SetLastWriteTimeUtc(other, File.GetLastWriteTimeUtc(path));
        File.Move(other, path, overwrite: true);
        var update = Assert.Single(source.Refresh());
        Assert.True(update.Seeded, "another file at the path is read like at startup");
        Assert.Contains(update.Responses, r => r.Response.Key == "devin:aurora:request:r2");
    }

    [Fact]
    public void ADatabaseIsNeverALinkAndASessionWithoutAFolderIsNamedByItsId()
    {
        var path = _temp.Combine("real", "sessions.db");
        using var database = new DevinCliDatabase(path);
        database.Session("night-owl", directory: string.Empty);
        database.Call("night-owl", "r1", NowSeconds - 20);
        if (TryLink(_temp.Combine("linked", "sessions.db"), path))
        {
            Assert.Null(DevinRoot.Location(_temp.Combine("linked")).DatabaseNow);
            Assert.Null(DevinRoot.Database(_temp.Combine("linked", "sessions.db")).DatabaseNow);
        }

        var source = new DevinSource([DevinRoot.Location(_temp.Combine("real"))], new Deduper());
        var update = Assert.Single(source.Seed(TodayStartMs).Updates);
        var state = update.Agents[path + "#night-owl"];
        Assert.Equal(("night-owl", (string?)null), (state.Project, state.CwdPath));
        Assert.Equal(path + "#night-owl", Assert.Single(update.Responses).Response.AgentId);
    }

    [Fact]
    public void ACopiedSessionInALaterDatabaseIsSkipped()
    {
        using var first = new DevinCliDatabase(_temp.Combine("a", "sessions.db"));
        using var second = new DevinCliDatabase(_temp.Combine("b", "sessions.db"));
        foreach (var database in new[] { first, second })
        {
            database.Session("aurora");
            database.Call("aurora", "r1", NowSeconds - 20);
        }

        // The later copy also has a call the earlier database lacks: the whole session is skipped, not only its known keys.
        second.Call("aurora", "r3", NowSeconds - 15);
        second.Session("comet");
        second.Call("comet", "r9", NowSeconds - 10);
        var source = new DevinSource([DevinRoot.Location(_temp.Combine("a")), DevinRoot.Location(_temp.Combine("b"))], new Deduper());
        var updates = source.Seed(TodayStartMs).Updates;
        Assert.Equal([first.Path + "#aurora", second.Path + "#comet"],
            updates.SelectMany(u => u.Responses).Select(r => r.Response.AgentId).Order(StringComparer.Ordinal));
        Assert.Equal(2, source.ActiveSessionCount(TodayStartMs));
        Assert.Equal(0, source.Readers[1].Agents.Count(a => a.SessionId == "aurora"));

        var enabled = new HashSet<SourceKind> { SourceKind.Devin };
        var configuration = new MonitorConfiguration(enabled)
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Devin, [DataLocation.At(_temp.Combine("a")), DataLocation.At(_temp.Combine("b"))]),
        };
        using var store = History.HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new History.HistoryAnalyzer(store);
        analyzer.Run(SourceLocations.Resolve(configuration), enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(2, analyzer.Days(enabled).Sum(day => day.Totals.Requests));
        Assert.DoesNotContain(second.Path + "#aurora", analyzer.Entries.Keys);
    }

    /// <summary>Through the monitor and the engine: today's calls, each stored copy once; a Devin Desktop folder in the list adds nothing.</summary>
    [Fact]
    public void TodayCountsEachCallOnce()
    {
        using var database = new DevinCliDatabase(_temp.Combine("cli", "sessions.db"));
        database.Session("aurora");
        for (var copy = 0; copy < 2; copy++)
        {
            database.Call("aurora", "r1", NowSeconds - 30, input: 100, output: 40);
        }

        _temp.WriteFile("Devin/User/acp-events/d.ndjson",
            """{"notification":{"sessionUpdate":"usage_update","used":9,"size":9,"_meta":{"cognition.ai/inputTokens":300,"cognition.ai/outputTokens":9}}}""" + "\n");
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Devin })
        {
            Locations = LocationLists.Automatic
                .With(LocationFolder.Devin, [DataLocation.At(_temp.Combine("cli")), DataLocation.At(_temp.Combine("Devin"))])
                .With(LocationFolder.OpenClaw, [DataLocation.At(_temp.Combine("no-claw"))]),
        };
        var now = NowSeconds * 1000;
        using var core = new MonitorCore(configuration, SourceLocations.Resolve(configuration), TodayStartMs);
        core.Seed(now, TodayStartMs, CancellationToken.None);
        SpeedSnapshot Snapshot() => core.BuildSnapshot(now, TodayStartMs, MonitorState.Running, observeLive: false).Snapshot;

        Assert.Equal((1L, 40L, 1_000L), (Snapshot().Today.Requests, Snapshot().Today.Output, Snapshot().Today.Input));
        var status = Snapshot().Sources.Single(s => s.Kind == SourceKind.Devin);
        Assert.Equal((1, true, _temp.Combine("cli")), (status.SessionCount, status.Available, status.RootPath));
        Assert.Contains(core.WatchRoots, root => root.Family == RootFamily.Devin && root.Path == _temp.Combine("cli"));
        Assert.Single(core.Devin!.Readers);

        database.Call("aurora", "r2", NowSeconds, input: 10, output: 5);
        Assert.True(core.PollRecent(now));
        Assert.Equal((2L, 45L), (Snapshot().Today.Requests, Snapshot().Today.Output));
        var agent = Snapshot().Agents.Single(a => a.Source == SourceKind.Devin);
        Assert.Equal(("aurora", "swe-1-6"), (agent.Project, agent.Model));
    }

    [Fact]
    public void ADatabaseWithoutTheTablesIsUnsupported()
    {
        var path = _temp.Combine("cli", "sessions.db");
        Directory.CreateDirectory(_temp.Combine("cli"));
        using (var writer = IO.Sqlite.SqliteConnection.Open(path, IO.Sqlite.SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT, model TEXT)");
        }

        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Devin })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Devin, [DataLocation.At(_temp.Combine("cli"))])
                .With(LocationFolder.OpenClaw, [DataLocation.At(_temp.Combine("no-claw"))]),
        };
        using var core = new MonitorCore(configuration, SourceLocations.Resolve(configuration), TodayStartMs);
        core.Seed(NowSeconds * 1000, TodayStartMs, CancellationToken.None);
        Assert.Contains(new MonitorWarning.UnsupportedFormat(SourceKind.Devin), core.Warnings());
        Assert.Equal(0, core.UnreadableFileCount);
    }

    // Location list

    [Fact]
    public void SameLocationsAndTheAutomaticEntry()
    {
        var database = _temp.WriteFile("share/devin/cli/sessions.db", string.Empty);
        string Key(string relative) => LocationLists.LocationKey(_temp.Combine(relative.Split('/')), LocationFolder.Devin);
        Assert.Equal(Key("share/devin"), Key("share/devin/cli"));
        Assert.NotEqual(Key("share"), Key("share/devin"));
        Assert.Equal(LocationPaths.Resolved(_temp.Combine("empty", "sessions.db")), Key("empty"));

        IReadOnlyList<string> automatic = [DevinRoot.Database(database).LocationKey];
        var lists = LocationLists.Automatic;
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(_temp.Combine("share", "devin"), LocationFolder.Devin, automatic).Outcome);
        (lists, var added) = lists.Add(_temp.Combine("work"), LocationFolder.Devin, automatic);
        Assert.Equal(LocationAddOutcome.Added, added);
        lists = lists.Remove(DataLocation.Automatic, LocationFolder.Devin);
        (lists, var restored) = lists.Add(_temp.Combine("share", "devin", "cli"), LocationFolder.Devin, automatic);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, restored);
        Assert.True(lists[LocationFolder.Devin][0].IsAutomatic);
        Assert.Equal([SourceKind.Devin], LocationFolder.Devin.Sources());
        Assert.False(LocationFolder.Devin.AutomaticIsWiderThanDefault());

        Assert.Equal([database], DevinPaths.Databases([DevinRoot.Database(database), DevinRoot.Location(_temp.Combine("share", "devin"))]));
        Assert.Null(DevinRoot.Location(_temp.Combine("Support", "Devin", "User", "acp-events")).DatabaseNow);
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Devin })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Devin,
                [DataLocation.Automatic, DataLocation.At(_temp.Combine("share", "devin")), DataLocation.At(_temp.Combine("work"))]),
        };
        var roots = SourceLocations.Resolve(configuration).DevinRoots;
        Assert.Equal([true, false, false], roots.Select(r => r.IsDatabase));
        Assert.Equal(LiveMonitor.DefaultDevinDatabase, roots[0].Path);
    }

    [Fact]
    public void TheAutomaticRootsFollowTheEnvironment()
    {
        static Func<string, string?> Env(params (string Key, string Value)[] values) =>
            key => values.FirstOrDefault(v => v.Key == key).Value;

        Assert.Equal("/Users/me/.local/share/devin/cli/sessions.db", DevinPaths.AutomaticDatabase(Env(), false, "/Users/me"));
        Assert.Equal("/x/devin/cli/sessions.db", DevinPaths.AutomaticDatabase(Env(("XDG_DATA_HOME", "/x")), false, "/Users/me"));
        Assert.Equal("/y/s.db", DevinPaths.AutomaticDatabase(Env(("XDG_DATA_HOME", "/x"), ("CHISEL_SESSION_DB", "/y/s.db")), false, "/h"));
        Assert.Equal("/h/.local/share/devin/cli/sessions.db", DevinPaths.AutomaticDatabase(Env(("CHISEL_SESSION_DB", "")), false, "/h"));
        Assert.Equal(@"C:\Users\me\AppData\Roaming\devin\cli\sessions.db", DevinPaths.AutomaticDatabase(Env(("XDG_DATA_HOME", "/x")), true, @"C:\Users\me"));
        Assert.Equal(@"D:\Roaming\devin\cli\sessions.db", DevinPaths.AutomaticDatabase(Env(("APPDATA", @"D:\Roaming")), true, @"C:\Users\me"));
        Assert.Equal(@"E:\s.db", DevinPaths.AutomaticDatabase(Env(("APPDATA", @"D:\Roaming"), ("CHISEL_SESSION_DB", @"E:\s.db")), true, @"C:\Users\me"));
        if (Environment.GetEnvironmentVariable("CHISEL_SESSION_DB") is not { Length: > 0 })
        {
            Assert.Equal(["devin", "cli"], LiveMonitor.DefaultDevinRoot.Split(Path.DirectorySeparatorChar)[^2..]);
        }
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
