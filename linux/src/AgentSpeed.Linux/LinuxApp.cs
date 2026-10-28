using System.Diagnostics;
using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Linux.UI;
using AgentSpeed.Linux.Services;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Reporting;
using AgentSpeed.Services;
using AgentSpeed.Traffic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using UiDispatcher = AgentSpeed.Linux.Platform.Dispatcher;

namespace AgentSpeed.Linux;

internal sealed class LinuxApp : Application
{
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private FileStream? _instance;
    private TrayIcon? _tray;
    private TopBarBridge? _bridge;
    private LinuxPowerEvents? _power;
    private readonly Avalonia.Threading.DispatcherTimer _maintenance = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _nextUpdateCheck;
    private readonly Dictionary<string, OrbWindow> _orbs = [];
    private readonly LinuxOrbSyncGate _orbSyncGate = new();
    private bool _disposing;
    public readonly Stopwatch Clock = Stopwatch.StartNew();
    public SettingsService Settings { get; private set; } = null!;
    internal LinuxAccessibilityState Accessibility { get; private set; } = null!;
    public Localizer Loc { get; private set; } = null!;
    public SpeedHub Speed { get; private set; } = null!;
    public HistoryHub History { get; private set; } = null!;
    public HistoryService HistoryService { get; private set; } = null!;
    public LiveMonitor Monitor { get; private set; } = null!;
    public TrafficService Traffic { get; private set; } = null!;
    public CostService Cost { get; private set; } = null!;
    public ReportController Report { get; private set; } = null!;
    public LinuxUpdater Updater { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;
    public bool Quitting { get; private set; }
    private bool _restart;
    public bool Preview { get; private set; }
    internal static string BuildVersion => System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LinuxApp).Assembly)?.InformationalVersion.Split('+')[0]
        ?? typeof(LinuxApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string Version => BuildVersion;
    public event Action? Changed;

    internal OrbLayoutResult PlaceNewOrb(OrbsDocument document, string newId, string? fromId)
    {
        var displays = LinuxOrbDisplays();
        var centers = _orbs.Values
            .Select(window => (window.Definition.Id, Center: window.LiveCenter))
            .ToDictionary(pair => pair.Id, pair => pair.Center, StringComparer.Ordinal);
        return LinuxOrbLayout.PlaceNew(document, displays, centers, newId, fromId);
    }

    internal IReadOnlyList<OrbDisplay> LinuxOrbDisplays()
    {
        var screens = Main?.Screens.All ?? Array.Empty<Screen>();
        var primary = Main?.Screens.Primary;
        return LinuxOrbLayout.Displays(screens, primary);
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        LinuxPaths.EnsurePrivate(LinuxPaths.Config);
        LinuxPaths.EnsurePrivate(LinuxPaths.Data);
        LinuxPaths.EnsurePrivate(LinuxPaths.Cache);
        try
        {
            _instance = new FileStream(Path.Combine(LinuxPaths.Cache, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            await TopBarBridge.ActivateExistingAsync("dashboard");
            desktop.Shutdown(); return;
        }
        Preview = desktop.Args?.Contains("--preview") == true || desktop.Args?.Contains("--smoke-test") == true;
        Accessibility = LinuxAccessibilityState.Create(Preview, LinuxAccessibility.ReadGnomeAsync);
        var dispatcher = new UiDispatcher();
        Settings = new SettingsService(dispatcher); Settings.Load();
        ApplyAppearance();
        if (Preview)
        {
            Settings.MakeEphemeral();
            Settings.Update(s => s with { NetworkEnabled = false, Report = s.Report with { Enabled = false },
                Orbs = new AgentSpeed.Presentation.Settings.Orbs.OrbsDocument(
                    AgentSpeed.Presentation.Orb.OrbStyles.All.Select(style => new AgentSpeed.Presentation.Settings.Orbs.OrbDefinition(style.ToString()) { Style = style })) });
        }
        Loc = new Localizer(Localizer.LoadEmbeddedTable(), LanguageResolver.Resolve(Settings.Current.Language, CultureInfo.CurrentUICulture));
        Speed = new SpeedHub(new UiSynchronizationContext());
        History = new HistoryHub(new UiSynchronizationContext());
        Cost = new CostService(dispatcher, Settings, Loc, Version, LinuxPaths.History);
        Cost.Prices.TableAccepted += (_, _) => History.Refresh();
        Monitor = new LiveMonitor(Speed, Settings.Current.ToMonitorConfiguration(), Cost.Book);
        HistoryService = new HistoryService(History, Settings.Current.ToMonitorConfiguration(), LinuxPaths.History, Cost.Book);
        Traffic = new TrafficService(Speed);
        Report = new ReportController(dispatcher, Settings, Speed, Traffic, Cost.Book, Version);
        Updater = new LinuxUpdater(this);
        _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.FirstCheck(DateTimeOffset.UtcNow, Settings.Current.LastUpdateCheck);
        Updater.Changed += () =>
        {
            if (Updater.Available is { } release) Main?.ShowUpdate(release.DisplayVersion);
        };
        Report.Activate();
        Speed.LivePublished += (_, s) => HistoryService.ObserveLive(s);
        Speed.SnapshotChanged += (_, _) => { RefreshTray(); Changed?.Invoke(); };
        Traffic.Hub.SnapshotChanged += (_, _) => Changed?.Invoke();
        Cost.Changed += (_, _) => Changed?.Invoke();
        Settings.Changed += (_, e) =>
        {
            Loc.LanguageCode = LanguageResolver.Resolve(e.Current.Language, CultureInfo.CurrentUICulture);
            ApplyAppearance();
            Monitor.Update(e.Current.ToMonitorConfiguration()); HistoryService.Update(e.Current.ToMonitorConfiguration());
            Cost.OnSettingsChanged(e.Previous, e.Current);
            Traffic.Monitor.SetEnabled(e.Current.Traffic.Enabled);
            if (!e.Current.NetworkEnabled) Updater.Cancel();
            if (e.Current.NetworkEnabled != e.Previous.NetworkEnabled || e.Current.AutoCheckUpdates != e.Previous.AutoCheckUpdates)
                _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.FirstCheck(DateTimeOffset.UtcNow, e.Current.LastUpdateCheck);
            SyncOrbs(); Changed?.Invoke();
        };
        Main = new MainWindow(this); desktop.MainWindow = Main;
        Main.Screens.Changed += ScreensChanged;
        ApplyAppearance();
        if (desktop.Args?.Contains("--background") != true) Main.Show();
        CreateTray(); SyncOrbs();
        desktop.Exit += (_, _) =>
        {
            DisposeServices();
            if (_restart) Process.Start(new ProcessStartInfo("/usr/bin/agent-speed") { UseShellExecute = false });
        };
        base.OnFrameworkInitializationCompleted();
        await RefreshAccessibilityAsync();
        if (Quitting) return;
        if (!Preview)
        {
            Monitor.Start(); HistoryService.Start(); Traffic.Monitor.Start(Settings.Current.Traffic.Enabled); Cost.Start();
        }
        else StartPreview();
        _bridge = new TopBarBridge(this);
        try { await _bridge.StartAsync(); }
        catch (Exception ex) { if (!Quitting) AgentSpeed.App.AppLog.Warn("GNOME top bar bridge is unavailable.", ex); }
        if (Quitting) return;
        _power = new LinuxPowerEvents(this);
        if (!Preview) await _power.StartAsync();
        if (Quitting) return;
        Main.PropertyChanged += (_, e) => { if (e.Property == TopLevel.ActualThemeVariantProperty) SyncOrbs(); };
        var maintenanceRunning = false;
        _maintenance.Tick += async (_, _) =>
        {
            if (maintenanceRunning || Quitting) return;
            maintenanceRunning = true;
            try
            {
                await RefreshAccessibilityAsync();
                if (!Quitting && !Preview && Version != "0.0.0" && Settings.Current.NetworkEnabled && Settings.Current.AutoCheckUpdates
                    && DateTimeOffset.UtcNow >= _nextUpdateCheck)
                {
                    _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.NextCheck(DateTimeOffset.UtcNow);
                    await Updater.CheckAsync(manual: false);
                }
            }
            finally { maintenanceRunning = false; }
        };
        _maintenance.Start();
        if (desktop.Args?.Contains("--smoke-test") == true)
        {
            await System.Threading.Tasks.Task.Delay(1800);
            if (Quitting) return;
            try
            {
                var first = _orbs.Values.First();
                X11Windows.SetClickThrough(first, true);
                if (X11Windows.InputRectangleCount(first) != 0) throw new InvalidOperationException("X11 click-through was not applied.");
                X11Windows.SetClickThrough(first, false, first.Definition.Size);
                if (X11Windows.InputRectangleCount(first) <= 0) throw new InvalidOperationException("X11 mouse input was not restored.");
                var center = (int)(first.Definition.Size * .75 * first.RenderScaling);
                if (X11Windows.InputContains(first, 0, 0) || !X11Windows.InputContains(first, center, center))
                    throw new InvalidOperationException("The orb input shape blocks transparent pixels or misses its visible center.");
                await Main.CheckSurfacesAsync();
                if (Quitting) return;
                foreach (var orb in _orbs.Values)
                {
                    var expected = RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light
                        ? AgentSpeed.Presentation.Colors.OrbAppearance.Light : AgentSpeed.Presentation.Colors.OrbAppearance.Dark;
                    if (Main.RequestedThemeVariant != RequestedThemeVariant) throw new InvalidOperationException("Main window theme is out of sync.");
                    if (orb.Appearance != expected) throw new InvalidOperationException("The orb did not follow the actual desktop window theme.");
                    orb.SavePreview(Path.Combine(LinuxPaths.Cache, $"smoke-{orb.Definition.Style}.png"));
                    orb.SavePreview(Path.Combine(LinuxPaths.Cache, $"smoke-{orb.Definition.Style}-cpu.png"), software: true);
                }
                var closedId = first.Definition.Id;
                first.Close(); // Exercise the same user-window close path used by the desktop window manager.
                if (_orbs.ContainsKey(closedId) || Settings.Current.Orbs.Find(closedId) is not { Visible: false })
                    throw new InvalidOperationException("Closing an orb left a stale window or a visible setting.");
                if (Settings.Current.Orbs.Layouts.All(layout => !layout.Positions.ContainsKey(closedId)))
                    throw new InvalidOperationException("Closing an orb did not persist its final position.");
                SyncOrbs();
                if (_orbs.ContainsKey(closedId))
                    throw new InvalidOperationException("SyncOrbs retained a closed orb window.");
                await CheckOrbLifecycleAsync(closedId);
            }
            catch (Exception ex) { Console.Error.WriteLine($"UI smoke test failed: {ex}"); Environment.ExitCode = 1; }
            finally { Quit(); }
        }
    }

    private async Task CheckOrbLifecycleAsync(string closedId)
    {
        Settings.Update(s => s with { Orbs = s.Orbs.Replace(s.Orbs.Find(closedId)! with { Visible = true }) });
        await Task.Delay(300);
        if (!_orbs.TryGetValue(closedId, out var restored))
            throw new InvalidOperationException("A user-closed orb could not be shown again.");
        restored.Position = new PixelPoint(restored.Position.X - 13, restored.Position.Y - 11);
        var wanted = restored.LiveCenter;
        // Hide before the 400 ms debounce expires: close must persist the final move synchronously.
        Settings.Update(s => s.WithShowOrbs(false));
        if (_orbs.Count != 0) throw new InvalidOperationException("Hiding all orbs retained live windows.");
        Settings.Update(s => s.WithShowOrbs(true));
        await Task.Delay(300);
        if (!_orbs.TryGetValue(closedId, out var shown) || OrbGeometry.Distance(wanted, shown.LiveCenter) > 2)
            throw new InvalidOperationException("Hide/show lost an orb move that was still waiting for its save debounce.");
        for (var i = 0; i < 3; i++)
        {
            Settings.Update(s => s.WithShowOrbs(false));
            Settings.Update(s => s.WithShowOrbs(true));
        }
        await Task.Delay(300);
        var originalCount = _orbs.Count;
        foreach (var duplicate in new[] { false, true })
        {
            var edit = duplicate ? OrbEditing.Duplicate(Settings.Current.Orbs, closedId)
                : OrbEditing.Add(Settings.Current.Orbs, closedId, SourceSet.Everything);
            if (edit is null) throw new InvalidOperationException("The preview could not add or duplicate an orb.");
            var id = edit.Created.Single();
            var placement = PlaceNewOrb(edit.Document, id, closedId);
            Settings.Update(s => s with { Orbs = placement.Document });
            await Task.Delay(300);
            if (_orbs.Count != originalCount + 1 || !_orbs.TryGetValue(id, out var added))
                throw new InvalidOperationException("Adding an orb did not create exactly one window.");
            var anchor = _orbs[closedId];
            var scale = Main.Screens.ScreenFromWindow(added)?.Scaling ?? 1;
            if (OrbGeometry.Distance(anchor.LiveCenter, added.LiveCenter)
                < (anchor.Definition.Size + added.Definition.Size) * scale / 2 + OrbGeometry.Gap * scale - 2)
                throw new InvalidOperationException("An added or duplicated orb overlaps its anchor.");
            Settings.Update(s => s with { Orbs = OrbEditing.Remove(s.Orbs, id)! });
            if (_orbs.Count != originalCount || _orbs.ContainsKey(id))
                throw new InvalidOperationException("Removing an orb retained a stale window.");
        }
    }

    public void SyncOrbs(bool repositionExisting = false)
    {
        if (Quitting || _disposing) return;
        _orbSyncGate.Run(repositionExisting, () =>
        {
            var repeat = false;
            do
            {
                repeat = false;
                var reposition = _orbSyncGate.TakeRepositionRequest();
                var settings = Settings.Current;
                var document = settings.Orbs;
                var screens = Main?.Screens.All ?? Array.Empty<Screen>();
                var layout = LinuxOrbLayout.Resolve(document, LinuxOrbDisplays());
                if (layout.Document != document)
                {
                    _orbSyncGate.RequestReposition(reposition);
                    Settings.Update(s => s with { Orbs = layout.Document });
                    repeat = true;
                    continue;
                }

                foreach (var (id, window) in _orbs.ToArray())
                    if (!settings.ShowOrbs || document.Find(id) is not { Visible: true })
                    {
                        LinuxOrbPositionLifecycle.Close(document.Find(id) is not null, window.FlushPosition, window.CloseFromApplication,
                            () => _orbs.Remove(id));
                    }
                if (settings.ShowOrbs)
                    foreach (var definition in document.Orbs.Where(o => o.Visible))
                    {
                        if (!layout.Locations.TryGetValue(definition.Id, out var location)) continue;
                        var screen = screens.FirstOrDefault(s => LinuxOrbLayout.Key(s) == location.Display)
                            ?? Main?.Screens.Primary ?? screens.FirstOrDefault();
                        if (screen is null) continue;
                        if (!_orbs.TryGetValue(definition.Id, out var window))
                        {
                            window = new OrbWindow(this, definition);
                            _orbs[definition.Id] = window;
                            window.PlaceAt(location, screen, layout.Arrangement);
                            window.Show();
                        }
                        else
                        {
                            var resized = window.Configure(definition);
                            if (reposition || resized) window.PlaceAt(location, screen, layout.Arrangement);
                        }
                    }
                Monitor.SetSubsets(document.Orbs.Select(o => o.TrackedSources).OfType<SourceSet>().Distinct().ToArray());
            } while (repeat || _orbSyncGate.HasRepositionRequest);
        });
    }
    private void ApplyAppearance()
    {
        RequestedThemeVariant = Settings.Current.Appearance switch
        {
            AppearanceMode.Light => Avalonia.Styling.ThemeVariant.Light,
            AppearanceMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Accessibility.Current.PrefersDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        if (Main is not null) Main.RequestedThemeVariant = RequestedThemeVariant;
    }
    private async Task RefreshAccessibilityAsync()
    {
        if (Preview || Quitting || !await Accessibility.RefreshAsync() || Quitting) return;
        var previousVariant = RequestedThemeVariant;
        ApplyAppearance();
        if (RequestedThemeVariant == previousVariant) Main?.RefreshAccessibilityPalette();
        SyncOrbs();
    }
    public void Activate(string action)
    {
        if (action == "quit") { Quit(); return; }
        if (action == "toggle-orbs") { Settings.Update(s => s.WithShowOrbs(!s.ShowOrbs)); return; }
        if (action == "restore-input")
        { Settings.Update(s => s with { Orbs = s.Orbs.WithOrbs(s.Orbs.Orbs.Select(o => o with { ClickThrough = false })) }); return; }
        Main.Show(); Main.Activate();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        X11Windows.Activate(Main);
        if (action is "dashboard" or "settings" or "history") Main.SelectPage(action);
    }
    private void CreateTray()
    {
        _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://agent-speed/Assets/icon.png"))), ToolTipText = "Agent Speed" };
        var menu = new NativeMenu();
        foreach (var (label, action) in new[] { ("Agent Speed", "dashboard"), ("Usage history", "history"), ("Settings", "settings"),
                     ("Show / hide orbs", "toggle-orbs"), ("Restore mouse input", "restore-input"), ("Quit", "quit") })
        { var item = new NativeMenuItem(label); item.Click += (_, _) => Activate(action); menu.Add(item); }
        _tray.Menu = menu; _tray.Clicked += (_, _) => Activate("dashboard"); _tray.IsVisible = true;
        TrayIcon.SetIcons(this, [_tray]);
    }
    private void RefreshTray()
    {
        if (_tray is not null) _tray.ToolTipText = $"Agent Speed · {Speed.Snapshot.Live.ValueFor(Settings.Current.Metric):0.0} tok/s";
    }
    private Avalonia.Threading.DispatcherTimer? _previewTimer;

    /// <summary>UI smoke only: freeze the synthetic preview clock so a screenshot does not race a timer tick.</summary>
    internal void PausePreviewForSmoke() => _previewTimer?.Stop();

    private void StartPreview()
    {
        History.Publish(HistoryState.Ready(DateTimeOffset.Now), PreviewHistory.Days());
        Speed.BeginPreview();
        _previewTimer = new(Avalonia.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
        void Publish()
        {
            var value = 84 + 38 * Math.Sin(Clock.Elapsed.TotalSeconds * .5);
            var now = DateTimeOffset.Now;
            Speed.PublishPreview(SpeedSnapshot.Empty with
            {
                Timestamp = now, State = MonitorState.Running, Live = new(value, value * 5), PeakToday = new(184, 920),
                Today = new(132, 1_480_000, 1_220_000, 0, 87_400), GeneratingCount = 1,
                Sources = [new(SourceKind.Codex, true, "preview", true, 6, new(value, value * 5), default, 1)],
                Agents = [new("preview", SourceKind.Codex, "Preview project", "gpt-6.1-sol", AgentPhase.Generating, false, new(value, value * 5), value, now)],
                History = Enumerable.Range(0, 120).Select(i => new HistoryPoint(now.AddSeconds(i - 119), new(84 + 38 * Math.Sin(i * .15), 0), default)).ToArray(),
            });
        }
        Publish();
        _previewTimer.Tick += (_, _) => Publish();
        _previewTimer.Start();
    }
    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        FlushOrbPositions();
        Settings.Flush();
        Report.PrepareToQuit(() => _desktop.Shutdown(Environment.ExitCode));
    }
    internal void FlushOrbPositions()
    {
        foreach (var orb in _orbs.Values.ToArray()) orb.FlushPosition();
    }
    private void ScreensChanged(object? sender, EventArgs e)
    {
        if (Quitting || _disposing) return;
        SyncOrbs(repositionExisting: true);
        foreach (var orb in _orbs.Values.ToArray()) orb.RefreshInputShape();
    }
    internal bool IsOrbWindowRegistered(OrbWindow window) =>
        _orbs.TryGetValue(window.Definition.Id, out var current) && ReferenceEquals(current, window);

    internal void OrbWindowClosed(OrbWindow window)
    {
        var id = window.Definition.Id;
        if (!_orbs.TryGetValue(id, out var current) || !ReferenceEquals(current, window)) return;
        _orbs.Remove(id);
        if (Quitting || _disposing || !Settings.Current.ShowOrbs
            || Settings.Current.Orbs.Find(id) is not { Visible: true }) return;

        Settings.Update(s => s.Orbs.Find(id) is { Visible: true } orb
            ? s with { Orbs = s.Orbs.Replace(orb with { Visible = false }) }
            : s);
    }

    public void Restart() { _restart = true; Quit(); }
    private void DisposeServices()
    {
        if (_disposing) return;
        _disposing = true;
        Quitting = true;
        if (Main is not null) Main.Screens.Changed -= ScreensChanged;
        _previewTimer?.Stop(); _maintenance.Stop(); _power?.Dispose();
        FlushOrbPositions(); Settings.Flush();
        foreach (var orb in _orbs.Values.ToArray()) orb.CloseFromApplication();
        _bridge?.Dispose(); _tray?.Dispose();
        Updater.Dispose(); Report.Dispose(); Monitor.Dispose(); HistoryService.Dispose(); Traffic.Dispose(); Cost.Dispose(); Settings.Dispose(); _instance?.Dispose();
    }
}
