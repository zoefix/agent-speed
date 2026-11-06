using AgentSpeed.Core.History;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteHistoryLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 8.3 <i>Time zone</i> and <i>Bounds</i>, and the staging of a large batch.</summary>
public sealed class RemoteHistoryZoneTests : IDisposable
{
    private static readonly IReadOnlySet<SourceKind> Codex = new HashSet<SourceKind> { SourceKind.Codex };

    private readonly TempDirectory _temp = new();
    private readonly HistoryStore _store;
    private string _zone = "Asia/Taipei";

    public RemoteHistoryZoneTests()
    {
        _store = HistoryStore.Open(_temp.Combine("history.sqlite"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _temp.Dispose();
    }

    private RemoteHistoryLedger Ledger(RemoteHistoryLimits? limits = null, long flushBytes = RemoteHistorySyncState.DefaultFlushBytes)
    {
        var ledger = new RemoteHistoryLedger(limits, flushBytes, () => _zone);
        ledger.Load(_store);
        return ledger;
    }

    private RemoteHistoryEffect Sync(RemoteHistoryLedger ledger, RelayMessage[] messages, string host = L.Host)
    {
        ledger.Receive(L.Due(host), _store);
        return ledger.Apply(_store, [.. messages.Select(m => L.Event(m, host)), L.Event(L.Done, host)]);
    }

    [Fact]
    public void TheZoneColumnIsAddedToAnEarlierTable()
    {
        var path = _temp.Combine("earlier", "history.sqlite");
        using (var store = HistoryStore.Open(path))
        {
            store.Connection.Execute($"""
                DROP TABLE remote_hosts; CREATE TABLE remote_hosts (id TEXT PRIMARY KEY, name TEXT);
                INSERT INTO remote_hosts VALUES ('{L.Host}', 'devbox');
                """);
        }

        using var reopened = HistoryStore.Open(path);
        Assert.Equal(new Dictionary<string, string?> { [L.Host] = "devbox" }, reopened.RemoteHostNames());
        Assert.Empty(reopened.RemoteHostZones());
        reopened.Connection.Execute("UPDATE remote_hosts SET zone = 'UTC'");
        Assert.Equal(L.Digests((L.Host, "UTC")), reopened.RemoteHostZones());
    }

    [Fact]
    public void RowsKeepTheZoneTheyWereComputedIn()
    {
        var ledger = Ledger();
        Sync(ledger, [L.Day("2026-11-01"), L.Day("2026-11-02")]);
        Assert.Equal(L.Digests((L.Host, "Asia/Taipei")), _store.RemoteHostZones());
        Assert.Equal(L.Digests((L.Host, "Asia/Taipei")), Ledger().Rows.Zones);
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests(("2026-11-01/codex", "aaaaaaaaaaaaaaa1"), ("2026-11-02/codex", "aaaaaaaaaaaaaaa2"))),
            ledger.Receive(L.Due(), _store));
    }

    [Fact]
    public void ASyncInAnotherZoneSendsNoDigestsAndReplacesEveryRow()
    {
        var ledger = Ledger();
        Sync(ledger, [L.Day("2026-11-01"), L.Perf("2026-11-01"), L.Day("2026-11-02")]);
        Sync(ledger, [L.Day("2026-11-02")], L.OtherHost);

        _zone = "America/Los_Angeles";
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests()), ledger.Receive(L.Due(), _store));
        // The relay relabelled its days: the second one moved, the first one is gone with its logs.
        var effect = ledger.Apply(_store, L.Event(L.Day("2026-11-03", digest: "cccccccccccccccc")), L.Event(L.Done));
        Assert.Equal(RemoteHistoryEffect.Changed, effect);
        Assert.Equal(L.Digests(("2026-11-03/codex", "cccccccccccccccc")), ledger.Rows.Digests(L.Host));
        Assert.Equal(["2026-11-03"], _store.RemoteDayRows().Where(r => r.Host == L.Host).Select(r => r.Day));
        Assert.Equal(0, L.Count(_store, "remote_perf"));
        Assert.Equal("America/Los_Angeles", _store.RemoteHostZones()[L.Host]);
        // Another host is only replaced at its own sync.
        Assert.Single(ledger.Rows.Digests(L.OtherHost));
        Assert.Equal("Asia/Taipei", _store.RemoteHostZones()[L.OtherHost]);

        // Back in step: digests again, and the next batch merges.
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests(("2026-11-03/codex", "cccccccccccccccc"))), ledger.Receive(L.Due(), _store));
        ledger.Apply(_store, L.Event(L.Day("2026-11-04")), L.Event(L.Done));
        Assert.Equal(2, ledger.Rows.Digests(L.Host).Count);
    }

    [Fact]
    public void AnEmptyAnswerInAnotherZoneLeavesTheHostWithoutRows()
    {
        var ledger = Ledger();
        Sync(ledger, [L.Day("2026-11-01")]);
        _zone = "Europe/Berlin";
        Assert.Equal(RemoteHistoryEffect.Changed, Sync(ledger, []));
        Assert.True(ledger.Rows.IsEmpty);
        Assert.Empty(ledger.Rows.Zones);
        Assert.Empty(_store.RemoteHostNames());
    }

    [Fact]
    public void RowsStoredWithoutAZoneTakeTheNextSyncsZone()
    {
        var ledger = Ledger();
        Sync(ledger, [L.Day("2026-11-01")]);
        _store.Connection.Execute("UPDATE remote_hosts SET zone = NULL");
        var reloaded = Ledger();
        Assert.Equal(RemoteHistoryEffect.Request(L.Host, L.Digests(("2026-11-01/codex", "aaaaaaaaaaaaaaa1"))), reloaded.Receive(L.Due(), _store));
        Assert.Equal(RemoteHistoryEffect.Changed, reloaded.Receive(L.Event(L.Done), _store));
        Assert.Equal(L.Digests((L.Host, "Asia/Taipei")), _store.RemoteHostZones());
        Assert.Single(reloaded.Rows.Digests(L.Host));
    }

    [Fact]
    public void ABatchBeyondTheBoundsIsDiscardedUntilASyncSucceeds()
    {
        var ledger = Ledger(new RemoteHistoryLimits(Days: 3, Perf: 2));
        Sync(ledger, [L.Day("2026-11-01"), L.Perf("2026-11-01"), L.Day("2026-11-02")]);
        var stored = _store.RemoteDayRows();

        // Two new days would leave four rows.
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, Sync(ledger, [L.Day("2026-11-03"), L.Day("2026-11-04")]));
        Assert.Equal([L.Host], ledger.TooLarge);
        Assert.Equal(stored, _store.RemoteDayRows());
        Assert.Equal(2, ledger.Rows.Digests(L.Host).Count);
        // Records count too; still too large changes nothing more.
        Assert.Equal(RemoteHistoryEffect.None, Sync(ledger, [L.Day("2026-11-02"), L.Perf("2026-11-02", model: "a"), L.Perf("2026-11-02", model: "b")]));
        Assert.Equal(1, L.Count(_store, "remote_perf"));

        Assert.Equal(RemoteHistoryEffect.Changed, Sync(ledger, [L.Day("2026-11-03")]));
        Assert.Empty(ledger.TooLarge);
        Assert.Equal(3, ledger.Rows.Digests(L.Host).Count);
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, Sync(ledger, [L.Day("2026-11-04")]));
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, Sync(ledger, []));
        Assert.Empty(ledger.TooLarge);
    }

    [Fact]
    public void ABatchAloneBeyondTheBoundsStopsBeingKept()
    {
        var ledger = Ledger(new RemoteHistoryLimits(Days: 2, Perf: 10));
        ledger.Receive(L.Due(), _store);
        ledger.Apply(_store, [.. ((string[])["2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04"]).Select(d => L.Event(L.Day(d)))]);
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, ledger.Receive(L.Event(L.Done), _store));
        Assert.True(ledger.Rows.IsEmpty);
        Assert.Equal(0, L.Count(_store, "remote_days"));

        // Deleting the host's history forgets it.
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, ledger.DeleteHost(L.Host, _store));
        Assert.Empty(ledger.TooLarge);
    }

    [Fact]
    public void ALargeBatchWaitsInTheStagingTablesUntilItCommits()
    {
        var ledger = Ledger(flushBytes: 1);
        ledger.Receive(L.Due(), _store);
        ledger.Apply(_store, L.Event(L.Day("2026-11-01")), L.Event(L.Perf("2026-11-01")), L.Event(L.Day("2026-11-02")),
            L.Event(L.Day("2026-11-03")));
        Assert.Equal(2, L.Count(_store, "temp.remote_stage_days"));
        Assert.Equal(1, L.Count(_store, "temp.remote_stage_perf"));
        Assert.Equal(0, L.Count(_store, "remote_days"));

        Assert.Equal(RemoteHistoryEffect.Changed, ledger.Receive(L.Event(L.Done), _store));
        Assert.Equal(3, L.Count(_store, "remote_days"));
        Assert.Equal(1, L.Count(_store, "remote_perf"));
        Assert.Equal(0, L.Count(_store, "temp.remote_stage_days"));
        Assert.Equal(3, ledger.Rows.Digests(L.Host).Count);
        Assert.Single(ledger.Rows.PerfRecords(Codex));
        Assert.Equal(L.Digests((L.Host, "Asia/Taipei")), ledger.Rows.Zones);

        // A staged day sent again replaces its row and its records; a broken connection leaves the store as it was.
        ledger.Apply(_store, L.Event(L.Day("2026-11-01", aggregate: L.Aggregate(output: 7), digest: "dddddddddddddddd")),
            L.Event(L.Day("2026-11-05")), L.Event(L.Day("2026-11-06")), L.Status(RemoteStatus.Unreachable), L.Event(L.Done));
        Assert.Equal(3, L.Count(_store, "remote_days"));
        ledger.Apply(_store, L.Status(RemoteStatus.Connected), L.Event(L.Day("2026-11-07")),
            L.Event(L.Day("2026-11-01", aggregate: L.Aggregate(output: 7), digest: "dddddddddddddddd")),
            L.Event(L.Day("2026-11-08")), L.Event(L.Done));
        Assert.Equal(5, L.Count(_store, "remote_days"));
        Assert.Equal(0, L.Count(_store, "remote_perf"));
        Assert.Equal("dddddddddddddddd", ledger.Rows.Digests(L.Host)["2026-11-01/codex"]);
        Assert.Equal(0, L.Count(_store, "temp.remote_stage_days"));
    }

    [Fact]
    public void AStagedBatchBeyondTheBoundsChangesNothing()
    {
        var ledger = Ledger(new RemoteHistoryLimits(Days: 3, Perf: 10), flushBytes: 1);
        Sync(ledger, [L.Day("2026-11-01"), L.Day("2026-11-02")]);
        Assert.Equal(RemoteHistoryEffect.TooLargeChanged, Sync(ledger, [L.Day("2026-11-03"), L.Day("2026-11-04"), L.Day("2026-11-05")]));
        Assert.Equal(2, L.Count(_store, "remote_days"));
        Assert.Equal(0, L.Count(_store, "temp.remote_stage_days"));
        Assert.Equal(2, ledger.Rows.Digests(L.Host).Count);
    }

    [Fact]
    public void ABatchThatCouldNotBeStagedCommitsNothing()
    {
        var ledger = Ledger(flushBytes: 1);
        _store.Connection.Execute("DROP TABLE temp.remote_stage_perf; DROP TABLE temp.remote_stage_days;");
        Assert.Equal(RemoteHistoryEffect.None, Sync(ledger, [L.Day("2026-11-01"), L.Day("2026-11-02"), L.Day("2026-11-03")]));
        Assert.Equal(0, L.Count(_store, "remote_days"));
        Assert.True(ledger.Rows.IsEmpty);
    }

    [Fact]
    public void BatchCountsFollowRepeatedDaysAndRecords()
    {
        var batch = new RemoteHistoryBatch(L.Host, 1);
        batch.Add(L.DayLine("2026-11-01"));
        batch.Add(L.PerfLine("2026-11-01", "a"));
        batch.Add(L.PerfLine("2026-11-01", "b"));
        batch.Add(L.DayLine("2026-11-02"));
        batch.TakeCompleted();
        batch.Add(L.PerfLine("2026-11-02", "a"));
        batch.Add(L.PerfLine("2026-11-02", "a"));
        Assert.Equal((2, 3), (batch.DayCount, batch.PerfCount));
        batch.Add(L.DayLine("2026-11-01"));
        Assert.Equal((2, 1), (batch.DayCount, batch.PerfCount));
        batch.Add(L.DayLine("2026-11-02"));
        Assert.Equal((2, 0), (batch.DayCount, batch.PerfCount));
        Assert.True(batch.Exceeds(new RemoteHistoryLimits(Days: 1, Perf: 10)));
        Assert.False(batch.Exceeds(new RemoteHistoryLimits(Days: 2, Perf: 0)));
    }
}
