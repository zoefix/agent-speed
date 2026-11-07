using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core.Lan;
using AgentSpeed.Lan;
using AgentSpeed.Presentation.Lan;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Reporting;

namespace AgentSpeed.Settings.Lan;

/// <summary>
/// Settings → API → 區域網路 (DESIGN 14): <i>Devices read</i> (the server switch, its status, access key, addresses and
/// port) and <i>Send to devices</i> (one row per target, Add Device… disabled at 8), and the footnote. The status lines
/// refresh every second while the pane is shown.
/// </summary>
internal sealed class LanPaneViewModel : ObservableObject
{
    public const string DocsUrl = "https://github.com/zoefix/agent-speed/blob/main/spec/LAN.md";
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    private readonly LanController _lan;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _copied;
    private readonly Func<bool> _confirmRegenerate;
    private Localizer _loc;
    private bool _visible;
    private bool _reduceMotion;
    private bool _keyCopied;
    private string _portText = string.Empty;
    private string? _portError;
    private string _serverStatusText = string.Empty;
    private RemoteDotKind _serverDot = RemoteDotKind.Idle;

    /// <param name="edit">Opens the Add / Edit sheet: a target to edit, or null to add one.</param>
    /// <param name="confirmRemove">Asks <c>lan.target.remove.confirm</c> with the target's name; true to remove.</param>
    /// <param name="confirmRegenerate">Asks <c>lan.key.regenerate.confirm</c>; true to make a new key.</param>
    public LanPaneViewModel(LanController lan, Localizer loc, Dispatcher dispatcher, Action<LanTarget?> edit, Func<string, bool> confirmRemove,
        Func<bool> confirmRegenerate)
    {
        _lan = lan;
        _loc = loc;
        _confirmRegenerate = confirmRegenerate;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatuses(), dispatcher);
        _tick.Stop();
        _copied = new DispatcherTimer(CopiedFor, DispatcherPriority.Background, (_, _) => KeyCopied = false, dispatcher);
        _copied.Stop();
        AddCommand = new RelayCommand(() => edit(null), () => _lan.Settings.CanAdd);
        EditCommand = new RelayCommand(p =>
        {
            if (p is LanTargetRowViewModel { Target: { } target })
            {
                edit(target);
            }
        });
        RemoveCommand = new RelayCommand(p =>
        {
            if (p is LanTargetRowViewModel { Target: { } target } row && confirmRemove(row.Name))
            {
                _lan.RemoveTarget(target.Id);
            }
        });
        CopyKeyCommand = new RelayCommand(CopyKey, () => _lan.Settings.Server.Key is not null);
        RegenerateCommand = new RelayCommand(() =>
        {
            if (_confirmRegenerate())
            {
                _lan.RegenerateKey();
            }
        });
        CopyCommand = new RelayCommand(p =>
        {
            if (p is string text)
            {
                ReportLinks.Copy(text);
            }
        });
        DocsCommand = new RelayCommand(() => ReportLinks.Open(DocsUrl));
        _lan.Changed += (_, _) => Sync();
        Sync();
    }

    public ObservableCollection<LanTargetRowViewModel> Targets { get; } = [];

    public ObservableCollection<LanAddressRow> Addresses { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand CopyKeyCommand { get; }

    public ICommand RegenerateCommand { get; }

    /// <summary>Copies the text it is given (the address pull-down's three forms).</summary>
    public ICommand CopyCommand { get; }

    public ICommand DocsCommand { get; }

    public bool NetworkEnabled => _lan.NetworkEnabled;

    public bool ServerEnabled
    {
        get => _lan.Settings.Server.Enabled;
        set
        {
            if (value != _lan.Settings.Server.Enabled)
            {
                _lan.SetServerEnabled(value);
            }
        }
    }

    /// <summary>The server's rows show while it is on (and the master switch too).</summary>
    public bool ShowsServerRows => ServerEnabled;

    /// <summary><c>lan.server.hint</c>, or <c>settings.network.offHint</c> while the master switch is off.</summary>
    public string ServerHint => NetworkEnabled ? _loc["lan.server.hint"] : _loc["settings.network.offHint"];

    public string ServerStatusText { get => _serverStatusText; private set => Set(ref _serverStatusText, value); }

    public RemoteDotKind ServerDot { get => _serverDot; private set => Set(ref _serverDot, value); }

    /// <summary><c>K7Q4-M2X9-PT3D</c>.</summary>
    public string KeyText => _lan.Settings.Server.Key is { } key ? LanKey.Display(key) : string.Empty;

    /// <summary>The key as the groups it shows, for screen readers.</summary>
    public string KeyAccessibleName => $"{_loc["lan.key"]}: {KeyText.Replace('-', ' ')}";

    public bool KeyCopied { get => _keyCopied; private set => Set(ref _keyCopied, value); }

    public string PortText
    {
        get => _portText;
        set
        {
            if (Set(ref _portText, value) && _portError is not null)
            {
                PortError = null;
            }
        }
    }

    /// <summary><c>lan.port.invalid</c> under the field while the typed port is not applied.</summary>
    public string? PortError { get => _portError; private set => Set(ref _portError, value); }

    public bool HasAddresses => Addresses.Count > 0;

    public bool HasTargets => Targets.Count > 0;

    /// <summary><c>2 / 8</c> beside the header while targets exist.</summary>
    public string CountText => _loc.Format("orbs.count", Targets.Count, LanSettings.MaxTargets);

    public bool IsEmpty => Targets.Count == 0;

    public bool CanAdd => _lan.Settings.CanAdd;

    /// <summary><c>lan.targets.limit</c>: the disabled Add Device…'s tooltip.</summary>
    public string? AddTooltip => CanAdd ? null : _loc.Format("lan.targets.limit", LanSettings.MaxTargets);

    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }

    /// <summary>The pane is shown: the status lines refresh every second only then.</summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (Set(ref _visible, value))
            {
                RefreshStatuses();
            }
        }
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        Sync();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Applies the typed port on Return or when the field loses focus; an invalid one shows the error and is kept.</summary>
    public void CommitPort()
    {
        var text = PortText.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && LanSettings.IsValidPort(port))
        {
            PortError = null;
            if (port != _lan.Settings.Server.Port)
            {
                _lan.SetPort(port);
            }

            PortText = port.ToString(CultureInfo.InvariantCulture);
            return;
        }

        PortError = _loc["lan.port.invalid"];
    }

    /// <summary>Esc in the port field: the stored port again.</summary>
    public void RestorePort()
    {
        PortError = null;
        PortText = _lan.Settings.Server.Port.ToString(CultureInfo.InvariantCulture);
    }

    private void CopyKey()
    {
        if (_lan.Settings.Server.Key is not { } key)
        {
            return;
        }

        ReportLinks.Copy(LanKey.Display(key));
        KeyCopied = true;
        _copied.Stop();
        _copied.Start();
    }

    private void Sync()
    {
        var settings = _lan.Settings;
        var ids = settings.Targets.Select(t => t.Id).ToList();
        for (var i = Targets.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Targets[i].Id))
            {
                Targets.RemoveAt(i);
            }
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var row = Targets.FirstOrDefault(r => r.Id == ids[i]);
            if (row is null)
            {
                Targets.Insert(Math.Min(i, Targets.Count), new LanTargetRowViewModel(ids[i], _lan.SetTargetEnabled));
            }
            else if (Targets.IndexOf(row) != i)
            {
                Targets.Move(Targets.IndexOf(row), i);
            }
        }

        if (_portError is null)
        {
            _portText = settings.Server.Port.ToString(CultureInfo.InvariantCulture);
        }

        SyncAddresses(settings);
        RefreshStatuses();
        OnPropertyChanged(nameof(NetworkEnabled));
        OnPropertyChanged(nameof(ServerEnabled));
        OnPropertyChanged(nameof(ShowsServerRows));
        OnPropertyChanged(nameof(ServerHint));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(KeyAccessibleName));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddTooltip));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SyncAddresses(LanSettings settings)
    {
        var port = settings.Server.Port;
        var key = settings.Server.Key is { } k ? LanKey.Display(k) : string.Empty;
        var rows = _lan.Addresses.Select(a =>
        {
            var tcp = $"{a}:{port}";
            return new LanAddressRow(tcp, $"http://{tcp}/v1/live?key={key}", $"ws://{tcp}/v1/stream?key={key}", tcp);
        }).ToList();
        if (!rows.SequenceEqual(Addresses))
        {
            Addresses.Clear();
            foreach (var row in rows)
            {
                Addresses.Add(row);
            }

            OnPropertyChanged(nameof(HasAddresses));
        }
    }

    private void RefreshStatuses()
    {
        var server = _lan.ServerStatus;
        ServerStatusText = LanStatusText.Server(server, _loc);
        ServerDot = LanStatusText.ServerDot(server);
        var settings = _lan.Settings;
        var nowMs = Environment.TickCount64;
        var now = DateTimeOffset.Now;
        foreach (var row in Targets)
        {
            if (settings.Target(row.Id) is { } target)
            {
                row.Refresh(target, _lan.TargetStatus(row.Id), nowMs, now, _loc);
            }
        }

        var ticking = _visible && (ServerEnabled || Targets.Any(r => r.Ticks));
        if (ticking != _tick.IsEnabled)
        {
            _tick.IsEnabled = ticking;
        }
    }
}
