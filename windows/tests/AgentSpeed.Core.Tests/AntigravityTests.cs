using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;
using System.Text;

namespace AgentSpeed.Core.Tests;

public sealed class AntigravityTests
{
    [Theory]
    [InlineData("Antigravity")]
    [InlineData("AntigravityCli")]
    public void DefaultConfigurationEnablesBothAntigravityClients(string source)
    {
        Assert.Contains(source, MonitorConfiguration.Default.EnabledSources.Select(s => s.ToString()));
    }

    [Theory]
    [InlineData(SourceKind.Antigravity)]
    [InlineData(SourceKind.AntigravityCli)]
    public void ReadsUsageAndTtftPlusStreamingWithoutDoubleCountingThinking(SourceKind kind)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.db");
        using var db = Database(path);
        Generation(db, 1, 1_790_000_010_000);
        var read = AntigravityDatabase.Read(path, kind);
        var response = Assert.Single(read.Responses);
        Assert.Equal(kind, response.Source);
        Assert.Equal(new TokenCounts(1520, 500, 20, 300, 50), response.Tokens);
        Assert.Equal(1_790_000_007_000, response.StartMs);
        Assert.Equal(1_790_000_010_000, response.EndMs);
        Assert.Equal("gemini-test", response.Model);
        Assert.Equal(0, read.Malformed);
    }

    [Fact]
    public void MissingDurationKeepsUsageButNeverInventsSpeedAndTornMetadataIsSkipped()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.db");
        using var db = Database(path);
        Generation(db, 1, 1_790_000_010_000, duration: false);
        using var bad = db.Prepare("INSERT INTO gen_metadata VALUES (2, ?1)");
        bad.BindBlob(1, new byte[] { 10, 127, 1 }).Run();
        var read = AntigravityDatabase.Read(path, SourceKind.Antigravity);
        Assert.Null(Assert.Single(read.Responses).StartMs);
        Assert.Equal(1, read.Malformed);
    }

    [Fact]
    public void PresentZeroTtftIsValidAndDoesNotLoseMeasuredSpeed()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.db");
        using var db = Database(path);
        Generation(db, 1, 1_790_000_010_000);
        var chat = Join(Bytes(4, Join(Number(2, 1000), Number(3, 300))), Bytes(11, []), Bytes(12, Number(1, 2)));
        using var update = db.Prepare("UPDATE gen_metadata SET data=?1 WHERE idx=1");
        update.BindBlob(1, Join(Bytes(1, chat), Number(2, 1))).Run();
        Assert.Equal(1_790_000_008_000, Assert.Single(AntigravityDatabase.Read(path, SourceKind.Antigravity).Responses).StartMs);
    }

    [Fact]
    public void UnreadableDatabaseIsReportedInsteadOfShowingEmptySuccess()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("broken.db"), "not sqlite");
        var config = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity });
        using var core = new MonitorCore(config, new SourceLocations("", "", "", "", [], temp.Path), 0);
        core.Seed(1000, 0, CancellationToken.None);
        Assert.Equal(1, core.UnreadableFileCount);
        Assert.Contains(new MonitorWarning.UnreadableFiles(1), core.Warnings());
    }

    [Fact]
    public void PollObservesWalAppendsAndDoesNotDuplicateRequests()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.db");
        using var db = Database(path);
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Generation(db, 1, now - 10_000);
        var config = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity });
        var locations = new SourceLocations("", "", "", "", [], temp.Path);
        using var core = new MonitorCore(config, locations, now - 60_000);
        core.Seed(now, now - 60_000, CancellationToken.None);
        Assert.Equal(1, Snapshot(core, now).Today.Requests);
        Assert.False(core.PollRecent(now));
        Generation(db, 2, now - 2000);
        Assert.True(core.PollRecent(now));
        var snap = Snapshot(core, now);
        Assert.Equal(2, snap.Today.Requests);
        Assert.Equal(600, snap.Today.Output);
        Assert.Equal(100d, snap.AverageToday);
        Assert.Equal(1, snap.Sources.Single(s => s.Kind == SourceKind.Antigravity).SessionCount);
        Assert.False(core.PollRecent(now));
        Assert.Equal(2, Snapshot(core, now).Today.Requests);
    }

    [Fact]
    public void DisabledSourceDoesNotReadDatabase()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("broken.db"), "not sqlite");
        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind>()),
            new SourceLocations("", "", "", "", [], temp.Path), 0);
        core.Seed(1000, 0, CancellationToken.None);
        Assert.Equal(0, core.UnreadableFileCount);
    }

    [Fact]
    public void HistoryIsPersistedAndRepeatedAnalysisDoesNotDoubleCount()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("conversations");
        Directory.CreateDirectory(root);
        using var db = Database(Path.Combine(root, "session.db"));
        Generation(db, 1, 1_790_000_010_000);
        using var store = HistoryStore.Open(temp.Combine("history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var roots = new SourceLocations("", "", "", "", [], root);
        var enabled = new HashSet<SourceKind> { SourceKind.Antigravity };
        analyzer.Run(roots, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        analyzer.Run(roots, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        var day = Assert.Single(analyzer.Days());
        Assert.Equal(SourceKind.Antigravity, day.Source);
        Assert.Equal(300, day.Totals.Output);
        Assert.Equal(1, day.Totals.Requests);
    }

    [Fact]
    public void IncompletePlannerIsGeneratingWithoutCountingARequest()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.db");
        using var db = Database(path);
        using var step = db.Prepare("INSERT INTO steps VALUES (0, 15, 8, ?1)");
        step.BindBlob(1, Bytes(1, Timestamp(1_790_000_000_000))).Run();
        var read = AntigravityDatabase.Read(path, SourceKind.AntigravityCli);
        Assert.Empty(read.Responses);
        Assert.Equal(AgentPhase.Generating, read.Agent.Phase);
        Assert.Equal(1_790_000_000_000, read.Agent.InFlightStartMs);
    }

    [Theory]
    [InlineData(SourceKind.Antigravity)]
    [InlineData(SourceKind.AntigravityCli)]
    public void RunningSummaryShowsLiveSpeedBeforePlannerIsPersistedAndIdleStopsIt(SourceKind kind)
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("conversations");
        Directory.CreateDirectory(root);
        using var db = Database(Path.Combine(root, "session.db"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Generation(db, 1, now - 20_000);
        using var summaries = SummaryDatabase(temp.Combine("conversation_summaries.db"));
        Summary(summaries, "CASCADE_RUN_STATUS_IDLE", now - 20_000);
        var config = new MonitorConfiguration(new HashSet<SourceKind> { kind });
        var locations = new SourceLocations("", "", "", "", [],
            kind == SourceKind.Antigravity ? root : "", kind == SourceKind.AntigravityCli ? root : "");
        using var core = new MonitorCore(config, locations, now - 60_000);
        core.Seed(now, now - 60_000, CancellationToken.None);
        Assert.Equal(0, Snapshot(core, now).Live.Output);

        // Only summary state changes: the conversation DB still contains the previous completed answer.
        Summary(summaries, "CASCADE_RUN_STATUS_RUNNING", now - 5000);
        Assert.True(core.PollRecent(now));
        var generating = Snapshot(core, now);
        Assert.Equal(1, generating.GeneratingCount);
        Assert.Equal(100, generating.Live.Output);
        Assert.Equal(1, generating.LiveEstimatedShare);
        Assert.Equal(1, generating.Today.Requests);

        Summary(summaries, "CASCADE_RUN_STATUS_IDLE", now);
        Assert.True(core.PollRecent(now));
        Assert.Equal(0, Snapshot(core, now).Live.Output);
        Assert.Equal(0, Snapshot(core, now).GeneratingCount);
    }

    [Fact]
    public void LosingSummaryStopsCachedGenerationInsteadOfLeavingTheGaugeRunning()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("conversations");
        Directory.CreateDirectory(root);
        using var db = Database(Path.Combine(root, "session.db"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Generation(db, 1, now - 20_000);
        var summaryPath = temp.Combine("conversation_summaries.db");
        using (var summaries = SummaryDatabase(summaryPath)) Summary(summaries, "CASCADE_RUN_STATUS_RUNNING", now - 5000);
        var config = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity });
        using var core = new MonitorCore(config, new SourceLocations("", "", "", "", [], root), now - 60_000);
        core.Seed(now, now - 60_000, CancellationToken.None);
        Assert.True(Snapshot(core, now).Live.Output > 0);
        File.Delete(summaryPath);
        Assert.True(core.PollRecent(now));
        Assert.Equal(0, Snapshot(core, now).Live.Output);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void RunningSummaryDoesNotTurnAnExecutingOrWaitingToolIntoGeneration(int status)
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("conversations");
        Directory.CreateDirectory(root);
        using var db = Database(Path.Combine(root, "session.db"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Generation(db, 1, now - 20_000);
        using var step = db.Prepare("INSERT INTO steps VALUES (2, 132, ?1, ?2)");
        step.Bind(1, (long)status).BindBlob(2, Bytes(1, Timestamp(now - 5000))).Run();
        using var summaries = SummaryDatabase(temp.Combine("conversation_summaries.db"));
        Summary(summaries, "CASCADE_RUN_STATUS_RUNNING", now - 5000);
        var config = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Antigravity });
        using var core = new MonitorCore(config, new SourceLocations("", "", "", "", [], root), now - 60_000);
        core.Seed(now, now - 60_000, CancellationToken.None);
        var snapshot = Snapshot(core, now);
        Assert.Equal(0, snapshot.GeneratingCount);
        Assert.Equal(1, snapshot.ToolsCount);
        Assert.Equal(0, snapshot.Live.Output);
    }

    private static SqliteConnection SummaryDatabase(string path)
    {
        var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute("CREATE TABLE conversation_summaries (conversation_id TEXT PRIMARY KEY, status TEXT, last_modified_time TEXT, last_user_input_time TEXT, killed INTEGER)");
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0");
        return db;
    }

    private static void Summary(SqliteConnection db, string status, long ms)
    {
        using var statement = db.Prepare("INSERT OR REPLACE INTO conversation_summaries VALUES ('session', ?1, ?2, ?2, 0)");
        statement.Bind(1, status).Bind(2, DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o")).Run();
    }

    private static SpeedSnapshot Snapshot(MonitorCore core, long now) =>
        core.BuildSnapshot(now, now - 60_000, MonitorState.Running, observeLive: false).Snapshot;

    internal static SqliteConnection Database(string path)
    {
        var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute("CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, status INTEGER, metadata BLOB);" +
            "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB)");
        return db;
    }

    internal static void Generation(SqliteConnection db, int idx, long end, bool duration = true)
    {
        var meta = Join(Bytes(1, Timestamp(end - 3000)), Bytes(7, Timestamp(end)), Bytes(8, Timestamp(end)));
        using var step = db.Prepare("INSERT INTO steps VALUES (?1, 15, 3, ?2)");
        step.Bind(1, (long)idx).BindBlob(2, meta).Run();
        var usage = Join(Number(2, 1000), Number(3, 300), Number(4, 20), Number(5, 500), Number(9, 50));
        var chat = Join(Bytes(4, usage), Bytes(19, Encoding.UTF8.GetBytes("gemini-test")));
        if (duration) chat = Join(chat, Bytes(11, Number(1, 1)), Bytes(12, Number(1, 2)));
        using var generation = db.Prepare("INSERT INTO gen_metadata VALUES (?1, ?2)");
        generation.Bind(1, (long)idx).BindBlob(2, Join(Bytes(1, chat), Bytes(2, Varint((ulong)idx)))).Run();
    }

    private static byte[] Timestamp(long ms) => Join(Number(1, (ulong)(ms / 1000)), Number(2, (ulong)(ms % 1000 * 1_000_000)));
    private static byte[] Number(int field, ulong value) => Join(Varint((ulong)(field << 3)), Varint(value));
    private static byte[] Bytes(int field, byte[] bytes) => Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)bytes.Length), bytes);
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value > 127) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
        bytes.Add((byte)value);
        return [.. bytes];
    }
}
