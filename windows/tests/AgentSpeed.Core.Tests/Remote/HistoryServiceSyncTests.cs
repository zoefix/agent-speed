using System.Collections.Concurrent;
using System.Diagnostics;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteHistoryLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The history service with the remote hosts' sync (REMOTE 8.2, 8.3).</summary>
public sealed class RemoteHistoryServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<(string Host, IReadOnlyDictionary<string, string> Digests)> _requests = new();

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private HistoryService Service(HistoryHub hub, RemoteHistorySync? remote = null, RemoteHistoryLimits? limits = null) => new(
        hub,
        new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex }, _temp.Combine("codex", "sessions"), _temp.Combine("claude"),
            _temp.Combine("dsh"), _temp.Combine("opencode"), _temp.Combine("gemini")),
        StorePath,
        TimeZoneInfo.Utc,
        new HistoryOptions { Workers = 1, RemoteLimits = limits ?? new RemoteHistoryLimits() },
        remote: remote ?? new RemoteHistorySync((host, digests) => _requests.Enqueue((host, digests))));

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("the expected history was not published");
            }

            await Task.Delay(20);
        }
    }

    private static Task<HistoryHub> Hub() => Task.Run(() => new HistoryHub());

    [Fact]
    public async Task SyncedDaysArePublishedWithTheLocalOnes()
    {
        var hub = await Hub();
        using var service = Service(hub);
        // Events before the service starts wait for it.
        service.Remote.SetHosts([(L.Host, "devbox")]);
        service.Remote.Receive(L.Status(RemoteStatus.Connected));
        service.Remote.Receive(L.Due());
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        await Until(() => _requests.Count == 1);
        var first = _requests.Single();
        Assert.Equal(L.Host, first.Host);
        Assert.Empty(first.Digests);

        foreach (var message in (RelayMessage[])[L.Day("2026-11-02"), L.Perf("2026-11-02"), L.Day("2026-11-02", SourceKind.Gemini), L.Done])
        {
            service.Remote.Receive(L.Event(message));
        }

        await Until(() => hub.Days.Any(d => d.Host == L.Host));
        Assert.Equal([$"2026-11-02/codex/{L.Host}"], hub.Days.Select(d => d.Id));
        Assert.Equal(L.Digests((L.Host, "devbox")), hub.HostNames);
        Assert.Equal(["gpt-5.5"], hub.PerfRecords.Select(r => r.Model));

        service.Remote.Receive(L.Due());
        await Until(() => _requests.Count == 2);
        Assert.Equal(L.Digests(("2026-11-02/codex", "aaaaaaaaaaaaaaa2"), ("2026-11-02/gemini", "aaaaaaaaaaaaaaa2")), _requests.Last().Digests);

        service.Remote.SetHosts([(L.Host, "build")]);
        await Until(() => hub.HostNames.GetValueOrDefault(L.Host) == "build");
    }

    [Fact]
    public async Task RemoteRowsCarryTheDaysLivePeakAndThePerfRecordsArePooled()
    {
        var hub = await Hub();
        var published = new ConcurrentQueue<IReadOnlyList<AgentSpeed.Core.Perf.PerfRecord>>();
        using var service = Service(hub);
        service.PerfRecordsUpdated = published.Enqueue;
        var day = DateTimeOffset.Parse("2026-11-02T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        service.ObserveLive(SpeedSnapshot.Empty with { Timestamp = day, State = MonitorState.Running, PeakToday = new Rate(88, 900) });
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        foreach (var hostEvent in (RemoteHostEvent[])[L.Event(L.Day("2026-11-02")), L.Event(L.Perf("2026-11-02")), L.Event(L.Done),
            L.Event(L.Day("2026-11-02"), L.OtherHost), L.Event(L.Perf("2026-11-02", record: L.Record(n: 6)), L.OtherHost),
            L.Event(L.Done, L.OtherHost)])
        {
            service.Remote.Receive(hostEvent);
        }

        await Until(() => hub.Days.Count == 2);
        Assert.All(hub.Days, d => Assert.Equal(88, d.Peak));
        Assert.Equal([L.OtherHost, L.Host], hub.Days.Select(d => d.Host));
        Assert.Equal(10, Assert.Single(hub.PerfRecords).Sums.N);
        await Until(() => published.LastOrDefault() is [{ Sums.N: 10 }]);
    }

    [Fact]
    public async Task ClearHistoryClearsTheHostsRowsAndAsksAgain()
    {
        var hub = await Hub();
        using var service = Service(hub);
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        foreach (var hostEvent in (RemoteHostEvent[])[L.Status(RemoteStatus.Connected), L.Event(L.Day("2026-11-02")), L.Event(L.Done)])
        {
            service.Remote.Receive(hostEvent);
        }

        await Until(() => hub.Days.Any(d => d.Host == L.Host));
        hub.Clear();
        await Until(() => !hub.Days.Any(d => d.Host == L.Host));
        await Until(() => _requests.Any(r => r.Host == L.Host && r.Digests.Count == 0));
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        service.Stop();
        using var store = HistoryStore.Open(StorePath);
        Assert.Empty(store.RemoteDayRows());
    }

    [Fact]
    public async Task DeletingAHostsHistoryRemovesItsRows()
    {
        var hub = await Hub();
        using var service = Service(hub);
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        foreach (var hostEvent in (RemoteHostEvent[])[L.Event(L.Day("2026-11-02")), L.Event(L.Done), L.Event(L.Day("2026-11-02"), L.OtherHost),
            L.Event(L.Done, L.OtherHost)])
        {
            service.Remote.Receive(hostEvent);
        }

        await Until(() => hub.Days.Count == 2);
        service.Remote.DeleteHistory(L.Host);
        await Until(() => hub.Days.Select(d => d.Host).SequenceEqual([L.OtherHost]));
    }

    [Fact]
    public async Task ADeletionBeforeTheServiceStartsIsAppliedWhenItStarts()
    {
        var hub = await Hub();
        var remote = new RemoteHistorySync((host, digests) => _requests.Enqueue((host, digests)));
        using (var service = Service(hub, remote))
        {
            service.Start();
            await Until(() => hub.State.Kind == HistoryStateKind.Ready);
            foreach (var hostEvent in (RemoteHostEvent[])[L.Event(L.Day("2026-11-02")), L.Event(L.Done)])
            {
                remote.Receive(hostEvent);
            }

            await Until(() => hub.Days.Count == 1);
        }

        remote.DeleteHistory(L.Host);
        var next = await Hub();
        using (var again = Service(next, remote))
        {
            again.Start();
            await Until(() => next.State.Kind == HistoryStateKind.Ready && next.Days.Count == 0);
        }

        using var store = HistoryStore.Open(StorePath);
        Assert.Empty(store.RemoteDayRows());
        Assert.Empty(store.RemoteHostNames());
    }

    [Fact]
    public async Task ABatchBeyondTheBoundsIsPublishedAsTooLargeUntilASyncSucceeds()
    {
        var hub = await Hub();
        using var service = Service(hub, limits: new RemoteHistoryLimits(Days: 1, Perf: 10));
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        foreach (var hostEvent in (RemoteHostEvent[])[L.Event(L.Day("2026-11-01")), L.Event(L.Day("2026-11-02")), L.Event(L.Done)])
        {
            service.Remote.Receive(hostEvent);
        }

        await Until(() => hub.RemoteTooLarge.Contains(L.Host));
        Assert.Empty(hub.Days);
        foreach (var hostEvent in (RemoteHostEvent[])[L.Event(L.Day("2026-11-02")), L.Event(L.Done)])
        {
            service.Remote.Receive(hostEvent);
        }

        await Until(() => hub.RemoteTooLarge.Count == 0 && hub.Days.Count == 1);
    }

    [Fact]
    public async Task WithoutACallbackTheCoordinatorsRequestsAnswer()
    {
        var hub = await Hub();
        var remote = new RemoteHistorySync();
        using var service = Service(hub, remote);
        service.Start();
        await Until(() => hub.State.Kind == HistoryStateKind.Ready);
        var requests = new Requests();
        ((IRemoteHistorySink)remote).RemoteHistory(L.Due(), requests);
        await Until(() => requests.Sent.Count == 1);
        Assert.Equal(L.Host, requests.Sent.Single());

        // An explicit callback wins.
        var explicitly = await Hub();
        using var other = new HistoryService(explicitly, new MonitorConfiguration(new HashSet<SourceKind>(), _temp.Combine("x"), _temp.Combine("y"),
            _temp.Combine("z"), _temp.Combine("w"), _temp.Combine("v")), _temp.Combine("other", "history.sqlite"), TimeZoneInfo.Utc,
            new HistoryOptions { Workers = 1 }, remote: new RemoteHistorySync((host, digests) => _requests.Enqueue((host, digests))));
        other.Start();
        await Until(() => explicitly.State.Kind == HistoryStateKind.Ready);
        ((IRemoteHistorySink)other.Remote).RemoteHistory(L.Due(), requests);
        await Until(() => _requests.Count == 1);
        Assert.Single(requests.Sent);
    }

    [Fact]
    public async Task TheHubPublishesTooLargeHostsOnlyWhenTheyChange()
    {
        var hub = await Hub();
        var changes = 0;
        hub.Changed += (_, _) => changes++;
        hub.PublishRemoteTooLarge(new HashSet<string>());
        Assert.Equal(0, changes);
        hub.PublishRemoteTooLarge(new HashSet<string> { L.Host });
        hub.PublishRemoteTooLarge(new HashSet<string> { L.Host });
        Assert.Equal(1, changes);
        Assert.Equal([L.Host], hub.RemoteTooLarge);

        var names = L.Digests((L.Host, "devbox"));
        hub.Publish(HistoryState.NotLoaded, [], names);
        var published = hub.HostNames;
        hub.Publish(HistoryState.NotLoaded, [], L.Digests((L.Host, "devbox")));
        Assert.Same(published, hub.HostNames);
        hub.Publish(HistoryState.NotLoaded);
        Assert.Equal(names, hub.HostNames);
    }

    private sealed class Requests : IRemoteHistoryRequests
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public void RequestHistory(string hostId, IReadOnlyDictionary<string, string> digests) => Sent.Enqueue(hostId);
    }
}
