using System.Net;

namespace AgentSpeed.Core.Lan;

/// <summary>Registers the server for discovery (LAN 4.1): DNS-SD <c>_agentspeed._tcp</c>; a failure is only logged.</summary>
public interface ILanAdvertiser : IDisposable
{
    void Register(string name, int port, string instance);

    void Unregister();
}

public sealed record LanServiceOptions
{
    public ILanResolver Resolver { get; init; } = SystemLanResolver.Instance;

    public ILanAdvertiser? Advertiser { get; init; }

    /// <summary>The server's address; null for every interface.</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>The HTTP targets' <c>User-Agent</c>: <c>AgentSpeed/1.7.0 (Windows)</c>.</summary>
    public string UserAgent { get; init; } = "AgentSpeed";

    /// <summary>Monotonic milliseconds; <see cref="Environment.TickCount64"/> by default.</summary>
    public Func<long>? Clock { get; init; }
}

/// <summary>
/// The local network feed (LAN 2–5) for one owner thread: follows the settings and the network master switch, builds a frame
/// at every engine tick while the server listens or a target is enabled, and hands it to the server and the targets.
/// </summary>
public sealed class LanService : IDisposable
{
    public const int AdvertisedNameBytes = 63;

    private readonly object _gate = new();
    private readonly LanFeed _feed = new();
    private readonly LanServer _server;
    private readonly LanTargets _targets;
    private readonly ILanAdvertiser? _advertiser;
    private readonly Func<long> _clock;
    private LanSettings _settings = LanSettings.Default;
    private bool _network;
    private string _name = LanText.DefaultComputerName;
    private LanTickValues _latestValues = LanTickValues.Empty;
    private (string Name, int Port, string Instance)? _advertised;

    public LanService(LanServiceOptions? options = null)
    {
        options ??= new LanServiceOptions();
        _clock = options.Clock ?? (() => Environment.TickCount64);
        _server = new LanServer(options.BindAddress, _clock);
        _targets = new LanTargets(options.Resolver, options.UserAgent, _clock);
        _advertiser = options.Advertiser;
        _server.Changed += OnServerChanged;
        _targets.Changed += RaiseChanged;
    }

    /// <summary>A status changed (server, devices or a target); raised on any thread.</summary>
    public event Action? Changed;

    public string Run => _feed.Run;

    /// <summary>The server is on, the network switch too: the port is wanted (LAN 4.1).</summary>
    public bool ServerWanted => _network && _settings.Server.Enabled && LanKey.IsValid(_settings.Server.Key);

    public bool FeedRunning => _feed.Running;

    public LanFrame? Latest => _feed.Latest;

    public int BoundPort => _server.BoundPort;

    public LanServerStatus ServerStatus
    {
        get
        {
            if (!_settings.Server.Enabled)
            {
                return LanServerStatus.Off;
            }

            return _network ? _server.Status : new LanServerStatus(LanServerStatusKind.NetworkOff, _settings.Server.Port);
        }
    }

    public LanTargetStatus TargetStatus(string id) => _targets.StatusOf(id);

    /// <summary>Follows the settings, the master switch (LAN 2.2) and the computer's name.</summary>
    public void Apply(LanSettings settings, bool networkEnabled, string computerName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _network = networkEnabled;
        _name = computerName;
        var feed = networkEnabled && settings.WantsFeed && settings.Instance is not null;
        if (!feed && _feed.Running)
        {
            _feed.Stop();
        }

        if (ServerWanted)
        {
            _server.Start(settings.Server.Port, settings.Server.Key!);
        }
        else
        {
            _server.Stop();
        }

        _targets.Apply(settings.Targets, networkEnabled);
        Advertise();
        RaiseChanged();
    }

    /// <summary>An engine tick (every 500 ms) with its live values; a frame is built while the feed runs (LAN 3.3).</summary>
    public void Tick(LanTickValues values, long wallMs)
    {
        ArgumentNullException.ThrowIfNull(values);
        _latestValues = values;
        if (!_network || !_settings.WantsFeed || _settings.Instance is not { } instance)
        {
            return;
        }

        var now = _clock();
        var (frame, changed) = _feed.Tick(now, wallMs, instance, _name, values);
        _server.Publish(frame, changed, now);
        _targets.Publish(frame, changed, now);
    }

    /// <summary><i>Send Test</i>: the latest frame once, or a frame built for it while the feed does not run.</summary>
    public Task<LanTargetStatus> TestAsync(LanTargetKind kind, string address, string? key, long wallMs, CancellationToken token)
    {
        if (!_network)
        {
            return Task.FromResult(LanTargetStatus.NetworkOff);
        }

        var frame = _feed.Latest ?? _feed.BuildOnce(wallMs, _settings.Instance ?? Reporting.ReportCanonical.NewUuid(), _name, _latestValues);
        return _targets.TestAsync(kind, address, key, frame, token);
    }

    public void Dispose()
    {
        _server.Changed -= OnServerChanged;
        _targets.Changed -= RaiseChanged;
        _server.Dispose();
        _targets.Dispose();
        lock (_gate)
        {
            if (_advertised is not null)
            {
                _advertiser?.Unregister();
                _advertised = null;
            }
        }

        _advertiser?.Dispose();
    }

    private void OnServerChanged()
    {
        Advertise();
        RaiseChanged();
    }

    /// <summary>While the server listens, its DNS-SD service is registered with the computer's name and the instance id.</summary>
    private void Advertise()
    {
        if (_advertiser is null)
        {
            return;
        }

        var status = _server.Status;
        lock (_gate)
        {
            (string, int, string)? wanted = status.Kind == LanServerStatusKind.Listening && _settings.Instance is { } instance
                ? (LanText.CutUtf8(LanText.ComputerName(_name), AdvertisedNameBytes), _server.BoundPort, instance)
                : null;
            if (wanted == _advertised)
            {
                return;
            }

            if (_advertised is not null)
            {
                _advertiser.Unregister();
            }

            _advertised = wanted;
            if (wanted is var (name, port, id))
            {
                _advertiser.Register(name, port, id);
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
