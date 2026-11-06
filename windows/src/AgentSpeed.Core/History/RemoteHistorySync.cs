using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

/// <summary>
/// The daily history of the remote hosts (REMOTE 8.2, 8.3): answers each host's <c>historyDue</c> with the digests it holds,
/// stores the days the relay sends in batches and has the history service publish them with the local days. Usable from any
/// thread; the work runs on the history service's thread.
/// </summary>
/// <remarks>
/// Hand it to the remote coordinator as its history sink and keep it in step with <see cref="SetHosts"/>. Before the history
/// service starts, the last status and a pending sync of every host, the names and the deletions wait here.
/// </remarks>
public sealed class RemoteHistorySync : IRemoteHistorySink
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RemoteHostEvent> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteHostEvent> _due = new(StringComparer.Ordinal);
    private readonly List<string> _deletions = [];
    private RemoteHistoryWorker? _worker;
    private Action<string, IReadOnlyDictionary<string, string>>? _request;
    private Dictionary<string, string>? _hosts;

    public RemoteHistorySync()
        : this(null)
    {
    }

    /// <param name="requestHistory">Sends a <c>history</c> request; without it, the requests that come with <see cref="IRemoteHistorySink.RemoteHistory"/> are used.</param>
    internal RemoteHistorySync(Action<string, IReadOnlyDictionary<string, string>>? requestHistory)
    {
        _request = requestHistory;
    }

    void IRemoteHistorySink.RemoteHistory(RemoteHostEvent hostEvent, IRemoteHistoryRequests requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_lock)
        {
            _request ??= requests.RequestHistory;
        }

        Receive(hostEvent);
    }

    /// <summary>A host's event, in the order the host sent them.</summary>
    internal void Receive(RemoteHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        lock (_lock)
        {
            if (_worker is { } worker)
            {
                worker.Remote(hostEvent);
                return;
            }

            switch (hostEvent.Kind)
            {
                case RemoteHostEventKind.Status:
                    _statuses[hostEvent.HostId] = hostEvent;
                    break;
                case RemoteHostEventKind.HistoryDue:
                    _due[hostEvent.HostId] = hostEvent;
                    break;
            }
        }
    }

    /// <summary>
    /// The hosts of the settings with their display names (REMOTE 2.1): stored hosts take the new names; hosts no longer listed
    /// keep their rows and their last name (REMOTE 2.2).
    /// </summary>
    public void SetHosts(IReadOnlyList<(string Id, string Name)> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name) in hosts)
        {
            names.TryAdd(id, name);
        }

        lock (_lock)
        {
            _hosts = names;
            _worker?.SetHosts(names);
        }
    }

    /// <summary>Deletes a host's daily history (<i>Also delete its usage history</i>, REMOTE 2.4).</summary>
    public void DeleteHistory(string hostId)
    {
        ArgumentNullException.ThrowIfNull(hostId);
        lock (_lock)
        {
            _statuses.Remove(hostId);
            _due.Remove(hostId);
            if (_worker is { } worker)
            {
                worker.DeleteHost(hostId);
            }
            else
            {
                _deletions.Add(hostId);
            }
        }
    }

    /// <summary>The history service started (or stopped, null); what waited goes to it.</summary>
    internal void Attach(RemoteHistoryWorker? worker)
    {
        lock (_lock)
        {
            _worker = worker;
            if (worker is null)
            {
                return;
            }

            if (_hosts is { } hosts)
            {
                worker.SetHosts(hosts);
            }

            foreach (var id in _deletions)
            {
                worker.DeleteHost(id);
            }

            foreach (var waiting in (Dictionary<string, RemoteHostEvent>[])[_statuses, _due])
            {
                foreach (var host in waiting.Keys.Order(StringComparer.Ordinal))
                {
                    worker.Remote(waiting[host]);
                }

                waiting.Clear();
            }

            _deletions.Clear();
        }
    }

    internal void Request(string hostId, IReadOnlyDictionary<string, string> digests)
    {
        Action<string, IReadOnlyDictionary<string, string>>? request;
        lock (_lock)
        {
            request = _request;
        }

        request?.Invoke(hostId, digests);
    }
}
