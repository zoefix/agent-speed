using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Core.Lan;
using AgentSpeed.Core.Lan.Windows;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Services;

namespace AgentSpeed.Lan;

/// <summary>
/// The local network feed on the UI thread (LAN, DESIGN 14): follows the <c>lan</c> settings and the network master switch,
/// feeds every engine tick's live values (the report's computation, METRICS 11.2) to the feed whether or not reporting is on,
/// and edits the settings for Settings → API → 區域網路. With <c>--demo-speed</c> the pinned demo values feed it instead.
/// </summary>
internal sealed class LanController : IDisposable
{
    private static readonly TimeSpan DemoTick = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher _ui;
    private readonly SettingsService _settings;
    private readonly SpeedHub _hub;
    private readonly LanService _service;
    private DispatcherTimer? _demo;
    private int _changedPosted;
    private bool _started;

    public LanController(Dispatcher ui, SettingsService settings, SpeedHub hub, string version)
    {
        _ui = ui;
        _settings = settings;
        _hub = hub;
        _service = new LanService(new LanServiceOptions
        {
            Advertiser = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? new LanDnsSd() : null,
            UserAgent = $"AgentSpeed/{version} (Windows)",
        });
        _service.Changed += PostChanged;
    }

    /// <summary>A status, the settings or the addresses changed; on the UI thread, at most once per dispatcher turn.</summary>
    public event EventHandler? Changed;

    public LanSettings Settings => _settings.Current.Lan;

    public bool NetworkEnabled => _settings.Current.NetworkEnabled;

    public LanServerStatus ServerStatus => _service.ServerStatus;

    public LanTargetStatus TargetStatus(string id) => _service.TargetStatus(id);

    /// <summary>This computer's local IPv4 addresses (DESIGN 14, Address); refreshed when the network changes.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; private set; } = [];

    public static string ComputerName
    {
        get
        {
            try
            {
                return Dns.GetHostName();
            }
            catch (System.Net.Sockets.SocketException)
            {
                return Environment.MachineName;
            }
        }
    }

    /// <param name="demo">The pinned demo speeds publish previews: tick the feed from them every 500 ms instead.</param>
    public void Start(bool demo)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        RefreshAddresses();
        if (Settings.WantsFeed && Settings.Instance is null)
        {
            Update(lan => lan.WithInstance());
        }

        Apply();
        if (demo)
        {
            _demo = new DispatcherTimer(DemoTick, DispatcherPriority.Background, (_, _) => Feed(_hub.Snapshot), _ui);
            _demo.Start();
        }
        else
        {
            _hub.LivePublished += OnLive;
        }
    }

    public void SetServerEnabled(bool enabled) => Update(lan => lan.WithServerEnabled(enabled));

    public void RegenerateKey() => Update(lan => lan.WithNewKey());

    public void SetPort(int port) => Update(lan => lan.WithPort(port));

    public void SaveTarget(LanTarget target) => Update(lan => lan.Saving(target));

    public void RemoveTarget(string id) => Update(lan => lan.Removing(id));

    public void SetTargetEnabled(string id, bool enabled) => Update(lan => lan.SettingEnabled(id, enabled));

    /// <summary><i>Send Test</i>: the current frame once to an address that is not saved (DESIGN 14).</summary>
    public Task<LanTargetStatus> TestAsync(LanTargetKind kind, string address, string? key, CancellationToken token)
    {
        if (Settings.Instance is null)
        {
            Update(lan => lan.WithInstance());
        }

        return _service.TestAsync(kind, address, key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), token);
    }

    public void Dispose()
    {
        _hub.LivePublished -= OnLive;
        _settings.Changed -= OnSettingsChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _demo?.Stop();
        _service.Changed -= PostChanged;
        _service.Dispose();
    }

    private void Update(Func<LanSettings, LanSettings> change) => _settings.Update(s => s with { Lan = change(s.Lan) });

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!Equals(e.Previous.Lan, e.Current.Lan) || e.Previous.NetworkEnabled != e.Current.NetworkEnabled)
        {
            Apply();
        }
    }

    private void Apply()
    {
        _service.Apply(Settings, NetworkEnabled, ComputerName);
        PostChanged();
    }

    private void OnLive(object? sender, SpeedSnapshot snapshot) => Feed(snapshot);

    /// <summary>One engine tick: the values every enabled source of every host has now, as the report reads them.</summary>
    private void Feed(SpeedSnapshot snapshot)
    {
        if (snapshot.State.Kind == MonitorStateKind.Starting || !NetworkEnabled || !Settings.WantsFeed)
        {
            return;
        }

        var values = LanTickValues.From(ReportSample.From(snapshot, ReportTrafficSample.Off));
        _service.Tick(values, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void OnAddressChanged(object? sender, EventArgs e) => _ui.BeginInvoke(() =>
    {
        RefreshAddresses();
        PostChanged();
    });

    private void RefreshAddresses() => Addresses = LanInterfaces.Addresses();

    private void PostChanged()
    {
        if (Interlocked.Exchange(ref _changedPosted, 1) == 1)
        {
            return;
        }

        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _changedPosted, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
