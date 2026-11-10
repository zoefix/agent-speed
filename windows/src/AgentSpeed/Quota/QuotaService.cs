using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Quota;

/// <summary>
/// The quotas of this computer (QUOTAS): Codex from the live monitor's rollouts, Claude Desktop from its cache, the
/// Claude CLI from the bridge's quota file, and which of their apps run and are signed in. Polls on a worker every
/// 5 s (each reader keeps its own cadence) and publishes <see cref="State"/> on the UI thread. Nothing runs for a
/// switch that is off.
/// </summary>
internal sealed class QuotaService : IDisposable
{
    private const long TickMs = 5_000;
    private const long BridgeCheckMs = 60_000;
    private const long DesktopListingMs = 60_000;
    private const long DesktopSignedInMs = 45 * 60_000;

    private readonly Dispatcher _ui;
    private readonly Func<QuotaSnapshot?> _codex;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly DesktopQuotaReader _desktop;
    private readonly ClaudeCliQuotaReader _cli;
    private readonly QuotaSignIn _signIn = QuotaSignIn.ForThisComputer();
    private readonly QuotaPresence? _presence = QuotaPresence.ForThisComputer();
    private volatile QuotaSettings _settings = QuotaSettings.Default;
    private volatile bool _resetDesktop;
    private volatile bool _cardDemand;
    private long _bridgeCheckedMs = long.MinValue;
    private long _listedMs = long.MinValue;
    private volatile bool _helperEnsured;
    private volatile ClaudeCliBridgeStatus _cliStatus = ClaudeCliBridgeStatus.Off;
    private volatile QuotaState? _demo;
    private volatile bool _disposed;

    public QuotaService(Dispatcher ui, Func<QuotaSnapshot?> codex)
    {
        _ui = ui;
        _codex = codex;
        Bridge = new ClaudeCliBridge(ClaudeCliPaths.Default(dataFolder: AppPaths.DataDirectory), OpenHelper);
        _desktop = new DesktopQuotaReader(DesktopCacheFolders);
        _cli = new ClaudeCliQuotaReader(Bridge.Paths.QuotaFile);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ClaudeCliBridge Bridge { get; }

    /// <summary>The latest quotas; set on the UI thread.</summary>
    public QuotaState State { get; private set; } = QuotaState.Empty;

    /// <summary>Raised on the UI thread when <see cref="State"/> changed.</summary>
    public event Action? Changed;

    public void Start() => _timer.Change(0, TickMs);

    /// <summary>The quota switches changed; a switch turned off forgets its source at once.</summary>
    public void Apply(QuotaSettings settings)
    {
        var before = _settings;
        _resetDesktop |= !settings.ClaudeDesktop && before.ClaudeDesktop;
        if (settings.ClaudeCli != before.ClaudeCli)
        {
            Interlocked.Exchange(ref _bridgeCheckedMs, long.MinValue);
            _helperEnsured = false;
        }

        _settings = settings;
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

    /// <summary>Settings opened: the bridge's status is checked again now.</summary>
    public void RefreshBridgeStatus()
    {
        Interlocked.Exchange(ref _bridgeCheckedMs, long.MinValue);
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
        var settings = _settings;
        var listing = _cardDemand || (settings.ClaudeDesktop && now - Interlocked.Read(ref _listedMs) >= DesktopListingMs);
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

        if (settings.ClaudeDesktop)
        {
            _desktop.Poll(_presence?.ClaudeDesktop ?? false);
        }

        if (settings.ClaudeCli)
        {
            if (!_helperEnsured)
            {
                _helperEnsured = Bridge.EnsureHelper();
            }

            _cli.Poll();
            if (now - Interlocked.Read(ref _bridgeCheckedMs) >= BridgeCheckMs)
            {
                _cliStatus = Bridge.Status();
                Interlocked.Exchange(ref _bridgeCheckedMs, now);
            }
        }
        else
        {
            _cliStatus = ClaudeCliBridgeStatus.Off;
        }

        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        if (settings.Codex && _codex() is { } codex)
        {
            snapshots[SourceKind.Codex] = codex;
        }

        if (settings.ClaudeDesktop && _desktop.Snapshot is { } desktop)
        {
            snapshots[SourceKind.ClaudeDesktop] = desktop;
        }

        if (settings.ClaudeCli && _cli.Snapshot is { } cli)
        {
            snapshots[SourceKind.ClaudeCode] = cli;
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

            if (p.ClaudeDesktop && snapshots.TryGetValue(SourceKind.ClaudeDesktop, out var d) && now - d.ObservedMs <= DesktopSignedInMs)
            {
                active.Add(SourceKind.ClaudeDesktop);
            }
        }

        return new QuotaState
        {
            Snapshots = snapshots.ToImmutable(),
            Active = active.ToImmutable(),
            CliStatus = _cliStatus,
            CliFileTime = settings.ClaudeCli ? _cli.FileTime : null,
        };
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

    /// <summary>QUOTAS 4.1 on Windows: <c>%APPDATA%\Claude</c>, and the Microsoft Store build's packaged copy (unverified).</summary>
    private static IReadOnlyList<string> DesktopCacheFolders()
    {
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
        _presence?.Dispose();
    }
}

/// <summary>The helper's file name.</summary>
internal static class ClaudeCliQuotaNames
{
    public const string HelperFile = "agent-speed-statusline.exe";
}
