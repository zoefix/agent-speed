using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

/// <summary>What one pass over the queued remote work changed (REMOTE 8.3).</summary>
/// <param name="Changed">The rows or names changed: the days, records and names are published again.</param>
/// <param name="TooLargeChanged">The <c>historyTooLarge</c> hosts changed.</param>
internal readonly record struct RemoteHistoryDrain(bool Changed, bool TooLargeChanged);

/// <summary>
/// The remote hosts' daily history inside one run of the history service (REMOTE 8.2, 8.3). The host events, the settings'
/// names and deletions are queued from any thread and applied in order on the history thread between analysis runs, so a
/// long local analysis delays them.
/// </summary>
internal sealed class RemoteHistoryWorker
{
    private readonly Lock _lock = new();
    private readonly Queue<Work> _queue = new();
    private readonly Action _signal;
    private readonly Action<string, IReadOnlyDictionary<string, string>> _request;

    /// <summary>Deletions that wait for the store to open; they are never dropped.</summary>
    private readonly List<string> _deletions = [];

    /// <param name="signal">Wakes the history thread.</param>
    /// <param name="request">Sends a <c>history</c> request to a host.</param>
    public RemoteHistoryWorker(Action signal, Action<string, IReadOnlyDictionary<string, string>> request, RemoteHistoryLedger ledger)
    {
        _signal = signal;
        _request = request;
        Ledger = ledger;
    }

    /// <summary>History thread only.</summary>
    public RemoteHistoryLedger Ledger { get; }

    public void Remote(RemoteHostEvent hostEvent) => Add(new Work.Event(hostEvent));

    public void SetHosts(IReadOnlyDictionary<string, string> names) => Add(new Work.Hosts(names));

    public void DeleteHost(string id) => Add(new Work.Delete(id));

    /// <summary>After <i>Clear history</i>: every connected host is asked for all of its days (REMOTE 8.3).</summary>
    public void Resync()
    {
        foreach (var host in Ledger.ConnectedHosts)
        {
            _request(host, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Applies the queued work in order. Without a store (it could not be opened) host events are dropped (the next sync sends
    /// their days again) and deletions wait.
    /// </summary>
    public RemoteHistoryDrain Drain(HistoryStore? store)
    {
        Work[] items;
        lock (_lock)
        {
            items = [.. _queue];
            _queue.Clear();
        }

        var changed = false;
        var tooLarge = false;
        void Apply(RemoteHistoryEffect effect)
        {
            switch (effect.Kind)
            {
                case RemoteHistoryEffectKind.Changed:
                    changed = true;
                    break;
                case RemoteHistoryEffectKind.TooLargeChanged:
                    tooLarge = true;
                    break;
                case RemoteHistoryEffectKind.Request:
                    _request(effect.Host!, effect.Digests!);
                    break;
            }
        }

        if (store is not null)
        {
            foreach (var id in _deletions)
            {
                Apply(Ledger.DeleteHost(id, store));
            }

            _deletions.Clear();
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case Work.Hosts hosts:
                    Apply(Ledger.SetHosts(hosts.Names, store));
                    break;
                case Work.Delete delete:
                    if (store is null)
                    {
                        _deletions.Add(delete.Id);
                    }
                    else
                    {
                        Apply(Ledger.DeleteHost(delete.Id, store));
                    }

                    break;
                case Work.Event remote when store is not null:
                    Apply(Ledger.Receive(remote.Value, store));
                    break;
            }
        }

        return new RemoteHistoryDrain(changed, tooLarge || changed);
    }

    private void Add(Work work)
    {
        lock (_lock)
        {
            _queue.Enqueue(work);
        }

        _signal();
    }

    private abstract record Work
    {
        public sealed record Event(RemoteHostEvent Value) : Work;

        public sealed record Hosts(IReadOnlyDictionary<string, string> Names) : Work;

        public sealed record Delete(string Id) : Work;
    }
}
