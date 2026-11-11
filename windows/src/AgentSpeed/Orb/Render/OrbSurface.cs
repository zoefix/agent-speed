using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Render;

/// <summary>
/// One floating orb's instrument (DESIGN 2, 2a): the shared body, aura, readout, wave, rays and pill plus the style's
/// renderer, all retained and cached. Frames come from the shared <see cref="OrbClock"/> at the frame class the scene
/// and the style ask for; nothing is drawn while the orb is static, hidden or paused.
/// </summary>
internal sealed class OrbSurface : FrameworkElement, IOrbFrameClient, IOrbSurface
{
    private const double CrossfadeSeconds = 0.25;

    private readonly OrbClock _clock;
    private readonly ContainerVisual _root = new();
    private readonly ScaleTransform _hoverScale = new(1, 1);
    private readonly DrawingVisual _aura = new();
    private readonly DrawingVisual _body = new();
    private readonly ContainerVisual _instrument = new();
    private readonly ContainerVisual _readoutHost = new();
    private readonly DrawingVisual _overlay = new();
    private readonly ContainerVisual _instrumentOverlay = new();
    private readonly DrawingVisual _highlight = new();
    private readonly TextDrawings _text = new();
    private readonly BrushCache _brushes = new();
    private readonly ReadoutLayer _readout;
    private readonly ReadoutLayer _readoutBelow;
    private readonly uint _seed;
    private OrbSurfaceConfig? _config;
    private OrbRenderContext? _context;
    private IOrbStyleRenderer? _renderer;
    private IOrbStyleRenderer? _outgoing;
    private double _crossfadeStart = double.NegativeInfinity;
    private double _lastRender = double.NaN;
    private double _lastOutgoing = double.NaN;
    private bool _snapNext = true;
    private bool _visibleActive;
    private bool _paused;
    private bool _locked;
    private int _auraKey = -1;
    private bool _overlayDrawn;
    private double _lastScale = double.NaN;
    private CriticalSpring _highlightAmount = new(0.15);
    private bool _highlighted;
    private bool _highlightDrawn;
    private FrameClass _demand = FrameClass.Fps60;
    private bool _unavailable;
    private bool _hovering;
    private ReadoutPalette? _belowPalette;
    private double _lastValue = double.NaN;
    private SpeedTier _lastTier = (SpeedTier)(-1);
    private SpeedTitle _lastTitle = (SpeedTitle)(-1);
    private string? _lastUnavailable;
    private SourceKind[] _lastAgents = [];
    private string _accessibleName = string.Empty;

    public OrbSurface(OrbClock clock, uint seed)
    {
        _clock = clock;
        _seed = seed;
        Scene = new OrbScene((int)(seed & 0x7FFF_FFFF)) { SharedSignOfLife = true };
        _readout = new ReadoutLayer(_text);
        _readoutBelow = new ReadoutLayer(_text);
        _aura.CacheMode = new BitmapCache(1);
        _body.CacheMode = new BitmapCache(1);
        _highlight.CacheMode = new BitmapCache(1);
        _root.Children.Add(_aura);
        _root.Children.Add(_body);
        _root.Children.Add(_instrument);
        _readoutHost.Children.Add(_readout.Root);
        _readoutHost.Children.Add(_readoutBelow.Root);
        _root.Children.Add(_readoutHost);
        _root.Children.Add(_instrumentOverlay);
        _root.Children.Add(_overlay);
        _root.Transform = _hoverScale;
        AddVisualChild(_root);
        AddVisualChild(_highlight);
        Focusable = false;
        SnapsToDevicePixels = false;
        IsVisibleChanged += (_, _) => UpdateActive();
        Loaded += (_, _) => UpdateActive();
        Unloaded += (_, _) => _clock.Unregister(this);
    }

    public OrbScene Scene { get; }

    public double Diameter => _config?.Size ?? 148;

    public OrbStyle InstrumentStyle => _config?.Style ?? OrbStyle.Led;

    /// <summary>Spoken text (updated at most once per second by the owner).</summary>
    public string AccessibleName
    {
        get => _accessibleName;
        set
        {
            if (_accessibleName == value)
            {
                return;
            }

            var old = _accessibleName;
            _accessibleName = value;
            if (UIElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, value);
            }
        }
    }

    /// <summary>Settings → 懸浮窗 rings this orb while its row is hovered, selected or focused (DESIGN 2b).</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted != value)
            {
                _highlighted = value;
                _clock.Kick(this);
            }
        }
    }

    /// <summary>Pauses rendering (cloaked, occluded, faded out); the first frame after resuming shows the current state.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused != value)
            {
                _paused = value;
                UpdateActive();
            }
        }
    }

    /// <summary>The session is locked or the console disconnected: no frames until it comes back.</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked != value)
            {
                _locked = value;
                _snapNext = true;
                if (!value)
                {
                    _clock.Kick(this);
                }
            }
        }
    }

    public bool IsActive => _visibleActive && !_paused && !_locked && _context is not null;

    public bool IsPreferred { get; set; }

    public FrameClass Demand => _demand;

    public FrameClass Cap { get; set; } = FrameClass.Fps60;

    public double? NextWake => Scene.NextWake;

    public void Configure(OrbSurfaceConfig config)
    {
        var previous = _config;
        _config = config;
        Scene.Effects = config.Effects;
        Scene.Redline = config.Redline;
        Scene.PulsesEnabled = config.PulsesEnabled;
        Scene.PeaksEnabled = config.PeaksEnabled;
        Scene.LedEffects = config.Style == OrbStyle.Led;
        Scene.SignOfLifeDuration = config.Style switch
        {
            OrbStyle.Minimal => 0,
            OrbStyle.Liquid => 1.2,
            _ => 0.9,
        };

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _context = new OrbRenderContext(config.Size, scale, OrbTokens.For(config.Appearance), config.Effects, config.HighContrast,
            config.SolidBody, config.BodyOpacity, _brushes);
        _text.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_renderer is null || _renderer.Style != config.Style)
        {
            var incoming = OrbStyleRenderers.Create(config.Style, Scene, _seed);
            if (_renderer is not null && previous is not null && config.Effects.SlideTransitions)
            {
                RemoveOutgoing();
                _outgoing = _renderer;
                _lastOutgoing = _lastRender;
                _crossfadeStart = Scene.Time;
            }
            else if (_renderer is not null)
            {
                Detach(_renderer);
            }

            _renderer = incoming;
            _instrument.Children.Add(incoming.Root);
            if (incoming.Overlay is { } overlay)
            {
                _instrumentOverlay.Children.Add(overlay);
            }

            _snapNext = true;
        }

        _renderer.Configure(_context);
        _outgoing?.Configure(_context);
        DrawBody();
        var palette = ReadoutPalette.For(_context.Tokens, config.HighContrast);
        _readout.Configure(_context, _renderer.Readout, palette, config.Texts);
        _readoutBelow.Configure(_context, _renderer.Readout, _renderer.BelowPalette ?? palette, config.Texts);
        _belowPalette = _renderer.BelowPalette;
        _auraKey = -1;
        _overlayDrawn = true;
        _highlightDrawn = false;
        _lastScale = double.NaN;
        InvalidateMeasure();
        UpdateActive();
        _clock.Kick(this);
    }

    /// <summary>Updates the tracked sources' bezel without rebuilding anything else.</summary>
    public void SetIdentity(IReadOnlyList<SourceKind>? sources, SourceSet unavailable)
    {
        if (_config is null)
        {
            return;
        }

        if (!SameSources(_config.Sources, sources) || _config.Unavailable != unavailable)
        {
            _config = _config with { Sources = sources, Unavailable = unavailable };
            DrawBody();
            _clock.Kick(this);
        }
    }

    /// <param name="unavailableTitle">Every source is unavailable: the value reads an en dash and this title.</param>
    public void SetData(double value, SpeedTier tier, SpeedTitle title, IReadOnlyList<SourceKind> generating, string infoLine,
        string? unavailableTitle)
    {
        var target = double.IsFinite(value) && value > 0 ? value : 0;
        if (target == _lastValue && tier == _lastTier && title == _lastTitle && infoLine == _readout.InfoLine
            && unavailableTitle == _lastUnavailable && generating.SequenceEqual(_lastAgents))
        {
            return;
        }

        _lastValue = target;
        _lastTier = tier;
        _lastTitle = title;
        _lastUnavailable = unavailableTitle;
        _lastAgents = generating.ToArray();
        var now = Scene.Time;
        Scene.Update(Math.Max(now, Now()));
        Scene.SetTarget(unavailableTitle is null ? value : 0, unavailableTitle is null ? tier : SpeedTier.Idle,
            unavailableTitle is null ? title : SpeedTitle.Idle);
        Scene.SetAgents(unavailableTitle is null ? generating : []);
        var unavailable = unavailableTitle is not null;
        _readout.InfoLine = unavailable ? string.Empty : infoLine;
        _readoutBelow.InfoLine = _readout.InfoLine;
        var marker = unavailable ? ("–", unavailableTitle!) : ((string, string)?)null;
        _readout.Unavailable = marker;
        _readoutBelow.Unavailable = marker;
        _unavailable = unavailable;
        _clock.Kick(this);
    }

    /// <summary>The WPF fallback renderer draws no two-line readout.</summary>
    public void SetLines(Presentation.Orb.Lines.OrbLinesSample? sample, bool enabled)
    {
    }

    public bool LinesVisible => false;

    public void SetCharacter(Presentation.Orb.OrbCharacterContent? content)
    {
    }

    public void SetCharacterReadings(Presentation.Orb.Lines.OrbLinesSample? performance, Presentation.Cost.LiveMoney? money, bool generating,
        Presentation.Orb.OrbLatency? latency, IReadOnlyList<Presentation.Orb.OrbModelRow> models, Presentation.Cost.CostToday? costToday)
    {
    }

    public void SetCharacterNetwork(Presentation.Orb.Lines.OrbLinesSample? network)
    {
    }

    public string? CharacterSentence(Presentation.Localization.Localizer localizer, string currencyName) => null;

    public Presentation.Orb.Lines.OrbLinesReading? LinesReading => null;

    public void SetHover(bool hovering)
    {
        if (_hovering == hovering)
        {
            return;
        }

        _hovering = hovering;
        Scene.Update(Now());
        Scene.SetHover(hovering);
        _clock.Kick(this);
    }

    public void Pulse(double speed, long outputTokens)
    {
        Scene.Update(Now());
        if (Scene.PulsesEnabled && Scene.Effects.Pulses)
        {
            var strength = Math.Clamp(Math.Log10(Math.Max(outputTokens, 1)) / 3, 0.3, 1.25);
            _renderer?.Model.Pulse(HeatMath.Heat(speed, Scene.Redline), strength);
        }

        Scene.TriggerPulse(speed, outputTokens);
        _clock.Kick(this);
    }

    public void Peak()
    {
        Scene.Update(Now());
        if (Scene.PeaksEnabled)
        {
            _renderer?.Model.Peak();
        }

        Scene.TriggerPeak();
        _clock.Kick(this);
    }

    public void Render(double now)
    {
        if (_context is not { } ctx || _renderer is not { } renderer || _config is not { } config)
        {
            return;
        }

        var dt = double.IsNaN(_lastRender) ? 0 : Math.Clamp(now - _lastRender, 0, 0.1);
        _lastRender = now;
        _text.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_snapNext)
        {
            Scene.Snap(now);
        }
        else
        {
            Scene.Update(now);
        }

        var frame = Frame(now, dt, ctx, config);
        if (_snapNext)
        {
            renderer.Model.Snap(frame);
            _snapNext = false;
        }
        else
        {
            renderer.Model.Update(frame);
        }

        renderer.Render(Scene, frame);

        RenderOutgoing(now, ctx, config);
        RenderAura(ctx, renderer);

        RenderOverlay(ctx, renderer);
        RenderReadout(now, ctx, renderer);
        RenderHover(ctx);
        RenderHighlight(dt, ctx);

        var demand = FramePolicy.Max(Scene.SharedFrameClass(config.Style == OrbStyle.Minimal), renderer.Model.Demand);
        if (_outgoing is not null || !_highlightAmount.IsSettled(_highlighted ? 1 : 0, 0.01))
        {
            demand = FramePolicy.Max(demand, FrameClass.Fps60);
        }

        _demand = demand;
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _root,
        1 => _highlight,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size availableSize) => new(Diameter * 1.5, Diameter * 1.5);

    protected override Size ArrangeOverride(Size finalSize) => new(Diameter * 1.5, Diameter * 1.5);

    protected override AutomationPeer OnCreateAutomationPeer() => new OrbSurfacePeer(this);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_config is { } config)
        {
            Configure(config);
        }
    }

    private OrbFrame Frame(double now, double dt, OrbRenderContext ctx, OrbSurfaceConfig config) => new(
        now,
        dt,
        Scene.Heat,
        Scene.Overdrive,
        Scene.HeatRaw,
        Scene.OverdriveRaw,
        Scene.Tier,
        config.Size,
        config.Effects,
        ctx.Dark,
        Scene.Flash,
        _unavailable ? null : Scene.SignOfLifeAge,
        _unavailable);

    private void RenderOutgoing(double now, OrbRenderContext ctx, OrbSurfaceConfig config)
    {
        if (_outgoing is not { } outgoing || _renderer is not { } incoming)
        {
            return;
        }

        var progress = Math.Clamp((Scene.Time - _crossfadeStart) / CrossfadeSeconds, 0, 1);
        if (progress >= 1)
        {
            RemoveOutgoing();
            incoming.Root.Opacity = 1;
            return;
        }

        var dt = double.IsNaN(_lastOutgoing) ? 0 : Math.Clamp(now - _lastOutgoing, 0, 0.1);
        _lastOutgoing = now;
        var frame = Frame(now, dt, ctx, config);
        outgoing.Model.Update(frame);
        outgoing.Render(Scene, frame);
        outgoing.Root.Opacity = 1 - progress;
        incoming.Root.Opacity = progress;
    }

    private void RemoveOutgoing()
    {
        if (_outgoing is { } outgoing)
        {
            Detach(outgoing);
            _outgoing = null;
        }
    }

    private void Detach(IOrbStyleRenderer renderer)
    {
        _instrument.Children.Remove(renderer.Root);
        if (renderer.Overlay is { } overlay)
        {
            _instrumentOverlay.Children.Remove(overlay);
        }
    }

    private void DrawBody()
    {
        if (_context is not { } ctx || _renderer is not { } renderer || _config is not { } config)
        {
            return;
        }

        using var dc = _body.RenderOpen();
        OrbChrome.DrawBody(dc, ctx, renderer.Body, config.Sources, config.Unavailable);
    }

    private void RenderAura(OrbRenderContext ctx, IOrbStyleRenderer renderer)
    {
        var intensity = renderer.SharedEffects && !ctx.HighContrast ? Scene.AuraIntensity * ctx.Tokens.AuraOpacityScale : 0;
        if (intensity <= 0.004)
        {
            SetOpacity(_aura, 0);
            return;
        }

        var key = HeatPalette.Bucket(Scene.Heat, 48);
        if (key != _auraKey)
        {
            _auraKey = key;
            using var dc = _aura.RenderOpen();
            var shapes = new OrbBrushesLite(ctx.Size);
            dc.DrawEllipse(shapes.Aura(Scene.Heat), null, ctx.Center, ctx.Size * 0.75, ctx.Size * 0.75);
        }

        SetOpacity(_aura, Math.Clamp(intensity, 0, 1));
    }

    private void RenderOverlay(OrbRenderContext ctx, IOrbStyleRenderer renderer)
    {
        var active = renderer.SharedEffects && !ctx.HighContrast && (Scene.PulseCount > 0 || Scene.PeakRays is not null);
        if (!active)
        {
            if (_overlayDrawn)
            {
                _overlayDrawn = false;
                using var clear = _overlay.RenderOpen();
            }

            return;
        }

        _overlayDrawn = true;
        using var dc = _overlay.RenderOpen();
        var s = ctx.Size;
        var c = ctx.Center;
        var dark = ctx.Dark;
        foreach (var wave in Scene.Pulses)
        {
            var eased = Easing.OutCubic(wave.Progress);
            var radius = Scene.Effects.PulseExpands ? (0.5 + 0.24 * eased) * s : 0.51 * s;
            var strength = Math.Clamp(wave.Strength, 0.3, 1.25);
            var thickness = Math.Max(0.3, 2 * s / 148 * (1 - wave.Progress) * (0.6 + 0.6 * strength));
            var alpha = 0.6 * (1 - wave.Progress) * (0.5 + 0.5 * Math.Min(1, strength));
            var color = dark ? HeatPalette.Sample(Math.Max(0.15, wave.Heat)) : ctx.Tokens.TierLabel(Math.Max(0.15, wave.Heat));
            dc.DrawEllipse(null, MediaColors.FrozenPen(_brushes.Get(color, alpha), thickness), c, radius, radius);
        }

        if (Scene.PeakRays is { } t && Scene.Effects.SlideTransitions)
        {
            var reach = Easing.OutCubic(t);
            var gold = dark ? HeatPalette.Gold.WithAlpha(0.9 * (1 - t)) : Rgba.FromHex("#D69E00").WithAlpha(0.8 * (1 - t));
            var pen = MediaColors.FrozenPen(_brushes.Get(gold), Math.Max(0.6, 1.6 * s / 148 * (1 - t)), PenLineCap.Round);
            for (var i = 0; i < 24; i++)
            {
                var angle = i * 15 + 7.45;
                dc.DrawLine(pen, OrbBrushes.Polar(c, (0.5 + 0.08 * reach) * s, angle), OrbBrushes.Polar(c, (0.54 + 0.2 * reach) * s, angle));
            }
        }
    }

    private void RenderReadout(double now, OrbRenderContext ctx, IOrbStyleRenderer renderer)
    {
        if (renderer.ReadoutSplit is { } split && renderer.BelowPalette is { } below)
        {
            if (!Equals(below, _belowPalette) && _config is { } config)
            {
                _belowPalette = below;
                _readoutBelow.Configure(ctx, renderer.Readout, below, config.Texts);
            }

            var y = ctx.Center.Y + split * ctx.Size;
            var bounds = ctx.Bounds;
            _readout.SetClip(new Rect(bounds.X, bounds.Y, bounds.Width, Math.Max(0, y - bounds.Y)));
            _readoutBelow.SetClip(new Rect(bounds.X, y, bounds.Width, Math.Max(0, bounds.Bottom - y)));
            _readout.Render(Scene, now);
            _readoutBelow.Render(Scene, now);
            _readoutBelow.Root.Opacity = 1;
        }
        else
        {
            _readout.SetClip(null);
            _readout.Render(Scene, now);
            if (_readoutBelow.Root.Opacity != 0)
            {
                _readoutBelow.Root.Opacity = 0;
            }
        }
    }

    private void RenderHover(OrbRenderContext ctx)
    {
        var scale = Math.Round((1 + 0.03 * Scene.Hover) * 2000) / 2000;
        if (scale == _lastScale)
        {
            return;
        }

        _lastScale = scale;
        _hoverScale.CenterX = ctx.Center.X;
        _hoverScale.CenterY = ctx.Center.Y;
        _hoverScale.ScaleX = scale;
        _hoverScale.ScaleY = scale;
    }

    private void RenderHighlight(double dt, OrbRenderContext ctx)
    {
        _highlightAmount.Step(_highlighted ? 1 : 0, dt > 0 ? dt : 1 / 60.0);
        if (!_highlightDrawn)
        {
            _highlightDrawn = true;
            using var dc = _highlight.RenderOpen();
            var accent = SystemParameters.HighContrast ? SystemColors.HighlightColor.ToRgba() : AccentColor();
            var radius = ctx.Size / 2 + 4 + 1;
            dc.DrawEllipse(null, MediaColors.FrozenPen(_brushes.Get(accent, 0.6), 2), ctx.Center, radius, radius);
        }

        SetOpacity(_highlight, Math.Clamp(_highlightAmount.Value, 0, 1));
    }

    private static Rgba AccentColor()
    {
        try
        {
            return SystemParameters.WindowGlassColor.ToRgba().WithAlpha(1);
        }
        catch (InvalidOperationException)
        {
            return Rgba.FromHex("#3B82F6");
        }
    }

    private static void SetOpacity(ContainerVisual visual, double opacity)
    {
        if (Math.Abs(visual.Opacity - opacity) > 0.002)
        {
            visual.Opacity = opacity;
        }
    }

    private static bool SameSources(IReadOnlyList<SourceKind>? a, IReadOnlyList<SourceKind>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    private void UpdateActive()
    {
        var active = IsVisible && IsLoaded;
        if (active == _visibleActive)
        {
            if (active)
            {
                _clock.Kick(this);
            }

            return;
        }

        _visibleActive = active;
        if (active)
        {
            _snapNext = true;
            _clock.Register(this);
        }
        else
        {
            _clock.Unregister(this);
        }
    }

    private double Now() => double.IsNaN(_lastRender) ? Scene.Time : Math.Max(_lastRender, Scene.Time);

    private sealed class OrbSurfacePeer(OrbSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => ((OrbSurface)Owner).AccessibleName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => "AgentSpeedOrb";
    }

    /// <summary>The aura brushes of <see cref="OrbBrushes"/> without its segment geometry.</summary>
    private sealed class OrbBrushesLite(double diameter)
    {
        private static readonly Dictionary<(int, int), RadialGradientBrush> Cache = new();

        public RadialGradientBrush Aura(double heat)
        {
            var bucket = HeatPalette.Bucket(heat, 48);
            if (Cache.TryGetValue((bucket, 0), out var brush))
            {
                return brush;
            }

            brush = new OrbBrushes(diameter).Aura(heat);
            Cache[(bucket, 0)] = brush;
            return brush;
        }
    }
}
