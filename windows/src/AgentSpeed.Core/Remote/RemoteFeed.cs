using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Remote;

/// <summary>Where the remote feed delivers its changes: the running monitor session, which applies them on its thread.</summary>
internal interface IRemoteFeedSink
{
    /// <summary>Called with the feed's lock held, so it must only enqueue.</summary>
    void RemoteFeed(IReadOnlyList<RemoteFeedChange> changes);
}

/// <summary>
/// The live side of every remote host (REMOTE 7): it mirrors what each host sent, so a monitor restart replays it as
/// seeded without reconnecting, and forwards every change to the attached session in the order the events came.
/// Thread-safe: events arrive on the coordinator's queue, hosts from the app and <see cref="Attach"/> from the monitor
/// thread; one lock orders them.
/// </summary>
internal sealed class RemoteFeed
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RemoteHostMirror> _mirrors = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeZoneInfo> _zone;
    private IReadOnlyList<RemoteHostInfo> _hosts = [];
    private IRemoteFeedSink? _sink;

    public RemoteFeed(Func<DateTimeOffset>? now = null, Func<TimeZoneInfo>? zone = null)
    {
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _zone = zone ?? (static () => TimeZoneInfo.Local);
    }

    /// <summary>Some remote host is configured.</summary>
    public bool HasHosts
    {
        get
        {
            lock (_lock)
            {
                return _hosts.Count > 0;
            }
        }
    }

    public IReadOnlyList<RemoteHostInfo> Hosts
    {
        get
        {
            lock (_lock)
            {
                return _hosts;
            }
        }
    }

    /// <summary>The configured hosts in settings order with their display names. A host that left the settings is forgotten.</summary>
    public void SetHosts(IReadOnlyList<RemoteHostInfo> hosts)
    {
        lock (_lock)
        {
            if (hosts.SequenceEqual(_hosts))
            {
                return;
            }

            var ids = hosts.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
            var changes = new List<RemoteFeedChange>();
            foreach (var id in _mirrors.Keys.Where(id => !ids.Contains(id)).Order(StringComparer.Ordinal).ToList())
            {
                _mirrors.Remove(id);
                changes.Add(new RemoteFeedChange.RemoveHost(id));
            }

            foreach (var host in hosts)
            {
                if (!_mirrors.ContainsKey(host.Id))
                {
                    _mirrors[host.Id] = new RemoteHostMirror(host.Id);
                }
            }

            _hosts = [.. hosts];
            changes.Add(new RemoteFeedChange.Hosts(_hosts));
            _sink?.RemoteFeed(changes);
        }
    }

    /// <summary>One event of the hub; events of hosts that are not configured (any more) are dropped.</summary>
    public void Receive(RemoteHostEvent hostEvent)
    {
        var now = _now();
        var nowMs = now.ToUnixTimeMilliseconds();
        var todayStartMs = LocalDay.StartMs(nowMs, _zone());
        lock (_lock)
        {
            if (!_mirrors.TryGetValue(hostEvent.HostId, out var mirror))
            {
                return;
            }

            var changes = mirror.Apply(hostEvent, nowMs, todayStartMs);
            if (changes.Count > 0)
            {
                _sink?.RemoteFeed(changes);
            }
        }
    }

    /// <summary>Makes <paramref name="sink"/> the receiver of every later change and returns the current state as seeded changes.</summary>
    public List<RemoteFeedChange> Attach(IRemoteFeedSink sink)
    {
        lock (_lock)
        {
            _sink = sink;
            var changes = new List<RemoteFeedChange> { new RemoteFeedChange.Hosts(_hosts) };
            foreach (var host in _hosts)
            {
                if (_mirrors.TryGetValue(host.Id, out var mirror))
                {
                    changes.AddRange(mirror.Replay());
                }
            }

            return changes;
        }
    }

    public void Detach(IRemoteFeedSink sink)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sink = null;
            }
        }
    }

    /// <summary>The mirror of one host (tests and diagnostics).</summary>
    public RemoteHostMirror? Mirror(string hostId)
    {
        lock (_lock)
        {
            return _mirrors.GetValueOrDefault(hostId);
        }
    }
}
