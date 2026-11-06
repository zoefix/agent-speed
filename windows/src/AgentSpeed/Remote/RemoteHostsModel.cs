using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Services;

namespace AgentSpeed.Remote;

/// <summary>
/// The remote hosts as every surface sees them (DESIGN 13), on the UI thread: the settings' hosts with their connection
/// statuses, <c>historyTooLarge</c> merged in, today's sessions, the pickers' choices and the edits of REMOTE 2.1 and 2.4.
/// <see cref="Changed"/> fires when a status, the hosts or a too-large flag change.
/// </summary>
internal sealed class RemoteHostsModel
{
    private readonly SettingsService _settings;
    private readonly HistoryHub _history;
    private readonly SpeedModel _speed;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, RemoteHostStatus> _statuses = new(StringComparer.Ordinal);
    private RemoteHostsService? _service;
    private IReadOnlySet<string> _tooLarge = new HashSet<string>(StringComparer.Ordinal);

    public RemoteHostsModel(SettingsService settings, HistoryHub history, SpeedModel speed, Dispatcher dispatcher)
    {
        _settings = settings;
        _history = history;
        _speed = speed;
        _dispatcher = dispatcher;
        _settings.Changed += (_, e) =>
        {
            if (e.Previous.RemoteHosts != e.Current.RemoteHosts || e.Previous.NetworkEnabled != e.Current.NetworkEnabled)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        _history.Changed += (_, _) =>
        {
            if (!_tooLarge.SetEquals(_history.RemoteTooLarge))
            {
                _tooLarge = _history.RemoteTooLarge;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? Changed;

    public RemoteHostsDocument Document => _settings.Current.RemoteHosts;

    public bool HasHosts => !Document.Hosts.IsEmpty;

    public bool NetworkEnabled => _settings.Current.NetworkEnabled;

    /// <summary>The connections of the app; set once the live monitor runs.</summary>
    public void Attach(RemoteHostsService service)
    {
        _service = service;
        service.Coordinator.StatusChanged += (_, e) => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnStatus(e.HostId, e.Status));
        foreach (var (id, status) in service.Coordinator.Statuses)
        {
            _statuses[id] = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The status <paramref name="host"/> shows (settings first, then its connection; <c>historyTooLarge</c> merged in).</summary>
    public RemoteHostStatus StatusOf(RemoteHost host) =>
        RemoteHostStates.Effective(host, NetworkEnabled, _statuses.GetValueOrDefault(host.Id), _tooLarge.Contains(host.Id));

    /// <summary>The status of a configured host; null for this computer and for a removed host.</summary>
    public RemoteHostStatus? StatusOf(string hostId) => Document.Host(hostId) is { } host ? StatusOf(host) : null;

    /// <summary>The dot of a host id in a picker or menu: this computer is always connected.</summary>
    public RemoteDotKind DotOf(string hostId) => StatusOf(hostId) is { } status ? RemoteDots.Of(status.Status) : RemoteDotKind.Connected;

    /// <summary>The short word of a host that is not connected (pickers, menus, dashboard); null while connected and for this computer.</summary>
    public string? WordOf(string hostId, Localizer loc) =>
        StatusOf(hostId) is { Status.Kind: not RemoteStatusKind.Connected } status ? RemoteStatusText.Word(status.Status, loc) : null;

    /// <summary>Today's main sessions on a host (the status line's agent count).</summary>
    public int SessionsOf(string hostId) => _speed.Snapshot.Hosts.FirstOrDefault(h => h.Id == hostId)?.Sessions ?? 0;

    /// <summary>The name a remote host goes by (the settings' name, else the last one the monitor knew); null for this computer.</summary>
    public string? NameOf(string hostId) =>
        hostId == RemoteHost.LocalId ? null : Document.DisplayName(hostId) ?? _speed.Snapshot.Hosts.FirstOrDefault(h => h.Id == hostId)?.Name;

    /// <summary>This computer, then every remote host in settings order; empty without remote hosts.</summary>
    public IReadOnlyList<OrbHostChoice> Choices(Localizer loc) => RemoteHostStates.Choices(Document, loc);

    public IReadOnlyList<string> Order => RemoteHostStates.Order(Document);

    /// <summary>The Hosts submenus' dots and words, by host id.</summary>
    public IReadOnlyDictionary<string, MenuHostState> MenuStates(Localizer loc)
    {
        var states = new Dictionary<string, MenuHostState>(StringComparer.Ordinal)
        {
            [RemoteHost.LocalId] = MenuHostState.Default(RemoteHost.LocalId),
        };
        foreach (var host in Document.Hosts)
        {
            states[host.Id] = new MenuHostState(DotOf(host.Id), WordOf(host.Id, loc));
        }

        return states;
    }

    // ---- Editing ----

    /// <summary>Appends a host; an enabled one connects at once. False at the limit or with a taken id.</summary>
    public bool Add(RemoteHost host)
    {
        var added = false;
        _settings.Update(s =>
        {
            if (s.RemoteHosts.Adding(host) is not { } next)
            {
                return s;
            }

            added = true;
            return s with { RemoteHosts = next };
        });
        return added;
    }

    /// <summary>Saves an edit; a host that is not connected tries again at once (REMOTE 4.4).</summary>
    public void Update(RemoteHost host)
    {
        _settings.Update(s => s.RemoteHosts.Updating(host) is { } next ? s with { RemoteHosts = next } : s);
        if (host.Enabled && StatusOf(host.Id)?.Status.Kind != RemoteStatusKind.Connected)
        {
            // After the coordinator applied the edit, so the retry reaches the new connection.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => RetryNow(host.Id));
        }
    }

    public void SetEnabled(string hostId, bool enabled) => _settings.Update(s =>
        s.RemoteHosts.Host(hostId) is { } host && host.Enabled != enabled
        && s.RemoteHosts.Updating(host with { Enabled = enabled }) is { } next
            ? s with { RemoteHosts = next }
            : s);

    public void RetryNow(string hostId)
    {
        try
        {
            _service?.Coordinator.RetryNow(hostId);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// REMOTE 2.4: the connection stops, the host leaves every orb and board selection (an emptied one becomes all), and its
    /// history goes only when asked.
    /// </summary>
    public void Remove(string hostId, bool deleteHistory)
    {
        if (Document.Host(hostId) is null)
        {
            return;
        }

        _settings.Update(s => s.WithoutRemoteHost(hostId));
        _statuses.Remove(hostId);
        if (deleteHistory)
        {
            try
            {
                _service?.History.DeleteHistory(hostId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                AppLog.Warn("Deleting a remote host's history failed.", ex);
            }
        }
    }

    /// <summary><i>Test Connection</i>: the probe of REMOTE 4.2 step 1, its outcome delivered on the UI thread.</summary>
    public RemoteConnectionTest StartTest(RemoteHost host, Action<RemoteTestOutcome> completion) =>
        RemoteConnectionTest.Start(host, AppPaths.Version, outcome => _dispatcher.BeginInvoke(() => completion(outcome)));

    /// <summary>
    /// Developer capture only: shows the clock note and the history note on a host until its connection or the history
    /// reports again, so their layout can be checked without a skewed clock or a huge history.
    /// </summary>
    internal void PreviewNotes(string hostId)
    {
        if (Document.Host(hostId) is not { } host)
        {
            return;
        }

        _statuses[hostId] = StatusOf(host) with { ClockOffsetMs = 12_400 };
        _tooLarge = new HashSet<string>(_tooLarge, StringComparer.Ordinal) { hostId };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatus(string hostId, RemoteHostStatus status)
    {
        if (Document.Host(hostId) is null)
        {
            return;
        }

        if (_statuses.TryGetValue(hostId, out var previous) && previous == status)
        {
            return;
        }

        _statuses[hostId] = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
