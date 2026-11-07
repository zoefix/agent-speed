using System.Collections.ObjectModel;
using System.Diagnostics;
using AgentSpeed.App;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Presentation.Updates;
using AgentSpeed.Services;
using AgentSpeed.Settings.TitleThemes;
using AgentSpeed.Taskbar;
using AgentSpeed.Titles;
using AgentSpeed.Updates;

namespace AgentSpeed.Settings;

internal sealed class NavItem(string key, string glyph, string titleKey) : ObservableObject
{
    private string _title = string.Empty;

    public string Key { get; } = key;

    public string Glyph { get; } = glyph;

    public string TitleKey { get; } = titleKey;

    public string Title { get => _title; set => Set(ref _title, value); }
}

internal sealed class SourceSettingsRow(SourceKind kind) : ObservableObject
{
    private bool _enabled;
    private string _status = string.Empty;
    private string _name = string.Empty;

    public SourceKind Kind { get; } = kind;

    /// <summary>The source's color (DESIGN 1, Source colors).</summary>
    public System.Windows.Media.Brush Dot { get; } =
        Theme.MediaColors.Frozen(Theme.MediaColors.ToColor(Presentation.Colors.HeatPalette.SourceColor(kind)));

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>One rung of the speed-title list under the redline slider: title, then its threshold at the current redline.</summary>
internal sealed class TitleLadderRow(SpeedTitle title, int index) : ObservableObject
{
    private string _name = string.Empty;
    private string _value = string.Empty;
    private IReadOnlyList<string> _valueShapes = [];
    private string _accessibleName = string.Empty;
    private System.Windows.Media.Brush _valueBrush = System.Windows.Media.Brushes.Gray;

    public SpeedTitle Title { get; } = title;

    /// <summary>Position of the title's tick on the heat strip.</summary>
    public int Index { get; } = index;

    public string Name { get => _name; private set => Set(ref _name, value); }

    /// <summary>Threshold in tok/s, compact and without the unit: <c>48</c>, <c>24k</c>.</summary>
    public string Value { get => _value; private set => Set(ref _value, value); }

    /// <summary>Every form the value takes over the slider range; the widest one sets the value's width.</summary>
    public IReadOnlyList<string> ValueShapes { get => _valueShapes; private set => Set(ref _valueShapes, value); }

    /// <summary>The heat color of the threshold (darkened on light backgrounds for contrast).</summary>
    public System.Windows.Media.Brush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }

    public string AccessibleName { get => _accessibleName; private set => Set(ref _accessibleName, value); }

    /// <param name="theme">Names the title (DESIGN 1, Where the theme applies): General here, the orb's own in the orb pane.</param>
    public void Refresh(Localizer loc, SpeedMetric metric, double redline, bool dark, TitleTheme theme)
    {
        Name = TitleLadderText.Name(Title, theme, loc);
        Value = Presentation.Formatting.DisplayText.TitleThreshold(Title, redline);
        ValueShapes = TitleLadderText.Shapes(Title, metric);
        AccessibleName = $"{Name}, {Value} {loc["unit.tokS"]}";
        ValueBrush = TitleHeatBrushes.For(SpeedTitles.HeatAt(Title, redline), dark);
    }
}

/// <summary>Live-applied settings. Every setter writes through <see cref="SettingsService"/> immediately.</summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly PreviewService _preview;
    private readonly UpdateService _updates;
    private Localizer _loc;
    private NavItem _selected;
    private bool _syncing;
    private bool _launchAtLogin;
    private SpeedSnapshot _snapshot = SpeedSnapshot.Empty;
    private TaskbarPresence _taskbarPresence = TaskbarPresence.Hidden;
    private bool _reduceMotion;
    private bool _darkTheme;
    private OrbsPane.OrbsPaneViewModel? _orbs;
    private TitleThemesService? _titleService;

    /// <param name="openHistory">Opens the Usage History window for all sources.</param>
    /// <param name="confirmClearHistory">Asks the user to confirm clearing the history; true to go ahead.</param>
    /// <param name="clearHistory">Deletes the stored daily history so it is recomputed from the logs.</param>
    /// <param name="cost">The cost feature: Settings → Cost and the network master switch in About.</param>
    public SettingsViewModel(SettingsService settings, PreviewService preview, Localizer localizer, Func<string?, string?> pickFolder,
        UpdateService updates, Action openUpdate, Action openHistory, Func<bool> confirmClearHistory, Action clearHistory, CostService cost)
    {
        _settings = settings;
        _preview = preview;
        _loc = localizer;
        _updates = updates;
        Cost = new CostPaneViewModel(settings, cost);

        Pages.Add(new NavItem("general", "", "settings.general"));
        Pages.Add(new NavItem("taskbar", "", "settings.taskbar"));
        Pages.Add(new NavItem("orb", "", "settings.orb"));
        Pages.Add(new NavItem("effects", "", "settings.effects"));
        Pages.Add(new NavItem("cost", "", "settings.cost"));
        Pages.Add(new NavItem("traffic", "", "settings.traffic"));
        Pages.Add(new NavItem("report", "\uEC05", "settings.report"));
        Pages.Add(new NavItem("sources", "", "settings.sources"));
        Pages.Add(new NavItem("remote", "\uE968", "settings.remote"));
        Pages.Add(new NavItem("about", "", "settings.about"));
        _selected = Pages[0];

        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var row = new SourceSettingsRow(kind);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SourceSettingsRow.Enabled) && !_syncing)
                {
                    _settings.Update(s => s.WithSourceEnabled(row.Kind, row.Enabled));
                }
            };
            Sources.Add(row);
        }

        PreviewCommand = new RelayCommand(StartPreview, () => !_preview.IsRunning);
        ResetTaskbarPositionCommand = new RelayCommand(() => _settings.Update(s => s with { TaskbarOffset = 0 }),
            () => _settings.Current.TaskbarOffset > 0);
        OpenHistoryCommand = new RelayCommand(openHistory);
        ClearHistoryCommand = new RelayCommand(() =>
        {
            if (confirmClearHistory())
            {
                clearHistory();
            }
        });
        OpenRepositoryCommand = new RelayCommand(() => OpenUrl(AppPaths.RepositoryUrl));
        OpenLicenseCommand = new RelayCommand(() => OpenUrl(AppPaths.LicenseUrl));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        CheckForUpdatesCommand = new RelayCommand(() => _ = _updates.CheckAsync(manual: true), () => !_updates.Status.IsBusy && _updates.NetworkEnabled);
        OpenUpdateCommand = new RelayCommand(openUpdate);
        _updates.Changed += (_, _) => OnUpdatesChanged();

        _settings.Changed += (_, _) => Sync();
        _preview.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsPreviewing));
            OnPropertyChanged(nameof(PreviewButtonText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        _launchAtLogin = AutoStart.IsEnabled;
        for (var i = 0; i < SpeedTitles.Scaled.Count; i++)
        {
            TitleLadder.Add(new TitleLadderRow(SpeedTitles.Scaled[i].Title, i));
        }

        SourcesPane = new Sources.SourcesPaneViewModel(settings, localizer, pickFolder, Sources);
        Relocalize(localizer);
        Sync();
    }

    public ObservableCollection<NavItem> Pages { get; } = new();

    /// <summary>Settings → 資料來源: the location list of every source (DESIGN 5, METRICS 2.1).</summary>
    public Sources.SourcesPaneViewModel SourcesPane { get; }

    public ObservableCollection<SourceSettingsRow> Sources { get; } = new();

    /// <summary>Settings → Cost (DESIGN 9) and the taskbar page's live value unit.</summary>
    public CostPaneViewModel Cost { get; }

    /// <summary>Settings → 網路流量 (DESIGN 10) and the taskbar page's traffic block; set by the app.</summary>
    public TrafficPaneViewModel? Traffic
    {
        get => _traffic;
        set => Set(ref _traffic, value);
    }

    private TrafficPaneViewModel? _traffic;

    /// <summary>Settings → API (DESIGN 11); set by the app.</summary>
    public ReportPaneViewModel? Report
    {
        get => _report;
        set => Set(ref _report, value);
    }

    private ReportPaneViewModel? _report;

    /// <summary>Settings → 遠端主機 (DESIGN 13); set by the app.</summary>
    public Remote.RemoteHostsPaneViewModel? Remote
    {
        get => _remote;
        set
        {
            if (Set(ref _remote, value) && value is not null)
            {
                value.ReduceMotion = _reduceMotion;
            }
        }
    }

    private Remote.RemoteHostsPaneViewModel? _remote;

    /// <summary>Any remote host is configured: About adds <c>privacy.remote</c> (DESIGN 13).</summary>
    public bool HasRemoteHosts => !S.RemoteHosts.Hosts.IsEmpty;

    /// <summary>Settings → API → 區域網路 (DESIGN 14); set by the app.</summary>
    public Lan.LanPaneViewModel? Lan
    {
        get => _lan;
        set
        {
            if (Set(ref _lan, value) && value is not null)
            {
                value.ReduceMotion = _reduceMotion;
            }
        }
    }

    private Lan.LanPaneViewModel? _lan;

    /// <summary>The API pane shows 區域網路; kept while the app runs, never stored, 即時上報 at launch (DESIGN 14).</summary>
    public bool ApiShowsLan
    {
        get => _apiShowsLan;
        set
        {
            if (Set(ref _apiShowsLan, value))
            {
                OnPropertyChanged(nameof(ApiShowsReport));
            }
        }
    }

    public bool ApiShowsReport
    {
        get => !_apiShowsLan;
        set => ApiShowsLan = !value;
    }

    private bool _apiShowsLan;

    /// <summary>The local network server is on or a target exists: About adds <c>privacy.lan</c> (DESIGN 14).</summary>
    public bool HasLanFeature => S.Lan.IsInUse;

    /// <summary>The speed titles that scale with the redline, climbing (DESIGN section 5, General).</summary>
    public ObservableCollection<TitleLadderRow> TitleLadder { get; } = new();

    /// <summary>Heat positions of the title ticks under the redline slider; they do not move with the redline.</summary>
    public IReadOnlyList<double> TitleTicks { get; } = SpeedTitles.Scaled.Select(step => SpeedTitles.HeatAt(step.Title, 1)).ToArray();

    /// <summary>Selects the threshold colors that keep their contrast on the current background.</summary>
    public bool DarkTheme
    {
        get => _darkTheme;
        set
        {
            if (Set(ref _darkTheme, value))
            {
                RefreshTitleLadder();
                if (Orbs is not null)
                {
                    Orbs.DarkTheme = value;
                }

                if (Boards is not null)
                {
                    Boards.DarkTheme = value;
                }

                if (TitleThemes is not null)
                {
                    TitleThemes.DarkTheme = value;
                }
            }
        }
    }

    public NavItem SelectedPage
    {
        get => _selected;
        set
        {
            if (value is not null && Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageKey));
            }
        }
    }

    public string PageKey => _selected.Key;

    public string PageTitle => _selected.Title;

    public RelayCommand PreviewCommand { get; }

    public RelayCommand OpenHistoryCommand { get; }

    /// <summary>Asks for confirmation, then clears the stored history.</summary>
    public RelayCommand ClearHistoryCommand { get; }

    public RelayCommand OpenRepositoryCommand { get; }

    public RelayCommand OpenLicenseCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand OpenUpdateCommand { get; }

    private AppSettings S => _settings.Current;

    public int AppearanceIndex
    {
        get => (int)S.Appearance;
        set => Apply(s => s with { Appearance = (AppearanceMode)value });
    }

    public int LanguageIndex
    {
        get => (int)S.Language;
        set => Apply(s => s with { Language = (AppLanguage)value });
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (AutoStart.SetEnabled(value))
            {
                _launchAtLogin = value;
            }

            _launchAtLogin = AutoStart.IsEnabled;
            OnPropertyChanged();
        }
    }

    public int MetricIndex
    {
        get => (int)S.Metric;
        set => Apply(s => s with { Metric = (SpeedMetric)value });
    }

    public bool IsTotalMetric => S.Metric == SpeedMetric.Total;

    public bool IsOutputMetric => !IsTotalMetric;

    public double RedlineOutputValue
    {
        get => S.RedlineOutput;
        set => Apply(s => s.WithRedline(SpeedMetric.Output, value));
    }

    public double RedlineTotalMinimumLog => Math.Log10(HeatMath.MinRedlineTotal);

    public double RedlineTotalMaximumLog => Math.Log10(HeatMath.MaxRedlineTotal);

    /// <summary>The total-throughput redline spans two decades, so its slider is logarithmic.</summary>
    public double RedlineTotalLog
    {
        get => Math.Log10(S.RedlineTotal);
        set => Apply(s => s.WithRedline(SpeedMetric.Total, Math.Pow(10, value)));
    }

    public string RedlineText => $"{SpeedFormat.Speed(S.Redline)} {_loc["unit.tokS"]}";

    public string RedlineHint => _loc["settings.redline.hint"];

    public bool ShowOnTaskbar
    {
        get => S.ShowOnTaskbar;
        set => Apply(s => s with { ShowOnTaskbar = value });
    }

    public int BarModeIndex
    {
        get => (int)S.BarMode;
        set => Apply(s => s with { BarMode = (BarDisplayMode)value });
    }

    public bool BarColoredGlyph
    {
        get => S.BarColoredGlyph;
        set => Apply(s => s with { BarColoredGlyph = value });
    }

    public bool BarShowUnit
    {
        get => S.BarShowUnit;
        set => Apply(s => s with { BarShowUnit = value });
    }

    /// <summary><c>settings.bar.intelligence</c>: the 性能 block after the network block (DESIGN 10).</summary>
    public bool BarIntelligence
    {
        get => S.BarIntelligence;
        set => Apply(s => s with { BarIntelligence = value });
    }

    /// <summary>The switch's caption while it is on: <c>settings.bar.intelligence.hint</c>; none while off.</summary>
    public string BarIntelligenceHint => S.BarIntelligence ? _loc["settings.bar.intelligence.hint"] : string.Empty;

    /// <summary>Back to the default spot next to the notification area (DESIGN 3, Position on the taskbar); disabled while there.</summary>
    public RelayCommand ResetTaskbarPositionCommand { get; }

    public string TaskbarStatus => _taskbarPresence switch
    {
        TaskbarPresence.Floating => _loc["win.settings.taskbar.floating"],
        TaskbarPresence.Unavailable when S.ShowOnTaskbar => _loc["win.settings.taskbar.unavailable"],
        _ => string.Empty,
    };

    public bool HasTaskbarStatus => !string.IsNullOrEmpty(TaskbarStatus);


    public int EffectsIndex
    {
        get => (int)S.Effects;
        set => Apply(s => s with { Effects = (EffectsIntensity)value });
    }

    public bool CelebratePeaks
    {
        get => S.CelebratePeaks;
        set => Apply(s => s with { CelebratePeaks = value });
    }

    public bool ResponsePulses
    {
        get => S.ResponsePulses;
        set => Apply(s => s with { ResponsePulses = value });
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            Set(ref _reduceMotion, value);
            SourcesPane.ReduceMotion = value;
            if (Orbs is not null)
            {
                Orbs.ReduceMotion = value;
            }

            if (TitleThemes is not null)
            {
                TitleThemes.ReduceMotion = value;
            }

            if (Remote is not null)
            {
                Remote.ReduceMotion = value;
            }

            if (Lan is not null)
            {
                Lan.ReduceMotion = value;
            }

            if (Boards is not null)
            {
                Boards.ReduceMotion = value;
            }
        }
    }

    public bool IsPreviewing => _preview.IsRunning;

    public string PreviewButtonText => _preview.IsRunning ? _loc["settings.effects.previewing"] : _loc["settings.effects.preview"];

    public string VersionText => _loc.Format("settings.about.version", AppPaths.Version);

    public string UpdateCurrentText => UpdateText.Current(_updates.CurrentVersion, _loc);

    /// <summary>Up to date, the available version, checking, progress, or the last error (UPDATES section 4).</summary>
    public string UpdateStatusText => _updates.Status.Phase == UpdatePhase.Idle && _updates.Available is { } release
        ? UpdateText.AvailableTitle(release, _loc)
        : UpdateText.StatusLine(_updates.Status, _loc);

    public bool HasUpdateStatus => UpdateStatusText.Length > 0;

    public bool UpdateStatusIsError => _updates.Status.IsFailed;

    public bool UpdateStatusIsAvailable => !UpdateStatusIsError && IsUpdateAvailable;

    public string UpdateLastCheckedText => UpdateText.LastChecked(S.LastUpdateCheck, DateTimeOffset.UtcNow, _loc);

    public bool HasUpdateLastChecked => S.LastUpdateCheck is not null;

    /// <summary>A release is on offer or being installed: the button opens the update window instead of checking again.</summary>
    public bool IsUpdateAvailable => UpdateRelease is not null;

    public bool ShowCheckButton => !IsUpdateAvailable;

    public string UpdateButtonText => UpdateRelease is { } release ? UpdateText.MenuItem(release, _loc) : string.Empty;

    /// <summary>The network master switch (NETWORK 1): off, no update, price or rate request is made.</summary>
    public bool NetworkEnabled
    {
        get => S.NetworkEnabled;
        set => Apply(s => s with { NetworkEnabled = value });
    }

    public bool NetworkOff => !S.NetworkEnabled;

    /// <summary><c>settings.network.offHint</c> on the controls the master switch disables; null while it is on.</summary>
    public string? NetworkOffTooltip => S.NetworkEnabled ? null : _loc["settings.network.offHint"];

    public string UpdateAutoHint => S.NetworkEnabled ? _loc["update.auto.hint"] : _loc["settings.network.offHint"];

    public bool AutoCheckUpdates
    {
        get => S.AutoCheckUpdates;
        set => Apply(s => s with { AutoCheckUpdates = value });
    }

    private ReleaseInfo? UpdateRelease => _updates.Status is { Release: { } release } status
                                          && (status.IsInstalling || status.Phase is UpdatePhase.Available or UpdatePhase.InstallFailed)
        ? release
        : _updates.Available;

    public string WindowTitle => $"{_loc["app.name"]} · {_loc["settings.title"]}";

    private string? _reportHost;

    /// <summary>
    /// About while live reporting is on (DESIGN 11, Menu, dashboard and About): the statements that would claim nothing is sent
    /// are replaced, and <c>privacy.report</c> follows them. <paramref name="host"/> is the destination; null while off.
    /// </summary>
    public void SetReporting(string? host)
    {
        if (_reportHost == host)
        {
            return;
        }

        _reportHost = host;
        OnPropertyChanged(nameof(PrivacyStatement));
        OnPropertyChanged(nameof(PrivacyTraffic));
        OnPropertyChanged(nameof(NetworkHint));
    }

    public string PrivacyStatement => _reportHost is { } host ? _loc.Format("privacy.statement.report", host) : _loc["privacy.statement"];

    public string PrivacyTraffic => _loc[_reportHost is null ? "privacy.traffic" : "privacy.traffic.report"];

    public string PrivacyReport => _loc["privacy.report"];

    public string NetworkHint => _loc[_reportHost is null ? "settings.network.hint" : "settings.network.hint.report"];

    public void Relocalize(Localizer localizer)
    {
        _loc = localizer;
        foreach (var page in Pages)
        {
            page.Title = localizer[page.TitleKey];
        }

        foreach (var row in Sources)
        {
            row.Name = localizer[Presentation.Formatting.DisplayText.SourceKey(row.Kind)];
        }

        RefreshTitleLadder();
        SourcesPane?.Relocalize(localizer);
        Orbs?.Relocalize(localizer);
        Boards?.Relocalize(localizer);
        TitleThemes?.Relocalize(localizer);
        Cost.Relocalize();
        Traffic?.Relocalize(localizer);
        Remote?.Relocalize(localizer);
        Lan?.Relocalize(localizer);
        OnPropertyChanged(string.Empty);
    }

    public void UpdateLive(SpeedSnapshot snapshot, TaskbarPresence presence)
    {
        _snapshot = snapshot;
        Orbs?.UpdateLive();
        Cost.UpdateLive(snapshot);
        Traffic?.Refresh();
        Remote?.UpdateLive();
        if (_taskbarPresence != presence)
        {
            _taskbarPresence = presence;
            OnPropertyChanged(nameof(TaskbarStatus));
            OnPropertyChanged(nameof(HasTaskbarStatus));
        }

        foreach (var row in Sources)
        {
            row.Status = StatusFor(row.Kind);
        }

        if (PageKey == "sources")
        {
            SourcesPane.RefreshIfDue();
        }

        OnPropertyChanged(nameof(UpdateLastCheckedText));
    }

    public void Select(string key)
    {
        if (Pages.FirstOrDefault(p => p.Key == key) is { } page)
        {
            SelectedPage = page;
        }
    }

    private void OnUpdatesChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(UpdateStatusText), nameof(HasUpdateStatus), nameof(UpdateStatusIsError), nameof(UpdateStatusIsAvailable),
                     nameof(UpdateLastCheckedText), nameof(HasUpdateLastChecked), nameof(IsUpdateAvailable), nameof(ShowCheckButton),
                     nameof(UpdateButtonText),
                 })
        {
            OnPropertyChanged(name);
        }

        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private string StatusFor(SourceKind kind)
    {
        var status = _snapshot.Sources.FirstOrDefault(s => s.Kind == kind);
        if (status is null)
        {
            return string.Empty;
        }

        if (!status.Enabled)
        {
            return _loc["source.disabled"];
        }

        var (available, sessions) = OnThisComputer(status);
        return available ? _loc.Format("source.sessions", sessions) : _loc["source.folderMissing"];
    }

    /// <summary>
    /// A source's state on this computer: with remote hosts, <c>Sources</c> adds every connected host, while this page is
    /// about this computer's own folders (DESIGN 13).
    /// </summary>
    private (bool Available, int Sessions) OnThisComputer(SourceStatus? status)
    {
        if (status is null)
        {
            return (false, 0);
        }

        if (_snapshot.Hosts.Count <= 1)
        {
            return (status.Available, status.SessionCount);
        }

        var local = _snapshot.HostSources.FirstOrDefault(c => c.Cell == HostSource.Local(status.Kind));
        return local is null ? (false, 0) : (local.Available, local.SessionCount);
    }

    private void Apply(Func<AppSettings, AppSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _settings.Update(change);
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var row in Sources)
            {
                row.Enabled = S.IsSourceEnabled(row.Kind);
            }

            SourcesPane?.Sync();
            RefreshTitleLadder();
            OnPropertyChanged(string.Empty);

            // Some changes come from outside this window (a drag of the taskbar widget), so ask the commands again.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshTitleLadder()
    {
        var theme = _titleService?.General ?? TitleTheme.Vehicles;
        foreach (var row in TitleLadder)
        {
            row.Refresh(_loc, S.Metric, S.Redline, _darkTheme, theme);
        }
    }

    private void StartPreview()
    {
        var s = S;
        var redline = s.Metric == SpeedMetric.Total ? s.RedlineTotal / 24 : s.RedlineOutput;
        _preview.Start(redline);
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Could not open the browser.", ex);
        }
    }

    private static void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { AppPaths.LogsDirectory } });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not open the logs folder.", ex);
        }
    }

    /// <summary>The floating orb pane (DESIGN 5); set by the app once the orb manager exists.</summary>
    public OrbsPane.OrbsPaneViewModel? Orbs
    {
        get => _orbs;
        set
        {
            if (ReferenceEquals(_orbs, value))
            {
                return;
            }

            if (_orbs is not null)
            {
                _orbs.PropertyChanged -= OnOrbsChanged;
            }

            _orbs = value;
            if (_orbs is not null)
            {
                _orbs.PropertyChanged += OnOrbsChanged;
            }

            TitleThemes?.Editor.OnLookChanged();
        }
    }

    /// <summary>The leaderboard boards' list and editor under the orbs (DESIGN 2c); set by the app.</summary>
    public BoardsPane.BoardsPaneViewModel? Boards { get; set; }

    /// <summary>The title themes (DESIGN 1); set by the app. Creates the General page's Title themes group.</summary>
    public TitleThemesService? TitleService
    {
        get => _titleService;
        set
        {
            if (_titleService is not null || value is null)
            {
                return;
            }

            _titleService = value;
            value.Changed += (_, _) => RefreshTitleLadder();
            TitleThemes = new TitleThemesViewModel(_settings, value, _loc)
            {
                DarkTheme = _darkTheme,
                ReduceMotion = _reduceMotion,
                SelectedOrbStyle = SelectedOrbStyle,
            };
            TitleThemes.Editor.OnLookChanged();
            RefreshTitleLadder();
            OnPropertyChanged(nameof(TitleThemes));
            OnPropertyChanged(nameof(HasTitleThemes));
        }
    }

    /// <summary>Settings → General, Title themes; null until <see cref="TitleService"/> is set.</summary>
    public TitleThemesViewModel? TitleThemes { get; private set; }

    public bool HasTitleThemes => TitleThemes is not null;

    /// <summary>The style of the orb selected in the floating orb pane (default the first orb).</summary>
    private Presentation.Orb.OrbStyle SelectedOrbStyle() =>
        _orbs?.SelectedRow?.Definition?.Style ?? S.Orbs.Orbs.FirstOrDefault()?.Style ?? Presentation.Orb.OrbStyle.Led;

    private void OnOrbsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrbsPane.OrbsPaneViewModel.SelectedRow) or null or "")
        {
            TitleThemes?.Editor.OnLookChanged();
        }
    }

    /// <summary>Selects an orb in the floating orb pane (Settings → 懸浮窗).</summary>
    public void SelectOrb(string orbId) => Orbs?.Select(orbId);
}
