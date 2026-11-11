using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Core.Remote;

/// <summary>A host's status changed (raised on a background thread; marshal to the UI).</summary>
public sealed class RemoteHostStatusEventArgs(string hostId, RemoteHostStatus status) : EventArgs
{
    public string HostId { get; } = hostId;

    public RemoteHostStatus Status { get; } = status;
}

/// <summary>
/// Connects the remote hosts of the settings to the live monitor (REMOTE 2, 4.4, 7): owns the connections, keeps them in
/// step with the hosts, the network master switch, the monitor's enabled sources and report watch, the price table and the
/// quota switches, routes live events to the monitor, history events to <see cref="HistorySink"/> and quota lines to
/// <see cref="QuotaReceived"/>. Callable from any thread.
/// </summary>
public sealed class RemoteCoordinator : IDisposable
{
    private static readonly TimeSpan PriceCheckInterval = TimeSpan.FromSeconds(30);

    private readonly LiveMonitor _monitor;
    private readonly PriceBook? _pricing;
    private readonly RemoteHub _hub;
    private readonly RemoteFeed _feed;
    private readonly Requests _requests;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RemoteHostStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Timer _priceTimer;
    private RemoteHostsDocument _document = RemoteHostsDocument.Empty;
    private bool _networkEnabled = true;
    private RemoteQuotaSwitches _quotaSwitches = new();
    private bool _configured;
    private Applied? _applied;
    private int? _pricedVersion;
    private IRemoteHistorySink? _historySink;
    private bool _disposed;

    /// <param name="appVersion">The app version (names the bundled relay on the host, REMOTE 4.2).</param>
    /// <param name="pricing">The live price book: its current table goes to every relay (REMOTE 5.3 <c>prices</c>).</param>
    /// <param name="relays">The assembly the relays are embedded in (REMOTE 3.1); the entry assembly when null.</param>
    public RemoteCoordinator(LiveMonitor monitor, string appVersion, PriceBook? pricing = null, System.Reflection.Assembly? relays = null)
        : this(monitor, RemoteEnvironment.System(appVersion, relays), pricing)
    {
    }

    internal RemoteCoordinator(LiveMonitor monitor, RemoteEnvironment environment, PriceBook? pricing = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _pricing = pricing ?? monitor.Pricing;
        _feed = monitor.RemoteFeed;
        var events = new RemoteQueue("remote.events");
        _hub = new RemoteHub(environment, events, Route);
        _requests = new Requests(_hub);
        monitor.RemoteSettingsChanged = MonitorSettingsChanged;
        _priceTimer = new Timer(_ => CheckPrices(), null, PriceCheckInterval, PriceCheckInterval);
    }

    /// <summary>A host's status changed (any thread).</summary>
    public event EventHandler<RemoteHostStatusEventArgs>? StatusChanged;

    /// <summary>
    /// A configured host's <c>quota</c>, <c>quotaRemoved</c> or <c>quotaBridge</c> line (REMOTE 11.1), times already local;
    /// raised on the remote event thread, in order (marshal to the UI). Quotas are display only: they never reach the
    /// monitor or the history.
    /// </summary>
    public event EventHandler<RemoteQuotaEventArgs>? QuotaReceived;

    /// <summary>The hosts last applied.</summary>
    public RemoteHostsDocument Document
    {
        get
        {
            lock (_lock)
            {
                return _document;
            }
        }
    }

    /// <summary>The last status of every configured host (the settings page's status lines; the snapshot carries them too).</summary>
    public IReadOnlyDictionary<string, RemoteHostStatus> Statuses
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, RemoteHostStatus>(_statuses, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The history service's remote side (<c>HistoryService.Remote</c>) receives the hosts' daily history (REMOTE 8.2, 8.3).</summary>
    public void AttachHistory(History.RemoteHistorySync? history) => HistorySink = history;

    /// <summary>Receives the history events (the daily-history sync, REMOTE 8.2).</summary>
    internal IRemoteHistorySink? HistorySink
    {
        get => Volatile.Read(ref _historySink);
        set => Volatile.Write(ref _historySink, value);
    }

    /// <summary>
    /// The settings: hosts, the network master switch (REMOTE 2.3) and the quota switches (REMOTE 11.1; null keeps the last
    /// ones, none on before the first). Applied only when something changed; a change of hosts or enabled sources may
    /// reconnect, a change of quota switches or of a host's bridge never does.
    /// </summary>
    public void Update(RemoteHostsDocument hosts, bool networkEnabled, RemoteQuotaSwitches? quota = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _document = hosts;
            _networkEnabled = networkEnabled;
            _quotaSwitches = quota ?? _quotaSwitches;
            _configured = true;
            var ids = hosts.Hosts.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _statuses.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _statuses.Remove(id);
            }
        }

        Apply();
    }

    /// <summary><i>Retry now</i>.</summary>
    public void RetryNow(string hostId) => _hub.RetryNow(hostId);

    /// <summary>The host's relay installs its Claude Code bridge again (a status line the user replaced, REMOTE 11.3).</summary>
    public void ReapplyQuota(string hostId) => _hub.ReapplyQuota(hostId);

    /// <summary>The computer woke from sleep (<c>SystemEvents.PowerModeChanged</c> Resume): hosts waiting for their back-off retry at once.</summary>
    public void NoteWake() => _hub.NoteWake();

    /// <summary>The network came back (<c>NetworkChange.NetworkAvailabilityChanged</c>): the same as a wake for the connections.</summary>
    public void NoteNetworkAvailable() => _hub.NoteWake();

    /// <summary>The system time zone changed.</summary>
    public void NoteTimeZoneChange() => _hub.NoteTimeZoneChange();

    /// <summary>Quitting: every relay stops; nothing reconnects until the next <see cref="Update"/>.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _applied = null;
            _configured = false;
        }

        _hub.StopAll();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _priceTimer.Dispose();
        if (_monitor.RemoteSettingsChanged == MonitorSettingsChanged)
        {
            _monitor.RemoteSettingsChanged = null;
        }

        Stop();
    }

    /// <summary>Hosts with a running connection (diagnostics).</summary>
    internal IReadOnlySet<string> ConnectedHostIds => _hub.ConnectedHostIds;

    internal RemoteHub Hub => _hub;

    private void MonitorSettingsChanged() => Apply();

    private void CheckPrices()
    {
        int? priced;
        lock (_lock)
        {
            priced = _pricedVersion;
        }

        if (_pricing is not null && _pricing.CurrentVersion != priced)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Applied next;
        IReadOnlyDictionary<string, string> names;
        lock (_lock)
        {
            if (!_configured || _disposed)
            {
                return;
            }

            var table = _pricing?.Current ?? PriceBook.Bundled;
            _pricedVersion = _pricing?.CurrentVersion;
            next = new Applied(_document, _networkEnabled, SourceSet.From(_monitor.Configuration.EnabledSources), table.Version, table.Data,
                _quotaSwitches);
            if (next.Equals(_applied))
            {
                next = next with { Unchanged = true };
            }
            else
            {
                _applied = next;
            }

            names = _document.DisplayNames();
        }

        if (!next.Unchanged)
        {
            _feed.SetHosts([.. next.Document.Hosts.Select(h => new RemoteHostInfo(h.Id, names.GetValueOrDefault(h.Id) ?? h.AutomaticName))]);
            _hub.Update(next.Document.Hosts, next.NetworkEnabled, next.Sources, next.PriceData, next.Quota);
        }

        _hub.SetWatch(_monitor.ReportWatchOn);
    }

    /// <summary>Where an event goes: the live feed (the monitor), the history sink, or only <see cref="QuotaReceived"/>.</summary>
    internal static (bool Live, bool History, bool Quota) RouteOf(RemoteHostEvent hostEvent) => hostEvent.Kind switch
    {
        RemoteHostEventKind.Status => (true, true, false),
        RemoteHostEventKind.HistoryDue => (false, true, false),
        RemoteHostEventKind.Message { Value: RelayMessage.HistoryDay or RelayMessage.HistoryPerf or RelayMessage.HistoryDone or RelayMessage.HistoryProgress } =>
            (false, true, false),
        RemoteHostEventKind.Message { Value: var message } when message.IsHistoryFailure => (false, true, false),
        RemoteHostEventKind.Message { Value: var message } when message.IsQuota => (false, false, true),
        _ => (true, false, false),
    };

    internal void Route(RemoteHostEvent hostEvent)
    {
        var (live, history, quota) = RouteOf(hostEvent);
        if (live)
        {
            _feed.Receive(hostEvent);
        }

        if (history)
        {
            HistorySink?.RemoteHistory(hostEvent, _requests);
        }

        if (quota)
        {
            RouteQuota(hostEvent);
        }

        if (hostEvent.Kind is RemoteHostEventKind.Status { Value: var status })
        {
            lock (_lock)
            {
                if (_document.Host(hostEvent.HostId) is null)
                {
                    return;
                }

                _statuses[hostEvent.HostId] = status;
            }

            StatusChanged?.Invoke(this, new RemoteHostStatusEventArgs(hostEvent.HostId, status));
        }
    }

    /// <summary>Hands a configured host's quota line to <see cref="QuotaReceived"/>; a removed host's lines are dropped.</summary>
    private void RouteQuota(RemoteHostEvent hostEvent)
    {
        if (hostEvent.Kind is not RemoteHostEventKind.Message { Value: var message })
        {
            return;
        }

        var args = message switch
        {
            RelayMessage.Quota { Value: var report } => new RemoteQuotaEventArgs(hostEvent.HostId, report: report),
            RelayMessage.QuotaRemoved { Source: var source } => new RemoteQuotaEventArgs(hostEvent.HostId, removedSource: source),
            RelayMessage.QuotaBridge { Value: var bridge } => new RemoteQuotaEventArgs(hostEvent.HostId, bridge: bridge),
            _ => null,
        };
        if (args is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_document.Host(hostEvent.HostId) is null)
            {
                return;
            }
        }

        QuotaReceived?.Invoke(this, args);
    }

    private sealed record Applied(
        RemoteHostsDocument Document, bool NetworkEnabled, SourceSet Sources, int PriceVersion, byte[] PriceData, RemoteQuotaSwitches Quota)
    {
        public bool Unchanged { get; init; }

        public bool Equals(Applied? other) => other is not null && Document == other.Document && NetworkEnabled == other.NetworkEnabled
            && Sources == other.Sources && PriceVersion == other.PriceVersion && Quota == other.Quota;

        public override int GetHashCode() => HashCode.Combine(Document, NetworkEnabled, Sources, PriceVersion, Quota);
    }

    private sealed class Requests(RemoteHub hub) : IRemoteHistoryRequests
    {
        public void RequestHistory(string hostId, IReadOnlyDictionary<string, string> digests) => hub.RequestHistory(hostId, digests);
    }
}
