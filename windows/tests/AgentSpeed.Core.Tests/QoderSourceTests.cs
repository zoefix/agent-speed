using System.Globalization;
using System.Text;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Qoder;

namespace AgentSpeed.Core.Tests;

/// <summary>Qoder IDE's and Qoder CLI's rules one by one (METRICS 4.18, 4.19): rows, live reading, several databases and the lists.</summary>
public sealed class QoderSourceTests : IDisposable
{
    private static readonly long NowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A start of day two hours ago: every row these tests write now is today's.</summary>
    private static readonly long TodayStartMs = NowMs - 2 * 3_600_000;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Iso(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private string Database(string folder = "Qoder") => _temp.Combine(folder, "SharedClientCache", "cache", "db", "local.db");

    // Rows

    [Fact]
    public void ARowGivesItsCallFromItsTokenInfoAlone()
    {
        Assert.Equal(new QoderUsage(16_846, 481, 2_048),
            QoderValues.Usage(Utf8("""{"prompt_tokens":16846,"completion_tokens":481,"cached_tokens":2048,"max_input_tokens":200000}""")));
        Assert.Equal(new QoderUsage(12, 7, 0), QoderValues.Usage(Utf8("""{"prompt_tokens":12.9,"completion_tokens":7,"cached_tokens":"x"}""")));
        Assert.Equal(new QoderUsage(1_000_000_000_000, 0, 0), QoderValues.Usage(Utf8("""{"prompt_tokens":1e999,"completion_tokens":-3}""")));
        Assert.Equal(new QoderUsage(5, 0, 0), QoderValues.Usage(Utf8("""{"prompt_tokens":1,"prompt_tokens":5}""")));

        // Strict JSON objects only: truncated text, arrays, NaN and trailing text are no token_info (the row is no call).
        Assert.Null(QoderValues.Usage(Utf8("""{"prompt_tokens": 10,""")));
        Assert.Null(QoderValues.Usage(Utf8("[10, 20]")));
        Assert.Null(QoderValues.Usage(Utf8("""{"prompt_tokens":NaN}""")));
        Assert.Null(QoderValues.Usage(Utf8("""{"prompt_tokens":1} x""")));

        Assert.Equal("efficient", QoderValues.ModelKey(Utf8("""{"model_key":" efficient "}""")));
        Assert.Null(QoderValues.ModelKey(Utf8("""{"model_key":"   "}""")));
        Assert.Null(QoderValues.ModelKey(Utf8("""{"model_key":7}""")));
        Assert.Null(QoderValues.ModelKey(Utf8("not json")));

        Assert.Equal(1_792_490_405_900, QoderValues.FinishedAt(Utf8("""{"ai-coding/turn-info":{"startedAt":1,"finishedAt":1792490405900.7},"modelConfig":{"key":"auto"}}""")));
        Assert.Null(QoderValues.FinishedAt(Utf8("""{"modelConfig":{"key":"performance"}}""")));
        Assert.Null(QoderValues.FinishedAt(Utf8("""{"ai-coding/turn-info":{"finishedAt":"soon"}}""")));
        Assert.Null(QoderValues.FinishedAt(Utf8("""{"ai-coding/turn-info":{"finishedAt":1e999}}""")));
        Assert.Null(QoderValues.FinishedAt(Utf8("{not json")));

        var row = new QoderRow(20, null, "copper", QoderRole.Assistant, 5_000, new QoderUsage(800, 50, 900), "efficient", "r6");
        var call = QoderCalls.Response(row, "copper", 5_000, 1_000, "db#copper")!;
        Assert.Equal("qoder:copper:row:20", call.Key);
        Assert.Equal(new TokenCounts(800, 800, 0, 50), call.Tokens);
        Assert.Equal((1_000L, 5_000L, (long?)null, "efficient"), (call.StartMs, call.EndMs, call.FirstMs, call.Model));
        Assert.Equal(((string?)null, true, false, SourceKind.Qoder, 1L), (call.Provider, call.Final, call.Fast, call.Source, call.Requests));
        Assert.Equal("qoder:6f1d", QoderCalls.Response(row with { MessageId = "6f1d" }, "copper", 5_000, null, "db#copper")!.Key);
        Assert.Null(QoderCalls.Response(row with { Usage = new QoderUsage(0, 0, 9) }, "copper", 5_000, null, "db#copper"));
        Assert.Null(QoderCalls.Response(row with { Role = QoderRole.User }, "copper", 5_000, null, "db#copper"));
        Assert.Null(QoderCalls.Response(row with { Usage = null }, "copper", 5_000, null, "db#copper"));
    }

    [Theory]
    [InlineData("/Users/dev/projects/aurora", "/Users/dev/projects/aurora")]
    [InlineData(@"C:\Users\dev\work\ledger", @"C:\Users\dev\work\ledger")]
    [InlineData("file:///Users/dev/projects/aurora%20labs", "/Users/dev/projects/aurora labs")]
    [InlineData("file:///c%3A/work/app", "c:/work/app")]
    [InlineData("file:///C:/Users/dev/My%20Project", "C:/Users/dev/My Project")]
    [InlineData("FILE:///d%3a/w/%E6%B8%AC%E8%A9%A6", "d:/w/測試")]
    [InlineData("file://wsl.localhost/Ubuntu/home/dev/app", "/Ubuntu/home/dev/app")]
    [InlineData("file://server", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void AProjectUriIsAPathOrAFileUri(string? uri, string? path)
    {
        Assert.Equal(path, QoderValues.ProjectPath(uri));
        if (path is not null)
        {
            Assert.Equal(PathText.Basename(path), PathText.Basename(QoderValues.ProjectPath(uri)!));
        }
    }

    // Live

    [Fact]
    public void TheStartupWindowStartsACallAtTheRowBelowIt()
    {
        var path = Database();
        using var database = new QoderIdeDatabase(path);
        database.Session("old-pond");
        database.Session("aurora", "file:///c%3A/work/aurora");
        var window = TodayStartMs - MonitorCore.BacklogWindowMs;
        database.Row("old-pond", "user", window - 7_200_000, "r0");
        database.Call("old-pond", window - 7_100_000, "r0");
        database.Row("aurora", "user", window - 4_000, "r1");
        var call = database.Call("aurora", TodayStartMs + 2_000, "r1", prompt: 1_000, completion: 40, cached: 900);
        var source = new QoderSource([QoderRoot.Location(_temp.Combine("Qoder"))], new Deduper());

        var (seeded, discovered, read, _) = source.Seed(TodayStartMs);
        Assert.Equal((1, 1), (discovered, read));
        var seed = Assert.Single(seeded);
        Assert.True(seed.Seeded);
        var (response, completion) = Assert.Single(seed.Responses);
        Assert.Equal(("qoder:" + call, true, window - 4_000, TodayStartMs + 2_000), (response.Key, completion, response.StartMs, response.EndMs));
        Assert.Equal(new TokenCounts(1_000, 900, 0, 40), response.Tokens);
        var id = path + "#aurora";
        Assert.Equal((AgentPhase.Tools, "aurora", "c:/work/aurora", "auto"),
            (seed.Agents[id].Phase, seed.Agents[id].Project, seed.Agents[id].CwdPath, seed.Agents[id].Model));
        Assert.Equal(TodayStartMs + 2_000, seed.Agents[id].LastLineMs);
        Assert.False(seed.Agents.ContainsKey(path + "#old-pond"), "only rows of the startup window");
        Assert.Equal(1, source.ActiveSessionCount(TodayStartMs));
    }

    [Fact]
    public void NewRowsAreLiveCompletionsAndATurnRecordMovesTheAgentToIdle()
    {
        var path = Database();
        using var database = new QoderIdeDatabase(path);
        database.Session("aurora");
        var source = new QoderSource([QoderRoot.Location(_temp.Combine("Qoder"))], new Deduper());
        source.Seed(TodayStartMs);
        var id = path + "#aurora";

        database.Row("aurora", "user", NowMs - 9_000, "r1");
        var prompt = Assert.Single(source.Refresh());
        Assert.False(prompt.Seeded);
        Assert.Empty(prompt.Responses);
        Assert.Equal((AgentPhase.Generating, NowMs - 9_000), (prompt.Agents[id].Phase, prompt.Agents[id].InFlightStartMs));

        var first = database.Call("aurora", NowMs - 6_000, "r1");
        database.Row("aurora", "tool", NowMs - 5_990, "r1");
        var second = database.Call("aurora", NowMs - 3_000, "r1", prompt: 1_200, completion: 60);
        var calls = Assert.Single(source.Refresh());
        Assert.Equal([("qoder:" + first, true, NowMs - 9_000), ("qoder:" + second, true, NowMs - 5_990)],
            calls.Responses.Select(r => (r.Response.Key, r.Completion, r.Response.StartMs!.Value)));
        Assert.Equal(AgentPhase.Tools, calls.Agents[id].Phase);

        // A finish earlier than the call's row is another turn's: the agent still waits.
        database.Finish("r1", "aurora", NowMs - 4_000);
        Assert.Equal(AgentPhase.Tools, source.Agents.Single().Phase);
        Assert.DoesNotContain(source.Refresh().SelectMany(u => u.Agents.Values), a => a.Phase == AgentPhase.Idle);

        database.Finish("r1", "aurora", NowMs - 2_500);
        var finished = Assert.Single(source.Refresh());
        Assert.Empty(finished.Responses);
        Assert.Equal((AgentPhase.Idle, (long?)null), (finished.Agents[id].Phase, finished.Agents[id].InFlightStartMs));
        Assert.Empty(source.Refresh());

        // A turn already finished when its row is read leaves the agent idle; a stored copy written earlier never moves it back.
        database.Row("aurora", "user", NowMs - 2_000, "r2");
        database.Call("aurora", NowMs - 1_000, "r2");
        database.Finish("r2", "aurora", NowMs - 500);
        database.Row("aurora", "tool", NowMs - 1_500, "r2");
        var turn = Assert.Single(source.Refresh());
        Assert.Equal(AgentPhase.Idle, turn.Agents[id].Phase);
        Assert.Equal(NowMs - 1_000, turn.Agents[id].LastLineMs);
    }

    [Fact]
    public void AReplacedDatabaseIsReadAgainLikeAtStartup()
    {
        var path = Database();
        using (var database = new QoderIdeDatabase(path))
        {
            database.Session("aurora");
            database.Row("aurora", "user", NowMs - 70_000, id: "u1");
            database.Call("aurora", NowMs - 60_000, id: "c1");
        }

        var source = new QoderSource([QoderRoot.Location(_temp.Combine("Qoder"))], new Deduper());
        source.Seed(TodayStartMs);
        var other = _temp.Combine("other", "local.db");
        using (var database = new QoderIdeDatabase(other))
        {
            database.Session("aurora");
            database.Row("aurora", "user", NowMs - 70_000, id: "u1");
            database.Call("aurora", NowMs - 60_000, id: "c1");
            database.Call("aurora", NowMs - 30_000, id: "c2");
            database.Row("aurora", "user", NowMs - 20_000, id: "u2");
        }

        File.Move(other, path, overwrite: true);
        var update = Assert.Single(source.Refresh());
        Assert.True(update.Seeded, "a replaced database is read like at startup");
        Assert.DoesNotContain(path + "#aurora", update.Removed);
        Assert.Equal([("qoder:c1", false), ("qoder:c2", true)], update.Responses.Select(r => (r.Response.Key, r.Completion)));
        Assert.Equal(AgentPhase.Generating, update.Agents[path + "#aurora"].Phase);
    }

    /// <summary>Qoder keeps its database in WAL mode: a closed one is read <c>immutable</c>, an open one <c>mode=ro</c>; neither gets side files.</summary>
    [Fact]
    public void ReadingNeverCreatesSideFilesAndFollowsTheWal()
    {
        var path = Database();
        using (var closed = new QoderIdeDatabase(path, wal: true))
        {
            closed.Session("aurora");
            closed.Row("aurora", "user", NowMs - 9_000, "r1");
            closed.Call("aurora", NowMs - 8_000, "r1");
        }

        Assert.False(File.Exists(path + "-wal"));
        Assert.True(IO.Sqlite.SqliteReadOnly.IsClosedWalDatabase(path));
        var source = new QoderSource([QoderRoot.Location(_temp.Combine("Qoder", "SharedClientCache"))], new Deduper());
        var (updates, _, read, _) = source.Seed(TodayStartMs);
        Assert.Equal((1, 1), (read, updates.Sum(u => u.Responses.Count)));
        Assert.Empty(SideFiles());
        Assert.Empty(source.Refresh());
        Assert.Empty(SideFiles());

        using var open = IO.Sqlite.SqliteConnection.Open(path, IO.Sqlite.SqliteOpenMode.ReadWriteCreate);
        using (var insert = open.Prepare(
            "INSERT INTO chat_message(id, session_id, request_id, role, token_info, model_info, gmt_create) VALUES (?, ?, ?, ?, ?, ?, ?)"))
        {
            insert.Bind(1, "m-9").Bind(2, "aurora").Bind(3, "r2").Bind(4, "assistant").Bind(5, """{"prompt_tokens":10,"completion_tokens":3}""")
                .Bind(6, """{"model_key":"lite"}""").Bind(7, NowMs - 1_000).Run();
        }

        Assert.True(File.Exists(path + "-wal"), "the writer keeps its WAL");
        var live = Assert.Single(source.Refresh());
        Assert.Equal(("qoder:m-9", "lite", NowMs - 8_000), (live.Responses.Single().Response.Key, live.Responses.Single().Response.Model,
            live.Responses.Single().Response.StartMs!.Value));
        Assert.True(source.IsWatchedPath(path + "-wal"));
        Assert.False(source.IsWatchedPath(path + "-shm"));
    }

    [Fact]
    public void ACopiedSessionInALaterDatabaseIsSkipped()
    {
        using var first = new QoderIdeDatabase(Database("a"));
        using var second = new QoderIdeDatabase(Database("b"));
        foreach (var database in new[] { first, second })
        {
            database.Session("aurora");
            database.Row("aurora", "user", NowMs - 30_000);
            database.Call("aurora", NowMs - 20_000, id: "shared");
        }

        // The later copy also has a call the earlier database lacks: the whole session is skipped, not only its known keys.
        second.Call("aurora", NowMs - 15_000);
        second.Session("comet");
        second.Row("comet", "user", NowMs - 12_000);
        second.Call("comet", NowMs - 10_000);
        var source = new QoderSource([QoderRoot.Location(_temp.Combine("a")), QoderRoot.Location(_temp.Combine("b"))], new Deduper());
        var updates = source.Seed(TodayStartMs).Updates;
        Assert.Equal([first.Path + "#aurora", second.Path + "#comet"],
            updates.SelectMany(u => u.Responses).Select(r => r.Response.AgentId).Order(StringComparer.Ordinal));
        Assert.Equal(2, source.ActiveSessionCount(TodayStartMs));
        Assert.DoesNotContain(source.Readers[1].Agents, a => a.SessionId == "aurora");

        var enabled = new HashSet<SourceKind> { SourceKind.Qoder };
        var configuration = new MonitorConfiguration(enabled)
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Qoder, [DataLocation.At(_temp.Combine("a")), DataLocation.At(_temp.Combine("b"))]),
        };
        using var store = History.HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new History.HistoryAnalyzer(store);
        analyzer.Run(SourceLocations.Resolve(configuration), enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(2, analyzer.Days(enabled).Sum(day => day.Totals.Requests));
        Assert.DoesNotContain(second.Path + "#aurora", analyzer.Entries.Keys);
    }

    /// <summary>Through the monitor and the engine: today's calls, sub-agent sessions counted and linked to their parent.</summary>
    [Fact]
    public void TodayCountsEveryCallAndSubAgentsLinkToTheirParent()
    {
        var path = Database();
        using var database = new QoderIdeDatabase(path);
        database.Session("aurora", "file:///c%3A/work/aurora");
        database.Session("scout", "/w/scout", parent: "aurora");
        database.Session("orphan", "/w/orphan", parent: "missing");
        database.Row("aurora", "user", NowMs - 30_000, "r1");
        database.Call("aurora", NowMs - 25_000, "r1", prompt: 1_000, completion: 40);
        database.Row("scout", "user", NowMs - 24_000, "r2");
        database.Call("scout", NowMs - 22_000, "r2", prompt: 500, completion: 20, model: "lite");
        database.Row("orphan", "user", NowMs - 21_000, "r3");
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Qoder })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Qoder, [DataLocation.At(_temp.Combine("Qoder"))])
                .With(LocationFolder.OpenClaw, [DataLocation.At(_temp.Combine("no-claw"))]),
        };
        using var core = new MonitorCore(configuration, SourceLocations.Resolve(configuration), TodayStartMs);
        core.Seed(NowMs, TodayStartMs, CancellationToken.None);
        SpeedSnapshot Snapshot() => core.BuildSnapshot(NowMs, TodayStartMs, MonitorState.Running, observeLive: false).Snapshot;

        Assert.Equal((2L, 60L, 1_500L), (Snapshot().Today.Requests, Snapshot().Today.Output, Snapshot().Today.Input));
        var status = Snapshot().Sources.Single(s => s.Kind == SourceKind.Qoder);
        Assert.Equal((3, true, _temp.Combine("Qoder")), (status.SessionCount, status.Available, status.RootPath));
        Assert.Contains(core.WatchRoots, root => root.Family == RootFamily.Qoder && root.Path == Path.GetDirectoryName(path));
        var agents = Snapshot().Agents.Where(a => a.Source == SourceKind.Qoder).ToDictionary(a => a.Id);
        Assert.Equal(("aurora", "auto", false, (string?)null), (agents[path + "#aurora"].Project, agents[path + "#aurora"].Model,
            agents[path + "#aurora"].IsSubagent, agents[path + "#aurora"].ParentId));
        Assert.Equal((true, path + "#aurora"), (agents[path + "#scout"].IsSubagent, agents[path + "#scout"].ParentId));
        Assert.Equal((true, (string?)null), (agents[path + "#orphan"].IsSubagent, agents[path + "#orphan"].ParentId));

        database.Call("orphan", NowMs - 1_000, "r3", prompt: 10, completion: 5);
        Assert.True(core.PollRecent(NowMs));
        Assert.Equal((3L, 65L), (Snapshot().Today.Requests, Snapshot().Today.Output));
    }

    [Fact]
    public void ADatabaseWithoutTheColumnsIsUnsupported()
    {
        var path = Database();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = IO.Sqlite.SqliteConnection.Open(path, IO.Sqlite.SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("""
                CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text);
                CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), role text, content text, gmt_create INTEGER);
                """);
        }

        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Qoder })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Qoder, [DataLocation.At(_temp.Combine("Qoder"))])
                .With(LocationFolder.OpenClaw, [DataLocation.At(_temp.Combine("no-claw"))]),
        };
        using var core = new MonitorCore(configuration, SourceLocations.Resolve(configuration), TodayStartMs);
        core.Seed(NowMs, TodayStartMs, CancellationToken.None);
        Assert.Contains(new MonitorWarning.UnsupportedFormat(SourceKind.Qoder), core.Warnings());
        Assert.Equal(0, core.UnreadableFileCount);
    }

    // Qoder CLI

    /// <summary>Qoder's own models keep no usage: the session, its phase and model show, its tokens do not; sub-agents are no sessions.</summary>
    [Fact]
    public void QoderCliShowsSessionsWithoutUsageAndCountsTokensWithIt()
    {
        static string Line(string kind, long ms, string body) =>
            "{\"type\":\"" + kind + "\",\"uuid\":\"u" + ms + "\",\"timestamp\":\"" + Iso(ms)
            + "\",\"cwd\":\"/w/atlas\",\"sessionId\":\"s1\",\"entrypoint\":\"cli\"" + body + "}";
        var root = _temp.Combine(".qoder", "projects");
        _temp.WriteFile(".qoder/projects/-w-atlas/s1.jsonl", string.Join('\n',
            """{"type":"runtime-config","model":"auto","timestamp":1792674194850}""",
            Line("user", NowMs - 9_000, ",\"message\":{\"role\":\"user\",\"content\":\"go\"}"),
            Line("assistant", NowMs - 6_000, ",\"message\":{\"id\":\"c1\",\"model\":\"auto\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}]}"),
            Line("user", NowMs - 5_900, ",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"x\"}]}"),
            Line("assistant", NowMs - 3_000, ",\"message\":{\"id\":\"c2\",\"model\":\"auto\",\"stop_reason\":\"end_turn\",\"content\":[],\"usage\":{\"input_tokens\":20341,\"cache_read_input_tokens\":20096,\"cache_creation_input_tokens\":0,\"output_tokens\":92,\"speed\":\"fast\"}}"))
            + "\n");
        _temp.WriteFile(".qoder/projects/-w-atlas/s1/subagents/agent-1.jsonl",
            Line("user", NowMs - 5_000, ",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"find\"}") + "\n");
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.QoderCli })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.QoderCli, [DataLocation.At(_temp.Combine(".qoder"))])
                .With(LocationFolder.OpenClaw, [DataLocation.At(_temp.Combine("no-claw"))]),
        };
        var locations = SourceLocations.Resolve(configuration);
        Assert.Equal([root], locations.QoderCliRoots);
        using var core = new MonitorCore(configuration, locations, TodayStartMs);
        core.Seed(NowMs, TodayStartMs, CancellationToken.None);
        var snapshot = core.BuildSnapshot(NowMs, TodayStartMs, MonitorState.Running, observeLive: false).Snapshot;

        Assert.Equal((1L, 92L, 20_341L, 20_096L), (snapshot.Today.Requests, snapshot.Today.Output, snapshot.Today.Input, snapshot.Today.CachedInput));
        var status = snapshot.Sources.Single(s => s.Kind == SourceKind.QoderCli);
        Assert.Equal((1, true, root), (status.SessionCount, status.Available, status.RootPath));
        var main = snapshot.Agents.Single(a => a.Id.EndsWith("s1.jsonl", StringComparison.Ordinal));
        Assert.Equal((SourceKind.QoderCli, "atlas", "auto", AgentPhase.Idle), (main.Source, main.Project, main.Model, main.Phase));
        var sub = snapshot.Agents.Single(a => a.IsSubagent);
        Assert.Equal((SourceKind.QoderCli, main.Id), (sub.Source, sub.ParentId));
    }

    /// <summary>Lines without usage claim their key: a fork's copy changes nothing, and a later line with usage completes the key.</summary>
    [Fact]
    public void QoderCliKeysAreClaimedWithoutAResponse()
    {
        static string Assistant(string id, long ms, string? usage, string stop = "end_turn") =>
            "{\"type\":\"assistant\",\"uuid\":\"u-" + id + ms + "\",\"timestamp\":\"" + Iso(ms)
            + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"custom-model\",\"stop_reason\":\"" + stop + "\",\"content\":[]"
            + (usage is null ? string.Empty : ",\"usage\":" + usage) + "}}";
        static string User(long ms) =>
            "{\"type\":\"user\",\"timestamp\":\"" + Iso(ms)
            + "\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}";
        var deduper = new Deduper();
        var sink = new RecordingSink();
        var parser = ClaudeParser.ForQoderCli("/p/a.jsonl", deduper);
        parser.Feed(Utf8(User(1_000)), sink);
        parser.Feed(Utf8(Assistant("k1", 2_000, null)), sink);
        Assert.Empty(sink.Emissions);
        Assert.Equal(("custom-model", AgentPhase.Idle, SourceKind.QoderCli), (parser.Model, parser.Phase, parser.Source));
        Assert.False(deduper.TryClaim("qodercli:k1", "/p/b.jsonl", out var owner));
        Assert.Equal("/p/a.jsonl", owner);

        // Zero usage is no usage; the pending start was consumed, so the next call starts at its own prompt.
        parser.Feed(Utf8(Assistant("k2", 3_000, """{"input_tokens":0,"output_tokens":0,"billable":true}""")), sink);
        parser.Feed(Utf8(User(4_000)), sink);
        parser.Feed(Utf8(Assistant("k1", 5_000, """{"input_tokens":12,"cache_read_input_tokens":4000,"cache_creation_input_tokens":1800,"output_tokens":64}""")), sink);
        var (response, completion) = Assert.Single(sink.Emissions);
        Assert.True(completion, "the key's first response is its completion");
        Assert.Equal(("qodercli:k1", 4_000L, 5_000L, (long?)null), (response.Key, response.StartMs, response.EndMs, response.FirstMs));
        Assert.Equal(new TokenCounts(5_812, 4_000, 1_800, 64), response.Tokens);
        Assert.Equal((false, ExecutionModes.Empty), (response.Fast, response.Modes));

        // A fork copies the lines: every key is owned by the first file, so nothing changes there.
        var fork = ClaudeParser.ForQoderCli("/p/b.jsonl", deduper);
        var forkSink = new RecordingSink();
        fork.Feed(Utf8(User(1_000)), forkSink);
        fork.Feed(Utf8(Assistant("k1", 5_000, """{"input_tokens":12,"output_tokens":64}""", stop: "tool_use")), forkSink);
        Assert.Empty(forkSink.Emissions);
        Assert.Equal(((string?)null, AgentPhase.Generating), (fork.Model, fork.Phase));
    }

    [Fact]
    public void QoderCliIgnoresApiRequestLinesAndUltracodeMarkers()
    {
        static string At(long ms) => Iso(ms);
        var sink = new RecordingSink();
        var parser = ClaudeParser.ForQoderCli("/p/c.jsonl", new Deduper());
        parser.Feed(Utf8("{\"type\":\"user\",\"timestamp\":\"" + At(1_000) + "\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}"), sink);
        parser.Feed(Utf8("{\"type\":\"api-request\",\"timestamp\":\"" + At(2_000) + "\"}"), sink);
        parser.Feed(Utf8("{\"type\":\"attachment\",\"timestamp\":\"" + At(2_100) + "\",\"attachment\":{\"type\":\"ultra_effort_enter\"}}"), sink);
        parser.Feed(Utf8("{\"type\":\"assistant\",\"timestamp\":\"" + At(3_000) + "\",\"entrypoint\":\"claude-desktop\",\"effort\":\"high\","
            + "\"message\":{\"id\":\"m\",\"model\":\"auto\",\"stop_reason\":null,\"content\":[],\"usage\":{\"input_tokens\":30,\"output_tokens\":5}}}"), sink);
        var response = Assert.Single(sink.Latest);
        Assert.Equal((1_000L, false), (response.StartMs!.Value, response.Final));
        Assert.Equal((SourceKind.QoderCli, ExecutionModes.Empty, ExecutionModes.Empty), (parser.Source, parser.Modes, response.Modes));
        Assert.Equal((AgentPhase.Generating, (long?)1_000), (parser.Phase, parser.InFlightStartMs));
        Assert.Equal(3_000, parser.LastLineMs);
    }

    // Location lists

    [Fact]
    public void SameLocationsAndTheAutomaticEntries()
    {
        var database = _temp.WriteFile("Roaming/Qoder/SharedClientCache/cache/db/local.db", string.Empty);
        string Key(string relative, LocationFolder folder = LocationFolder.Qoder) =>
            LocationLists.LocationKey(_temp.Combine(relative.Split('/')), folder);
        Assert.Equal(LocationPaths.Resolved(database), Key("Roaming/Qoder"));
        Assert.Equal(Key("Roaming/Qoder"), Key("Roaming/Qoder/SharedClientCache"));
        Assert.Equal(Key("Roaming/Qoder"), Key("Roaming/Qoder/SharedClientCache/cache/db"));
        Assert.NotEqual(Key("Roaming/Qoder"), Key("Roaming/Qoder/SharedClientCache/cache"));
        Assert.Equal(Key("Empty/QoderCN"), Key("Empty/QoderCN/SharedClientCache"));
        Assert.Equal(Key("Empty/QoderCN"), Key("Empty/QoderCN/SharedClientCache/cache/db"));
        Assert.Equal(Key(".qoder", LocationFolder.QoderCli), Key(".qoder/projects", LocationFolder.QoderCli));
        Assert.NotEqual(Key(".qoder", LocationFolder.QoderCli), Key(".qoder-cn", LocationFolder.QoderCli));

        IReadOnlyList<string> automatic = [QoderRoot.Database(database).LocationKey, QoderRoot.Database(_temp.Combine("Roaming", "QoderCN", "SharedClientCache", "cache", "db", "local.db")).LocationKey];
        var lists = LocationLists.Automatic;
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(_temp.Combine("Roaming", "Qoder", "SharedClientCache"), LocationFolder.Qoder, automatic).Outcome);
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(_temp.Combine("Roaming", "QoderCN"), LocationFolder.Qoder, automatic).Outcome);
        (lists, var added) = lists.Add(_temp.Combine("portable", "user-data"), LocationFolder.Qoder, automatic);
        Assert.Equal(LocationAddOutcome.Added, added);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(_temp.Combine("portable", "user-data", "SharedClientCache"), LocationFolder.Qoder, automatic).Outcome);
        lists = lists.Remove(DataLocation.Automatic, LocationFolder.Qoder);
        (lists, var restored) = lists.Add(_temp.Combine("Roaming", "Qoder"), LocationFolder.Qoder, automatic);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, restored);
        Assert.True(lists[LocationFolder.Qoder][0].IsAutomatic);
        Assert.Equal([SourceKind.Qoder], LocationFolder.Qoder.Sources());
        Assert.Equal([SourceKind.QoderCli], LocationFolder.QoderCli.Sources());
        Assert.False(LocationFolder.Qoder.AutomaticIsWiderThanDefault());
        Assert.False(LocationFolder.QoderCli.AutomaticIsWiderThanDefault());

        Assert.Equal([database], QoderPaths.Databases([QoderRoot.Database(database), QoderRoot.Location(_temp.Combine("Roaming", "Qoder"))]));
        Assert.Null(QoderRoot.Location(_temp.Combine("Roaming", "Qoder", "User", "globalStorage")).DatabaseNow);
        Assert.Equal(_temp.Combine("Roaming", "Qoder"), QoderRoot.Database(database).DisplayFolder);
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Qoder, SourceKind.QoderCli })
        {
            Locations = LocationLists.Automatic
                .With(LocationFolder.Qoder, [DataLocation.Automatic, DataLocation.At(_temp.Combine("Roaming", "Qoder")), DataLocation.At(_temp.Combine("work"))])
                .With(LocationFolder.QoderCli, [DataLocation.Automatic, DataLocation.At(_temp.Combine(".qoder")), DataLocation.At(_temp.Combine("cli", "projects"))]),
        };
        var resolved = SourceLocations.Resolve(configuration);
        Assert.Equal([true, true, false, false], resolved.QoderRoots.Select(r => r.IsDatabase));
        Assert.Equal(LiveMonitor.DefaultQoderDatabases, resolved.QoderRoots.Take(2).Select(r => r.Path));
        Assert.Equal([.. LiveMonitor.DefaultQoderCliRoots, _temp.Combine(".qoder", "projects"), _temp.Combine("cli", "projects")], resolved.QoderCliRoots);
        Assert.Equal(resolved.QoderCliRoots, resolved.FileRoots().Where(r => r.Family == RootFamily.QoderCli).Select(r => r.Path));
    }

    [Fact]
    public void TheAutomaticRootsFollowTheEnvironment()
    {
        static Func<string, string?> Env(params (string Key, string Value)[] values) =>
            key => values.FirstOrDefault(v => v.Key == key).Value;

        Assert.Equal([@"C:\Users\me\AppData\Roaming\Qoder\SharedClientCache\cache\db\local.db", @"C:\Users\me\AppData\Roaming\QoderCN\SharedClientCache\cache\db\local.db"],
            QoderPaths.AutomaticDatabases(Env(("XDG_CONFIG_HOME", "/x")), windows: true, macOS: false, @"C:\Users\me"));
        Assert.Equal(@"D:\Roaming\Qoder\SharedClientCache\cache\db\local.db",
            QoderPaths.AutomaticDatabases(Env(("APPDATA", @"D:\Roaming")), windows: true, macOS: false, @"C:\Users\me")[0]);
        Assert.Equal("/Users/me/Library/Application Support/QoderCN/SharedClientCache/cache/db/local.db",
            QoderPaths.AutomaticDatabases(Env(("XDG_CONFIG_HOME", "/x")), windows: false, macOS: true, "/Users/me")[1]);
        Assert.Equal("/x/Qoder/SharedClientCache/cache/db/local.db", QoderPaths.AutomaticDatabases(Env(("XDG_CONFIG_HOME", "/x")), false, false, "/h")[0]);
        Assert.Equal("/h/.config/Qoder/SharedClientCache/cache/db/local.db", QoderPaths.AutomaticDatabases(Env(("XDG_CONFIG_HOME", "")), false, false, "/h")[0]);

        Assert.Equal([@"C:\Users\me\.qoder\projects", @"C:\Users\me\.qoder-cn\projects"], QoderCliPaths.AutomaticRoots(Env(), windows: true, @"C:\Users\me"));
        Assert.Equal([@"E:\cfg\projects", @"C:\Users\me\.qoder-cn\projects"],
            QoderCliPaths.AutomaticRoots(Env(("QODER_CONFIG_DIR", @"E:\cfg")), windows: true, @"C:\Users\me"));
        Assert.Equal(["/h/.qoder/projects", "/srv/cn/projects"], QoderCliPaths.AutomaticRoots(Env(("QODERCN_CONFIG_DIR", "/srv/cn"), ("QODER_CONFIG_DIR", "")), false, "/h"));
        Assert.Equal(@"C:\Users\me\.qoder\projects", QoderCliPaths.RootOf(@"C:\Users\me\.qoder\", windows: true));
        Assert.Equal(@"C:\Users\me\.qoder\Projects", QoderCliPaths.RootOf(@"C:\Users\me\.qoder\Projects", windows: true));
        Assert.Equal("/data/Projects/projects", QoderCliPaths.RootOf("/data/Projects", windows: false));

        Assert.Equal(["Qoder", "QoderCN"], LiveMonitor.DefaultQoderRoots.Select(Path.GetFileName));
        Assert.Equal(LiveMonitor.DefaultQoderRoots, AutomaticLocations.All(LocationFolder.Qoder));
        Assert.All(LiveMonitor.DefaultQoderDatabases, d => Assert.EndsWith(Path.Combine("SharedClientCache", "cache", "db", "local.db"), d, StringComparison.Ordinal));
        if (Environment.GetEnvironmentVariable("QODER_CONFIG_DIR") is not { Length: > 0 } && Environment.GetEnvironmentVariable("QODERCN_CONFIG_DIR") is not { Length: > 0 })
        {
            Assert.Equal([".qoder", ".qoder-cn"], LiveMonitor.DefaultQoderCliRoots.Select(r => Path.GetFileName(Path.GetDirectoryName(r))));
        }
    }

    private List<string> SideFiles() =>
        [.. Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith("-wal", StringComparison.Ordinal) || f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("-journal", StringComparison.Ordinal))];
}
