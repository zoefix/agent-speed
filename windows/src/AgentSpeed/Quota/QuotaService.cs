using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Quota;

/// <summary>What the quota readers may do: the switches, the sources whose quota shows, the phase 2 routes and the network.</summary>
internal sealed record QuotaInputs(QuotaSettings Quota, IReadOnlySet<SourceKind> Shown, IReadOnlySet<QuotaRoute> Routes, bool Network)
{
    public static QuotaInputs Default { get; } = Of(AppSettings.Default);

    public static QuotaInputs Of(AppSettings settings) =>
        new(settings.Quota, QuotaSettings.Sources.Where(settings.ShowsQuota).ToHashSet(), settings.QuotaRoutes, settings.NetworkEnabled);

    public bool Equals(QuotaInputs? other) =>
        other is not null && Quota == other.Quota && Network == other.Network && Shown.SetEquals(other.Shown) && Routes.SetEquals(other.Routes);

    public override int GetHashCode() => HashCode.Combine(Quota, Network, Shown.Count, Routes.Count);
}

/// <summary>
/// The quotas of this computer (QUOTAS): Codex from the live monitor's rollouts, Claude Desktop from its cache, the
/// Claude CLI from the bridge's quota file, the phase 2 readers of QUOTAS 9 through the vendor service, every remote
/// host's lines (REMOTE 11), and which of their apps run and are signed in. Polls on a worker every 5 s (each reader
/// keeps its own cadence; a vendor round every 15 s runs beside it) and publishes <see cref="State"/> on the UI thread.
/// Nothing runs for a source that is disabled or a switch that is off.
/// </summary>
internal sealed class QuotaService : IDisposable
{
    private const long TickMs = 5_000;
    private const long BridgeCheckMs = 60_000;
    private const long DesktopListingMs = 60_000;

    /// <summary>
    /// Desktop only refreshes its cached response while signed in and in use (when its window comes to the front, then
    /// every few minutes while it stays there), so a Desktop left in the background keeps an older one (QUOTAS 6).
    /// </summary>
    internal const long DesktopSignedInMs = 24 * 3_600_000L;

    private const long VendorRoundMs = 15_000;

    private readonly Dispatcher _ui;
    private readonly Func<QuotaSnapshot?> _codex;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly DesktopQuotaReader _desktop;
    private readonly ClaudeCliQuotaReader _cli;
    private readonly QuotaSignIn _signIn = QuotaSignIn.ForThisComputer();
    private readonly QuotaPresence? _presence = QuotaPresence.ForThisComputer();
    private readonly QuotaVendorService _vendor;
    private readonly IZcodeKeyStore _zcodeKeys;
    private readonly AntigravityProcessLocator? _antigravity;
    private readonly QuotaHttpTransport _transport = new();
    private readonly Lock _hostsGate = new();
    private ImmutableDictionary<string, HostQuotaState> _hosts = ImmutableDictionary<string, HostQuotaState>.Empty;
    private volatile QuotaInputs _inputs = QuotaInputs.Default;
    private volatile QuotaVendorReadings _readings = QuotaVendorReadings.Empty;
    private volatile bool _vendorInFlight;
    private volatile bool _vendorNow = true;
    private volatile bool _readLocalNow = true;
    private volatile bool _dashboardOpen;
    private volatile bool _zcodeKeySaved;
    private long _vendorRoundMs = long.MinValue;
    private volatile bool _resetDesktop;
    private volatile bool _cardDemand;
    private long _bridgeCheckedMs = long.MinValue;
    private long _listedMs = long.MinValue;
    private volatile bool _helperEnsured;
    private volatile ClaudeCliBridgeStatus _cliStatus = ClaudeCliBridgeStatus.Off;
    private volatile QuotaState? _demo;
    private volatile bool _disposed;

    /// <param name="sandboxed">The developer capture: no browser is read, the ZCode key stays in memory and no vendor is asked.</param>
    public QuotaService(Dispatcher ui, Func<QuotaSnapshot?> codex, bool sandboxed = false)
    {
        _ui = ui;
        _codex = codex;
        Bridge = new ClaudeCliBridge(ClaudeCliPaths.Default(dataFolder: AppPaths.DataDirectory), OpenHelper);
        _desktop = new DesktopQuotaReader(DesktopCacheFolders);
        _cli = new ClaudeCliQuotaReader(Bridge.Paths.QuotaFile);
        _zcodeKeys = sandboxed || !OperatingSystem.IsWindows() ? new MemoryZcodeKeyStore() : new CredentialManagerZcodeKeyStore();
        _antigravity = OperatingSystem.IsWindows() && !sandboxed ? new AntigravityProcessLocator(Home) : null;
        _vendor = new QuotaVendorService(new QuotaVendorEnvironment
        {
            Home = Home,
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Transport = sandboxed ? new RefusingTransport() : _transport,
            UserAgent = HttpFetchRequest.UserAgentFor(AppPaths.Version),
            ZcodeKey = _zcodeKeys.Read,
            Browser = sandboxed ? null : new BrowserCookieReader(),
            Antigravity = _antigravity,
        })
        {
            Log = message => AppLog.Info("Quota: " + message),
        };
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ClaudeCliBridge Bridge { get; }

    /// <summary>The latest quotas; set on the UI thread.</summary>
    public QuotaState State { get; private set; } = QuotaState.Empty;

    /// <summary>Raised on the UI thread when <see cref="State"/> changed.</summary>
    public event Action? Changed;

    public void Start()
    {
        ThreadPool.QueueUserWorkItem(_ => _zcodeKeySaved = _zcodeKeys.HasKey());
        _timer.Change(0, TickMs);
    }

    /// <summary>The switches, the enabled sources or the network changed: a source turned off forgets its quota at once.</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var next = QuotaInputs.Of(settings);
        var before = _inputs;
        if (next.Equals(before))
        {
            return;
        }

        _resetDesktop |= !next.Shown.Contains(SourceKind.ClaudeDesktop) && before.Shown.Contains(SourceKind.ClaudeDesktop);
        if (next.Quota.ClaudeCli != before.Quota.ClaudeCli)
        {
            Interlocked.Exchange(ref _bridgeCheckedMs, long.MinValue);
            _helperEnsured = false;
        }

        if (next.Quota.ZcodeRegion != before.Quota.ZcodeRegion)
        {
            _vendor.Forget(QuotaRoute.Zcode);
        }

        _inputs = next;
        _vendorNow = true;
        _readLocalNow = true;
        Poke();
    }

    /// <summary>The dashboard is open: vendor requests may run while no agent does (QUOTAS 9.1).</summary>
    public void SetDashboardOpen(bool open)
    {
        if (_dashboardOpen == open)
        {
            return;
        }

        _dashboardOpen = open;
        if (open)
        {
            _vendorNow = true;
            Poke();
        }
    }

    /// <summary>Saves a pasted ZCode key to Credential Manager; the field then shows it as saved and never shows it again.</summary>
    public bool SaveZcodeKey(string text)
    {
        if (ZcodeKey.Cleaned(text) is not { } key || !_zcodeKeys.Save(key))
        {
            return false;
        }

        _zcodeKeySaved = true;
        ForgetZcode();
        return true;
    }

    public void RemoveZcodeKey()
    {
        _zcodeKeys.Remove();
        _zcodeKeySaved = false;
        ForgetZcode();
    }

    /// <summary>A new key or region asks at once and never shows the previous key's numbers.</summary>
    private void ForgetZcode()
    {
        _vendor.Forget(QuotaRoute.Zcode);
        _vendorNow = true;
        Poke();
    }

    /// <summary>A host's quota line (REMOTE 11.1), from the remote event thread; <paramref name="shows"/> says whether its source shows.</summary>
    public void ApplyRemote(RemoteQuotaEventArgs line, bool shows)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_hostsGate)
        {
            var state = _hosts.GetValueOrDefault(line.HostId) ?? HostQuotaState.Empty;
            if (line.Report is { } report)
            {
                if (!shows)
                {
                    return;
                }

                state = state with { Reports = state.Reports.SetItem(report.Source, report) };
            }
            else if (line.RemovedSource is { } source)
            {
                state = state with { Reports = state.Reports.Remove(source) };
            }
            else if (line.Bridge is { } bridge)
            {
                state = state with { Bridge = bridge };
            }

            _hosts = _hosts.SetItem(line.HostId, state);
        }

        Poke();
    }

    /// <summary>Removed hosts and switched-off sources leave at once; a host whose bridge is off forgets its state.</summary>
    public void PruneHosts(RemoteHostsDocument document, Func<SourceKind, RemoteHost, bool> shows)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shows);
        lock (_hostsGate)
        {
            var next = ImmutableDictionary.CreateBuilder<string, HostQuotaState>();
            foreach (var (id, state) in _hosts)
            {
                if (document.Hosts.FirstOrDefault(h => h.Id == id) is not { } host)
                {
                    continue;
                }

                next[id] = state with
                {
                    Reports = state.Reports.Where(p => shows(p.Key, host)).ToImmutableDictionary(),
                    Bridge = host.QuotaClaudeCli ? state.Bridge : null,
                };
            }

            _hosts = next.ToImmutable();
        }

        Poke();
    }

    /// <summary>An orb shows the 額度 card (a character orb with the readout on): the running apps are listed every 15 s.</summary>
    public void SetCardDemand(bool demand)
    {
        if (_cardDemand == demand)
        {
            return;
        }

        _cardDemand = demand;
        _listedMs = long.MinValue;
        Poke();
    }

    /// <summary>Settings opened: the bridge's status is checked again now, and the local files are read again.</summary>
    public void RefreshBridgeStatus()
    {
        Interlocked.Exchange(ref _bridgeCheckedMs, long.MinValue);
        _readLocalNow = true;
        _vendorNow = true;
        Poke();
    }

    /// <summary>Fixed quotas for the developer capture (<c>op quota-demo</c>); null returns to the real readers.</summary>
    public void SetDemo(QuotaState? demo)
    {
        _demo = demo;
        if (demo is null)
        {
            Poke();
        }
        else
        {
            Publish(demo);
        }
    }

    /// <summary>Turns the bridge on (QUOTAS 5.2), on the caller's thread; the status says how it went.</summary>
    public ClaudeCliBridgeStatus TurnOnBridge()
    {
        var status = Bridge.TurnOn();
        _cliStatus = status;
        Interlocked.Exchange(ref _bridgeCheckedMs, Now());
        AppLog.Info($"Quota: the Claude Code status line bridge was turned on ({status}).");
        Poke();
        return status;
    }

    /// <summary>Turns the bridge off (QUOTAS 5.3) at once.</summary>
    public void TurnOffBridge()
    {
        Bridge.TurnOff();
        _cliStatus = ClaudeCliBridgeStatus.Off;
        AppLog.Info("Quota: the Claude Code status line bridge was turned off.");
        Poke();
    }

    private void Poke()
    {
        if (!_disposed)
        {
            _timer.Change(0, TickMs);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Tick()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        QuotaState next;
        try
        {
            next = Build(Now());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Quota: a poll failed.", ex);
            return;
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        if (_demo is null)
        {
            Publish(next);
        }
    }

    /// <summary>One poll of every switched-on reader; under the gate.</summary>
    private QuotaState Build(long now)
    {
        var inputs = _inputs;
        var settings = inputs.Quota;
        var shown = inputs.Shown;
        var routes = inputs.Routes;
        var listing = _cardDemand || (shown.Contains(SourceKind.ClaudeDesktop) && now - Interlocked.Read(ref _listedMs) >= DesktopListingMs)
            || (routes.Count > 0 && now - Interlocked.Read(ref _listedMs) >= QuotaPresence.ListIntervalMs);
        if (_presence is { } presence && listing)
        {
            presence.Refresh(now, force: _listedMs == long.MinValue);
            _listedMs = now;
            if (presence.ClaudeDesktopStarted)
            {
                _desktop.DesktopStarted();
            }
        }

        if (_resetDesktop)
        {
            _resetDesktop = false;
            _desktop.Reset();
        }

        if (shown.Contains(SourceKind.ClaudeDesktop))
        {
            _desktop.Poll(_presence?.ClaudeDesktop ?? false);
        }

        if (settings.ClaudeCli)
        {
            if (!_helperEnsured)
            {
                _helperEnsured = Bridge.EnsureHelper();
            }

            // The bridge stays switchable while Claude Code is disabled (it can always be restored); its file is not read.
            if (shown.Contains(SourceKind.ClaudeCode))
            {
                _cli.Poll();
            }
            if (now - Interlocked.Read(ref _bridgeCheckedMs) >= BridgeCheckMs)
            {
                _cliStatus = Bridge.Status();
                Interlocked.Exchange(ref _bridgeCheckedMs, now);
            }
            else if (_cliStatus is ClaudeCliBridgeStatus.Waiting or ClaudeCliBridgeStatus.Ok)
            {
                // Between the settings checks, the quota file (checked every 5 s) tells waiting from connected.
                _cliStatus = _cli.FileTime is null ? ClaudeCliBridgeStatus.Waiting : ClaudeCliBridgeStatus.Ok;
            }
        }
        else
        {
            _cliStatus = ClaudeCliBridgeStatus.Off;
        }

        StartVendorRound(now, inputs);
        var readings = _readings;
        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        if (shown.Contains(SourceKind.Codex) && _codex() is { } codex)
        {
            snapshots[SourceKind.Codex] = codex;
        }

        if (shown.Contains(SourceKind.ClaudeDesktop) && _desktop.Snapshot is { } desktop)
        {
            snapshots[SourceKind.ClaudeDesktop] = desktop;
        }

        if (shown.Contains(SourceKind.ClaudeCode) && _cli.Snapshot is { } cli)
        {
            snapshots[SourceKind.ClaudeCode] = cli;
        }

        foreach (var source in shown.Where(s => !IsPhaseOne(s)))
        {
            if (readings.Snapshot(source) is { } snapshot)
            {
                snapshots[source] = snapshot;
            }
        }

        var active = ImmutableHashSet.CreateBuilder<SourceKind>();
        if (_cardDemand && _presence is { } p)
        {
            if (p.Codex && snapshots.ContainsKey(SourceKind.Codex) && _signIn.Codex)
            {
                active.Add(SourceKind.Codex);
            }

            if (p.ClaudeCode && snapshots.ContainsKey(SourceKind.ClaudeCode) && _signIn.ClaudeCode)
            {
                active.Add(SourceKind.ClaudeCode);
            }

            if (DesktopOnCard(p.ClaudeDesktop, snapshots.GetValueOrDefault(SourceKind.ClaudeDesktop), now))
            {
                active.Add(SourceKind.ClaudeDesktop);
            }

            foreach (var source in snapshots.Keys.Where(s => !IsPhaseOne(s)))
            {
                var runs = p.Runs(source) || (source == SourceKind.Antigravity && readings.AntigravityRuns);
                if (runs && readings.SignedIn(source))
                {
                    active.Add(source);
                }
            }
        }

        return new QuotaState
        {
            Snapshots = snapshots.ToImmutable(),
            Active = active.ToImmutable(),
            CliStatus = _cliStatus,
            CliFileTime = settings.ClaudeCli ? _cli.FileTime : null,
            RouteStatuses = readings.Routes.Where(r => routes.Contains(r.Key) && r.Value.Status is not null)
                .ToImmutableDictionary(r => r.Key, r => r.Value.Status!.Value),
            ZcodeKeySaved = _zcodeKeySaved,
            Hosts = _hosts,
        };
    }

    private static bool IsPhaseOne(SourceKind source) => source is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.ClaudeDesktop;

    /// <summary>Claude Desktop is on the 額度 card while it runs and its snapshot is at most <see cref="DesktopSignedInMs"/> old.</summary>
    internal static bool DesktopOnCard(bool runs, QuotaSnapshot? snapshot, long nowMs) =>
        runs && snapshot is not null && nowMs - snapshot.ObservedMs <= DesktopSignedInMs;

    /// <summary>A vendor round every 15 s (sooner when something changed), beside the file round; the service keeps its own cadence.</summary>
    private void StartVendorRound(long now, QuotaInputs inputs)
    {
        if (_vendorInFlight || (inputs.Routes.Count == 0 && _readings.Routes.IsEmpty))
        {
            return;
        }

        if (!_vendorNow && now - Interlocked.Read(ref _vendorRoundMs) < VendorRoundMs)
        {
            return;
        }

        _vendorNow = false;
        Interlocked.Exchange(ref _vendorRoundMs, now);
        _vendorInFlight = true;
        var round = new QuotaVendorRound(now, inputs.Network, inputs.Routes, _presence?.Running ?? new HashSet<SourceKind>(), _dashboardOpen,
            inputs.Quota.ZcodeRegion, _readLocalNow);
        _readLocalNow = false;
        _ = Task.Run(async () =>
        {
            try
            {
                _readings = await _vendor.RefreshAsync(round).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Quota: a vendor round failed.", ex);
            }
            finally
            {
                _vendorInFlight = false;
            }

            Poke();
        });
    }

    private void Publish(QuotaState next)
    {
        _ui.BeginInvoke(() =>
        {
            if (next.Equals(State))
            {
                return;
            }

            State = next;
            Changed?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// QUOTAS 4.1 on Windows: <c>%APPDATA%\Claude</c>, and the Microsoft Store build's packaged copy (unverified). The
    /// developer variable <c>AGENT_SPEED_CLAUDE_CACHE_DIR</c> (a fully qualified local folder) replaces them for a test session.
    /// </summary>
    private static IReadOnlyList<string> DesktopCacheFolders()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CLAUDE_CACHE_DIR") is { Length: > 0 } test && Path.IsPathFullyQualified(test)
            && !test.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return [test];
        }

        var folders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "Cache", "Cache_Data"),
        };
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
            {
                folders.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "Cache", "Cache_Data")));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only the classic folder then.
        }

        return folders;
    }

    /// <summary>The helper beside the app's executable, else the copy embedded for this architecture (QUOTAS 5.1).</summary>
    private static Stream? OpenHelper()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, ClaudeCliQuotaNames.HelperFile);
        if (File.Exists(beside))
        {
            return File.OpenRead(beside);
        }

        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream($"AgentSpeed.StatuslineHelper.{rid}.exe");
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        lock (_gate)
        {
            // A poll still running finishes before the process listing goes away.
            _presence?.Dispose();
            _antigravity?.Dispose();
        }

        _transport.Dispose();
    }
}

/// <summary>The developer capture's transport: nothing leaves the computer.</summary>
internal sealed class RefusingTransport : IQuotaHttpTransport
{
    public Task<QuotaHttpResponse> SendAsync(QuotaHttpRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<QuotaHttpResponse>(new QuotaHttpException(QuotaHttpFailure.DisallowedUrl, "capture"));
}

/// <summary>The helper's file name.</summary>
internal static class ClaudeCliQuotaNames
{
    public const string HelperFile = "agent-speed-statusline.exe";
}
