using AgentSpeed.Core;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using DispatcherTimer = Avalonia.Threading.DispatcherTimer;

namespace AgentSpeed.Linux.UI;

internal sealed class OrbWindow : Window
{
    private readonly LinuxApp _app;
    private readonly OrbControl _canvas;
    private readonly TextBlock _name = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, FontSize = 11 };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly EventHandler _scalingChanged;
    private bool _restoring;
    private bool _opened;
    private bool _closingFromApplication;
    private string _arrangement = "";
    public OrbDefinition Definition { get; private set; }
    internal OrbAppearance Appearance => _canvas.Appearance;
    internal PointD LiveCenter
    {
        get
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scale = screen?.Scaling ?? 1;
            return new PointD(Position.X + Width * scale / 2, Position.Y + Definition.Size * .75 * scale);
        }
    }
    public OrbWindow(LinuxApp app, OrbDefinition definition)
    {
        _app = app; Definition = definition;
        WindowDecorations = WindowDecorations.None; CanResize = false; ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _canvas = new OrbControl(app, definition);
        var panel = new StackPanel(); panel.Children.Add(_canvas);
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(215, 20, 25, 35)),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 4), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Child = _name });
        Content = panel;
        _canvas.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            { if (e.ClickCount == 2) app.Activate("dashboard"); else BeginMoveDrag(e); }
        };
        var menu = new ContextMenu();
        foreach (var (label, action) in new[] { ("Open panel", "dashboard"), ("Settings", "settings"), ("Restore mouse input", "restore-input") })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => app.Activate(action); menu.Items.Add(item); }
        ContextMenu = menu;
        Opened += (_, _) =>
        {
            // Mutter can overwrite the initial position while mapping the window.
            // Ignore those startup moves and restore after its first configure.
            DispatcherTimer.RunOnce(() =>
            {
                if (!IsVisible) return;
                app.SyncOrbs(repositionExisting: true); _opened = true;
            }, TimeSpan.FromMilliseconds(250));
            RefreshInputShape(); _canvas.Start();
        };
        PositionChanged += (_, _) => { if (!_restoring && _opened) { _save.Stop(); _save.Start(); } };
        _save.Tick += (_, _) => { _save.Stop(); SavePosition(); };
        Closing += (_, _) => { if (!_closingFromApplication && _app.IsOrbWindowRegistered(this)) FlushPosition(); };
        Closed += (_, _) =>
        {
            _save.Stop(); ScalingChanged -= _scalingChanged; _canvas.Dispose(); _app.OrbWindowClosed(this);
        };
        Configure(definition);
        _scalingChanged = (_, _) => RefreshInputShape();
        ScalingChanged += _scalingChanged;
    }
    internal bool Configure(OrbDefinition definition)
    {
        var resize = Definition.Size != definition.Size;
        Definition = definition; Title = definition.Name ?? "Agent Speed"; Topmost = definition.AlwaysOnTop;
        RequestedThemeVariant = _app.RequestedThemeVariant;
        Width = definition.Size * 1.5; Height = definition.Size * 1.5 + 30;
        _name.Text = definition.Name ?? (definition.Sources.IsAll ? "Agent Speed" : string.Join(" + ", definition.TrackedSources ?? default));
        _name.Foreground = Brushes.White;
        if (_name.Parent is Control plate) plate.IsVisible = definition.ShowName ?? (_app.Settings.Current.OrbNames == OrbNamesMode.Always
            || _app.Settings.Current.OrbNames == OrbNamesMode.Auto && (_app.Settings.Current.Orbs.Orbs.Count(o => o.Visible) > 1 || !definition.Sources.IsAll));
        _canvas.Configure(definition);
        if (_opened) RefreshInputShape();
        return resize;
    }
    internal void RefreshInputShape() => X11Windows.SetClickThrough(this, Definition.ClickThrough, Definition.Size,
        _name.Parent is Control { IsVisible: true });
    internal void PlaceAt(OrbLocation location, Screen screen, string arrangement)
    {
        _arrangement = arrangement;
        _restoring = true;
        try { Position = PositionFor(location.Center, screen); }
        finally { _restoring = false; }
    }
    private void SavePosition()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary; if (screen is null) return;
        var scale = screen.Scaling; var work = screen.WorkingArea;
        var radius = Definition.Size * scale / 2;
        var center = new PointD(Position.X + Width * scale / 2, Position.Y + Definition.Size * .75 * scale);
        var record = OrbPositionRecord.FromCenter(LinuxOrbLayout.Key(screen), center, radius, new(work.X, work.Y, work.Width, work.Height), scale);
        var snapped = record.ToCenter(new(work.X, work.Y, work.Width, work.Height), scale, radius);
        _restoring = true;
        try { Position = PositionFor(snapped, screen); }
        finally { _restoring = false; }
        center = new PointD(Position.X + Width * scale / 2, Position.Y + Definition.Size * .75 * scale);
        record = OrbPositionRecord.FromCenter(LinuxOrbLayout.Key(screen), center, radius, new(work.X, work.Y, work.Width, work.Height), scale);
        _app.Settings.Update(s => s with { Orbs = s.Orbs.WithPosition(_arrangement, Definition.Id, record) });
    }
    private PixelPoint PositionFor(PointD center, Screen screen)
    {
        var work = screen.WorkingArea; var scale = screen.Scaling;
        return new PixelPoint((int)Math.Clamp(Math.Round(center.X - Width * scale / 2), work.X, Math.Max(work.X, work.Right - Width * scale)),
            (int)Math.Clamp(Math.Round(center.Y - Definition.Size * .75 * scale), work.Y, Math.Max(work.Y, work.Bottom - Height * scale)));
    }
    public void SavePreview(string path, bool software = false) => _canvas.SavePreview(path, software);
    public void FlushPosition() { _save.Stop(); if (_opened) SavePosition(); }
    internal void CloseFromApplication()
    {
        if (_closingFromApplication) return;
        _closingFromApplication = true;
        Close();
    }
}

internal sealed class OrbControl : Control, IDisposable
{
    internal OrbAppearance Appearance => _orb.Context?.Tokens.Appearance ?? OrbAppearance.Dark;
    private readonly LinuxApp _app;
    private readonly SkiaGlyphs _glyphs = new();
    private readonly SkiaOrbRenderer _renderer;
    private readonly OrbGpuOrb _orb;
    private readonly OrbFeed _feed;
    private readonly object _gate = new();
    private readonly DispatcherTimer _timer = new();
    private OrbDefinition _definition;
    private bool _disposed;
    private bool _software;
    public OrbControl(LinuxApp app, OrbDefinition definition)
    {
        _app = app; _definition = definition; _renderer = new(_glyphs);
        _orb = new(42, _glyphs); _feed = new(definition.Id) { Cost = app.Cost.Display };
        _timer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible) return;
            InvalidateVisual();
            _timer.Interval = _software ? TimeSpan.FromSeconds(_feed.Value <= 0 ? .2 : 1d / 15) : _orb.Demand switch { FrameClass.Fps60 => TimeSpan.FromSeconds(1d / (_app.Settings.Current.Orbs.Count >= FramePolicy.CrowdedCount && !IsPointerOver ? 30 : 60)),
                FrameClass.Fps30 => TimeSpan.FromSeconds(1d / 30), _ => TimeSpan.FromSeconds(1d / 15) };
        };
        PointerEntered += (_, _) => { lock (_gate) _orb.SetHover(app.Clock.Elapsed.TotalSeconds, true); };
        PointerExited += (_, _) => { lock (_gate) _orb.SetHover(app.Clock.Elapsed.TotalSeconds, false); };
        app.Speed.EventRaised += OnSpeedEvent;
        app.Changed += Wake;
        Configure(definition);
    }
    public void Configure(OrbDefinition definition)
    {
        _definition = definition; Width = Height = definition.Size * 1.5;
        var settings = _app.Settings.Current;
        lock (_gate)
        {
            _feed.Configure(definition.TrackedSources, settings.Metric, definition.EffectiveRedline(settings.Metric, settings),
                (long)(_app.Clock.Elapsed.TotalSeconds * 1000));
            _feed.Unit = definition.Unit; _feed.Previewing = _app.Preview;
            var accessibility = _app.Accessibility.Current;
            var effects = new EffectsPolicy(settings.Effects, accessibility.ReduceMotion, accessibility.HighContrast);
            var strings = new OrbReadoutStrings(_app.Loc["unit.tokS"], _app.Loc["peak.new"],
                settings.TitleThemes.OrbTheme(definition.TitleTheme).Labels(_app.Loc), _app.Loc.UsesLatinScript);
            // Auto is resolved from GNOME's actual preference by LinuxApp.
            var tokens = _app.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light ? OrbTokens.Light : OrbTokens.Dark;
            _orb.Configure(new(definition.Style, definition.Size, effects, settings.ResponsePulses, settings.CelebratePeaks, _feed.Redline, strings,
                definition.TrackedSources?.ToArray(), default), new(definition.Size, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1,
                tokens, effects, accessibility.HighContrast ? OrbContrastColors.Default : null, bodyOpacity: definition.Opacity));
        }
        InvalidateVisual();
    }
    public void Start() { _timer.Interval = TimeSpan.FromSeconds(1d / 15); _timer.Start(); }
    private void Wake() { if (!_disposed) { InvalidateVisual(); _timer.Start(); } }
    private void OnSpeedEvent(object? sender, SpeedEvent e)
    {
        lock (_gate)
        {
            if (!_feed.Accepts(e)) return;
            var now = _app.Clock.Elapsed.TotalSeconds;
            if (e is SpeedEvent.ResponseCompleted completed) _orb.Pulse(now, completed.Response.Speed ?? 0, completed.Response.OutputTokens);
            if (e is SpeedEvent.NewPeak) _orb.Peak(now);
        }
    }
    private void BuildFrame()
    {
        var now = _app.Clock.Elapsed.TotalSeconds;
        if (_feed.Update(_app.Speed.Snapshot, now)) _orb.Peak(now);
        _feed.Tick(now);
        _orb.SetData(now, _feed.Value, _feed.Tier, _feed.Title,
            _feed.Reading.Agents.Where(a => a.Phase == AgentPhase.Generating).Select(a => a.Source).Distinct().ToArray(),
            $"{_feed.Reading.Agents.Count} agents", _feed.IsUnavailable ? "No sources" : null);
        _orb.Scene.SetMoney(_feed.Money);
        var rate = _definition.Traffic == OrbTraffic.Machine ? _app.Traffic.Hub.Snapshot.MachineLive
            : _app.Traffic.Hub.Snapshot.Live(_definition.TrackedSources, _app.Settings.Current.Traffic.IncludeTools);
        _orb.SetTraffic(now, rate, _app.Settings.Current.Traffic.Bits, _definition.Traffic != OrbTraffic.Off);
        _glyphs.BeginFrame();
        _orb.Render(now, opacity: _definition.FadeWhenIdle && _feed.Value <= 0 ? .45 : 1);
        if (_orb.Demand == FrameClass.None && _orb.NextWake is null) _timer.Stop();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        lock (_gate) BuildFrame();
        context.Custom(new DrawOperation(this, new Rect(Bounds.Size)));
    }
    public void SavePreview(string path, bool software = false)
    {
        lock (_gate)
        {
            BuildFrame();
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo((int)Width * 2, (int)Height * 2));
            surface.Canvas.Clear(SkiaSharp.SKColors.Transparent); surface.Canvas.Scale(2); _renderer.Draw(surface.Canvas, _orb.List, software);
            using var image = surface.Snapshot(); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var output = File.Create(path); data.SaveTo(output);
        }
    }
    private sealed class DrawOperation(OrbControl owner, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>(); if (feature is null) return;
            using var lease = feature.Lease();
            lock (owner._gate) if (!owner._disposed)
            { owner._software = lease.GrContext is null; owner._renderer.Draw(lease.SkCanvas, owner._orb.List, owner._software); }
        }
    }
    public void Dispose()
    {
        _timer.Stop(); _app.Speed.EventRaised -= OnSpeedEvent; _app.Changed -= Wake;
        lock (_gate) { _disposed = true; _renderer.Dispose(); _glyphs.Dispose(); }
    }
}
