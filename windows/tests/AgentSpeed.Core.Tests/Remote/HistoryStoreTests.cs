using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteHistoryLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The side tables of REMOTE 8.3 and the batch rules of the sync (8.2, 8.3).</summary>
public sealed class RemoteHistoryStoreTests : IDisposable
{
    private static readonly IReadOnlySet<SourceKind> Codex = new HashSet<SourceKind> { SourceKind.Codex };
    private static readonly string[] RemoteTables = ["remote_hosts", "remote_days", "remote_perf"];

    private readonly TempDirectory _temp = new();
    private readonly HistoryStore _store;

    public RemoteHistoryStoreTests()
    {
        _store = HistoryStore.Open(_temp.Combine("history.sqlite"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _temp.Dispose();
    }

    private RemoteHistoryLedger Ledger()
    {
        var ledger = new RemoteHistoryLedger(zone: static () => "Asia/Taipei");
        ledger.Load(_store);
        return ledger;
    }

    private static List<DailyUsage> Usage(RemoteHistoryLedger ledger, IReadOnlySet<SourceKind>? sources = null) =>
        [.. ledger.Rows.Usage(sources ?? Codex, _ => null).OrderBy(d => d.Day, StringComparer.Ordinal)];

    private static HashSet<string> Tables(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var rows = connection.Prepare("SELECT name FROM sqlite_master WHERE type = 'table'");
        while (rows.Step())
        {
            names.Add(rows.GetString(0)!);
        }

        return names;
    }

    [Fact]
    public void SideTablesAreCreatedOnAnExistingStoreWithoutChangingItsVersion()
    {
        var path = _temp.Combine("existing", "history.sqlite");
        using (var store = HistoryStore.Open(path))
        {
            store.Connection.Execute("DROP TABLE remote_hosts; DROP TABLE remote_days; DROP TABLE remote_perf;");
            Assert.DoesNotContain(Tables(store.Connection), t => RemoteTables.Contains(t));
        }

        using var reopened = HistoryStore.Open(path);
        Assert.Superset(RemoteTables.ToHashSet(StringComparer.Ordinal), Tables(reopened.Connection));
        Assert.Equal(HistoryStore.SchemaVersion, reopened.Connection.ScalarInt64("PRAGMA user_version"));
    }

    [Fact]
    public void ABatchCommitsAtHistoryDone()
    {
        var ledger = Ledger();
        Assert.Equal(RemoteHistoryEffect.None, ledger.Apply(_store, L.Status(RemoteStatus.Connected), L.Event(L.Day("2026-11-02")),
            L.Event(L.Perf("2026-11-02")), L.Event(L.Day("2026-11-03"))));
        Assert.Equal(0, L.Count(_store, "remote_days"));
        Assert.True(ledger.Rows.IsEmpty);

        Assert.Equal(RemoteHistoryEffect.Changed, ledger.Receive(L.Event(L.Done), _store));
        Assert.Equal(2, L.Count(_store, "remote_days"));
        Assert.Equal(1, L.Count(_store, "remote_perf"));
        var days = Usage(ledger);
        Assert.Equal(2, days.Count);
        Assert.All(days, d => Assert.Equal((L.Host, 340L), (d.Host, d.Totals.Output)));
        Assert.Equal([new PerfRecordKey("2026-11-02", SourceKind.Codex, "gpt-5.5")],
            ledger.Rows.PerfRecords(Codex).Select(r => new PerfRecordKey(r.Day, r.Source, r.Model)));

        // A reopened ledger reads the same rows back.
        var reloaded = Ledger();
        Assert.Equal(days.Select(L.Describe), Usage(reloaded).Select(L.Describe));
        Assert.Equal(ledger.Rows.Digests(L.Host), reloaded.Rows.Digests(L.Host));
    }

    [Fact]
    public void AConnectionChangeRollsTheBatchBack()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-01"), connection: 1), L.Event(L.Day("2026-11-02"), connection: 2),
            L.Event(L.Done, connection: 2));
        Assert.Equal(["2026-11-02/codex"], ledger.Rows.Digests(L.Host).Keys);

        // A done of the old connection after the new one started commits nothing.
        ledger.Apply(_store, L.Event(L.Day("2026-11-03"), connection: 3), L.Event(L.Done, connection: 2));
        Assert.Equal(["2026-11-02/codex"], ledger.Rows.Digests(L.Host).Keys);
    }

    [Fact]
    public void AStatusLeavingConnectedRollsTheBatchBack()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Status(RemoteStatus.Connected), L.Event(L.Day("2026-11-01")), L.Status(RemoteStatus.Unreachable),
            L.Event(L.Done));
        Assert.True(ledger.Rows.IsEmpty);
        Assert.Equal(0, L.Count(_store, "remote_days"));

        // A repeated `connected` (a new clock offset) keeps it; a failed analysis drops it.
        ledger.Apply(_store, L.Status(RemoteStatus.Connected), L.Event(L.Day("2026-11-01")), L.Status(RemoteStatus.Connected),
            L.Event(L.Done));
        Assert.Single(ledger.Rows.Digests(L.Host));
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Failed), L.Event(L.Done));
        Assert.Single(ledger.Rows.Digests(L.Host));
    }

    [Fact]
    public void ADayReplacesItsRowAndItsRecords()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Perf("2026-11-02", model: "a")),
            L.Event(L.Perf("2026-11-02", model: "b")), L.Event(L.Day("2026-11-03")),
            L.Event(L.Perf("2026-11-03", model: "a")), L.Event(L.Done));
        Assert.Equal(3, L.Count(_store, "remote_perf"));

        ledger.Apply(_store, L.Event(L.Day("2026-11-02", aggregate: L.Aggregate(output: 999), digest: "0123456789abcdef")),
            L.Event(L.Perf("2026-11-02", model: "b", record: L.Record(n: 9))), L.Event(L.Done));
        Assert.Equal(["2026-11-02 b 9", "2026-11-03 a 4"], ledger.Rows.PerfRecords(Codex).Select(r => $"{r.Day} {r.Model} {r.Sums.N}"));
        Assert.Equal(2, L.Count(_store, "remote_perf"));
        Assert.Equal(L.Digests(("2026-11-02/codex", "0123456789abcdef"), ("2026-11-03/codex", "aaaaaaaaaaaaaaa3")),
            ledger.Rows.Digests(L.Host));
        Assert.Equal(999, Usage(ledger).Single(d => d.Day == "2026-11-02").Totals.Output);
    }

    [Fact]
    public void RecordsOnlyFollowTheirOwnDay()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Perf("2026-11-01")), L.Event(L.Day("2026-11-02")), L.Event(L.Perf("2026-11-01")),
            L.Event(L.Perf("2026-11-02", SourceKind.ClaudeCode)), L.Event(L.Done));
        Assert.Empty(ledger.Rows.PerfRecords(Enum.GetValues<SourceKind>().ToHashSet()));
    }

    [Fact]
    public void APerformanceOnlyDayTravels()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-03", aggregate: L.EmptyAggregate)), L.Event(L.Perf("2026-11-03")), L.Event(L.Done));
        Assert.Single(ledger.Rows.PerfRecords(Codex));
        Assert.Equal(TokenTotals.Zero, Assert.Single(Usage(ledger)).Totals);
    }

    [Fact]
    public void HistoryDueSendsTheStoredDigests()
    {
        var ledger = Ledger();
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests()), ledger.Receive(L.Due(), _store));
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Day("2026-11-03", SourceKind.ClaudeCode)),
            L.Event(L.Day("2026-11-03"), host: L.OtherHost), L.Event(L.Done), L.Event(L.Done, host: L.OtherHost));
        var expected = _store.RemoteDayRows().Where(r => r.Host == L.Host)
            .ToDictionary(r => $"{r.Day}/{r.Source}", r => r.Digest, StringComparer.Ordinal);
        Assert.Equal(2, expected.Count);
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, expected), ledger.Receive(L.Due(), _store));
        Assert.Equal(RemoteHistoryEffect.Request(L.OtherHost, L.Digests(("2026-11-03/codex", "aaaaaaaaaaaaaaa3"))),
            ledger.Receive(L.Due(L.OtherHost), _store));
    }

    [Fact]
    public void ADayTheRelayDidNotSendIsNeverDeleted()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-01")), L.Event(L.Day("2026-11-02")), L.Event(L.Done));
        // The relay's cache was deleted: it now only knows the second day, which changed.
        ledger.Apply(_store, L.Event(L.Day("2026-11-02", aggregate: L.Aggregate(output: 5), digest: "bbbbbbbbbbbbbbbb")), L.Event(L.Done));
        // An answer without days changes nothing either.
        Assert.Equal(RemoteHistoryEffect.None, ledger.Receive(L.Event(L.Done), _store));
        Assert.Equal(L.Digests(("2026-11-01/codex", "aaaaaaaaaaaaaaa1"), ("2026-11-02/codex", "bbbbbbbbbbbbbbbb")),
            ledger.Rows.Digests(L.Host));
        Assert.Equal(2, L.Count(_store, "remote_days"));
    }

    [Fact]
    public void DeletingAHostRemovesOnlyItsRows()
    {
        var ledger = Ledger();
        ledger.SetHosts(L.Digests((L.Host, "devbox"), (L.OtherHost, "gpu")), _store);
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Perf("2026-11-02")),
            L.Event(L.Day("2026-11-02"), host: L.OtherHost), L.Event(L.Done), L.Event(L.Done, host: L.OtherHost));
        Assert.Equal(L.Digests((L.Host, "devbox"), (L.OtherHost, "gpu")), ledger.Rows.Names);

        Assert.Equal(RemoteHistoryEffect.Changed, ledger.DeleteHost(L.Host, _store));
        Assert.Equal([L.OtherHost], ledger.Rows.Hosts);
        Assert.Equal(L.Digests((L.OtherHost, "gpu")), ledger.Rows.Names);
        Assert.Equal([L.OtherHost], _store.RemoteDayRows().Select(r => r.Host));
        Assert.Equal(0, L.Count(_store, "remote_perf"));
        Assert.Equal([L.OtherHost], _store.RemoteHostNames().Keys);
        Assert.Equal(RemoteHistoryEffect.None, ledger.DeleteHost(L.Host, _store));
    }

    [Fact]
    public void HostsAreRenamedAndRemovedOnesKeepTheirName()
    {
        var ledger = Ledger();
        ledger.SetHosts(L.Digests((L.Host, "devbox")), _store);
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Done));
        Assert.Equal(RemoteHistoryEffect.Changed, ledger.SetHosts(L.Digests((L.Host, "build server")), _store));
        Assert.Equal("build server", _store.RemoteHostNames()[L.Host]);
        // Removed from the settings: the rows and the last name stay.
        Assert.Equal(RemoteHistoryEffect.None, ledger.SetHosts(L.Digests(), _store));
        Assert.Equal(L.Digests((L.Host, "build server")), ledger.Rows.Names);
        Assert.Single(ledger.Rows.Digests(L.Host));
    }

    [Fact]
    public void AHostSyncedBeforeItHadANameTakesTheSettingsName()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-02")), L.Event(L.Done));
        Assert.Empty(ledger.Rows.Names);
        Assert.Null(_store.RemoteHostNames()[L.Host]);
        Assert.Equal(RemoteHistoryEffect.Changed, ledger.SetHosts(L.Digests((L.Host, "devbox")), _store));
        Assert.Equal(L.Digests((L.Host, "devbox")), Ledger().Rows.Names);
    }

    [Fact]
    public void UnreadableStoredRowsAreLeftOutSoTheNextSyncSendsThemAgain()
    {
        var ledger = Ledger();
        ledger.Apply(_store, L.Event(L.Day("2026-11-01")), L.Event(L.Perf("2026-11-01")), L.Event(L.Day("2026-11-02")),
            L.Event(L.Perf("2026-11-02")), L.Event(L.Done));
        _store.Connection.Execute("""
            UPDATE remote_days SET data = '{"requests":1}' WHERE day = '2026-11-01';
            UPDATE remote_perf SET data = 'not json' WHERE day = '2026-11-02';
            INSERT INTO remote_days VALUES ('8C0D6B0A-1111-4222-8333-944455556666', '2026-11-03', 'unknownSource', '{}', 'cccccccccccccccc');
            """);

        var reloaded = Ledger();
        Assert.Equal(["2026-11-02"], Usage(reloaded, Enum.GetValues<SourceKind>().ToHashSet()).Select(d => d.Day));
        Assert.Equal(["2026-11-01"], reloaded.Rows.PerfRecords(Codex).Select(r => r.Day));
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests(("2026-11-02/codex", "aaaaaaaaaaaaaaa2"))),
            reloaded.Receive(L.Due(), _store));
    }

    [Fact]
    public void ALargeBatchStoresItsCompleteDaysEarly()
    {
        var batch = new RemoteHistoryBatch(L.Host, 1);
        foreach (var day in (string[])["2026-11-01", "2026-11-02", "2026-11-03"])
        {
            batch.Add(L.DayLine(day));
        }

        batch.Add(L.PerfLine("2026-11-03"));
        var early = batch.TakeCompleted();
        Assert.Equal(["2026-11-01", "2026-11-02"], early.Select(d => d.Day));
        Assert.Equal("2026-11-03", Assert.Single(batch.Days).Day);
        Assert.Single(batch.Days[0].Perf);
        Assert.Equal(batch.Days[0].ByteCount, batch.ByteCount);
    }
}
