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
    private readonly Func<string?, string?> _pickFolder;
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
        _pickFolder = pickFolder;
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
        ChangeCodexCommand = new RelayCommand(() => ChangeFolder(CodexPath, (s, path) => s with { CodexRoot = path }));
        ChangeClaudeCommand = new RelayCommand(() => ChangeFolder(ClaudePath, (s, path) => s with { ClaudeRoot = path }));
        ChangeDshCommand = new RelayCommand(() => ChangeFolder(DshPath, (s, path) => s with { DshRoot = path }));
        ResetCodexCommand = new RelayCommand(() => _settings.Update(s => s with { CodexRoot = null }), () => _settings.Current.CodexRoot is not null);
        ResetClaudeCommand = new RelayCommand(() => _settings.Update(s => s with { ClaudeRoot = null }), () => _settings.Current.ClaudeRoot is not null);
        ResetDshCommand = new RelayCommand(() => _settings.Update(s => s with { DshRoot = null }), () => _settings.Current.DshRoot is not null);
        ChangeOpenCodeCommand = new RelayCommand(() => ChangeFolder(OpenCodePath, (s, path) => s with { OpenCodeRoot = path }));
        ChangeGeminiCommand = new RelayCommand(() => ChangeFolder(GeminiPath, (s, path) => s with { GeminiRoot = path }));
        ResetOpenCodeCommand = new RelayCommand(() => _settings.Update(s => s with { OpenCodeRoot = null }), () => _settings.Current.OpenCodeRoot is not null);
        ResetGeminiCommand = new RelayCommand(() => _settings.Update(s => s with { GeminiRoot = null }), () => _settings.Current.GeminiRoot is not null);
        ChangeOmpCommand = new RelayCommand(() => ChangeFolder(OmpPath, (s, path) => s with { OmpRoot = path }));
        ChangeAntigravityCommand = new RelayCommand(() =>
            ChangeFolder(AntigravityFolders.FirstOrDefault() ?? string.Empty, (s, path) => s with { AntigravityRoot = path }));
        ResetOmpCommand = new RelayCommand(() => _settings.Update(s => s with { OmpRoot = null }), () => _settings.Current.OmpRoot is not null);
        ResetAntigravityCommand = new RelayCommand(() => _settings.Update(s => s with { AntigravityRoot = null }),
            () => _settings.Current.AntigravityRoot is not null);
        ChangeWorkBuddyCommand = new RelayCommand(() => ChangeFolder(WorkBuddyPath, (s, path) => s with { WorkBuddyRoot = path }));
        ChangeWorkBuddyAiCommand = new RelayCommand(() => ChangeFolder(WorkBuddyAiPath, (s, path) => s with { WorkBuddyAiRoot = path }));
        ResetWorkBuddyCommand = new RelayCommand(() => _settings.Update(s => s with { WorkBuddyRoot = null }),
            () => _settings.Current.WorkBuddyRoot is not null);
        ResetWorkBuddyAiCommand = new RelayCommand(() => _settings.Update(s => s with { WorkBuddyAiRoot = null }),
            () => _settings.Current.WorkBuddyAiRoot is not null);
        ChangeZCodeCommand = new RelayCommand(() => ChangeFolder(ZCodePath, (s, path) => s with { ZCodeRoot = path }));
        ResetZCodeCommand = new RelayCommand(() => _settings.Update(s => s with { ZCodeRoot = null }),
            () => _settings.Current.ZCodeRoot is not null);
        ChangeKimiCommand = new RelayCommand(() => ChangeFolder(KimiPath, (s, path) => s with { KimiRoot = path }));
        ResetKimiCommand = new RelayCommand(() => _settings.Update(s => s with { KimiRoot = null }),
            () => _settings.Current.KimiRoot is not null);
        ChangeGrokBuildCommand = new RelayCommand(() => ChangeFolder(GrokBuildPath, (s, path) => s with { GrokBuildRoot = path }));
        ResetGrokBuildCommand = new RelayCommand(() => _settings.Update(s => s with { GrokBuildRoot = null }),
            () => _settings.Current.GrokBuildRoot is not null);
        ChangePiCommand = new RelayCommand(() => ChangeFolder(PiPath, (s, path) => s with { PiRoot = path }));
        ResetPiCommand = new RelayCommand(() => _settings.Update(s => s with { PiRoot = null }), () => _settings.Current.PiRoot is not null);
        ChangeOpenClawCommand = new RelayCommand(() => ChangeFolder(OpenClawFolder, (s, path) => s with { OpenClawRoot = path }));
        ResetOpenClawCommand = new RelayCommand(() => _settings.Update(s => s with { OpenClawRoot = null }),
            () => _settings.Current.OpenClawRoot is not null);
        ChangeHermesCommand = new RelayCommand(() => ChangeFolder(HermesPath, (s, path) => s with { HermesRoot = path }));
        ResetHermesCommand = new RelayCommand(() => _settings.Update(s => s with { HermesRoot = null }),
            () => _settings.Current.HermesRoot is not null);
        ChangeDroidCommand = new RelayCommand(() => ChangeFolder(DroidPath, (s, path) => s with { DroidRoot = path }));
        ResetDroidCommand = new RelayCommand(() => _settings.Update(s => s with { DroidRoot = null }),
            () => _settings.Current.DroidRoot is not null);
        ResetTaskbarPositionCommand = new RelayCommand(() => _settings.Update(s => s with { TaskbarOffset = 0 }),
            () => _settings.Current.TaskbarOffset > 0);
        ChangeTraeCommand = new RelayCommand(() => ChangeFolder(TraeFolders.FirstOrDefault() ?? string.Empty, (s, path) => s with { TraeRoot = path }));
        ResetTraeCommand = new RelayCommand(() => _settings.Update(s => s with { TraeRoot = null }),
            () => _settings.Current.TraeRoot is not null);
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

        Relocalize(localizer);
        Sync();
    }

    public ObservableCollection<NavItem> Pages { get; } = new();

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


    public RelayCommand ChangeCodexCommand { get; }

    public RelayCommand ChangeClaudeCommand { get; }

    public RelayCommand ChangeDshCommand { get; }

    public RelayCommand ResetCodexCommand { get; }

    public RelayCommand ResetClaudeCommand { get; }

    public RelayCommand ResetDshCommand { get; }

    public RelayCommand ChangeOpenCodeCommand { get; }

    public RelayCommand ChangeGeminiCommand { get; }

    public RelayCommand ResetOpenCodeCommand { get; }

    public RelayCommand ResetGeminiCommand { get; }

    public RelayCommand ChangeOmpCommand { get; }

    public RelayCommand ChangeAntigravityCommand { get; }

    public RelayCommand ResetOmpCommand { get; }

    public RelayCommand ResetAntigravityCommand { get; }

    public RelayCommand ChangeWorkBuddyCommand { get; }

    public RelayCommand ChangeWorkBuddyAiCommand { get; }

    public RelayCommand ResetWorkBuddyCommand { get; }

    public RelayCommand ResetWorkBuddyAiCommand { get; }

    public RelayCommand ChangeZCodeCommand { get; }

    public RelayCommand ResetZCodeCommand { get; }

    public RelayCommand ChangeKimiCommand { get; }

    public RelayCommand ResetKimiCommand { get; }

    public RelayCommand ChangeGrokBuildCommand { get; }

    public RelayCommand ResetGrokBuildCommand { get; }

    public RelayCommand ChangePiCommand { get; }

    public RelayCommand ResetPiCommand { get; }

    public RelayCommand ChangeOpenClawCommand { get; }

    public RelayCommand ResetOpenClawCommand { get; }

    public RelayCommand ChangeHermesCommand { get; }

    public RelayCommand ResetHermesCommand { get; }

    public RelayCommand ChangeDroidCommand { get; }

    public RelayCommand ResetDroidCommand { get; }
    public RelayCommand ChangeTraeCommand { get; }

    public RelayCommand ResetTraeCommand { get; }

    public RelayCommand OpenHistoryCommand { get; }

    /// <summary>Asks for confirmation, then clears the stored history.</summary>
    public RelayCommand ClearHistoryCommand { get; }

    public RelayCommand OpenRepositoryCommand { get; }

    public RelayCommand OpenLicenseCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand OpenUpdateCommand { get; }

    private AppSettings S => _settings.Current;

    private IReadOnlyList<string> AntigravityFolders =>
        SourceFolders.Antigravity(S.AntigravityRoot, LiveMonitor.DefaultAntigravityStores, Directory.Exists);

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

    /// <summary><c>settings.bar.intelligence</c>: the 智力 block after the network block (DESIGN 10).</summary>
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
            if (Orbs is not null)
            {
                Orbs.ReduceMotion = value;
            }

            if (TitleThemes is not null)
            {
                TitleThemes.ReduceMotion = value;
            }
        }
    }

    public bool IsPreviewing => _preview.IsRunning;

    public string PreviewButtonText => _preview.IsRunning ? _loc["settings.effects.previewing"] : _loc["settings.effects.preview"];

    public string CodexPath => S.CodexRoot ?? LiveMonitor.DefaultCodexRoot;

    public string ClaudePath => S.ClaudeRoot ?? LiveMonitor.DefaultClaudeRoot;

    public string DshPath => S.DshRoot ?? LiveMonitor.DefaultDshRoot;

    public bool CodexIsCustom => S.CodexRoot is not null;

    public bool ClaudeIsCustom => S.ClaudeRoot is not null;

    public bool DshIsCustom => S.DshRoot is not null;

    /// <summary>The folder that contains opencode.db.</summary>
    public string OpenCodePath => S.OpenCodeRoot ?? LiveMonitor.DefaultOpenCodeRoot;

    /// <summary>One Gemini CLI <c>tmp</c> folder.</summary>
    public string GeminiPath => S.GeminiRoot ?? LiveMonitor.DefaultGeminiRoot;

    public bool OpenCodeIsCustom => S.OpenCodeRoot is not null;

    public bool GeminiIsCustom => S.GeminiRoot is not null;

    public string OpenCodeStatus => StatusFor(SourceKind.OpenCode);

    public string GeminiStatus => StatusFor(SourceKind.Gemini);

    /// <summary>The default omp <c>sessions</c> root (named profiles are read too), or the one chosen folder.</summary>
    public string OmpPath => S.OmpRoot ?? LiveMonitor.DefaultOmpRoot;

    /// <summary>The chosen Antigravity folder, else the default stores found, one per line.</summary>
    public string AntigravityPath => string.Join(Environment.NewLine, AntigravityFolders);

    public bool OmpIsCustom => S.OmpRoot is not null;

    public bool AntigravityIsCustom => S.AntigravityRoot is not null;

    public string OmpStatus => StatusFor(SourceKind.Omp);

    public string AntigravityStatus => StatusFor(SourceKind.Antigravity);

    /// <summary>The WorkBuddy data folder: the default, the chosen one, or the parent of a chosen <c>projects</c> folder.</summary>
    public string WorkBuddyPath => LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, S.WorkBuddyRoot);

    /// <summary>The WorkBuddy AI data folder, like <see cref="WorkBuddyPath"/>.</summary>
    public string WorkBuddyAiPath => LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, S.WorkBuddyAiRoot);

    public bool WorkBuddyIsCustom => S.WorkBuddyRoot is not null;

    public bool WorkBuddyAiIsCustom => S.WorkBuddyAiRoot is not null;

    /// <summary>Main session files with a line today; an installed but unused app shows 0 (DESIGN 5).</summary>
    public string WorkBuddyStatus => StatusFor(SourceKind.WorkBuddy);

    public string WorkBuddyAiStatus => StatusFor(SourceKind.WorkBuddyAi);

    public string ZCodePath => S.ZCodeRoot ?? LiveMonitor.DefaultZCodeRoot;

    public string ZCodeStatus => StatusFor(SourceKind.ZCode);

    public string KimiPath => S.KimiRoot ?? LiveMonitor.DefaultKimiRoot;

    public string KimiStatus => StatusFor(SourceKind.Kimi);

    public string GrokBuildPath => S.GrokBuildRoot ?? LiveMonitor.DefaultGrokBuildRoot;

    public string GrokBuildStatus => StatusFor(SourceKind.GrokBuild);

    /// <summary>Pi's default sessions root (its custom session folder is read too), or the one chosen folder.</summary>
    public string PiPath => S.PiRoot ?? LiveMonitor.DefaultPiRoot;

    /// <summary>Main session files of every Pi root with a line today; sub-agent files are not sessions (DESIGN 5).</summary>
    public string PiStatus => StatusFor(SourceKind.Pi);

    /// <summary>The chosen OpenClaw folder or database, else the state folders found (<c>~/.openclaw</c> first), one per line.</summary>
    public string OpenClawPath => S.OpenClawRoot
        ?? (LiveMonitor.DefaultOpenClawRoots.Where(Directory.Exists).ToList() is { Count: > 0 } found
            ? string.Join(Environment.NewLine, found)
            : LiveMonitor.DefaultOpenClawRoot);

    /// <summary>Transcript windows of every agent database with an entry today that are not sub-agents (DESIGN 5).</summary>
    public string OpenClawStatus => StatusFor(SourceKind.OpenClaw);

    // The picker starts in the chosen folder (a chosen database: its agent folder), else the default state folder.
    private string OpenClawFolder => S.OpenClawRoot is { } chosen && File.Exists(chosen)
        ? Path.GetDirectoryName(chosen) ?? chosen
        : S.OpenClawRoot ?? LiveMonitor.DefaultOpenClawRoot;

    /// <summary>The Hermes home (<c>%LOCALAPPDATA%\hermes</c> unless <c>%HERMES_HOME%</c> says otherwise), or the chosen home or <c>logs</c> folder.</summary>
    public string HermesPath => S.HermesRoot ?? LiveMonitor.DefaultHermesRoot;

    /// <summary>Today's main sessions of every profile; a home without logs shows 0 (DESIGN 5).</summary>
    public string HermesStatus => StatusFor(SourceKind.Hermes);

    /// <summary>The Factory Droid sessions folder: the default, or the chosen folder's <c>sessions</c> folder (or the folder itself).</summary>
    public string DroidPath => LiveMonitor.DroidRoot(S.DroidRoot);

    /// <summary>Today's main sessions; Task-tool workers are not counted (DESIGN 5).</summary>
    public string DroidStatus => StatusFor(SourceKind.Droid);
    public IReadOnlyList<string> TraeFolders => SourceFolders.Antigravity(S.TraeRoot, LiveMonitor.DefaultTraeFolders, Directory.Exists);

    public string TraePath => string.Join(Environment.NewLine, TraeFolders);

    public string TraeStatus => StatusFor(SourceKind.Trae);

    public string CodexStatus => StatusFor(SourceKind.Codex);

    public string DshStatus => StatusFor(SourceKind.DeepSeek);

    public string ClaudeStatus
    {
        get
        {
            var code = _snapshot.Sources.FirstOrDefault(s => s.Kind == SourceKind.ClaudeCode);
            var desktop = _snapshot.Sources.FirstOrDefault(s => s.Kind == SourceKind.ClaudeDesktop);
            if (code is null && desktop is null)
            {
                return string.Empty;
            }

            var available = (code?.Available ?? false) || (desktop?.Available ?? false);
            if (!available)
            {
                return _loc["source.folderMissing"];
            }

            return _loc.Format("source.sessions", (code?.SessionCount ?? 0) + (desktop?.SessionCount ?? 0));
        }
    }

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
        Orbs?.Relocalize(localizer);
        Boards?.Relocalize(localizer);
        TitleThemes?.Relocalize(localizer);
        Cost.Relocalize();
        Traffic?.Relocalize(localizer);
        OnPropertyChanged(string.Empty);
    }

    public void UpdateLive(SpeedSnapshot snapshot, TaskbarPresence presence)
    {
        _snapshot = snapshot;
        Orbs?.UpdateLive();
        Cost.UpdateLive(snapshot);
        Traffic?.Refresh();
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

        OnPropertyChanged(nameof(CodexStatus));
        OnPropertyChanged(nameof(ClaudeStatus));
        OnPropertyChanged(nameof(DshStatus));
        OnPropertyChanged(nameof(OpenCodeStatus));
        OnPropertyChanged(nameof(GeminiStatus));
        OnPropertyChanged(nameof(OmpStatus));
        OnPropertyChanged(nameof(AntigravityStatus));
        OnPropertyChanged(nameof(AntigravityPath));
        OnPropertyChanged(nameof(WorkBuddyStatus));
        OnPropertyChanged(nameof(WorkBuddyAiStatus));
        OnPropertyChanged(nameof(ZCodeStatus));
        OnPropertyChanged(nameof(KimiStatus));
        OnPropertyChanged(nameof(GrokBuildStatus));
        OnPropertyChanged(nameof(PiStatus));
        OnPropertyChanged(nameof(OpenClawStatus));
        OnPropertyChanged(nameof(OpenClawPath));
        OnPropertyChanged(nameof(HermesStatus));
        OnPropertyChanged(nameof(DroidStatus));
        OnPropertyChanged(nameof(DroidPath));
        OnPropertyChanged(nameof(TraeStatus));
        OnPropertyChanged(nameof(TraePath));
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

        return status.Available ? _loc.Format("source.sessions", status.SessionCount) : _loc["source.folderMissing"];
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

    private void ChangeFolder(string current, Func<AppSettings, string, AppSettings> apply)
    {
        var picked = _pickFolder(Directory.Exists(current) ? current : null);
        if (string.IsNullOrWhiteSpace(picked) || !Path.IsPathFullyQualified(picked))
        {
            return;
        }

        _settings.Update(s => apply(s, picked));
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
