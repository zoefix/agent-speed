using System.Diagnostics;
using System.Windows.Input;
using AgentSpeed.App;
using AgentSpeed.Controls;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;
using AgentSpeed.Traffic;

namespace AgentSpeed.Settings;

/// <summary>
/// Settings → 網路流量 (DESIGN 10): measuring, tools, the rate unit, the taskbar block, and the traffic helper's status
/// with install, update and remove. Every switch applies live through <see cref="SettingsService"/>; the helper actions
/// show <c>traffic.helper.uacNote</c> inline before the administrator prompt and their result after it.
/// </summary>
internal sealed class TrafficPaneViewModel : ObservableObject
{
    /// <summary>What the helper does, for <c>traffic.helper.details</c>.</summary>
    public const string DetailsUrl = "https://github.com/zoefix/agent-speed/blob/main/spec/WINDOWS-SERVICE.md";

    private readonly SettingsService _settings;
    private readonly TrafficService _traffic;
    private readonly ITrafficHelperHost? _helper;
    private readonly Func<nint> _owner;
    private readonly Func<bool> _confirmRemove;
    private Localizer _loc;
    private HelperInstallState _install = HelperInstallState.Unknown;
    private TrafficHelperStatus? _status;
    private bool _busy;
    private string? _note;
    private bool _noteIsError;

    /// <param name="helper">Null where the helper does not apply (no Windows service support in this build).</param>
    /// <param name="owner">The settings window's handle, the owner of the administrator prompt.</param>
    /// <param name="confirmRemove">Asks <c>traffic.helper.removeConfirm</c>; true to remove.</param>
    public TrafficPaneViewModel(SettingsService settings, TrafficService traffic, ITrafficHelperHost? helper, Localizer localizer,
        Func<nint> owner, Func<bool> confirmRemove)
    {
        _settings = settings;
        _traffic = traffic;
        _helper = helper;
        _loc = localizer;
        _owner = owner;
        _confirmRemove = confirmRemove;
        PrimaryCommand = new RelayCommand(() => _ = InstallAsync(), () => !_busy && _install.Bundled);
        RemoveCommand = new RelayCommand(() => _ = RemoveAsync(), () => !_busy);
        RetryCommand = new RelayCommand(() => _traffic.Retry());
        DetailsCommand = new RelayCommand(OpenDetails);
        _settings.Changed += (_, _) => OnPropertyChanged(string.Empty);
        Refresh();
    }

    private TrafficSettings T => _settings.Current.Traffic;

    public bool Enabled
    {
        get => T.Enabled;
        set => _settings.Update(s => s with { Traffic = s.Traffic with { Enabled = value } });
    }

    public bool IncludeTools
    {
        get => T.IncludeTools;
        set => _settings.Update(s => s with { Traffic = s.Traffic with { IncludeTools = value } });
    }

    public bool UnitIsBytes
    {
        get => !T.Bits;
        set
        {
            if (value)
            {
                _settings.Update(s => s with { Traffic = s.Traffic with { Unit = TrafficUnit.Bytes } });
            }
        }
    }

    public bool UnitIsBits
    {
        get => T.Bits;
        set
        {
            if (value)
            {
                _settings.Update(s => s with { Traffic = s.Traffic with { Unit = TrafficUnit.Bits } });
            }
        }
    }

    /// <summary>
    /// <c>settings.bar.traffic</c>: the taskbar's network block — off, this computer or the agents (the same control as on the
    /// taskbar page; 性能 has its own switch there).
    /// </summary>
    public int BarIndex
    {
        get => (int)T.Bar;
        set
        {
            if (value is >= 0 and <= (int)BarTraffic.Agents)
            {
                _settings.Update(s => s with { Traffic = s.Traffic with { Bar = (BarTraffic)value } });
            }
        }
    }

    private string? _reportHost;

    /// <summary><c>report.traffic.note</c> while a traffic section is being reported (DESIGN 11), naming the destination.</summary>
    public string? ReportNote => _reportHost is { } host ? _loc.Format("report.traffic.note", host) : null;

    public void SetReporting(string? host)
    {
        if (_reportHost != host)
        {
            _reportHost = host;
            OnPropertyChanged(nameof(ReportNote));
        }
    }

    public string DetailsLink => DetailsUrl;

    /// <summary>The helper section applies (Windows builds with the service support).</summary>
    public bool ShowsHelper => _helper is not null;

    public string StatusText => _status?.Text ?? string.Empty;

    public TrafficHelperDot StatusDot => _status?.Dot ?? TrafficHelperDot.Neutral;

    public bool ShowsPrimary => _status is { ShowsInstall: true } or { ShowsUpdate: true };

    public string PrimaryText => _loc[_status is { ShowsUpdate: true } ? "traffic.helper.update" : "traffic.helper.install"];

    public bool ShowsRemove => _status?.ShowsRemove ?? false;

    /// <summary>A failure the sampler retries by itself can be retried now.</summary>
    public bool ShowsRetry => _status?.Kind == TrafficHelperStatusKind.Error && !_busy;

    /// <summary>This build carries no helper (a macOS cross build): the install button explains instead.</summary>
    public string? PrimaryTooltip => _install.Bundled ? null : _loc["traffic.unavailable.sampler"];

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                OnPropertyChanged(nameof(ShowsRetry));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>The inline note: the UAC note while waiting, then the result.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    public bool HasNote => !string.IsNullOrEmpty(_note);

    public bool NoteIsError { get => _noteIsError; private set => Set(ref _noteIsError, value); }

    public ICommand PrimaryCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand DetailsCommand { get; }

    public void Relocalize(Localizer localizer)
    {
        _loc = localizer;
        Refresh();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-reads the service state (about once a second while Settings is open, and after each action).</summary>
    public void Refresh()
    {
        if (_helper is null)
        {
            return;
        }

        _install = _helper.Query();
        var snapshot = _traffic.Snapshot;
        var status = TrafficHelperStatus.From(_install.Installed, _install.Allowed, _install.Version, _install.Running, snapshot.ProcessState,
            snapshot.ProcessError, _loc);
        if (status == _status)
        {
            return;
        }

        _status = status;
        foreach (var name in new[]
                 {
                     nameof(StatusText), nameof(StatusDot), nameof(ShowsPrimary), nameof(PrimaryText), nameof(ShowsRemove), nameof(ShowsRetry),
                     nameof(PrimaryTooltip),
                 })
        {
            OnPropertyChanged(name);
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private async Task InstallAsync()
    {
        if (_helper is null || _busy)
        {
            return;
        }

        ShowWaiting();
        HelperActionReport report;
        try
        {
            report = await _helper.InstallAsync(_owner(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Installing the traffic helper failed.", ex);
            report = new HelperActionReport(HelperActionResultKind.Failed, ex.Message);
        }

        Finish(report, "traffic.helper.installed");
    }

    private async Task RemoveAsync()
    {
        if (_helper is null || _busy || !_confirmRemove())
        {
            return;
        }

        ShowWaiting();
        HelperActionReport report;
        try
        {
            report = await _helper.RemoveAsync(_owner(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Removing the traffic helper failed.", ex);
            report = new HelperActionReport(HelperActionResultKind.Failed, ex.Message);
        }

        Finish(report, "traffic.helper.removed");
    }

    private void ShowWaiting()
    {
        IsBusy = true;
        NoteIsError = false;
        Note = _loc["traffic.helper.uacNote"];
        OnPropertyChanged(nameof(HasNote));
    }

    private void Finish(HelperActionReport report, string doneKey)
    {
        IsBusy = false;
        NoteIsError = report.Result == HelperActionResultKind.Failed;
        Note = report.Result switch
        {
            HelperActionResultKind.Done => _loc[doneKey],
            HelperActionResultKind.Cancelled => _loc["traffic.helper.cancelled"],
            _ => _loc.Format("traffic.helper.error", string.IsNullOrWhiteSpace(report.Message) ? _loc["traffic.unavailable.sampler"] : report.Message),
        };
        OnPropertyChanged(nameof(HasNote));
        _traffic.Retry();
        Refresh();
    }

    private static void OpenDetails()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DetailsUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Opening the traffic helper details failed.", ex);
        }
    }
}
