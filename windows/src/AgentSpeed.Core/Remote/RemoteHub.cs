namespace AgentSpeed.Core.Remote;

/// <summary>
/// Owns one connection per enabled host and keeps them in step with the settings (REMOTE 2.3, 4.4, 7 <i>Sources</i>).
/// UI-agnostic: the app forwards wake-from-sleep and network changes with <see cref="NoteWake"/>. Every mutable field is
/// only touched on the hub's queue.
/// </summary>
internal sealed class RemoteHub
{
    private static readonly Lazy<RemotePriceTableJson> BundledPrices =
        new(() => RemotePriceTableJson.From(Pricing.PriceBook.Bundled.Data) ?? throw new InvalidOperationException("The bundled price table is not JSON."));

    /// <summary>How much longer than a connection's own limit the hub keeps a retiring connection.</summary>
    private static readonly TimeSpan RetireGrace = TimeSpan.FromSeconds(5);

    private readonly RemoteEnvironment _environment;
    private readonly RemoteQueue _eventQueue;
    private readonly Action<RemoteHostEvent> _onEvent;
    private readonly RemoteQueue _queue = new("remote.hub");
    private readonly Dictionary<string, RemoteConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>The host value each connection was made for.</summary>
    private readonly Dictionary<string, RemoteHost> _connectedHosts = new(StringComparer.Ordinal);

    /// <summary>The status the hub itself reported for a host without a connection (<c>disabled</c>, <c>networkOff</c>, invalid).</summary>
    private readonly Dictionary<string, RemoteStatus> _idleStatuses = new(StringComparer.Ordinal);

    /// <summary>Removed hosts whose relay restores their Claude Code status line before it stops (REMOTE 2.4).</summary>
    private readonly Dictionary<string, RemoteConnection> _retiring = new(StringComparer.Ordinal);
    private RemoteSessionSettings? _settings;

    public RemoteHub(RemoteEnvironment environment, RemoteQueue eventQueue, Action<RemoteHostEvent> onEvent)
    {
        _environment = environment;
        _eventQueue = eventQueue;
        _onEvent = onEvent;
    }

    /// <summary>
    /// Applies the settings: starts the connections of enabled, valid hosts and stops the others (removed or disabled hosts;
    /// every host while the master switch is off). A host whose destination or folder changed reconnects, and every host
    /// does when the enabled sources change. A new price table goes to the running relays without a reconnect;
    /// <paramref name="priceTable"/> is the current table's JSON (an invalid one keeps the previous, else the bundled, table).
    /// <paramref name="quota"/> holds the app's quota switches (none on when null): each host's config adds its own Claude Code
    /// bridge (REMOTE 11.1) and goes to its relay without a reconnect. A removed host whose bridge is on restores its status
    /// line first (REMOTE 2.4).
    /// </summary>
    public void Update(IReadOnlyList<RemoteHost> hosts, bool networkEnabled, SourceSet enabledSources, ReadOnlyMemory<byte> priceTable,
        RemoteQuotaSwitches? quota = null) => _queue.Sync(() =>
    {
        var switches = quota ?? new RemoteQuotaSwitches();
        var prices = RemotePriceTableJson.From(priceTable.Span) ?? _settings?.Prices ?? BundledPrices.Value;
        var shared = new RemoteSessionSettings(enabledSources, prices, _settings?.Watch ?? false);
        var sourcesChanged = _settings is { } previous && previous.Sources != enabledSources;
        _settings = shared;

        var valid = RemoteHostsDocument.Normalized(hosts);
        var ids = valid.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _connections.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            if (networkEnabled && _connectedHosts.GetValueOrDefault(id)?.QuotaClaudeCli == true && _connections.Remove(id, out var leaving))
            {
                _connectedHosts.Remove(id);
                Retire(id, leaving);
            }
            else
            {
                RemoveConnection(id, null);
            }
        }

        foreach (var id in _idleStatuses.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _idleStatuses.Remove(id);
        }

        foreach (var host in valid)
        {
            RemoteStatus? idle = !networkEnabled ? RemoteStatus.NetworkOff
                : !host.Enabled ? RemoteStatus.Disabled
                : !host.IsConnectable ? RemoteStatus.InvalidDestination
                : null;
            if (idle is { } idleStatus)
            {
                if (_connections.ContainsKey(host.Id))
                {
                    RemoveConnection(host.Id, idleStatus);
                    _idleStatuses[host.Id] = idleStatus;
                }
                else
                {
                    ReportIdle(host.Id, idleStatus);
                }

                continue;
            }

            _idleStatuses.Remove(host.Id);
            var config = RemoteQuotaConfig.ForHost(host, switches, networkEnabled, enabledSources);
            if (_connections.TryGetValue(host.Id, out var existing))
            {
                var edited = !_connectedHosts.TryGetValue(host.Id, out var made) || made.ConnectionIdentity != host.ConnectionIdentity;
                if (edited || sourcesChanged)
                {
                    RemoveConnection(host.Id, null);
                }
                else
                {
                    _connectedHosts[host.Id] = host;
                    existing.UpdatePrices(prices);
                    existing.UpdateQuota(config);
                    continue;
                }
            }

            var connection = new RemoteConnection(host, shared with { Quota = config }, _environment, _eventQueue, _onEvent);
            _connections[host.Id] = connection;
            _connectedHosts[host.Id] = host;
            connection.Start();
        }
    });

    /// <summary>
    /// Sends the host's quota config without and again with its Claude Code bridge: its relay turns the bridge off (leaving a
    /// replaced status line alone) and installs it again (QUOTAS 5.2, 5.3).
    /// </summary>
    public void ReapplyQuota(string hostId) => _queue.Sync(() => _connections.GetValueOrDefault(hostId)?.ReapplyQuota());

    /// <summary><i>Retry now</i> (or a fresh look after the user fixed something on the host).</summary>
    public void RetryNow(string hostId) => _queue.Sync(() => _connections.GetValueOrDefault(hostId)?.RetryNow());

    /// <summary>Answers <c>historyDue</c> with the digests the app holds for the host.</summary>
    public void RequestHistory(string hostId, IReadOnlyDictionary<string, string> digests) =>
        _queue.Sync(() => _connections.GetValueOrDefault(hostId)?.RequestHistory(digests));

    /// <summary>Live reporting's watch (METRICS 11.10): <c>tool</c> lines are sent only while on.</summary>
    public void SetWatch(bool on) => _queue.Sync(() =>
    {
        if (_settings is not null)
        {
            _settings = _settings with { Watch = on };
        }

        foreach (var connection in _connections.Values)
        {
            connection.SetWatch(on);
        }
    });

    /// <summary>The computer woke from sleep or its network came back: hosts waiting for their back-off retry at once.</summary>
    public void NoteWake() => _queue.Sync(() =>
    {
        foreach (var connection in _connections.Values)
        {
            connection.NoteWake();
        }
    });

    public void NoteTimeZoneChange() => _queue.Sync(() =>
    {
        foreach (var connection in _connections.Values)
        {
            connection.NoteTimeZoneChange();
        }
    });

    /// <summary>Quitting: every relay's stdin closes and every <c>ssh</c> ends.</summary>
    public void StopAll() => _queue.Sync(() =>
    {
        foreach (var id in _connections.Keys.ToList())
        {
            RemoveConnection(id, null);
        }

        foreach (var connection in _retiring.Values)
        {
            connection.Stop();
        }

        _retiring.Clear();
    });

    /// <summary>Hosts with a connection (tests and diagnostics).</summary>
    public IReadOnlySet<string> ConnectedHostIds => _queue.Sync(() => (IReadOnlySet<string>)_connections.Keys.ToHashSet(StringComparer.Ordinal));

    /// <summary>Removed hosts that are restoring their status line before they stop (tests and diagnostics).</summary>
    public IReadOnlySet<string> RetiringHostIds => _queue.Sync(() => (IReadOnlySet<string>)_retiring.Keys.ToHashSet(StringComparer.Ordinal));

    internal RemoteConnection? Connection(string hostId) => _queue.Sync(() => _connections.GetValueOrDefault(hostId));

    internal RemoteConnection? RetiringConnection(string hostId) => _queue.Sync(() => _retiring.GetValueOrDefault(hostId));

    /// <summary>The connection stops by itself (bridge off, its limit, the relay ending); this only bounds how long the hub holds it.</summary>
    private void Retire(string id, RemoteConnection connection)
    {
        if (_retiring.Remove(id, out var earlier))
        {
            earlier.Stop();
        }

        _retiring[id] = connection;
        connection.Retire();
        _environment.Scheduling.Schedule(_environment.Scheduling.Now + RemoteConnection.RetireLimit + RetireGrace, _queue, () =>
        {
            if (_retiring.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
            {
                _retiring.Remove(id);
                connection.Stop();
            }
        });
    }

    private void RemoveConnection(string id, RemoteStatus? finalStatus)
    {
        if (_connections.Remove(id, out var connection))
        {
            connection.Stop(finalStatus);
        }

        _connectedHosts.Remove(id);
    }

    private void ReportIdle(string id, RemoteStatus status)
    {
        if (_idleStatuses.TryGetValue(id, out var current) && current == status)
        {
            return;
        }

        _idleStatuses[id] = status;
        var hostEvent = new RemoteHostEvent(id, 0, new RemoteHostEventKind.Status(new RemoteHostStatus(status)));
        var onEvent = _onEvent;
        _eventQueue.Post(() => onEvent(hostEvent));
    }
}
