using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Tests;

public sealed class TraeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly long Start = Ts.Ms("2026-10-20T00:00:00Z");
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SharedPlainEncryptedAndCommittedWalFixturesMatchIndependentExpectations()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("trae-expected.json")));
        foreach (var file in expected.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(file.GetProperty("path").GetString()!);
            var before = Directory.GetFiles(Path.GetDirectoryName(path)!).Order().ToArray();
            using var db = TraeDatabase.Open(path);
            var actual = db.Read().Select(row => row.Response(path)).ToArray();
            var rows = file.GetProperty("responses").EnumerateArray().ToArray();
            Assert.Equal(rows.Length, actual.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Equal(rows[i].GetProperty("key").GetString(), actual[i].Key);
                Assert.Equal(rows[i].GetProperty("endMs").GetInt64(), actual[i].EndMs);
                Assert.Equal(rows[i].GetProperty("input").GetInt64(), actual[i].Tokens.Input);
                Assert.Equal(rows[i].GetProperty("output").GetInt64(), actual[i].Tokens.Output);
                Assert.Equal(rows[i].GetProperty("cachedInput").GetInt64(), actual[i].Tokens.CachedInput);
                Assert.Equal(rows[i].GetProperty("cacheWriteInput").GetInt64(), actual[i].Tokens.CacheWriteInput);
                Assert.Equal(rows[i].GetProperty("reasoning").GetInt64(), actual[i].Tokens.Reasoning);
                Assert.Equal(rows[i].GetProperty("model").GetString(), actual[i].Model);
                Assert.Null(actual[i].StartMs);
            }
            Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(path)!).Order());
        }
    }

    [Fact]
    public void TamperedEncryptedPagesAndUnsupportedWalFailClosed()
    {
        var bytes = File.ReadAllBytes(FixtureLocator.PathOf("trae/encrypted/database.db"));
        bytes[100] ^= 1;
        Assert.Throws<InvalidDataException>(() => TraeSnapshot.Decode(bytes, []));
        bytes = File.ReadAllBytes(FixtureLocator.PathOf("trae/encrypted/database.db"));
        Assert.Throws<IOException>(() => TraeSnapshot.Decode(bytes, new byte[20]));
        var wal = File.ReadAllBytes(FixtureLocator.PathOf("trae/wal/database.db-wal"));
        wal[24] ^= 1;
        Assert.Throws<InvalidDataException>(() => TraeSnapshot.Decode(bytes, wal));
    }

    [Fact]
    public void SourceDeduplicatesAndRefreshesCorrectedUsageWithoutInventingSpeed()
    {
        var path = Copy("plain");
        var source = new TraeSource(path, new Deduper());
        var first = source.Seed(Start);
        Assert.Equal(3, first.Responses.Count);
        Assert.True(source.PartialHistory);
        Assert.Equal(2, source.ActiveSessionCount(Start));
        Assert.False(source.Refresh(true).Changed);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
            writer.Execute("UPDATE chat_turn SET context=json_set(context,'$.token_usage.completion_tokens',150,'$.token_usage.total_tokens',950) WHERE turn_id='t-two'");
        var update = source.Refresh(true);
        var response = Assert.Single(update.Responses);
        Assert.Equal(150, response.Response.Tokens.Output);
        Assert.False(response.Completion);
        Assert.Null(response.Response.StartMs);
        Assert.False(source.Unsupported);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
            writer.Execute("UPDATE chat_turn SET deleted_at=1 WHERE session_id='s-main'");
        Assert.Single(source.Refresh(true).Removed);
        Assert.Equal(1, source.ActiveSessionCount(Start));
    }

    [Fact]
    public void MonitorAndPersistedHistoryKeepPartialUsageAndExcludeMeasuredSpeed()
    {
        var path = Copy("encrypted");
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [path]);
        var enabled = new HashSet<SourceKind> { SourceKind.Trae };
        using (var core = new MonitorCore(new MonitorConfiguration(enabled), locations, Start))
        {
            core.Seed(Start + 50_000_000, Start, CancellationToken.None);
            var (snapshot, _) = core.BuildSnapshot(Start + 50_000_000, Start, MonitorState.Running, false);
            Assert.Equal(3, snapshot.Today.Requests);
            Assert.Equal(340, snapshot.Today.Output);
            Assert.Null(snapshot.AverageToday);
            Assert.Equal(0, snapshot.Live.Output);
            Assert.Contains(snapshot.Warnings, warning => warning is MonitorWarning.PartialHistory);
            Assert.Equal(2, core.SessionCount(SourceKind.Trae, Start));
        }
        var storePath = _temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(storePath))
        {
            var analyzer = new HistoryAnalyzer(store);
            Assert.Equal(0, analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None).FilesUnreadable);
            var day = Assert.Single(analyzer.Days());
            Assert.Equal((3, 340), (day.Totals.Requests, day.Totals.Output));
            Assert.Null(day.AverageSpeed);
            Assert.Equal(0, analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None).FilesPlanned);
        }
        using var reopened = HistoryStore.Open(storePath);
        Assert.Equal(340, Assert.Single(new HistoryAnalyzer(reopened).Days()).Totals.Output);
    }

    [Fact]
    public void RootsPreferAgentStoreAndAcceptExplicitDatabaseAndDisabledSource()
    {
        var app = _temp.Combine("Trae CN");
        var chat = _temp.WriteFile("Trae CN/ModularData/ai-chat/database.db", "");
        Assert.Equal(chat, TraePaths.DatabasePath(app));
        var agent = _temp.WriteFile("Trae CN/ModularData/ai-agent/database.db", "");
        Assert.Equal(agent, TraePaths.DatabasePath(app));
        Assert.Equal(chat, TraePaths.DatabasePath(chat));
        Assert.Equal(agent, TraePaths.DatabasePath(Path.GetDirectoryName(agent)!));
        var configuration = new MonitorConfiguration(new HashSet<SourceKind>(), TraeRoot: app);
        Assert.Equal([app], SourceLocations.Resolve(configuration).TraeStores);
        using var core = new MonitorCore(configuration, new SourceLocations("", "", "", "", [], TraeStores: [app]), Start);
        core.Seed(Start + 1000, Start, CancellationToken.None);
        Assert.False(core.AnyEnabledRootAvailable);
        Assert.DoesNotContain(core.WatchRoots, root => root.Family == RootFamily.Trae);
    }

    [Fact]
    public void ActiveRollbackJournalIsNeverReadAndPlainSnapshotCannotBeModified()
    {
        var path = Copy("plain");
        File.WriteAllBytes(path + "-journal", [1, 2, 3]);
        Assert.Throws<IOException>(() => TraeDatabase.Open(path));
        File.WriteAllBytes(path + "-journal", new byte[512]);
        using (var readable = TraeDatabase.Open(path)) Assert.Equal(3, readable.Read().Count);
        File.Delete(path + "-journal");
        using var db = SqliteConnection.FromSnapshot(TraeSnapshot.Read(path));
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM chat_turn"));
    }

    [Fact]
    public void CopiedStoresDoNotDuplicateSessionsOrUsage()
    {
        var first = Copy("plain");
        var copy = Copy("encrypted");
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [first, copy]);
        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Trae }), locations, Start);
        core.Seed(Start + 50_000_000, Start, CancellationToken.None);
        var snapshot = core.BuildSnapshot(Start + 50_000_000, Start, MonitorState.Running, false).Snapshot;
        Assert.Equal(340, snapshot.Today.Output);
        Assert.Equal(2, core.SessionCount(SourceKind.Trae, Start));
    }

    [Fact]
    public void SupportedStoreWarnsAboutPartialHistoryEvenWithoutRecentUsage()
    {
        var source = new TraeSource(Copy("encrypted"), new Deduper());
        Assert.Empty(source.Seed(Start + 10 * 86_400_000).Responses);
        Assert.True(source.PartialHistory);
        Assert.False(source.Unsupported);
    }

    [Fact]
    public void PersistedHistoryNeverCountsACopiedStoreAgainAfterRestart()
    {
        var first = Copy("plain");
        var copy = Copy("encrypted");
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [first]);
        var enabled = new HashSet<SourceKind> { SourceKind.Trae };
        var storePath = _temp.Combine("copied-history.sqlite");
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal(340, Assert.Single(history.Days()).Totals.Output);
        }
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations with { TraeStores = [first, copy] }, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            var day = Assert.Single(history.Days());
            Assert.Equal((3, 340), (day.Totals.Requests, day.Totals.Output));
        }
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations with { TraeStores = [copy] }, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            var day = Assert.Single(history.Days());
            Assert.Equal((3, 340), (day.Totals.Requests, day.Totals.Output));
        }
    }

    private string Copy(string name)
    {
        var path = _temp.Combine(name, "database.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixtureLocator.PathOf($"trae/{name}/database.db"), path);
        return path;
    }
}
