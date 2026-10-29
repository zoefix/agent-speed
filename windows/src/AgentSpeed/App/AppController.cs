using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Dashboard;
using AgentSpeed.History;
using AgentSpeed.Interop;
using AgentSpeed.Localization;
using AgentSpeed.Menus;
using AgentSpeed.Modes;
using AgentSpeed.Orb;
using AgentSpeed.Orb.Hero;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Presentation.Updates;
using AgentSpeed.Services;
using AgentSpeed.Settings;
using AgentSpeed.Taskbar;
using AgentSpeed.Theme;
using AgentSpeed.Titles;
using AgentSpeed.Tray;
using AgentSpeed.Updates;

namespace AgentSpeed.App;

/// <summary>Composition root: owns every service and surface, and wires data, settings and theme to them.</summary>
internal sealed partial class AppController : IAppCommands, IOrbHost, IDisposable
{
    private const double HeroDiameter = 112;

    private readonly App _app;
    private readonly SingleInstance _instance;
    private readonly LaunchArguments _launch;
    private Dispatcher _ui = null!;
    private Localizer _loc = null!;
    private ThemeService _theme = null!;
    private SettingsService _settings = null!;
    private TitleThemesService _titles = null!;
    private CostService _cost = null!;
    private Traffic.TrafficService _traffic = null!;
    private TaskbarState? _lastTaskbarState;
    private readonly ThinkingZeroHold _barThinking = new();
    private SpeedHub _hub = null!;
    private HistoryHub _historyHub = null!;
    private HistoryController? _history;
    private HistoryService? _historyService;
    private PinnedSpeedDriver? _pinnedSpeeds;
    private HeroTourDriver? _heroTour;
    private SpeedTitle _dashboardTitle = SpeedTitle.Idle;
    private DispatcherTimer? _historyStartFallback;
    private bool _historyStarted;
    private SpeedModel _speed = null!;
    private PreviewService _preview = null!;
    private LiveMonitor? _monitor;
    private ShellMessageWindow? _shell;
    private TrayIcon? _tray;
    private TaskbarWidget? _taskbar;
    private HoverTooltip? _tooltip;
    private MenuHost? _menu;
    private OrbClock? _orbClock;
    private OrbManager? _orbs;
    private DevCapture? _capture;
    private DashboardWindow? _dashboard;
    private readonly DashboardViewModel _dashboardModel = new();
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsModel;
    private UpdateService _updates = null!;
    private UpdateWindow? _updateWindow;
    private UpdateWindowViewModel? _updateWindowModel;
    private bool _balloonOpensUpdate;
    private string? _updateDoneText;
    private bool _updateDoneShown;
    private DispatcherTimer? _dashboardTimer;
    private bool _dashboardDirty;
    private bool _tooltipShown;
    private long _lastSettingsLiveUpdate;
    private bool _started;
    private bool _quitting;
    private bool _disposed;

    public AppController(App app, SingleInstance instance, LaunchArguments launch)
    {
        _app = app;
        _instance = instance;
        _launch = launch;
    }

    private AppSettings Settings => _settings.Current;

    public void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
        var phases = new StartupPhases();
        Orb.Gpu.ShaderLibrary.Preload();

        _settings = new SettingsService(_ui);
        _settings.Load();
        if (_launch.EphemeralSettings)
        {
            _settings.MakeEphemeral();
            _settings.Update(s => OrbDemoSetup.Apply(s, _launch));
            AppLog.Info($"Orb development session ({_launch.OrbDemo ?? "grid"}): settings stay in memory.");
        }

        _loc = new Localizer(Localizer.LoadEmbeddedTable(), ResolveLanguage(Settings.Language));
        LocalizationSource.Instance.Attach(_loc);
        _titles = new TitleThemesService(_ui, _settings, () => _loc);
        _cost = new CostService(_ui, _settings, _loc, AppPaths.Version, HistoryService.DefaultStorePath);
        _cost.Prices.TableAccepted += (_, _) => _historyHub?.Refresh();
        _cost.Changed += (_, _) => OnCostChanged();
        _dashboardModel.Cost = _cost.Display;
        _traffic = new Traffic.TrafficService(_ui, _settings) { Helper = new Traffic.WindowsTrafficHelperHost() };
        _traffic.Hub.SnapshotChanged += (_, _) => OnTrafficChanged();
        phases.Mark("settings+strings");

        _theme = new ThemeService(_ui, Settings.Appearance, _launch.ReduceMotion);
        _app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AgentSpeed;component/Theme/Styles.xaml", UriKind.Absolute),
        });
        AppFonts.Apply(_app.Resources, _loc.LanguageCode);
        phases.Mark("styles");

        _hub = new SpeedHub(new DispatcherSynchronizationContext(_ui));
        _historyHub = new HistoryHub(new DispatcherSynchronizationContext(_ui));
        _history = new HistoryController(_historyHub, _settings, _theme, _loc, _cost.Display) { Traffic = _traffic.Hub, Prices = _cost.Book };
        _hub.SnapshotChanged += (_, snapshot) =>
        {
            if (!_hub.IsPreviewing)
            {
                _historyService?.ObserveLive(snapshot);
            }
        };
        CreateReporting();
        _speed = new SpeedModel(_hub, _ui);
        _speed.Configure(Settings.Metric, Settings.Redline);
        _preview = new PreviewService(_hub, _ui);

        _shell = new ShellMessageWindow();
        _shell.TaskbarCreated += (_, _) => _taskbar?.Reembed();
        _shell.DisplayChanged += (_, _) => OnDisplayChanged();
        _shell.SettingChanged += (_, area) => OnSystemSettingChanged(area);
        _shell.SessionEnding += (_, _) => Quit();
        _app.SessionEnding += (_, _) => Quit();

        _tray = new TrayIcon(_shell, _ui);
        _tray.Invoked += (_, _) => ToggleDashboard(TrayAnchor());
        _tray.ContextMenuRequested += (_, _) => ShowMenu();
        _tray.BalloonClicked += (_, _) => OnBalloonClicked();
        _tray.Show(TrayState());
        phases.Mark("tray");

        _updates = new UpdateService(_ui, _settings, AppPaths.Version);
        _updates.Changed += (_, _) => OnUpdatesChanged();
        _updates.NotificationDue += (_, release) => ShowBalloon(UpdateText.AvailableTitle(release, _loc), _loc["update.notification.body"], opensUpdate: true);
        _updates.RelaunchRequested += (_, _) => Quit();

        _menu = new MenuHost();
        _tooltip = new HoverTooltip();

        _taskbar = new TaskbarWidget(_ui);
        _taskbar.Invoked += (_, bounds) => ToggleDashboard(Center(bounds));
        _taskbar.MenuRequested += (_, _) => ShowMenu();
        _taskbar.HoverChanged += (_, hover) => OnTaskbarHover(hover.IsOver, hover.Bounds);
        _taskbar.PresenceChanged += (_, _) => _settingsModel?.UpdateLive(_speed.Snapshot, _taskbar.Presence);
        _taskbar.Moved += (_, offset) => OnTaskbarMoved(offset);
        _taskbar.Start();
        phases.Mark("taskbar");

        _speed.Changed += (_, _) => OnSpeedChanged();
        _speed.EventRaised += (_, e) => OnSpeedEvent(e);
        _settings.Changed += (_, e) => OnSettingsChanged(e.Previous, e.Current);
        _theme.Changed += (_, _) => OnThemeChanged();
        _loc.LanguageChanged += (_, _) => OnLanguageChanged();
        _preview.StateChanged += (_, _) => _dashboard?.SetPreviewing(_preview.IsRunning);

        _dashboardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FlushDashboard(), _ui);
        _dashboardTimer.Stop();

        _orbClock = new OrbClock(_ui, () => _speed.Now);
        OrbClock.Current = _orbClock;
        if (Orb.Gpu.GpuDevice.Initialize())
        {
            _orbClock.UseCompositorTicker();
            if (_launch.EphemeralSettings)
            {
                Orb.Gpu.OrbGpuRenderer.Timer = new Orb.Gpu.GpuTimer();
            }
        }
        _orbs = new OrbManager(_ui, _orbClock, _settings, _theme, _speed, _loc, this) { Titles = _titles, Cost = _cost.Display };
        _orbs.Apply();
        StartBoards();
        _titles.Changed += (_, _) => OnTitlesChanged();
        phases.Mark("orb");
        UpdateTaskbar();
        StartMonitor();
        phases.Mark("monitor");
        _instance.Listen(() => _ui.BeginInvoke(() => ShowDashboard(CursorAnchor())));
        _started = true;
        AppLog.Info($"Started (language {_loc.LanguageCode}, {(_theme.IsDark ? "dark" : "light")} theme). {phases}");

        // Off the critical path: the Fluent dictionaries (only needed by menus, the flyout and settings) and housekeeping.
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var fluent = System.Diagnostics.Stopwatch.StartNew();
            _theme.EnableFluent();
            AutoStart.Refresh();
            AppLog.Info($"Fluent theme ready in {fluent.ElapsedMilliseconds} ms.");
            StartUpdates();
            _cost.Start();
            _traffic.Start();
            StartReporting();
            if (_launch.DemoSpeedValues is { Count: > 0 } speeds)
            {
                HeroStats.Enabled = true;
                AppLog.Info($"--demo-speed: holding the speed at {_launch.DemoSpeeds} tok/s in turn.");
                _pinnedSpeeds = new PinnedSpeedDriver(_hub, _ui, speeds);
                _pinnedSpeeds.Start();
            }

            if (_launch.HeroDemo is { } scenarioName && HeroTour.TryParse(scenarioName, out var scenario))
            {
                StartHeroDemo(scenario);
            }
            else if (_launch.OrbTour)
            {
                AppLog.Info("--orb-tour: looping the orbs through the speed tour.");
                _heroTour = new HeroTourDriver(_hub, _ui, HeroTourScenario.Tour, () => _speed.Redline);
                _heroTour.Start();
            }

            if (_launch.EphemeralSettings && _orbClock is { } clock)
            {
                // The orb benchmark harness (DESIGN 2a, Budgets): frame rate and process CPU (% of one core) every 10 s.
                var last = clock.FramesRendered;
                var lastTicks = clock.Ticks;
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var lastCpu = process.TotalProcessorTime;
                var lastWall = System.Diagnostics.Stopwatch.GetTimestamp();
                var lastPresented = Orb.Gpu.GpuOrbSurface.FramesPresented;
                var lastSkipped = Orb.Gpu.GpuOrbSurface.FramesSkipped;
                var stats = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var frames = clock.FramesRendered;
                    var ticks = clock.Ticks;
                    process.Refresh();
                    var cpu = process.TotalProcessorTime;
                    var wall = System.Diagnostics.Stopwatch.GetElapsedTime(lastWall).TotalSeconds;
                    var gpu = Orb.Gpu.OrbGpuRenderer.Timer?.Drain();
                    var presented = Orb.Gpu.GpuOrbSurface.FramesPresented;
                    var skipped = Orb.Gpu.GpuOrbSurface.FramesSkipped;
                    AppLog.Info($"ORBSTATS frames/s={(frames - last) / 10.0:0.0} ticks/s={(ticks - lastTicks) / 10.0:0.0} orbs={Settings.Orbs.Orbs.Length} hz={clock.RefreshHz:0.#} cpu={(cpu - lastCpu).TotalSeconds / wall * 100:0.00}% ws={process.WorkingSet64 / (1024 * 1024)}MB renderer={(Orb.Gpu.GpuDevice.Shared is null ? "wpf" : "d3d")} clock={clock.Source} presented={presented - lastPresented} skipped={skipped - lastSkipped} busy={Orb.Gpu.GpuOrbSurface.FramesBusy} gpu={(gpu is { Frames: > 0 } g ? g.Milliseconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "ms" : "-")}");
                    lastPresented = presented;
                    lastSkipped = skipped;
                    last = frames;
                    lastTicks = ticks;
                    lastCpu = cpu;
                    lastWall = System.Diagnostics.Stopwatch.GetTimestamp();
                }, _ui);
                stats.Start();
            }

            if (_launch.CaptureDir is { } captureDir && Directory.Exists(captureDir))
            {
                _capture = new DevCapture(_ui, captureDir, () => _orbs?.CaptureTargets() ?? [], CaptureMenu, target =>
                {
                    if (target == "dashboard")
                    {
                        return _dashboard is { IsShown: true } shown ? shown.Content as FrameworkElement : null;
                    }

                    if (ReportCaptureTarget(target) is { } reportTarget)
                    {
                        return reportTarget;
                    }

                    if (target == "history")
                    {
                        // An open window is captured as the ops left it (op history-all, op history-<source>).
                        if (_history?.IsOpen != true)
                        {
                            _history?.Open(null, traffic: true);
                        }

                        return _history?.CaptureContent;
                    }

                    OpenSettings(_capturePage);
                    return _settingsWindow?.FindName(char.ToUpperInvariant(_capturePage[0]) + _capturePage[1..] + "Page") as FrameworkElement;
                })
                {
                    Taskbar = (dark, scale) => _lastTaskbarState is { } state
                        ? TaskbarCapture.Render(dark is { } d ? state with { TaskbarDark = d } : state, _speed.Value, _speed.Tier, _speed.Redline, scale)
                        : null,
                    Badges = () => AgentSpeed.Orb.Hero.Badge.BadgeCapture.Presets(_loc),
                    Operation = op =>
                    {
                        var first = Settings.Orbs.Orbs.FirstOrDefault()?.Id;
                        switch (op)
                        {
                            case "add" when first is not null:
                                _orbs?.Add(first);
                                break;
                            case "split" when first is not null:
                                _orbs?.Split(first);
                                break;
                            case "merge" when first is not null:
                                _orbs?.Merge(first);
                                break;
                            case "undo":
                                _orbs?.Undo();
                                break;
                            case "corner" when first is not null:
                                _orbs?.MoveToNextCorner(first);
                                break;
                            default:
                                RunTitleCaptureOperation(op);
                                break;
                        }
                    },
                };
            }

            if (_launch.PreviewHistory)
            {
                StartHistoryPreview();
            }
            else if (_launch.ReportE2E is null || Environment.GetEnvironmentVariable("AGENT_SPEED_REPORT_E2E_HISTORY") == "1")
            {
                ScheduleHistoryStart();
            }
        });
    }

    public void OpenUpdate()
    {
        _updates.Present();
        ShowUpdateWindow();
    }

    public void CheckForUpdates()
    {
        _ = _updates.CheckAsync(manual: true);
        ShowUpdateWindow();
    }

    public void ToggleDashboardFromMenu() => ShowDashboard(CursorAnchor());

    /// <summary>Opens (or focuses) the Usage History window, filtered to <paramref name="source"/> or all sources.</summary>
    public void OpenHistory(SourceKind? source = null) => ShowHistory(source);

    /// <summary>Deletes the stored daily history and recomputes it from the logs that still exist.</summary>
    public void ClearHistory()
    {
        StartHistory();
        // Today's traffic restarts at zero and no traffic write is in flight while the store is deleted (METRICS 10.9).
        _traffic.ClearHistory();
        _historyHub.Clear();
    }

    public void UpdateSettings(Func<AppSettings, AppSettings> change) => _settings.Update(change);

    private bool ConfirmClearHistory() => ConfirmDialog.Show(_settingsWindow, _loc["app.name"], _loc["win.history.clear.title"],
        _history?.HasTrafficHistory == true ? $"{_loc["history.clear.confirm"]} {_loc["history.clear.traffic"]}" : _loc["history.clear.confirm"],
        _loc["history.clear.action"], _loc["history.cancel"]);

    /// <summary><c>traffic.helper.removeConfirm</c> / <c>traffic.helper.removeDetail</c> (DESIGN 10); true to remove.</summary>
    private bool ConfirmRemoveHelper() => ConfirmDialog.Show(_settingsWindow, _loc["app.name"], _loc["traffic.helper.removeConfirm"],
        _loc["traffic.helper.removeDetail"], _loc["traffic.helper.remove"].TrimEnd('…', '.'), _loc["history.cancel"]);

    public void OpenSettings(string? page = null)
    {
        _dashboard?.Dismiss();
        if (_settingsWindow is null)
        {
            _settingsModel ??= new SettingsViewModel(_settings, _preview, _loc, initial => SettingsWindow.PickFolder(_settingsWindow, initial),
                _updates, OpenUpdate, () => OpenHistory(), ConfirmClearHistory, ClearHistory, _cost) { TitleService = _titles };
            _settingsModel.Report ??= CreateReportPane();
            _settingsModel.Traffic ??= new TrafficPaneViewModel(_settings, _traffic, _traffic.Helper, _loc,
                () => _settingsWindow is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : 0,
                ConfirmRemoveHelper);
            if (_settingsModel.Orbs is null && _orbs is not null)
            {
                _settingsModel.Orbs = new Settings.OrbsPane.OrbsPaneViewModel(_settings, _orbs, _speed, _loc) { TitleService = _titles };
            }

            _settingsModel.Boards ??= new Settings.BoardsPane.BoardsPaneViewModel(_settings, _speed, _loc, AddBoard, RemoveBoard) { DarkTheme = _theme.IsDark };

            _settingsModel.ReduceMotion = _theme.ReduceMotion;
            _settingsModel.DarkTheme = _theme.IsDark;
            _settingsModel.UpdateLive(_speed.Snapshot, _taskbar?.Presence ?? TaskbarPresence.Hidden);
            _settingsWindow = new SettingsWindow(_settingsModel) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _orbs?.Highlight(null);
            };
        }

        if (page is not null)
        {
            _settingsWindow.ShowPage(page);
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    public void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        AppLog.Info("Quitting.");
        // One leaving report when it can be sent within a second (REPORTING 7.1), then the app exits.
        if (_report is { } report)
        {
            report.PrepareToQuit(() =>
            {
                Shutdown();
                _app.Shutdown();
            });
            return;
        }

        Shutdown();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Shutdown();
        _disposed = true;
    }

    private void Shutdown()
    {
        if (_disposed || !_started)
        {
            return;
        }

        _disposed = true;
        Try(() => _historyStartFallback?.Stop());
        Try(() => _history?.Dispose());
        Try(() => _historyService?.Dispose());
        Try(() => _settingsModel?.Report?.Dispose());
        Try(() => _report?.Dispose());
        Try(() => _consentDialog?.CloseFromController());
        Try(() => _previewWindow?.Close());
        Try(() => _traffic?.Dispose());
        Try(() => _cost.Dispose());
        Try(() => _settings.Flush());
        Try(() => _preview.Stop());
        Try(() => _pinnedSpeeds?.Dispose());
        Try(() => _heroTour?.Dispose());
        Try(() => _monitor?.Dispose());
        Try(() => _taskbar?.Dispose());
        Try(() => _tray?.Dispose());
        Try(() => _dashboardTimer?.Stop());
        Try(() => _menu?.Close());
        Try(() => _tooltip?.Close());
        Try(() => _boards?.Dispose());
        Try(() => _orbs?.Dispose());
        Try(() => _dashboard?.CloseForShutdown());
        Try(() => _settingsWindow?.Close());
        Try(() => _updateWindow?.Close());
        Try(() => _updates?.Dispose());
        Try(() => _speed.Dispose());
        Try(() => _theme.Dispose());
        Try(() => _shell?.Dispose());
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Cleanup step failed.", ex);
        }
    }

    private void StartMonitor()
    {
        if (_launch.ReportE2E is not null)
        {
            AppLog.Info("--report-e2e: the live monitor stays off; a synthetic engine feed drives live reporting.");
            return;
        }

        try
        {
            _monitor = new LiveMonitor(_hub, Settings.ToMonitorConfiguration(), _cost.Book);
            _monitor.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("The live monitor failed to start.", ex);
        }
    }

    private static string ResolveLanguage(AppLanguage language) =>
        LanguageResolver.Resolve(language, CultureInfo.CurrentUICulture);

    // ---- Data flow ----

    private void OnSpeedChanged()
    {
        var snapshot = _speed.Snapshot;
        var value = _speed.Value;
        var tier = _speed.Tier;
        var title = _speed.Title;
        var generating = snapshot.Agents.Where(a => a.Phase == AgentPhase.Generating).Select(a => a.Source).ToArray();
        var accessible = DisplayText.Accessibility(value, _titles.Text(title, _titles.General), _loc);

        _orbs?.Update(snapshot);
        _boards?.Update(snapshot);
        if (!_speed.IsPreviewing)
        {
            _traffic.UpdateAgents(snapshot.Agents);
        }

        if (_dashboard is { IsShown: true } dashboard)
        {
            dashboard.Hero.SetData(value, tier, title, generating, accessible);
            _dashboardDirty = true;
            if (title != _dashboardTitle)
            {
                // The title label changes in the same frame as the hero vehicle.
                RefreshDashboard();
            }

            if (!_dashboardTimer!.IsEnabled)
            {
                FlushDashboard();
                _dashboardTimer.Start();
            }
        }

        RememberHeroTitle(title);

        UpdateTaskbar();
        _tray?.Update(TrayState());

        if (_tooltipShown)
        {
            _tooltip?.SetText(LiveLine(), LiveMoney() is { } money ? money.HoverLine : DisplayText.TodaySummary(snapshot, _loc), TooltipTop());
        }

        if (!_historyStarted && snapshot.State.Kind != MonitorStateKind.Starting && _historyStartFallback is not null)
        {
            StartHistory();
        }

        if (_settingsModel is not null && _settingsWindow is not null && Environment.TickCount64 - _lastSettingsLiveUpdate > 1000)
        {
            _lastSettingsLiveUpdate = Environment.TickCount64;
            _settingsModel.UpdateLive(snapshot, _taskbar?.Presence ?? TaskbarPresence.Hidden);
        }
    }

    private void OnSpeedEvent(SpeedEvent speedEvent)
    {
        _orbs?.OnEvent(speedEvent);
        _boards?.OnEvent(speedEvent);
        switch (speedEvent)
        {
            case SpeedEvent.ResponseCompleted completed:
                var speed = completed.Response.Speed ?? _speed.Value;
                var tokens = _speed.Metric == SpeedMetric.Total ? completed.Response.TotalTokens : completed.Response.OutputTokens;
                var pulseSpeed = _speed.Metric == SpeedMetric.Total && completed.Response.Duration is { TotalSeconds: > 0 } d
                    ? completed.Response.TotalTokens / d.TotalSeconds
                    : speed;
                if (_dashboard is { IsShown: true } dashboard)
                {
                    dashboard.Hero.Pulse(pulseSpeed, tokens);
                }

                break;
            case SpeedEvent.NewPeak:
                if (_dashboard is { IsShown: true } shown)
                {
                    shown.Hero.Peak();
                }

                break;
        }
    }

    private void FlushDashboard()
    {
        if (_dashboard is not { IsShown: true } || !_dashboardDirty)
        {
            _dashboardTimer?.Stop();
            return;
        }

        RefreshDashboard();
    }

    private void RefreshDashboard()
    {
        if (_dashboard is not { } dashboard)
        {
            return;
        }

        _dashboardDirty = false;
        _dashboardTitle = _speed.Title;
        var snapshot = _speed.Snapshot;
        _dashboardModel.ReduceMotion = _theme.ReduceMotion;
        _dashboardModel.AgentsExpanded = Settings.AgentsExpanded;
        _dashboardModel.TitleTheme = _titles.General;
        _dashboardModel.TitleSlides = !_theme.ReduceMotion && Settings.Effects != EffectsIntensity.Off;
        _dashboardModel.CapsuleAppearance = CapsuleAppearance;
        _dashboardModel.TransparencyEnabled = _theme.TransparencyEnabled;
        _dashboardModel.LiveUnit = Settings.Cost.LiveUnit;
        _dashboardModel.Update(snapshot, _speed.Value, _speed.Title, _speed.Redline, _speed.Metric, _loc, _theme.IsDark);
        var peak = snapshot.PeakToday.ValueFor(_speed.Metric);
        var summary = $"{DisplayText.SpeedLine(_speed.Value, _titles.Text(_speed.Title, _titles.General), _loc)} · {_loc["today.peak"]} {SpeedFormat.Speed(peak)}";
        dashboard.LiveChart.SetData(snapshot.History, _speed.Metric, _speed.Redline, peak, Settings.ChartSeconds,
            _loc["win.chart.peak"], _loc["unit.tokS"], summary);
    }

    // ---- Surfaces ----

    private void ConfigureOrb()
    {
        var s = Settings;
        _orbs?.Apply();
        _boards?.Apply();
        // The hero's ring stays at Soft (DESIGN 4.1); its vehicle layer follows the Effects setting (DESIGN 4c).
        var general = _titles.General;
        var hero = new HeroConfig(new HeroOptions(s.Effects, _theme.ReduceMotion, _theme.HighContrast, _theme.IsDark), s.HeroLastTitle,
            _launch.HeroGlow == "strokes" ? HeroGlowMode.Strokes : HeroGlowMode.Blur)
        {
            Titles = general.ShowsVehicleArt ? null : new HeroTitles(general.Labels(_loc), _loc.LanguageCode),
        };
        _dashboard?.Hero.Configure(BuildOrbConfig(HeroDiameter, EffectsIntensity.Soft) with { BodyOpacity = 1, Hero = _launch.NoHero ? null : hero });
    }

    /// <summary>Persists the last non-idle title so the hero parks that vehicle across relaunches (not during previews).</summary>
    private void RememberHeroTitle(SpeedTitle title)
    {
        if (title == SpeedTitle.Idle || _speed.IsPreviewing || VehicleArt.KeyFor(title) is not { } key || key == Settings.HeroLastTitle)
        {
            return;
        }

        _settings.Update(s => s with { HeroLastTitle = key });
    }

    /// <summary>Developer option <c>--hero-demo</c>: the hero vehicle tour on a loop, with the dashboard open.</summary>
    private void StartHeroDemo(HeroTourScenario scenario)
    {
        AppLog.Info($"--hero-demo: running the {scenario} scenario ({_launch.HeroGlow ?? "blur"} glow).");
        HeroStats.Enabled = true;
        _heroTour = new HeroTourDriver(_hub, _ui, scenario, () => _speed.Redline);
        _heroTour.Start();
        ShowDashboard(CursorAnchor());
    }

    /// <summary>How effort capsules draw: the theme, high contrast, Reduce Motion and the Effects setting (DESIGN 4, Agents).</summary>
    private CapsuleAppearance CapsuleAppearance =>
        new(_theme.IsDark, _theme.HighContrast, _theme.ReduceMotion, Settings.Effects != EffectsIntensity.Off);

    private OrbConfig BuildOrbConfig(double diameter, EffectsIntensity? cap)
    {
        var s = Settings;
        var policy = new EffectsPolicy(s.Effects, _theme.ReduceMotion, _theme.HighContrast);
        if (cap is { } c)
        {
            policy = policy.CappedAt(c);
        }

        var labels = _titles.General.Labels(_loc);
        return new OrbConfig(diameter, _theme.IsDark ? OrbAppearance.Dark : OrbAppearance.Light, s.OrbOpacity, policy,
            _theme.HighContrast, !_theme.TransparencyEnabled, s.ResponsePulses, s.CelebratePeaks, _speed.Redline,
            _loc["unit.tokS"], _loc["peak.new"], labels);
    }

    private void UpdateTaskbar()
    {
        if (_taskbar is null)
        {
            return;
        }

        var s = Settings;
        var title = _titles.Text(_speed.Title, _titles.General);
        var money = LiveMoney();
        var bar = money is null ? ((string?)null, _loc["unit.tokS"]) : CostDisplay.BarValue(money, s.BarShowUnit);
        var accessible = money is null ? DisplayText.Accessibility(_speed.Value, title, _loc) : _cost.Display.LiveAccessibility(money, title);
        var secondary = BarSecondaryValue();
        if (secondary is not null)
        {
            accessible = $"{accessible}{SpokenSeparator}{secondary.Spoken}";
        }

        // DESIGN 10: the network block's words first, then the intelligence block's.
        var traffic = BarTrafficBlock.From(s, _traffic.Snapshot, _loc);
        var intelligence = BarIntelligence();
        accessible = BarReadouts.Append(accessible, SpokenSeparator, traffic, intelligence);

        _lastTaskbarState = new TaskbarState(
            s.ShowOnTaskbar,
            _speed.Value,
            _speed.Tier,
            _speed.Redline,
            s.BarMode,
            s.BarColoredGlyph,
            s.BarShowUnit,
            _theme.TaskbarIsDark,
            _theme.HighContrast,
            _theme.ReduceMotion,
            bar.Item2,
            $"{_loc["app.name"]}: {accessible}",
            bar.Item1,
            money?.Unknown ?? false,
            secondary,
            traffic,
            intelligence,
            s.TaskbarOffset);
        _taskbar.Update(_lastTaskbarState);
    }

    /// <summary>The taskbar intelligence block, its thinking line hidden after 3 s at 0 (DESIGN 10).</summary>
    private BarIntelligenceBlock? BarIntelligence() => BarIntelligenceBlock.From(Settings, _speed.Snapshot, _loc, _barThinking, _speed.Now);

    /// <summary>The tooltip's second line while the intelligence block shows a score: <c>task.tooltip.top</c> (DESIGN 10).</summary>
    private string? TooltipTop() => BarIntelligence()?.Top;

    /// <summary>The taskbar money block (DESIGN 3): the live rate and today's total after tok/s with the live value "tok/s + cost".</summary>
    private BarSecondary? BarSecondaryValue()
    {
        var s = Settings;
        return s.BarSecondary && s.Cost.LiveUnit == LiveUnit.Tokens ? BarSecondary.From(s, _speed.Snapshot, _cost.Display) : null;
    }

    /// <summary>Joins spoken parts: <c>、</c> in Japanese, <c>, </c> in English, <c>，</c> in Chinese.</summary>
    private string SpokenSeparator => _loc.LanguageCode switch
    {
        LanguageCodes.Ja => "、",
        LanguageCodes.En => ", ",
        _ => "，",
    };

    /// <summary>The billable live cost rate when <c>settings.liveUnit</c> is money (DESIGN 9); null for tok/s.</summary>
    private LiveMoney? LiveMoney() =>
        Settings.Cost.LiveUnit == LiveUnit.Cost ? _cost.Display.Live(_speed.Snapshot) : null;

    /// <summary>
    /// The taskbar and tray tooltip line: tok/s or money, the second value when shown (DESIGN 3), the title, then the
    /// readout blocks' words (DESIGN 10): network speed, then 性能.
    /// </summary>
    private string LiveLine()
    {
        var title = _titles.Text(_speed.Title, _titles.General);
        var main = LiveMoney() is { } money
            ? $"{money.NumberText} {money.Unit}"
            : $"{SpeedFormat.Speed(_speed.Value)} {_loc["unit.tokS"]}";
        var line = BarSecondaryValue() is { } secondary ? $"{main} · {secondary.Spoken} · {title}" : $"{main} · {title}";
        return BarReadouts.Append(line, " · ", BarTrafficBlock.From(Settings, _traffic.Snapshot, _loc), BarIntelligence());
    }

    // ---- Traffic ----

    /// <summary>Per-process traffic can be measured now (the menus' sources choice says it is unavailable otherwise).</summary>
    private bool SourceTrafficAvailable => _traffic.Snapshot.ProcessState.UnavailableReason is null;

    /// <summary>A traffic sample (1 Hz while measuring): the orbs' readouts, the taskbar block, the dashboard and the demand.</summary>
    private void OnTrafficChanged()
    {
        var snapshot = _traffic.Snapshot;
        _orbs?.UpdateTraffic(snapshot);
        RefreshDashboardTraffic();
        if (TrafficSettings.IsNetwork(Settings.Traffic.Bar))
        {
            UpdateTaskbar();
            if (_tooltipShown)
            {
                _tooltip?.SetText(LiveLine(), LiveMoney() is { } money ? money.HoverLine : DisplayText.TodaySummary(_speed.Snapshot, _loc),
                    TooltipTop());
            }
        }

        UpdateTrafficDemand();
    }

    /// <summary>The dashboard's Network section and its sparkline (DESIGN 10), while the dashboard shows.</summary>
    private void RefreshDashboardTraffic()
    {
        if (_dashboard is not { IsShown: true } dashboard)
        {
            return;
        }

        var traffic = Settings.Traffic;
        var snapshot = _traffic.Snapshot;
        _dashboardModel.UpdateTraffic(snapshot, traffic.Enabled, traffic.IncludeTools, traffic.Bits, _loc);
        if (!_dashboardModel.TrafficShown)
        {
            return;
        }

        var window = snapshot.MachineHistory.Count > TrafficSparkline.WindowSeconds
            ? snapshot.MachineHistory.Skip(snapshot.MachineHistory.Count - TrafficSparkline.WindowSeconds).ToArray()
            : snapshot.MachineHistory;
        var text = new TrafficText(_loc, traffic.Bits);
        var rx = window.Count > 0 ? window.Max(p => p.Rate.Rx) : 0;
        var tx = window.Count > 0 ? window.Max(p => p.Rate.Tx) : 0;
        dashboard.SetTrafficChart(window, traffic.Bits, _loc.Format("traffic.a11y.chart", TrafficText.Spoken(text.Rate(rx)), TrafficText.Spoken(text.Rate(tx))));
    }

    /// <summary>
    /// METRICS 10.10: per-source traffic is on screen while the dashboard is open, while the taskbar shows the agents' block,
    /// or while an orb's sources readout is showing (until it has faded out).
    /// </summary>
    private void UpdateTrafficDemand()
    {
        var s = Settings;
        var bar = s.ShowOnTaskbar && s.Traffic.Bar == BarTraffic.Agents && s.BarMode != BarDisplayMode.GlyphOnly;
        _traffic.SetDemand(s.Traffic.Enabled && ((_dashboard?.IsShown ?? false) || bar || (_orbs?.ShowsSourceTraffic ?? false)));
    }

    private TrayVisualState TrayState() => new(
        HeatMath.Heat(_speed.Value, _speed.Redline),
        _theme.TaskbarIsDark,
        _theme.HighContrast,
        !Settings.BarColoredGlyph,
        $"{_loc["app.name"]}\n{LiveLine()}");

    private void ShowMenu()
    {
        if (_menu is null || _quitting)
        {
            return;
        }

        HideTooltip();
        _menu.Show(AppMenu.Build(this, Settings, _loc, _updates.Available, SourceAvailability.From(_speed.Snapshot.Sources), SourceTrafficAvailable,
            ReportMenuState()));
    }

    private void ToggleDashboard(POINT anchor)
    {
        if (_dashboard is { IsShown: true } shown)
        {
            // Ignore the second half of a double click that just opened the flyout.
            if (Environment.TickCount64 - shown.LastShownTick > 350)
            {
                shown.Dismiss();
            }

            return;
        }

        // A click on the tray or taskbar first deactivates (and hides) an open flyout; do not reopen it.
        if (_dashboard is not null && Environment.TickCount64 - _dashboard.LastHiddenTick < 350)
        {
            return;
        }

        ShowDashboard(anchor);
    }

    private void ShowDashboard(POINT anchor)
    {
        if (_quitting)
        {
            return;
        }

        HideTooltip();
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_dashboardModel, () => _speed.Now) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
            _dashboard.SettingsRequested += (_, _) => OpenSettings();
            _dashboard.SourcesRequested += (_, _) => OpenSettings("sources");
            _dashboard.HistoryRequested += (_, source) => OpenHistory(source);
            _dashboard.QuitRequested += (_, _) => Quit();
            _dashboard.OrbToggled += (_, visible) => _settings.Update(s => s.WithShowOrbs(visible));
            _dashboard.RangeChanged += (_, seconds) => _settings.Update(s => s with { ChartSeconds = seconds });
            _dashboard.AgentsExpandedChanged += (_, expanded) => _settings.Update(s => s with { AgentsExpanded = expanded });
            _dashboard.UpdateRequested += (_, _) => OpenUpdate();
            _dashboard.UnitToggleRequested += (_, _) => ToggleLiveUnit();
            _dashboard.TrafficHistoryRequested += (_, source) => ShowHistory(source, traffic: true);
            _dashboard.TrafficSettingsRequested += (_, _) => OpenSettings("traffic");
            _dashboard.BoardsMenuRequested += (_, _) => ShowDashboardBoardsMenu();
            AttachReporting(_dashboard);
            _dashboard.IsVisibleChanged += (_, _) =>
            {
                if (_dashboard?.IsVisible != true)
                {
                    _dashboardTimer?.Stop();
                    if (_updateDoneShown)
                    {
                        _updateDoneText = null;
                        _dashboard?.SetUpdateDone(null);
                    }
                }
            };
            ConfigureOrb();
        }

        _dashboard.SetRange(Settings.ChartSeconds);
        _dashboard.SetOrbVisible(Settings.ShowOrbs);
        _dashboard.SetPreviewing(_preview.IsRunning);
        _dashboard.SetUpdate(UpdatePillText());
        _dashboard.SetUpdateDone(_updateDoneText);
        _updateDoneShown = _updateDoneText is not null;
        ShowReportInvitation();
        RefreshReportSurfaces();
        _dashboard.ApplyBackground();
        RefreshDashboard();
        _dashboard.ShowNear(anchor, _theme.ReduceMotion);
        OnSpeedChanged();
        RefreshDashboardTraffic();
        UpdateTrafficDemand();
    }

    private void OnTaskbarHover(bool isOver, RECT bounds)
    {
        if (_tooltip is null)
        {
            return;
        }

        if (!isOver || (_dashboard?.IsShown ?? false) || (_menu?.IsOpen ?? false))
        {
            HideTooltip();
            return;
        }

        _tooltipShown = true;
        _tooltip.Language = AppFonts.XmlLanguageFor(_loc.LanguageCode);
        var summary = LiveMoney() is { } money ? money.HoverLine : DisplayText.TodaySummary(_speed.Snapshot, _loc);
        _tooltip.ShowFor(bounds, LiveLine(), summary, _theme.ReduceMotion, _loc["taskbar.dragHint"], TooltipTop());
    }

    /// <summary>The user dragged the taskbar widget along the taskbar (DESIGN 3, Position on the taskbar).</summary>
    private void OnTaskbarMoved(double offset)
    {
        AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Taskbar widget moved to {offset:0.##} DIPs from the notification area."));
        _settings.Update(s => s with { TaskbarOffset = offset });
    }

    private void HideTooltip()
    {
        _tooltipShown = false;
        _tooltip?.HideTip();
    }

    // ---- Usage history ----

    /// <param name="traffic">Open on the traffic metric (a row of the dashboard's Network section, DESIGN 10).</param>
    private void ShowHistory(SourceKind? source, bool traffic = false)
    {
        if (_quitting)
        {
            return;
        }

        _dashboard?.Dismiss();
        StartHistory();
        _history?.Open(source, traffic);
    }

    /// <summary>
    /// The daily history analysis starts once the live monitor has finished seeding today, when the history is first
    /// needed, or after 20 s at the latest, so it never competes with the first live numbers.
    /// </summary>
    private void ScheduleHistoryStart()
    {
        _historyStartFallback = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => StartHistory(), _ui);
        _historyStartFallback.Start();
        if (_speed.Snapshot.State.Kind != MonitorStateKind.Starting)
        {
            StartHistory();
        }
    }

    private void StartHistory()
    {
        _historyStartFallback?.Stop();
        if (_historyStarted || _quitting)
        {
            return;
        }

        _historyStarted = true;
        try
        {
            _historyService = new HistoryService(_historyHub, Settings.ToMonitorConfiguration(), pricing: _cost.Book)
            {
                PerfRecordsUpdated = records => _monitor?.SetPerfRecords(records),
            };
            _historyService.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("The daily history analysis failed to start.", ex);
            _historyHub.Publish(HistoryState.Failed(ex.GetType().Name));
        }
    }

    /// <summary>Developer option <c>--preview-history</c>: generated days instead of the real analysis, then the window.</summary>
    private void StartHistoryPreview()
    {
        _historyStarted = true;
        AppLog.Info("--preview-history: showing generated usage history.");
        _history?.StartPreview();
        ShowHistory(null);
    }

    // ---- Changes ----

    private void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        if (previous with { HeroLastTitle = current.HeroLastTitle, HistoryModeKind = current.HistoryModeKind } == current)
        {
            // Only the parked hero vehicle or the history's effort / speed choice was remembered; the history shows it already.
            return;
        }

        if (previous with { Report = current.Report } == current)
        {
            // Live reporting's own state (DESIGN 11, Storage); its surfaces follow ReportController.Changed.
            return;
        }

        if (previous.Appearance != current.Appearance)
        {
            _theme.Mode = current.Appearance;
        }

        _updates.OnSettingsChanged(previous, current);
        _cost.OnSettingsChanged(previous, current);
        _traffic.OnSettingsChanged(previous, current);
        if (previous.Traffic.IncludeTools != current.Traffic.IncludeTools || previous.EnabledSourceSet() != current.EnabledSourceSet())
        {
            _history?.OnTrafficSettingsChanged();
        }

        if (previous.Effects != current.Effects)
        {
            // The history's top-rung meters shimmer only with effects on.
            _history?.OnThemeChanged();
        }

        if (previous.Language != current.Language)
        {
            _loc.LanguageCode = ResolveLanguage(current.Language);
        }

        if (previous.Metric != current.Metric || previous.Redline != current.Redline)
        {
            _speed.Configure(current.Metric, current.Redline);
        }

        if (!previous.ToMonitorConfiguration().Equals(current.ToMonitorConfiguration()))
        {
            try
            {
                _monitor?.Update(current.ToMonitorConfiguration());
            }
            catch (Exception ex)
            {
                AppLog.Error("Updating the live monitor configuration failed.", ex);
            }

            try
            {
                _historyService?.Update(current.ToMonitorConfiguration());
            }
            catch (Exception ex)
            {
                AppLog.Error("Updating the history configuration failed.", ex);
            }
        }

        ConfigureOrb();
        _orbs?.UpdateTraffic(_traffic.Snapshot);
        UpdateTaskbar();
        UpdateTrafficDemand();
        _tray?.Update(TrayState());
        _dashboard?.SetOrbVisible(current.ShowOrbs);
        if (previous.ChartSeconds != current.ChartSeconds)
        {
            _dashboard?.SetRange(current.ChartSeconds);
        }

        _dashboardDirty = true;
        FlushDashboard();
    }

    /// <summary>The settings page the developer capture's <c>settings</c> request renders (<c>op page-general</c> …).</summary>
    private string _capturePage = "orb";

    /// <summary>
    /// Developer capture operations for the title themes (only with <c>--capture-dir</c>): <c>page-&lt;key&gt;</c>,
    /// <c>theme-&lt;preset id in lowercase&gt;</c>, <c>theme-custom</c> (a sample custom set), <c>lang-&lt;code&gt;</c>,
    /// <c>appearance-&lt;auto|light|dark&gt;</c>, <c>dashboard</c>, live reporting's <c>report-enable</c>, <c>report-start</c>,
    /// <c>report-preview</c>, <c>report-cancel</c>, and <c>demo-reasoning-off</c> / <c>demo-reasoning-on</c> (with
    /// <c>--demo-speed</c>: the pinned agents stop or resume reporting reasoning, so the thinking rate reads 0).
    /// </summary>
    private void RunTitleCaptureOperation(string op)
    {
        if (op.StartsWith("page-", StringComparison.Ordinal))
        {
            _capturePage = op[5..];
            OpenSettings(_capturePage);
        }
        else if (op == "theme-custom")
        {
            var id = _titles.Copy("cultivation");
            if (id is not null)
            {
                _titles.Rename(id, "我的修仙路");
                _titles.CommitField(id, 4, "結丹");
                _titles.CommitField(id, 12, "飛升");
            }
        }
        else if (op.StartsWith("theme-", StringComparison.Ordinal))
        {
            var id = TitlePresets.All.Select(TitlePresets.Id).FirstOrDefault(p => p.Equals(op[6..], StringComparison.OrdinalIgnoreCase));
            if (id is not null)
            {
                _titles.Select(id);
            }
        }
        else if (op.StartsWith("lang-", StringComparison.Ordinal))
        {
            var language = op[5..] switch
            {
                "zh-hant" => AppLanguage.ZhHant,
                "zh-hans" => AppLanguage.ZhHans,
                "ja" => AppLanguage.Ja,
                "en" => AppLanguage.En,
                _ => AppLanguage.System,
            };
            _settings.Update(s => s with { Language = language });
        }
        else if (op.StartsWith("appearance-", StringComparison.Ordinal) && Enum.TryParse<AppearanceMode>(op[11..], true, out var mode))
        {
            _settings.Update(s => s with { Appearance = mode });
        }
        else if (op == "dashboard")
        {
            ShowDashboard(CursorAnchor());
        }
        else if (op is "demo-reasoning-off" or "demo-reasoning-on" && _pinnedSpeeds is { } pinned)
        {
            pinned.Reasoning = op == "demo-reasoning-on";
        }
        else if (op == "report-enable")
        {
            _report?.SetEnabled(true);
        }
        else if (op == "report-start")
        {
            _report?.StartReporting();
        }
        else if (op == "report-preview")
        {
            OpenReportPreview();
        }
        else if (op == "report-cancel")
        {
            _report?.CancelConsent();
            _previewWindow?.Close();
        }
        else if (op == "history-all")
        {
            _history?.Open(null);
        }
        else if (op.StartsWith("history-", StringComparison.Ordinal) && Enum.TryParse<SourceKind>(op[8..], true, out var source))
        {
            _history?.Open(source);
        }
    }

    /// <summary>A title theme, a custom set or a draft changed: every surface takes its new titles.</summary>
    /// <summary>The hero's unit: switches <c>settings.liveUnit</c> and announces the new unit once (DESIGN 9 Accessibility).</summary>
    private void ToggleLiveUnit()
    {
        var next = Settings.Cost.LiveUnit == LiveUnit.Cost ? LiveUnit.Tokens : LiveUnit.Cost;
        _settings.Update(s => s with { Cost = s.Cost with { LiveUnit = next } });
        var unit = next == LiveUnit.Cost ? _cost.Display.UnitPerSecond : _loc["unit.tokS"];
        if (_dashboard is { IsShown: true } dashboard)
        {
            dashboard.Announce(_loc.Format("a11y.unitChanged", unit));
        }
    }

    /// <summary>The currency, rates, subscription marks or live unit changed: every money value is shown again (DESIGN 9).</summary>
    private void OnCostChanged()
    {
        if (!_started)
        {
            return;
        }

        _orbs?.Apply();
        _orbs?.Update(_speed.Snapshot);
        _boards?.Apply();
        UpdateTaskbar();
        _tray?.Update(TrayState());
        _dashboardDirty = true;
        FlushDashboard();
    }

    private void OnTitlesChanged()
    {
        ConfigureOrb();
        UpdateTaskbar();
        _tray?.Update(TrayState());
        if (_dashboard is { IsShown: true } dashboard)
        {
            dashboard.Hero.SetData(_speed.Value, _speed.Tier, _speed.Title,
                _speed.Snapshot.Agents.Where(a => a.Phase == AgentPhase.Generating).Select(a => a.Source).ToArray(),
                DisplayText.Accessibility(_speed.Value, _titles.Text(_speed.Title, _titles.General), _loc));
        }

        _dashboardDirty = true;
        FlushDashboard();
    }

    private void OnThemeChanged()
    {
        ConfigureOrb();
        UpdateTaskbar();
        _tray?.Update(TrayState());
        _dashboard?.ApplyBackground();
        _dashboard?.ReportInvitation.SetAppearance(_theme.IsDark, _theme.HighContrast, _theme.ReduceMotion);
        _history?.OnThemeChanged();
        if (_settingsModel is not null)
        {
            _settingsModel.ReduceMotion = _theme.ReduceMotion;
            _settingsModel.DarkTheme = _theme.IsDark;
        }

        _dashboardDirty = true;
        FlushDashboard();
    }

    private void OnLanguageChanged()
    {
        AppFonts.Apply(_app.Resources, _loc.LanguageCode);
        var language = AppFonts.XmlLanguageFor(_loc.LanguageCode);
        foreach (Window window in _app.Windows)
        {
            window.Language = language;
        }

        _settingsModel?.Relocalize(_loc);
        _settingsModel?.Report?.SetLocalizer(_loc);
        RefreshReportSurfaces();
        _orbs?.Relocalize(_loc);
        _boards?.Relocalize(_loc);
        _updateWindowModel?.Relocalize(_loc);
        _history?.OnLanguageChanged();
        _dashboard?.SetUpdate(UpdatePillText());
        ConfigureOrb();
        UpdateTaskbar();
        _tray?.Update(TrayState());
        OnSpeedChanged();
        _dashboardDirty = true;
        FlushDashboard();
    }

    private void OnDisplayChanged()
    {
        _orbs?.OnDisplayChanged();
        _boards?.OnDisplayChanged();
        _taskbar?.Reposition();
        _tray?.Refresh();
        _dashboard?.Dismiss();
    }

    private void OnSystemSettingChanged(string? area)
    {
        _theme.QueueRefresh();
        _taskbar?.Reposition();
        if (area is "WindowMetrics" or "TraySettings")
        {
            _tray?.Refresh();
        }
    }

    // ---- Updates ----

    private void StartUpdates()
    {
        _updates.Start();
        _ = Task.Run(UpdateRelaunch.CleanupLeftovers);
        if (_launch.AfterUpdate && !string.Equals(_launch.PreviousVersion, AppPaths.Version, StringComparison.Ordinal))
        {
            _updateDoneText = _loc.Format("update.done", AppPaths.Version);
            ShowBalloon(_loc["app.name"], _updateDoneText, opensUpdate: false);
        }

        if (_launch.CheckUpdatesNow)
        {
            _ = CheckNowAtStartupAsync();
        }
    }

    /// <summary>Development flag <c>--check-updates-now</c>: a manual check that opens the window when something is found.</summary>
    private async Task CheckNowAtStartupAsync()
    {
        AppLog.Info("--check-updates-now: checking for updates.");
        try
        {
            await _updates.CheckAsync(manual: true);
            if (_updates.Available is not null && !_quitting)
            {
                OpenUpdate();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            AppLog.Warn($"--check-updates-now failed: {UpdateLog.Describe(ex)}");
        }
    }

    private void ShowUpdateWindow()
    {
        if (_quitting)
        {
            return;
        }

        _dashboard?.Dismiss();
        if (_updateWindow is null)
        {
            _updateWindowModel = new UpdateWindowViewModel(_updates, _loc);
            _updateWindow = new UpdateWindow(_updateWindowModel) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
            _updateWindow.Closed += (_, _) =>
            {
                _updateWindow = null;
                _updateWindowModel = null;
            };
        }

        if (!_updateWindow.IsVisible)
        {
            _updateWindow.Show();
        }

        if (_updateWindow.WindowState == WindowState.Minimized)
        {
            _updateWindow.WindowState = WindowState.Normal;
        }

        _updateWindow.Activate();
    }

    private void OnUpdatesChanged() => _dashboard?.SetUpdate(UpdatePillText());

    private string? UpdatePillText() => _updates.Available is { } release ? UpdateText.Pill(release, _loc) : null;

    private void ShowBalloon(string title, string text, bool opensUpdate)
    {
        _balloonOpensUpdate = opensUpdate;
        _tray?.ShowBalloon(title, text);
    }

    private void OnBalloonClicked()
    {
        if (_balloonOpensUpdate && _updates.Available is not null)
        {
            OpenUpdate();
        }
    }

    // ---- Anchors (physical pixels) ----

    private POINT TrayAnchor()
    {
        if (_tray?.GetBounds() is { } rect)
        {
            return Center(rect);
        }

        return CursorAnchor();
    }

    // ---- Floating orbs (IOrbHost and the orb commands of IAppCommands) ----

    public void ShowDashboardAt(POINT anchor) => ShowDashboard(anchor);

    /// <summary>Opens a menu for <see cref="DevCapture"/> and returns it (<c>menu-tray</c> or <c>menu-orb</c>).</summary>
    private System.Windows.Controls.ContextMenu? CaptureMenu(string kind)
    {
        if (_menu is null)
        {
            return null;
        }

        var menu = kind == "menu-orb" && Settings.Orbs.Orbs.FirstOrDefault() is { } orb
            ? OrbMenu.Build(this, orb.Id, Settings, SourceAvailability.From(_speed.Snapshot.Sources), _loc, SourceTrafficAvailable)
            : AppMenu.Build(this, Settings, _loc, _updates.Available, SourceAvailability.From(_speed.Snapshot.Sources), SourceTrafficAvailable,
                ReportMenuState());
        if (menu is null)
        {
            return null;
        }

        var at = Monitors.Primary() is { } primary ? new POINT(primary.WorkArea.Left + 400, primary.WorkArea.Top + 200) : new POINT(400, 200);
        _menu.Show(menu, at);
        return menu;
    }

    public void ShowOrbMenu(string orbId)
    {
        if (_menu is null || _quitting)
        {
            return;
        }

        HideTooltip();
        if (OrbMenu.Build(this, orbId, Settings, SourceAvailability.From(_speed.Snapshot.Sources), _loc, SourceTrafficAvailable) is { } menu)
        {
            _menu.Show(menu);
        }
    }

    public void OpenOrbSettings(string orbId)
    {
        OpenSettings("orb");
        _settingsModel?.SelectOrb(orbId);
    }

    public void AddOrb(string fromOrbId) => _orbs?.Add(fromOrbId);

    public void DuplicateOrb(string orbId) => _orbs?.Duplicate(orbId);

    public void SplitOrb(string orbId) => _orbs?.Split(orbId);

    public void MergeOrbs(string keepOrbId) => _orbs?.Merge(keepOrbId);

    public void RemoveOrb(string orbId)
    {
        if (Settings.Orbs.Find(orbId) is not { } orb
            || !OrbMenu.ConfirmRemove(null, OrbNaming.DisplayName(orb, _loc), _loc))
        {
            return;
        }

        _orbs?.Remove(orbId);
    }

    private static POINT CursorAnchor() => User32.GetCursorPos(out var point) ? point : new POINT(0, 0);

    private static POINT Center(RECT rect) => new((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
}
