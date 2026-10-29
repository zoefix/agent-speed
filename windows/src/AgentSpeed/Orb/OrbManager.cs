using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Interop;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Orb.Names;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Services;
using AgentSpeed.Theme;
using AgentSpeed.Titles;

namespace AgentSpeed.Orb;

/// <summary>What the orb manager needs from the application.</summary>
internal interface IOrbHost
{
    void ShowDashboardAt(POINT anchor);

    void ShowOrbMenu(string orbId);

    void OpenOrbSettings(string orbId);
}

/// <summary>Kind of the last undoable change (DESIGN 2b, Split and merge: undo for 8 s).</summary>
internal enum OrbUndoKind
{
    Split,
    SplitLimited,
    Merge,
    Remove,
}

/// <summary>The pending undo: the document before the change, restored exactly by <see cref="OrbManager.Undo"/>.</summary>
internal sealed record OrbUndo(OrbUndoKind Kind, OrbsDocument Before, string AnchorId, DateTime Expires, string Text);

/// <summary>
/// The floating orbs (DESIGN 2b): one window per visible orb, each with its own data feed (METRICS 6.8), positions per
/// display arrangement, magnetic placement with alignment guides, z-order, name plates, source and undo chips, and the
/// editing operations (add, duplicate, split, merge, remove, undo). The dashboard and the taskbar keep showing all
/// sources.
/// </summary>
internal sealed class OrbManager : IDisposable
{
    private static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ChipTime = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _ui;
    private readonly OrbClock _clock;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly SpeedModel _speed;
    private readonly IOrbHost _host;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _deadline;
    private readonly DispatcherTimer _chipTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly List<OrbGuideWindow> _guides = [];
    private readonly NamePlates _plates;
    private Localizer _loc;
    private OrbChipWindow? _chip;
    private string? _chipOrb;
    private DateTime _chipUntil;
    private OrbChipWindow? _undoChip;
    private OrbsDocument? _known;
    private bool _started;
    private bool _disposed;
    private string? _preferred;
    private uint _seed = 1;
    private bool _platesDirty;
    private SourceAvailability? _availability;
    private TrafficSnapshot _traffic = TrafficSnapshot.Off;

    public OrbManager(Dispatcher ui, OrbClock clock, SettingsService settings, ThemeService theme, SpeedModel speed, Localizer loc, IOrbHost host)
    {
        _ui = ui;
        _clock = clock;
        _settings = settings;
        _theme = theme;
        _speed = speed;
        _loc = loc;
        _host = host;
        _plates = new NamePlates(theme, loc, () => _speed.Now);
        _deadline = new DispatcherTimer(DispatcherPriority.Normal, ui);
        _deadline.Tick += (_, _) =>
        {
            _deadline.Stop();
            TickFeeds();
        };
        _chipTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => RefreshChip(), ui);
        _chipTimer.Stop();
        _undoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => CheckUndo(), ui);
        _undoTimer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        GpuOrbSurface.RendererFailed += OnRendererFailed;
        UnitAnnounced += (_, e) =>
        {
            if (_entries.TryGetValue(e.OrbId, out var entry))
            {
                entry.Window.Announce(e.Text);
            }
        };
        CompositorTicker.DisplayStateChanged += OnDisplayStateChanged;
    }

    /// <summary>Rendering pauses while the session is locked (DESIGN 2a, Frame policy); data keeps flowing.</summary>
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e) => _ui.BeginInvoke(() =>
    {
        _locked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
            || (_locked && e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect));
        foreach (var entry in _entries.Values)
        {
            entry.Window.Surface.Locked = _locked;
        }
    });

    private bool _locked;

    /// <summary>The GPU could not be used any more: every orb is rebuilt with the WPF renderer.</summary>
    private bool _wpfFallback;

    private void OnRendererFailed(object? sender, EventArgs e) => _ui.BeginInvoke(() =>
    {
        if (_wpfFallback || _disposed)
        {
            return;
        }

        App.AppLog.Warn("The orb's GPU renderer failed; switching the orbs to the WPF renderer.");
        _wpfFallback = true;
        foreach (var id in _entries.Keys.ToArray())
        {
            CloseEntry(id);
        }

        Apply();
    });

    /// <summary>Rendering pauses while the console display is off (DESIGN 2a, Frame policy); the first frame after shows the current state.</summary>
    private void OnDisplayStateChanged(object? sender, EventArgs e)
    {
        foreach (var entry in _entries.Values)
        {
            entry.Window.Surface.Locked = _locked || CompositorTicker.DisplayOff;
        }
    }

    /// <summary>Raised when the pending undo appears, changes or expires (the Settings banner follows it).</summary>
    public event EventHandler? UndoChanged;

    public OrbUndo? PendingUndo { get; private set; }

    private AppSettings Settings => _settings.Current;

    /// <summary>Each orb's title theme (its own, else General); without it the orbs show the Vehicles titles.</summary>
    public TitleThemesService? Titles { get; init; }

    private TitleTheme ThemeOf(OrbDefinition orb) => Titles?.ForOrb(orb) ?? TitleTheme.Vehicles;

    /// <summary>Formats the money unit of orbs set to cost (DESIGN 9); without it every orb shows tok/s.</summary>
    public Presentation.Cost.CostDisplay? Cost { get; init; }

    /// <summary>Visible orb windows by id (for thumbnails' live values and the settings highlight).</summary>
    public IReadOnlyDictionary<string, OrbFeed> Feeds => _entries.ToDictionary(e => e.Key, e => e.Value.Feed);

    /// <summary>Live centers of the shown orbs (physical pixels).</summary>
    public IReadOnlyDictionary<string, PointD> Centers =>
        _entries.Where(e => e.Value.Window.HasCenter).ToDictionary(e => e.Key, e => e.Value.Window.Center, StringComparer.Ordinal);

    /// <summary>
    /// The visible orb surfaces, then their name plates and the undo chip, with their screen rectangles (physical px) and
    /// window opacity, for the developer capture.
    /// </summary>
    public IReadOnlyList<(FrameworkElement Visual, Rect ScreenRect, double Opacity)> CaptureTargets()
    {
        var list = new List<(FrameworkElement, Rect, double)>();
        foreach (var entry in _entries.Values)
        {
            if (entry.Window.Handle != 0 && User32.GetWindowRect(entry.Window.Handle, out var rect))
            {
                list.Add((new OrbSnapshotElement(entry.Window), new Rect(rect.Left, rect.Top, rect.Width, rect.Height), entry.Window.Opacity));
            }
        }

        list.AddRange(_plates.CaptureTargets());
        if (_chip is { IsVisible: true } chip && chip.Content is FrameworkElement content
            && User32.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(chip).Handle, out var chipRect))
        {
            list.Add((content, new Rect(chipRect.Left, chipRect.Top, chipRect.Width, chipRect.Height), chip.Opacity));
        }

        return list;
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        _plates.Relocalize(loc);
        foreach (var (id, entry) in _entries)
        {
            SetActions(entry.Window, id);
            entry.Window.SetUnitHint(_loc["orbs.unit.toggleHint"]);
            entry.Window.SetLanguage(AppFonts.XmlLanguageFor(_loc.LanguageCode));
        }

        Apply();
    }

    /// <summary>Synchronizes the windows with the settings (document, master switch, theme, effects, metric).</summary>
    public void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var settings = Settings;
        var document = settings.Orbs;
        var previous = _known;
        _known = document;
        var visible = settings.ShowOrbs ? document.Orbs.Where(o => o.Visible).ToArray() : [];
        var ids = visible.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            CloseEntry(id);
        }

        var created = new List<string>();
        var resourced = new List<string>();
        foreach (var orb in visible)
        {
            if (!_entries.TryGetValue(orb.Id, out var entry))
            {
                entry = CreateEntry(orb);
                var isNew = _started && previous is not null && previous.Find(orb.Id) is null;
                entry.Feed.Configure(orb.TrackedSources, settings.Metric, orb.EffectiveRedline(settings.Metric, settings), NowMs(), isNew);
                created.Add(orb.Id);
                if (isNew)
                {
                    entry.ShowChipOnLanding = true;
                }
            }
            else
            {
                var sourcesChanged = entry.Definition.TrackedSources != orb.TrackedSources;
                entry.Feed.Configure(orb.TrackedSources, settings.Metric, orb.EffectiveRedline(settings.Metric, settings), NowMs());
                if (sourcesChanged)
                {
                    resourced.Add(orb.Id);
                }
            }

            entry.Definition = orb;
            var unitChanged = entry.Feed.Unit != orb.Unit;
            entry.Feed.Unit = orb.Unit;
            entry.Feed.Cost = Cost;
            ConfigureEntry(entry);
            if (unitChanged && _started)
            {
                // The value switches with the unit line at once (DESIGN 9 Orb, Transition), not with the next snapshot.
                entry.Feed.Update(_speed.Snapshot, _speed.Now);
                Push(entry, settings.Orbs.Orbs.Length == 1);
            }
        }

        foreach (var id in created)
        {
            var window = _entries[id].Window;
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (_started && previous is not null && previous.Find(id) is null && !_entries[id].Silent && _entries[id].FlyFrom is null)
            {
                window.Appear();
            }
        }

        PositionAll(created);

        _started = true;
        _clock.RefreshDisplay();
        UpdateTraffic(_traffic);
        Update(_speed.Snapshot);
        ResolvePlates();

        // After the plates are laid out for the new sources: the chip (or the plate folding it) shows the new content.
        foreach (var id in resourced)
        {
            ShowChip(id, ChipTime);
        }
    }

    /// <summary>Applies a data snapshot to every orb.</summary>
    public void Update(SpeedSnapshot snapshot)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var now = _speed.Now;
        var single = Settings.Orbs.Orbs.Length == 1;
        foreach (var entry in _entries.Values)
        {
            var feed = entry.Feed;
            feed.Previewing = _speed.IsPreviewing;
            var fired = feed.Update(snapshot, now);
            if (entry.Definition.Traffic == OrbTraffic.Intelligence)
            {
                FeedLines(entry);
            }

            Push(entry, single);
            if (fired)
            {
                entry.Window.Surface.Peak();
            }
        }

        ScheduleDeadline();
        var availability = Availability();
        if (_availability != availability)
        {
            _availability = availability;
            ResolvePlates();
        }
    }

    /// <summary>Applies one traffic sample to the readout of every orb that shows network speed (DESIGN 10).</summary>
    public void UpdateTraffic(TrafficSnapshot traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        _traffic = traffic;
        foreach (var entry in _entries.Values)
        {
            if (entry.Definition.Traffic != OrbTraffic.Intelligence)
            {
                FeedLines(entry);
            }
        }
    }

    /// <summary>
    /// The orb's two-line readout (DESIGN 10): its sources' traffic with tools per the setting (<c>sources</c>), this
    /// computer (<c>machine</c>), the live intelligence of its sources (<c>intelligence</c>, METRICS 6.9) or nothing. Network
    /// speed is not drawn while measuring is off or, for sources, while per-process traffic is unavailable; intelligence
    /// needs neither. The quiet rule keeps a quiet orb free of frames.
    /// </summary>
    private void FeedLines(Entry entry)
    {
        var mode = entry.Definition.Traffic;
        var settings = Settings.Traffic;
        var network = settings.Enabled && _traffic.Enabled
            && (mode == OrbTraffic.Machine || (mode == OrbTraffic.Sources && _traffic.ProcessState.UnavailableReason is null));
        OrbLinesSample? sample = mode switch
        {
            OrbTraffic.Intelligence => IntelligenceSample(entry.Feed.Reading),
            OrbTraffic.Machine when network => OrbLinesText.Traffic(_traffic.MachineLive ?? TrafficRate.Zero, settings.Bits, _loc),
            OrbTraffic.Sources when network => OrbLinesText.Traffic(
                _traffic.Live(entry.Definition.TrackedSources, settings.IncludeTools) ?? TrafficRate.Zero, settings.Bits, _loc),
            _ => null,
        };
        entry.Window.Surface.SetLines(sample, sample is not null);
    }

    /// <summary>
    /// 性能 of the orb's agents <c>A</c> (METRICS 6.8, 6.9 <i>Sets and groups</i>): the highest score, live before resting,
    /// with the supplying agent's state and N, over the thinking rate of the orb's sources.
    /// </summary>
    private OrbLinesSample IntelligenceSample(SubsetReading reading) =>
        OrbLinesText.Intelligence(PerfSetReading.Of(reading.Intelligence, reading.Agents), reading.Intelligence,
            reading.GeneratingSources.Count > 0, _loc);

    /// <summary>
    /// Some visible orb shows its sources' traffic right now (until its readout has faded out): per-source sampling runs
    /// every second (METRICS 10.10).
    /// </summary>
    public bool ShowsSourceTraffic => _entries.Values.Any(e => e.Definition.Traffic == OrbTraffic.Sources && e.Window.Surface.LinesVisible);

    /// <summary>Delivers an engine event to the orbs it belongs to (completions by source, all-sources new peak).</summary>
    public void OnEvent(SpeedEvent speedEvent)
    {
        foreach (var entry in _entries.Values)
        {
            if (!entry.Feed.Accepts(speedEvent))
            {
                continue;
            }

            switch (speedEvent)
            {
                case SpeedEvent.ResponseCompleted completed:
                    var response = completed.Response;
                    var metric = Settings.Metric;
                    var tokens = metric == SpeedMetric.Total ? response.TotalTokens : response.OutputTokens;
                    var speed = metric == SpeedMetric.Total && response.Duration is { TotalSeconds: > 0 } d
                        ? response.TotalTokens / d.TotalSeconds
                        : response.Speed ?? entry.Feed.Value;
                    entry.Window.Surface.Pulse(speed, tokens);
                    break;
                case SpeedEvent.NewPeak:
                    entry.Window.Surface.Peak();
                    break;
            }
        }
    }

    public void OnDisplayChanged()
    {
        _clock.RefreshDisplay();
        PositionAll([]);
        ResolvePlates();
    }

    /// <summary>Rings one orb on the desktop while its Settings row is hovered, selected or focused (null: none).</summary>
    public void Highlight(string? orbId)
    {
        foreach (var (id, entry) in _entries)
        {
            entry.Window.Surface.Highlighted = id == orbId;
        }
    }

    // ---- Editing operations (menu and Settings) ----

    public void Add(string? fromId)
    {
        if (OrbEditing.Add(Settings.Orbs, fromId, AvailableSources()) is not { } result)
        {
            return;
        }

        var placed = OrbArrangement.PlaceNew(result.Document, Displays(), Centers, result.Created[0], fromId);
        Commit(placed.Document);
        ShowNewChip(result.Created[0]);
    }

    public void Duplicate(string id)
    {
        if (OrbEditing.Duplicate(Settings.Orbs, id) is not { } result)
        {
            return;
        }

        var placed = OrbArrangement.PlaceNew(result.Document, Displays(), Centers, result.Created[0], id);
        Commit(placed.Document);
        ShowNewChip(result.Created[0]);
    }

    public bool CanSplit(string id) => OrbEditing.CanSplit(Settings.Orbs, id, AvailableSources());

    public void Split(string id)
    {
        var before = Settings.Orbs;
        if (OrbEditing.Split(before, id, AvailableSources()) is not { } result)
        {
            return;
        }

        var placed = OrbArrangement.PlaceSplit(result.Document, Displays(), Centers, id, result.Created);
        FlyFrom(id, result.Created);
        Commit(placed.Document);
        SetUndo(SplitUndo(result, before, id, result.Created.Contains(id) ? result.Created.Count : result.Created.Count + 1));
        ShowChip(id, ChipTime);
    }

    public void SplitBySource(string? selectedId)
    {
        var before = Settings.Orbs;
        if (OrbEditing.SplitBySource(before, selectedId, AvailableSources()) is not { } result)
        {
            return;
        }

        var origin = result.Document.Orbs[0].Id;
        var placed = OrbArrangement.PlaceSplit(result.Document, Displays(), Centers, origin, result.Created);
        Commit(placed.Document);
        SetUndo(SplitUndo(result, before, origin, result.Document.Orbs.Length));
    }

    private OrbUndo SplitUndo(OrbEditResult result, OrbsDocument before, string anchor, int count)
    {
        return result.Limited
            ? new OrbUndo(OrbUndoKind.SplitLimited, before, anchor, DateTime.UtcNow + UndoTime, _loc.Format("orbs.didSplitLimited", count))
            : new OrbUndo(OrbUndoKind.Split, before, anchor, DateTime.UtcNow + UndoTime, _loc.Format("orbs.didSplit", count));
    }

    public void Merge(string keepId)
    {
        var before = Settings.Orbs;
        if (OrbEditing.Merge(before, keepId) is not { } merged)
        {
            return;
        }

        FlyTo(keepId, before.Orbs.Select(o => o.Id).Where(id => id != keepId).ToArray(), () =>
        {
            Commit(merged);
            SetUndo(new OrbUndo(OrbUndoKind.Merge, before, keepId, DateTime.UtcNow + UndoTime, _loc["orbs.didMerge"]));
            ShowChip(keepId, ChipTime);
        });
    }

    public void Remove(string id)
    {
        var before = Settings.Orbs;
        if (OrbEditing.Remove(before, id) is not { } removed)
        {
            return;
        }

        var anchor = removed.Orbs.FirstOrDefault(o => o.Visible)?.Id ?? removed.Orbs[0].Id;
        var name = before.Find(id) is { } gone ? OrbNaming.DisplayName(gone, _loc) : string.Empty;
        void Finish()
        {
            Commit(removed);
            SetUndo(new OrbUndo(OrbUndoKind.Remove, before, anchor, DateTime.UtcNow + UndoTime, _loc.Format("orbs.didRemove", name)));
        }

        if (_entries.TryGetValue(id, out var entry) && entry.Window.IsVisible)
        {
            _plates.Vanish(id);
            entry.Window.Vanish(Finish);
        }
        else
        {
            Finish();
        }
    }

    public void Undo()
    {
        if (PendingUndo is not { } undo)
        {
            return;
        }

        SetUndo(null);
        foreach (var entry in _entries.Values)
        {
            entry.Silent = true;
        }

        Commit(undo.Before);
    }

    public void MoveToNextCorner(string id)
    {
        var placed = OrbArrangement.MoveToNextCorner(Settings.Orbs, Displays(), Centers, id);
        Commit(placed.Document);
        if (placed.Locations.TryGetValue(id, out var location) && _entries.TryGetValue(id, out var entry))
        {
            entry.Window.AnimateTo(location.Center);
        }
    }

    public void ResetPosition(string id)
    {
        var placed = OrbArrangement.ResetPosition(Settings.Orbs, Displays(), Centers, id);
        Commit(placed.Document);
        if (placed.Locations.TryGetValue(id, out var location) && _entries.TryGetValue(id, out var entry))
        {
            entry.Window.AnimateTo(location.Center);
        }
    }

    public SourceSet AvailableSources()
    {
        var snapshot = _speed.Snapshot;
        var set = SourceSet.Empty;
        foreach (var status in snapshot.Sources)
        {
            if (status.Enabled && !OrbSubset.IsUnavailable(status))
            {
                set = set.With(status.Kind);
            }
        }

        return set;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        GpuOrbSurface.RendererFailed -= OnRendererFailed;
        CompositorTicker.DisplayStateChanged -= OnDisplayStateChanged;
        _plates.Dispose();
        _deadline.Stop();
        _chipTimer.Stop();
        _undoTimer.Stop();
        foreach (var id in _entries.Keys.ToArray())
        {
            CloseEntry(id);
        }

        _chip?.Close();
        _undoChip?.Close();
        foreach (var guide in _guides)
        {
            guide.Close();
        }
    }

    // ---- Entries ----

    private Entry CreateEntry(OrbDefinition orb)
    {
        var seed = _seed++ * 2654435761u | 1;
        IOrbWindow window = !_wpfFallback && GpuDevice.Shared is { } device
            ? new GpuOrbWindow(orb.Id, () => _speed.Now, _clock, seed, device)
            : new OrbWindow(orb.Id, () => _speed.Now, _clock, seed);
        window.SetLanguage(AppFonts.XmlLanguageFor(_loc.LanguageCode));
        var entry = new Entry(orb, window, new OrbFeed(orb.Id));
        if (_pendingFlights.Remove(orb.Id, out var from))
        {
            entry.FlyFrom = from;
            entry.ShowChipOnLanding = true;
        }

        window.DashboardRequested += (_, _) => _host.ShowDashboardAt(new POINT((int)window.Center.X, (int)window.Center.Y));
        window.MenuRequested += (_, _) => _host.ShowOrbMenu(orb.Id);
        window.Interacted += (_, _) => Prefer(orb.Id);
        window.HoverChanged += (_, hovering) => OnHover(orb.Id, hovering);
        window.DragAssist = raw => DragAssist(orb.Id, raw);
        window.DragMoved += (_, _) =>
        {
            ShowChip(orb.Id, TimeSpan.Zero);
        };
        window.DragReleased += (_, raw) => OnDragReleased(orb.Id, raw);
        window.UnitToggleRequested += (_, _) => ToggleUnit(orb.Id);
        window.SetUnitHint(_loc["orbs.unit.toggleHint"]);
        SetActions(window, orb.Id);
        _entries[orb.Id] = entry;
        return entry;
    }

    private void SetActions(IOrbWindow window, string id) => window.SetActions(
    [
        (_loc["menu.dashboard"], () => _host.ShowDashboardAt(new POINT((int)window.Center.X, (int)window.Center.Y))),
        (_loc["a11y.orb.showMenu"], () => _host.ShowOrbMenu(id)),
        (_loc["a11y.orb.moveCorner"], () => MoveToNextCorner(id)),
        (_loc["a11y.orb.toggleUnit"], () => ToggleUnit(id)),
        (_loc["menu.orb.settings"], () => _host.OpenOrbSettings(id)),
    ]);

    /// <summary>
    /// Switches an orb between tok/s and money (DESIGN 9 Orb, Switching): the unit click of a floating orb window (after
    /// the double-click interval, see <see cref="OrbUnitToggle"/>) and the UI Automation action. Persisted at once; the new
    /// unit is announced once (<c>a11y.unitChanged</c>).
    /// </summary>
    public void ToggleUnit(string orbId)
    {
        if (_disposed || Settings.Orbs.Find(orbId) is not { } orb)
        {
            return;
        }

        var next = orb.Unit.Toggled();
        _settings.Update(s => s with { Orbs = OrbEditing.SetUnit(s.Orbs, orbId, next) });
        var unit = next == OrbUnit.Cost && Cost is { } cost ? cost.UnitPerSecond : _loc["unit.tokS"];
        UnitAnnounced?.Invoke(this, (orbId, _loc.Format("a11y.unitChanged", unit)));
    }

    /// <summary>A unit switch to announce once through UI Automation by the orb's window (orb id, text).</summary>
    public event EventHandler<(string OrbId, string Text)>? UnitAnnounced;

    private void CloseEntry(string id)
    {
        if (!_entries.Remove(id, out var entry))
        {
            return;
        }

        if (_chipOrb == id)
        {
            HideChip();
        }

        _plates.Remove(id);
        entry.Window.Close();
    }

    private void ConfigureEntry(Entry entry)
    {
        var settings = Settings;
        var orb = entry.Definition;
        var effects = new EffectsPolicy(settings.Effects, _theme.ReduceMotion, _theme.HighContrast);
        var texts = Texts(ThemeOf(orb), orb.Unit == OrbUnit.Cost && Cost is { } cost ? cost.UnitPerSecond : null);
        var sources = orb.TrackedSources is { } set ? set.ToArray() : null;
        var unavailable = UnavailableOf(entry);
        var config = new OrbSurfaceConfig(orb.Style, orb.Size, _theme.IsDark ? OrbAppearance.Dark : OrbAppearance.Light, effects,
            _theme.HighContrast, !_theme.TransparencyEnabled, orb.Opacity, settings.ResponsePulses, settings.CelebratePeaks,
            orb.EffectiveRedline(settings.Metric, settings), texts, sources, unavailable);
        var options = new OrbWindowOptions(orb.AlwaysOnTop, orb.ClickThrough, orb.FadeWhenIdle, entry.Feed.IsUnavailable, _theme.ReduceMotion);
        if (entry.Config is { } last && last with { Sources = null } == config with { Sources = null }
            && (last.Sources ?? []).SequenceEqual(config.Sources ?? []) && (last.Sources is null) == (config.Sources is null) && entry.Options == options)
        {
            return;
        }

        entry.Config = config;
        entry.Options = options;
        entry.Window.Configure(config, options);
        entry.Window.Surface.IsPreferred = _preferred == orb.Id;
    }

    private readonly Dictionary<(TitleTheme Theme, string? Unit), ReadoutTexts> _texts = new();
    private string? _textsLanguage;

    /// <summary>
    /// The readout texts of a title theme, rebuilt only when the language or the theme's titles change (so unchanged
    /// configurations compare equal).
    /// </summary>
    /// <param name="moneyUnit">The money unit line (<c>US$/s</c>, DESIGN 9) of an orb set to cost; null for tok/s.</param>
    private ReadoutTexts Texts(TitleTheme theme, string? moneyUnit = null)
    {
        if (_textsLanguage != _loc.LanguageCode || _texts.Count > 32)
        {
            _texts.Clear();
            _textsLanguage = _loc.LanguageCode;
        }

        if (!_texts.TryGetValue((theme, moneyUnit), out var texts))
        {
            texts = new ReadoutTexts(moneyUnit ?? _loc["unit.tokS"], _loc["peak.new"], theme.Labels(_loc), _loc.UsesLatinScript);
            _texts[(theme, moneyUnit)] = texts;
        }

        return texts;
    }

    private SourceSet UnavailableOf(Entry entry)
    {
        var set = SourceSet.Empty;
        foreach (var source in entry.Feed.Reading.Unavailable)
        {
            set = set.With(source);
        }

        return set;
    }

    private void Push(Entry entry, bool single)
    {
        var feed = entry.Feed;
        var reading = feed.Reading;
        var surface = entry.Window.Surface;
        var orb = entry.Definition;
        string? unavailableTitle = null;
        if (reading.State != SubsetState.Available)
        {
            unavailableTitle = _loc[reading.State == SubsetState.AllDisabled ? "source.disabled" : "source.notFound"];
        }

        // Money (DESIGN 9): the value and hover line change; heat, tiers and titles keep following tokens.
        var money = unavailableTitle is null ? feed.Money : null;
        surface.Scene.SetMoney(money);
        var info = money?.HoverLine ?? _loc.Format("today.summary", SpeedFormat.Tokens(reading.Today.Output), reading.Agents.Count);
        surface.SetData(feed.Value, feed.Tier, feed.Title, reading.GeneratingSources, info, unavailableTitle);
        surface.SetIdentity(orb.TrackedSources?.ToArray(), UnavailableOf(entry));
        entry.Window.SetDimmed(unavailableTitle is not null);
        entry.Window.NoteActivity(feed.Tier != SpeedTier.Idle);

        var name = OrbNaming.DisplayName(orb, _loc);
        string accessible;
        if (unavailableTitle is not null)
        {
            accessible = _loc.Format("a11y.orb.unavailable", name);
        }
        else if (money is not null && Cost is { } costDisplay)
        {
            var named = single && orb.TrackedSources is null ? null : name;
            accessible = costDisplay.LiveAccessibility(money, ThemeOf(orb).Text(feed.Title, _loc), named);
        }
        else if (single && orb.TrackedSources is null)
        {
            accessible = DisplayText.Accessibility(feed.Value, ThemeOf(orb).Text(feed.Title, _loc), _loc);
        }
        else
        {
            accessible = _loc.Format("a11y.orb.named", name, SpeedFormat.Speed(feed.Value), ThemeOf(orb).Text(feed.Title, _loc));
        }

        var lines = unavailableTitle is null && surface.LinesVisible ? surface.LinesReading : null;
        if (lines is { } shown)
        {
            accessible += DisplayText.SpokenSeparator(_loc) + shown.Spoken(_clock.Now);
        }

        entry.Window.SetDiscHint(lines?.Content.Detail);

        if (accessible != entry.PendingName)
        {
            entry.PendingName = accessible;
        }

        if (entry.PendingName != surface.AccessibleName && (DateTime.UtcNow - entry.LastNameUpdate).TotalSeconds >= 1)
        {
            surface.AccessibleName = entry.PendingName;
            entry.LastNameUpdate = DateTime.UtcNow;
        }
    }

    private void TickFeeds()
    {
        var now = _speed.Now;
        var single = Settings.Orbs.Orbs.Length == 1;
        foreach (var entry in _entries.Values)
        {
            if (entry.Feed.Tick(now))
            {
                Push(entry, single);
            }
        }

        ScheduleDeadline();
    }

    private void ScheduleDeadline()
    {
        double? next = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.Feed.NextDeadline is { } at && (next is null || at < next))
            {
                next = at;
            }
        }

        _deadline.Stop();
        if (next is { } deadline)
        {
            _deadline.Interval = TimeSpan.FromSeconds(Math.Max(0.02, deadline - _speed.Now + 0.01));
            _deadline.Start();
        }
    }

    // ---- Positions ----

    private static IReadOnlyList<OrbDisplay> Displays() =>
        Monitors.All().Select(m => new OrbDisplay(m.Key, m.WorkRect, m.Scale, m.IsPrimary)).ToArray();

    private void PositionAll(IReadOnlyCollection<string> fresh)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var result = OrbArrangement.Resolve(Settings.Orbs, Displays());
        var stagger = 0;
        foreach (var (id, location) in result.Locations)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Window.IsDragging)
            {
                continue;
            }

            entry.Target = location.Center;
            if (fresh.Contains(id) || !entry.Window.HasCenter || entry.Silent)
            {
                if (entry.FlyFrom is { } from && !_theme.ReduceMotion)
                {
                    entry.Window.MoveTo(from);
                    var delay = TimeSpan.FromMilliseconds(60 * stagger++);
                    var target = location.Center;
                    entry.FlyFrom = null;
                    After(delay, () => entry.Window.AnimateTo(target, 0.45, 0.78, () => LandChip(entry)));
                }
                else
                {
                    entry.Window.MoveTo(location.Center);
                    LandChip(entry);
                }
            }
            else if (Distance(entry.Window.Center, location.Center) > 0.5)
            {
                entry.Window.AnimateTo(location.Center);
            }

            entry.Silent = false;
        }

        if (!ReferenceEquals(result.Document, Settings.Orbs) && !result.Document.Equals(Settings.Orbs))
        {
            Commit(result.Document);
        }
    }

    /// <summary>
    /// A new orb landed: its name plate fades in 0.28 s later and stays, or (without a plate) its chip shows for 2 s.
    /// Decided once the current layout pass has resolved the plates.
    /// </summary>
    private void LandChip(Entry entry)
    {
        if (!entry.ShowChipOnLanding)
        {
            return;
        }

        _ui.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!entry.ShowChipOnLanding || !_entries.ContainsValue(entry))
            {
                return;
            }

            entry.ShowChipOnLanding = false;
            if (_plates.Owns(entry.Definition.Id))
            {
                _plates.Land(entry.Window, entry.Definition);
            }
            else
            {
                ShowChip(entry.Definition.Id, ChipTime);
            }
        });
    }

    /// <summary>A new orb's 2 s chip, unless its name plate takes its place.</summary>
    private void ShowNewChip(string id)
    {
        if (!_plates.Owns(id))
        {
            ShowChip(id, ChipTime);
        }
    }

    /// <summary>
    /// Lays the name plates out for the orbs' resting centers (the targets of running moves). Deferred while an orb is
    /// dragged: its plate keeps its slot and travels with it until the release.
    /// </summary>
    private void ResolvePlates()
    {
        if (_disposed)
        {
            return;
        }

        if (_entries.Values.Any(e => e.Window.IsDragging))
        {
            _platesDirty = true;
            return;
        }

        _platesDirty = false;
        var hadPlateUndo = _plates.ShowsUndo;
        var document = Settings.Orbs;
        var orbs = _entries.Values
            .Where(e => e.Target is not null || e.Window.HasCenter)
            .OrderBy(e => document.IndexOf(e.Definition.Id))
            .Select(e => new PlateOrb(e.Definition, e.Window, e.Target ?? e.Window.Center, e.ShowChipOnLanding))
            .ToArray();
        _plates.Resolve(orbs, Settings, Availability());

        // The anchor's plate went away: the undo stays reachable on the plain chip for the rest of its 8 s.
        if (hadPlateUndo && !_plates.ShowsUndo && PendingUndo is not null)
        {
            ShowUndoChip();
        }
    }

    private SourceAvailability Availability() => SourceAvailability.From(_speed.Snapshot.Sources);

    private PointD DragAssist(string id, PointD raw)
    {
        var result = OrbArrangement.Drag(Settings.Orbs, Displays(), Centers, id, raw);
        ShowGuides(result?.Guides ?? []);
        return result?.Target ?? raw;
    }

    private void OnDragReleased(string id, PointD raw)
    {
        ShowGuides([]);
        if (!_entries.TryGetValue(id, out var entry))
        {
            return;
        }

        var target = OrbArrangement.Release(Settings.Orbs, Displays(), Centers, id, raw);
        var center = target?.Center ?? raw;
        entry.Target = center;
        entry.Window.AnimateTo(center, done: () =>
        {
            entry.Target = entry.Window.Center;
            Commit(OrbArrangement.Commit(Settings.Orbs, Displays(), id, entry.Window.Center));
            ResolvePlates();
            LandChip(entry);
            RefreshChip();
        });
    }

    private void ShowGuides(IReadOnlyList<OrbGuide> guides)
    {
        var accent = AccentColor();
        var scale = Monitors.Primary()?.Scale ?? 1;
        for (var i = 0; i < guides.Count; i++)
        {
            if (i >= _guides.Count)
            {
                _guides.Add(new OrbGuideWindow());
            }

            var at = Monitors.FromPoint(new POINT((int)guides[i].Start.X, (int)guides[i].Start.Y));
            _guides[i].ShowGuide(guides[i], at?.Scale ?? scale, accent);
        }

        for (var i = guides.Count; i < _guides.Count; i++)
        {
            _guides[i].Hide();
        }
    }

    private void FlyFrom(string originId, IReadOnlyList<string> created)
    {
        if (!_entries.TryGetValue(originId, out var origin) || !origin.Window.HasCenter)
        {
            return;
        }

        foreach (var id in created)
        {
            _pendingFlights[id] = origin.Window.Center;
        }
    }

    private readonly Dictionary<string, PointD> _pendingFlights = new(StringComparer.Ordinal);

    private void FlyTo(string keepId, IReadOnlyList<string> others, Action done)
    {
        if (_theme.ReduceMotion || !_entries.TryGetValue(keepId, out var keep) || !keep.Window.HasCenter)
        {
            done();
            return;
        }

        var remaining = 0;
        foreach (var id in others)
        {
            if (!_entries.TryGetValue(id, out var entry) || !entry.Window.IsVisible)
            {
                continue;
            }

            remaining++;
            _plates.Vanish(id);
            entry.Window.AnimateTo(keep.Window.Center, 0.3, 1, () =>
            {
                entry.Window.Vanish(() =>
                {
                    if (--remaining == 0)
                    {
                        done();
                    }
                }, 0.1);
            });
        }

        if (remaining == 0)
        {
            done();
        }
    }

    private void Commit(OrbsDocument document) => _settings.Update(s => s with { Orbs = document });

    private void Prefer(string id)
    {
        _preferred = id;
        foreach (var (key, entry) in _entries)
        {
            entry.Window.Surface.IsPreferred = key == id;
        }

        if (_entries.TryGetValue(id, out var front))
        {
            front.Window.BringToFront();
        }
    }

    // ---- Chips ----

    private void OnHover(string id, bool hovering)
    {
        _plates.FollowHover(id);
        if (hovering)
        {
            Prefer(id);
            _hoverStart = DateTime.UtcNow;
            _hoverOrb = id;
            _chipTimer.Start();
        }
        else if (_hoverOrb == id)
        {
            _hoverOrb = null;
            RefreshChip();
        }
    }

    private DateTime _hoverStart;
    private string? _hoverOrb;

    private void ShowChip(string id, TimeSpan duration)
    {
        _chipOrb = id;
        _chipUntil = DateTime.UtcNow + duration;
        _chipTimer.Start();
        RefreshChip();
    }

    private void RefreshChip()
    {
        if (_platesDirty)
        {
            ResolvePlates();
        }

        string? id = null;
        if (_hoverOrb is { } hover && (DateTime.UtcNow - _hoverStart).TotalSeconds >= 0.35)
        {
            id = hover;
        }
        else if (_chipOrb is { } chip && (DateTime.UtcNow < _chipUntil || _entries.TryGetValue(chip, out var dragged) && dragged.Window.IsDragging))
        {
            id = chip;
        }

        // While a name plate shows, the chip is folded into it instead of drawn as a second capsule.
        _plates.Fold(id);
        if (id is null || _plates.Owns(id) || !_entries.TryGetValue(id, out var entry) || !ChipApplies(entry))
        {
            if (_hoverOrb is null && (_chipOrb is null || DateTime.UtcNow >= _chipUntil))
            {
                _chipTimer.Stop();
            }

            HideChip();
            return;
        }

        var orb = entry.Definition;
        var reading = entry.Feed.Reading;
        var dots = orb.TrackedSources?.ToArray() ?? [];
        var text = ChipText(orb, reading);
        _chip ??= new OrbChipWindow(withButton: false);
        var monitor = Monitors.FromPoint(new POINT((int)entry.Window.Center.X, (int)entry.Window.Center.Y));
        var scale = monitor?.Scale ?? 1;
        _chip.SetContent(dots, UnavailableOf(entry), text, null, _theme.IsDark, AppFonts.XmlLanguageFor(_loc.LanguageCode));
        _chip.PlaceNear(entry.Window.Center, orb.Size / 2 * scale, monitor?.WorkRect ?? new RectD(0, 0, 1920, 1080), scale, OtherDiscs(id, scale));
        _chip.Opacity = 1;
    }

    private bool ChipApplies(Entry entry) => entry.Definition.TrackedSources is not null || Settings.Orbs.Orbs.Length > 1;

    private string ChipText(OrbDefinition orb, SubsetReading reading)
    {
        if (orb.TrackedSources is not { } set)
        {
            return _loc["orbs.sources.all"];
        }

        var name = OrbNaming.DisplayName(orb, _loc);
        if (reading.State != SubsetState.Available)
        {
            var status = _loc[reading.State == SubsetState.AllDisabled ? "source.disabled" : "source.notFound"];
            return $"{name} · {status}";
        }

        return set.Count <= 2 ? name : $"{OrbNaming.SourceName(set.First(), _loc)} +{set.Count - 1}";
    }

    private IReadOnlyList<(PointD Center, double Radius)> OtherDiscs(string id, double scale) =>
        _entries.Where(e => e.Key != id && e.Value.Window.HasCenter)
            .Select(e => (e.Value.Window.Center, e.Value.Definition.Size / 2 * scale))
            .ToArray();

    private void HideChip()
    {
        if (_chip is { IsVisible: true })
        {
            _chip.Hide();
        }
    }

    // ---- Undo ----

    private void SetUndo(OrbUndo? undo)
    {
        PendingUndo = undo;
        UndoChanged?.Invoke(this, EventArgs.Empty);
        if (undo is null)
        {
            _undoTimer.Stop();
            _undoChip?.Hide();
            _plates.HideUndo();
            return;
        }

        _undoTimer.Start();
        _ui.BeginInvoke(DispatcherPriority.Background, ShowUndoChip);
    }

    private void ShowUndoChip()
    {
        if (PendingUndo is not { } undo || !_entries.TryGetValue(undo.AnchorId, out var entry) || !entry.Window.HasCenter)
        {
            return;
        }

        var text = undo.Text;
        if (_plates.ShowUndo(entry.Window, entry.Definition, text, Undo))
        {
            _undoChip?.Hide();
            return;
        }

        if (_undoChip is null)
        {
            _undoChip = new OrbChipWindow(withButton: true);
            _undoChip.ButtonClicked += (_, _) => Undo();
        }

        var monitor = Monitors.FromPoint(new POINT((int)entry.Window.Center.X, (int)entry.Window.Center.Y));
        var scale = monitor?.Scale ?? 1;
        _undoChip.SetContent([], SourceSet.Empty, text, _loc["orbs.undo"], _theme.IsDark, AppFonts.XmlLanguageFor(_loc.LanguageCode));
        _undoChip.PlaceNear(entry.Window.Center, entry.Definition.Size / 2 * scale, monitor?.WorkRect ?? new RectD(0, 0, 1920, 1080), scale,
            OtherDiscs(undo.AnchorId, scale), stackOffset: 30 * scale);
        _undoChip.FadeIn(_theme.ReduceMotion);
    }

    private void CheckUndo()
    {
        if (PendingUndo is { } undo && DateTime.UtcNow >= undo.Expires)
        {
            SetUndo(null);
        }
    }

    private static Color AccentColor()
    {
        try
        {
            return SystemParameters.HighContrast ? SystemColors.HighlightColor : SystemParameters.WindowGlassColor;
        }
        catch (InvalidOperationException)
        {
            return Color.FromRgb(0x3B, 0x82, 0xF6);
        }
    }

    private void After(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }

        var timer = new DispatcherTimer(delay, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            action();
        }, _ui);
        timer.Start();
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class Entry(OrbDefinition definition, IOrbWindow window, OrbFeed feed)
    {
        public OrbDefinition Definition { get; set; } = definition;

        public IOrbWindow Window { get; } = window;

        public OrbFeed Feed { get; } = feed;

        public string PendingName { get; set; } = string.Empty;

        public DateTime LastNameUpdate { get; set; }

        public bool ShowChipOnLanding { get; set; }

        /// <summary>Placed without animation (undo restores exactly).</summary>
        public bool Silent { get; set; }

        public PointD? FlyFrom { get; set; }

        /// <summary>Where the disc rests once its running move ends (physical px): the name plates are laid out for it.</summary>
        public PointD? Target { get; set; }

        public OrbSurfaceConfig? Config { get; set; }

        public OrbWindowOptions Options { get; set; }
    }
}
