using System.Text.Json;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.OpenCode;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class OpenCodeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose() => _temp.Dispose();

    private string Root => _temp.Combine("opencode");

    private string DatabasePath => Path.Combine(Root, OpenCodeDatabase.FileName);

    [Fact]
    public void FixtureDatabaseMatchesParserExpectations()
    {
        var expected = ParserFixtureTests.Expected.GetProperty("opencode");
        Directory.CreateDirectory(Root);
        File.Copy(FixtureLocator.PathOf(expected.GetProperty("database").GetString()!), DatabasePath);
        using var source = new OpenCodeSource(Root, new Deduper());
        Assert.True(source.RefreshAvailability());
        var update = source.Seed(todayStartMs: 0);

        var agents = expected.GetProperty("agents").EnumerateObject().ToList();
        Assert.Equal(agents.Select(a => a.Name).Order(StringComparer.Ordinal), update.Agents.Keys.Order(StringComparer.Ordinal));
        var parents = ParentLinks.Resolve(
            [.. update.Agents.Values.Select(s => new AgentLink(s.Id, LinkKind.OpenCodeSession, s.SessionId, s.ParentSessionId, s.CreatedMs))],
            StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var e = agent.Value;
            var state = update.Agents[agent.Name];
            Assert.Equal(e.GetProperty("project").GetString(), state.Project);
            Assert.Equal(e.GetProperty("model").GetString(), state.Model);
            Assert.Equal(e.GetProperty("isSubagent").GetBoolean(), state.IsSubagent);
            Assert.Equal(e.GetProperty("parentId").GetString(), parents.GetValueOrDefault(agent.Name));
            Assert.Equal(e.GetProperty("phase").GetString(), ParserFixtureTests.PhaseName(state.Phase));
            Assert.Equal(ParserFixtureTests.NullableLong(e.GetProperty("inFlightStartMs")), state.InFlightStartMs);
            Assert.Equal(ParserFixtureTests.NullableLong(e.GetProperty("lastLineMs")), state.LastLineMs);

            var responses = update.Responses.Where(r => r.Response.AgentId == agent.Name).ToList();
            var expectedResponses = e.GetProperty("responses").EnumerateArray().ToList();
            Assert.Equal(expectedResponses.Count, responses.Count);
            for (var i = 0; i < responses.Count; i++)
            {
                var (r, isCompletion) = responses[i];
                var x = expectedResponses[i];
                Assert.True(isCompletion);
                Assert.Equal(SourceKind.OpenCode, r.Source);
                Assert.Equal(x.GetProperty("key").GetString(), r.Key);
                Assert.Equal(ParserFixtureTests.NullableLong(x.GetProperty("startMs")), r.StartMs);
                Assert.Equal(x.GetProperty("endMs").GetInt64(), r.EndMs);
                Assert.Equal(
                    (x.GetProperty("input").GetInt64(), x.GetProperty("cachedInput").GetInt64(), x.GetProperty("cacheWriteInput").GetInt64(),
                        x.GetProperty("output").GetInt64(), x.GetProperty("reasoning").GetInt64()),
                    (r.Tokens.Input, r.Tokens.CachedInput, r.Tokens.CacheWriteInput, r.Tokens.Output, r.Tokens.Reasoning));
                Assert.Equal(x.GetProperty("model").GetString(), r.Model);
                Assert.Equal(x.GetProperty("final").GetBoolean(), r.Final);
            }
        }
    }

    [Fact]
    public void MonitorCoreSeedsTheFixtureAndLinksTheSubSession()
    {
        Directory.CreateDirectory(Root);
        File.Copy(FixtureLocator.PathOf("opencode/opencode.db"), DatabasePath);
        const long Last = 1_792_490_748_000;
        var today = Last - 3_600_000;
        using var core = Core(today);
        var report = core.Seed(Last, today, CancellationToken.None);
        var (snapshot, events) = core.BuildSnapshot(Last + 1000, today, MonitorState.Running, observeLive: false);

        Assert.Equal((1, 1), (report.FilesDiscovered, report.FilesRead));
        Assert.Empty(events);
        var status = snapshot.Sources.Single(s => s.Kind == SourceKind.OpenCode);
        Assert.True(status.Available);
        Assert.Equal(Root, status.RootPath);
        Assert.Equal(2, status.SessionCount);
        Assert.Equal(new TokenTotals(4, 7909, 2200, 700, 1470, 130), status.Today);
        var main = snapshot.Agents.Single(a => a.Id == "opencode:ses_main");
        var sub = snapshot.Agents.Single(a => a.Id == "opencode:ses_sub");
        Assert.Equal(("nebula", "claude-opus-5-5", AgentPhase.Idle, (string?)null), (main.Project, main.Model, main.Phase, main.ParentId));
        Assert.Equal((AgentPhase.Generating, "opencode:ses_main", true), (sub.Phase, sub.ParentId, sub.IsSubagent));
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void ReadsRowsWrittenWhileOpenAndEmitsAnInFlightRowExactlyOnce()
    {
        using var writer = OpenCodeWriter.Create(DatabasePath);
        var t = _now - 60_000;
        writer.Session("ses_a", null, "/w/comet", t, t);
        writer.Message("u1", "ses_a", "user", 1, t, t, """{"time":{"created":0}}""");
        using var source = new OpenCodeSource(Root, new Deduper());
        source.RefreshAvailability();
        Assert.Equal(OpenCodeStatus.Ready, source.Status);
        Assert.Empty(source.Seed(_now - 3_600_000).Responses);
        Assert.Null(source.RefreshIfChanged());

        writer.Message("m1", "ses_a", "assistant", 2, t + 1000, t + 1000, OpenCodeWriter.Assistant(t + 1000));
        writer.TouchSession("ses_a", t + 1000);
        var inFlight = source.RefreshIfChanged();
        Assert.NotNull(inFlight);
        Assert.Empty(inFlight.Responses);
        Assert.Equal((AgentPhase.Generating, t + 1000), (inFlight.Agents["opencode:ses_a"].Phase, inFlight.Agents["opencode:ses_a"].InFlightStartMs));

        writer.UpdateMessage("m1", t + 5000, OpenCodeWriter.Assistant(t + 1000, streamed: t + 4000, output: 90, finish: "stop"));
        var finished = source.Refresh();
        var (response, isCompletion) = Assert.Single(finished.Responses);
        Assert.True(isCompletion);
        Assert.Equal(("m1", t + 1000, t + 4000, 90L), (response.Key, response.StartMs, response.EndMs, response.Tokens.Output));
        Assert.Equal(AgentPhase.Idle, finished.Agents["opencode:ses_a"].Phase);

        Assert.Empty(source.Refresh().Responses);
        writer.UpdateMessage("m1", t + 6000, OpenCodeWriter.Assistant(t + 1000, streamed: t + 4000, completed: t + 5500, output: 90, finish: "stop"));
        Assert.Empty(source.Refresh().Responses);
        writer.UpdateMessage("m1", t + 7000, OpenCodeWriter.Assistant(t + 1000, streamed: t + 4000, completed: t + 5500, output: 95, finish: "stop"));
        var corrected = Assert.Single(source.Refresh().Responses);
        Assert.Equal((95L, false), (corrected.Response.Tokens.Output, corrected.IsCompletion));
    }

    [Fact]
    public void NewSessionsAndTheirParentsAreFollowed()
    {
        using var writer = OpenCodeWriter.Create(DatabasePath);
        var t = _now - 120_000;
        writer.Session("ses_main", null, "/w/comet", t, t);
        using var source = new OpenCodeSource(Root, new Deduper());
        source.RefreshAvailability();
        source.Seed(_now - 3_600_000);

        writer.Session("ses_child", "ses_main", "/w/comet", t + 10_000, t + 12_000);
        writer.Message("c1", "ses_child", "assistant", 1, t + 10_500, t + 12_000,
            OpenCodeWriter.Assistant(t + 10_500, streamed: t + 11_500, output: 40, finish: "tool-calls"));
        var update = source.Refresh();
        var child = update.Agents["opencode:ses_child"];
        Assert.Equal(("ses_main", true, AgentPhase.Tools, "comet"), (child.ParentSessionId, child.IsSubagent, child.Phase, child.Project));
        Assert.Single(update.Responses);

        writer.DeleteSession("ses_child");
        var removed = source.Rediscover(_now, _now - 3_600_000);
        Assert.Contains("opencode:ses_child", removed.Removed);
    }

    [Fact]
    public void WatcherPathsAndPollingDriveTheEngine()
    {
        using var writer = OpenCodeWriter.Create(DatabasePath);
        var t = _now - 30_000;
        writer.Session("ses_a", null, "/w/comet", t, t);
        var today = _now - 3_600_000;
        using var core = Core(today);
        core.Seed(_now, today, CancellationToken.None);
        Assert.False(core.ProcessPaths([Path.Combine(Root, "unrelated.txt")], _now, today));

        writer.Message("m1", "ses_a", "assistant", 1, t + 1000, t + 3000,
            OpenCodeWriter.Assistant(t + 1000, streamed: t + 3000, output: 60, finish: "stop"));
        Assert.True(core.ProcessPaths([DatabasePath + "-wal"], _now, today));
        var (snapshot, events) = core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: true);
        Assert.Equal(60, snapshot.Today.Output);
        var completed = Assert.IsType<SpeedEvent.ResponseCompleted>(Assert.Single(events));
        Assert.Equal((SourceKind.OpenCode, "comet"), (completed.Response.Source, completed.Response.Project));

        writer.Message("m2", "ses_a", "assistant", 2, t + 4000, t + 6000,
            OpenCodeWriter.Assistant(t + 4000, streamed: t + 6000, output: 30, finish: "stop"));
        Assert.True(core.PollRecent(_now));
        Assert.False(core.PollRecent(_now));
        Assert.Equal(90, core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: true).Snapshot.Today.Output);
    }

    [Fact]
    public void UnsupportedDatabasesAreReportedAndContributeNothing()
    {
        Directory.CreateDirectory(Root);
        using (var db = SqliteConnection.Open(DatabasePath, SqliteOpenMode.ReadWriteCreate))
        {
            db.Execute("CREATE TABLE session (id TEXT PRIMARY KEY)");
        }

        var today = _now - 3_600_000;
        using var core = Core(today);
        core.Seed(_now, today, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: false);

        Assert.Contains(new MonitorWarning.UnsupportedFormat(SourceKind.OpenCode), snapshot.Warnings);
        Assert.True(snapshot.Sources.Single(s => s.Kind == SourceKind.OpenCode).Available);
        Assert.Equal(OpenCodeStatus.Unsupported, core.OpenCode!.Status);
    }

    [Fact]
    public void ADatabaseThatAppearsLaterIsSeededOnRediscovery()
    {
        var today = _now - 3_600_000;
        using var core = Core(today);
        core.Seed(_now, today, CancellationToken.None);
        Assert.False(core.AnyEnabledRootAvailable);

        using var writer = OpenCodeWriter.Create(DatabasePath);
        writer.Session("ses_a", null, "/w/comet", _now - 10_000, _now - 5_000);
        writer.Message("m1", "ses_a", "assistant", 1, _now - 9_000, _now - 5_000,
            OpenCodeWriter.Assistant(_now - 9_000, streamed: _now - 6_000, output: 12, finish: "stop"));
        Assert.True(core.Rediscover(_now, today, CancellationToken.None));
        Assert.True(core.AnyEnabledRootAvailable);
        Assert.Equal(12, core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: false).Snapshot.Today.Output);
    }

    [Fact]
    public void ADatabaseClosedByOpenCodeIsReadImmutablyUntilItChanges()
    {
        var t = _now - 60_000;
        using (var writer = OpenCodeWriter.Create(DatabasePath))
        {
            writer.Session("ses_a", null, "/w/comet", t, t + 5000);
            writer.Message("m1", "ses_a", "assistant", 1, t + 1000, t + 5000,
                OpenCodeWriter.Assistant(t + 1000, streamed: t + 4000, output: 70, finish: "stop"));
        }

        OpenCodeWriter.DeleteWalFiles(DatabasePath);
        Assert.True(OpenCodeDatabase.IsClosedWalDatabase(DatabasePath));
        Assert.False(OpenCodeDatabase.IsClosedWalDatabase(FixtureLocator.PathOf("opencode/opencode.db")));
        var before = Directory.GetFiles(Root).Order(StringComparer.Ordinal).ToList();
        using var source = new OpenCodeSource(Root, new Deduper());
        source.RefreshAvailability();

        Assert.Equal((OpenCodeStatus.Ready, true), (source.Status, source.IsImmutable));
        Assert.Equal(70, Assert.Single(source.Seed(_now - 3_600_000).Responses).Response.Tokens.Output);
        Assert.Null(source.RefreshIfChanged());
        Assert.Equal(before, Directory.GetFiles(Root).Order(StringComparer.Ordinal).ToList());

        using (var writer = OpenCodeWriter.Open(DatabasePath))
        {
            writer.Message("m2", "ses_a", "assistant", 2, t + 6000, t + 9000,
                OpenCodeWriter.Assistant(t + 6000, streamed: t + 9000, output: 30, finish: "stop"));
            writer.TouchSession("ses_a", t + 9000);
            Assert.False(OpenCodeDatabase.IsClosedWalDatabase(DatabasePath));
            var update = source.RefreshIfChanged();
            Assert.NotNull(update);
            Assert.False(source.IsImmutable);
            Assert.Equal("m2", Assert.Single(update.Responses).Response.Key);
        }
    }

    [Fact]
    public void TheHistoryReadsAClosedDatabaseWithoutCreatingFiles()
    {
        using (var writer = OpenCodeWriter.Create(DatabasePath))
        {
            writer.Session("ses_a", null, "/w/comet", ClaudeLines.T0, ClaudeLines.T0 + 5000);
            writer.Message("m1", "ses_a", "assistant", 1, ClaudeLines.T0 + 1000, ClaudeLines.T0 + 5000,
                OpenCodeWriter.Assistant(ClaudeLines.T0 + 1000, streamed: ClaudeLines.T0 + 4000, output: 70, finish: "stop"));
        }

        OpenCodeWriter.DeleteWalFiles(DatabasePath);
        using var store = History.HistoryStore.Open(_temp.Combine("store", "history.sqlite"));
        var analyzer = new History.HistoryAnalyzer(store);
        var report = analyzer.Run(
            new SourceLocations(string.Empty, string.Empty, string.Empty, Root, []),
            new HashSet<SourceKind> { SourceKind.OpenCode },
            TimeZoneInfo.Utc,
            CancellationToken.None);

        Assert.Equal((1, 0), (report.SessionsAnalyzed, report.FilesUnreadable));
        Assert.Equal(70, Assert.Single(analyzer.Days()).Totals.Output);
        Assert.Equal([DatabasePath], Directory.GetFiles(Root));
    }

    [Fact]
    public void ImmutableUrisEscapeEveryUnusualCharacter()
    {
        var uri = SqliteConnection.ImmutableUri(Path.Combine(Path.GetTempPath(), "a b", "c?#%é", "opencode.db"));
        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.EndsWith("/a%20b/c%3F%23%25%C3%A9/opencode.db?mode=ro&immutable=1", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedDataCountsAndForkCopiesAreIgnored()
    {
        var session = new OpenCodeSessionInfo("s", null, "/w/x", 10_000, 20_000);
        OpenCodeRow Row(string id, long seq, string type, string data) =>
            new(id, "s", OpenCodeRow.KindOf(System.Text.Encoding.UTF8.GetBytes(type)), seq, 0, 15_000 + seq,
                OpenCodeDataReader.Read(System.Text.Encoding.UTF8.GetBytes(data)));
        var rows = new[]
        {
            Row("bad", 1, "assistant", "{not json"),
            Row("copy", 2, "assistant", OpenCodeWriter.Assistant(1000, streamed: 2000, output: 5, finish: "tool-calls")),
            Row("array", 3, "user", "[1,2]"),
        };

        var state = OpenCodeRules.Replay(session, rows);
        Assert.Equal((2L, AgentPhase.Idle, (long?)null), (state.MalformedRows, state.Phase, state.LastLineMs));
        Assert.Null(OpenCodeRules.Response(rows[1], session));
        Assert.Equal("x", state.Project);
    }

    private MonitorCore Core(long todayStartMs) => new(
        new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.OpenCode }),
        new SourceLocations(_temp.Combine("codex"), _temp.Combine("claude"), _temp.Combine("dsh"), Root, []),
        todayStartMs);
}

/// <summary>Writes an OpenCode-shaped database in WAL mode, the way OpenCode does while the monitor reads it.</summary>
internal sealed class OpenCodeWriter : IDisposable
{
    private readonly SqliteConnection _db;

    private OpenCodeWriter(SqliteConnection db)
    {
        _db = db;
    }

    public static OpenCodeWriter Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE session_v2 (id text PRIMARY KEY, project_id text NOT NULL, parent_id text, slug text NOT NULL,
              directory text NOT NULL, version text NOT NULL, time_created integer NOT NULL, time_updated integer NOT NULL);
            CREATE TABLE session_message (id text PRIMARY KEY, session_id text NOT NULL, type text NOT NULL, seq integer NOT NULL,
              time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL);
            CREATE INDEX session_message_time_created_idx ON session_message (time_created);
            """);
        return new OpenCodeWriter(db);
    }

    /// <summary>Opens an existing database again, the way OpenCode does when it starts.</summary>
    public static OpenCodeWriter Open(string path) => new(SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate));

    /// <summary>What a clean shutdown leaves behind: the database file alone.</summary>
    public static void DeleteWalFiles(string path)
    {
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    public static string Assistant(
        long created, long? streamed = null, long? completed = null, long output = 10, string? finish = null, string model = "claude-opus-5-5")
    {
        var times = $"\"created\":{created}" + (streamed is { } s ? $",\"streamed\":{s}" : "") + (completed is { } c ? $",\"completed\":{c}" : "");
        var tokens = streamed is null ? "" : $",\"tokens\":{{\"input\":3,\"output\":{output},\"reasoning\":0,\"cache\":{{\"read\":100,\"write\":0}}}}";
        var finishJson = finish is null ? "" : $",\"finish\":{JsonSerializer.Serialize(finish)}";
        return $"{{\"time\":{{{times}}},\"model\":{{\"id\":{JsonSerializer.Serialize(model)}}}{finishJson}{tokens}}}";
    }

    public void Session(string id, string? parent, string directory, long created, long updated)
    {
        using var insert = _db.Prepare(
            "INSERT INTO session_v2 VALUES (?1, 'p', ?2, 'slug', ?3, '2.0', ?4, ?5)");
        insert.Bind(1, id).Bind(2, parent).Bind(3, directory).Bind(4, created).Bind(5, updated).Run();
    }

    public void TouchSession(string id, long updated)
    {
        using var update = _db.Prepare("UPDATE session_v2 SET time_updated = ?2 WHERE id = ?1");
        update.Bind(1, id).Bind(2, updated).Run();
    }

    public void DeleteSession(string id)
    {
        using var delete = _db.Prepare("DELETE FROM session_v2 WHERE id = ?1");
        delete.Bind(1, id).Run();
    }

    public void Message(string id, string session, string type, long seq, long created, long updated, string data)
    {
        using var insert = _db.Prepare("INSERT INTO session_message VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)");
        insert.Bind(1, id).Bind(2, session).Bind(3, type).Bind(4, seq).Bind(5, created).Bind(6, updated).Bind(7, data).Run();
    }

    public void UpdateMessage(string id, long updated, string data)
    {
        using var update = _db.Prepare("UPDATE session_message SET time_updated = ?2, data = ?3 WHERE id = ?1");
        update.Bind(1, id).Bind(2, updated).Bind(3, data).Run();
    }

    public void Dispose() => _db.Dispose();
}
