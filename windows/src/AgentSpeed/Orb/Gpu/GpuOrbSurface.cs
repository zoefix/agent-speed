using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Gpu;

/// <summary>Motion of the whole orb that its window adds on top of the instrument (appear, vanish, the idle fade).</summary>
/// <param name="Scale">Extra scale about the disc center.</param>
/// <param name="Opacity">Opacity of everything.</param>
/// <param name="Demand">Frames the motion needs.</param>
internal readonly record struct OrbWindowMotion(double Scale, double Opacity, FrameClass Demand);

/// <summary>
/// One floating orb's instrument on the GPU (DESIGN 2, 2a): the platform-neutral <see cref="OrbGpuOrb"/> builds each frame's
/// draw list on the shared clock, and this surface encodes it with Direct3D 11 into the window's composition swap chain —
/// only when the frame differs from the one on screen.
/// </summary>
internal sealed class GpuOrbSurface : IOrbSurface, IOrbFrameClient, IDisposable
{
    private readonly GpuDevice _device;
    private readonly OrbClock _clock;
    private readonly OrbAutomation _automation;
    private readonly OrbGpuOrb _orb;
    private CompositionSurface? _surface;
    private OrbSurfaceConfig? _config;
    private ReadoutTexts? _texts;
    private OrbReadoutStrings? _strings;
    private double _scale = 1;
    private int _pixels = 1;
    private bool _visible;
    private bool _paused;
    private bool _locked;
    private bool _forcePresent = true;
    private ulong _presented;
    private double _unitHoverUntil = double.NegativeInfinity;
    private double? _glyphRetry;
    private int _presentedGeneration = -1;
    private FrameClass _demand = FrameClass.Fps60;
    private string _accessibleName = string.Empty;

    public GpuOrbSurface(GpuDevice device, OrbClock clock, uint seed, OrbAutomation automation)
    {
        _device = device;
        _clock = clock;
        _automation = automation;
        _orb = new OrbGpuOrb(seed, device.Atlas.ForScale(1));
        device.Recovered += OnRecovered;
        CharacterGpu.AssetReady += OnCharacterAsset;
    }

    /// <summary>A character atlas finished decoding: draw a frame so it shows (the frame's fingerprint may not change).</summary>
    private void OnCharacterAsset(object? sender, EventArgs e)
    {
        if (InstrumentStyle != OrbStyle.Character)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _forcePresent = true;
            _clock.Kick(this);
        });
    }

    private void OnRecovered(object? sender, EventArgs e)
    {
        _forcePresent = true;
        _clock.Kick(this);
    }

    /// <summary>Raised when the GPU can no longer be used (device lost and not recoverable): the owner falls back to WPF.</summary>
    public static event EventHandler? RendererFailed;

    /// <summary>Frames presented by every GPU orb (benchmarks).</summary>
    public static long FramesPresented { get; private set; }

    /// <summary>Frames the compositor was not ready for (retried on the next tick).</summary>
    public static long FramesBusy { get; private set; }

    /// <summary>Frames built but not presented because nothing changed (benchmarks).</summary>
    public static long FramesSkipped { get; private set; }

    /// <summary>The window's motion (appear, vanish, fade), sampled each frame.</summary>
    public Func<double, OrbWindowMotion>? Motion { get; set; }

    /// <summary>The window handle (the swap chain's composition target).</summary>
    public nint Hwnd { get; set; }

    public OrbScene Scene => _orb.Scene;

    public double Diameter => _config?.Size ?? 148;

    public OrbStyle InstrumentStyle => _config?.Style ?? OrbStyle.Led;

    public string AccessibleName
    {
        get => _accessibleName;
        set
        {
            _accessibleName = value;
            _automation.Name = value;
        }
    }

    public bool Highlighted
    {
        get => _orb.Highlighted;
        set
        {
            if (_orb.Highlighted != value)
            {
                _orb.Highlighted = value;
                _clock.Kick(this);
            }
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused != value)
            {
                _paused = value;
                Resumed(!value);
            }
        }
    }

    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked != value)
            {
                _locked = value;
                Resumed(!value);
            }
        }
    }

    /// <summary>The window is shown (frames only while it is).</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            if (value)
            {
                _orb.SnapNext();
                _forcePresent = true;
                _clock.Register(this);
            }
            else
            {
                _clock.Unregister(this);
            }
        }
    }

    public bool IsActive => _visible && !_paused && !_locked && !CompositorTicker.DisplayOff && _orb.IsConfigured && Hwnd != 0;

    public bool IsPreferred { get; set; }

    public FrameClass Demand => _demand;

    public FrameClass Cap { get; set; } = FrameClass.Fps60;

    /// <summary>The orb's next scheduled change, or sooner the retry of a string its last frame could not draw.</summary>
    public double? NextWake => _glyphRetry is { } retry && (_orb.NextWake is not { } wake || retry < wake) ? retry : _orb.NextWake;

    /// <summary>Window side in physical pixels and the display scale (dpi / 96) it is drawn at.</summary>
    public void SetPixels(int pixels, double scale)
    {
        pixels = Math.Max(1, pixels);
        var scaleChanged = Math.Abs(scale - _scale) > 0.001;
        if (pixels == _pixels && !scaleChanged)
        {
            return;
        }

        _pixels = pixels;
        _scale = scale;
        _forcePresent = true;
        if (scaleChanged && _config is { } config)
        {
            Configure(config);
        }
        else
        {
            _clock.Kick(this);
        }
    }

    public void Configure(OrbSurfaceConfig config)
    {
        _config = config;
        if (!ReferenceEquals(config.Texts, _texts) || _strings is null)
        {
            _texts = config.Texts;
            _strings = new OrbReadoutStrings(config.Texts.Unit, config.Texts.Peak, config.Texts.Titles, config.Texts.Latin);
        }

        var contrast = config.HighContrast
            ? new OrbContrastColors(SystemColors.WindowTextColor.ToRgba(), SystemColors.WindowColor.ToRgba(), SystemColors.HighlightColor.ToRgba(),
                SystemColors.GrayTextColor.ToRgba())
            : (OrbContrastColors?)null;
        var context = new OrbGpuContext(config.Size, _scale, OrbTokens.For(config.Appearance), config.Effects, contrast, config.SolidBody,
            config.BodyOpacity);
        var gpu = new OrbGpuConfig(config.Style, config.Size, config.Effects, config.PulsesEnabled, config.PeaksEnabled, config.Redline, _strings,
            config.Sources, config.Unavailable);
        _orb.Readout.Glyphs = _device.Atlas.ForScale(_scale);
        _orb.Configure(gpu, context);
        _orb.Accent = config.HighContrast ? SystemColors.HighlightColor.ToRgba() : Accent();
        _forcePresent = true;
        _clock.Kick(this);
    }

    public void SetIdentity(IReadOnlyList<SourceKind>? sources, SourceSet unavailable)
    {
        if (_orb.SetIdentity(sources, unavailable))
        {
            _config = _config is null ? null : _config with { Sources = sources, Unavailable = unavailable };
            _clock.Kick(this);
        }
    }

    public void SetData(double value, SpeedTier tier, SpeedTitle title, IReadOnlyList<SourceKind> generating, string infoLine, string? unavailableTitle)
    {
        if (_orb.SetData(Now(), value, tier, title, generating, infoLine, unavailableTitle))
        {
            _clock.Kick(this);
        }
    }

    public void SetHover(bool hovering)
    {
        _orb.SetHover(Now(), hovering);
        _clock.Kick(this);
    }

    public void SetLines(Presentation.Orb.Lines.OrbLinesSample? sample, bool enabled)
    {
        if (_orb.SetLines(Now(), sample, enabled))
        {
            _clock.Kick(this);
        }
    }

    public bool LinesVisible => _orb.LinesVisible(Now());

    public void SetCharacter(Presentation.Orb.OrbCharacterContent? content)
    {
        if (_orb.SetCharacter(content))
        {
            _forcePresent = true;
            _clock.Kick(this);
        }
    }

    public void SetCharacterReadings(Presentation.Orb.Lines.OrbLinesSample? performance, Presentation.Cost.LiveMoney? money, bool generating)
    {
        if (_orb.SetCharacterReadings(Now(), performance, money, generating))
        {
            _clock.Kick(this);
        }
    }

    public void SetCharacterNetwork(Presentation.Orb.Lines.OrbLinesSample? network)
    {
        if (_orb.SetCharacterNetwork(Now(), network))
        {
            _clock.Kick(this);
        }
    }

    public string? CharacterSentence(Presentation.Localization.Localizer localizer, string currencyName) =>
        InstrumentStyle == OrbStyle.Character && _orb.Character is { } content
            ? OrbCharacterSpeech.Sentence(_orb.CharacterReadings, content.Readouts, Now(), localizer, currencyName)
            : null;

    public Presentation.Orb.Lines.OrbLinesReading? LinesReading => _orb.LinesReading;

    public void Pulse(double speed, long outputTokens)
    {
        _orb.Pulse(Now(), speed, outputTokens);
        _clock.Kick(this);
    }

    public void Peak()
    {
        _orb.Peak(Now());
        _clock.Kick(this);
    }

    /// <summary>Asks for a frame now (window motion started).</summary>
    public void Kick() => _clock.Kick(this);

    /// <summary>
    /// Whether a canvas point (DIP from the window's top-left) is on the unit's hit area (DESIGN 9 Switching): the unit text,
    /// or the value without a unit line (<c>minimal</c>, orbs below 96 DIP), grown to at least 44 × 20 and clipped to the disc.
    /// </summary>
    public bool IsOnUnit(double x, double y)
    {
        if (_orb.Style is not { } style || _orb.Context is not { } ctx)
        {
            return false;
        }

        var layout = style.Readout.At(ctx.Size);
        var width = layout.UnitSize is null ? _orb.Readout.ValueWidth : _orb.Readout.UnitWidth;
        if (_orb.UnitOnDial && layout.UnitSize is not null)
        {
            // The needle's unit on the dial face (DESIGN 10): its click target follows it.
            layout = layout with
            {
                UnitSize = Presentation.Orb.Lines.OrbLinesPlacement.NeedleDialUnitSize,
                UnitGap = Presentation.Orb.Lines.OrbLinesPlacement.NeedleDialUnitBaseline - layout.ValueBaseline,
            };
            width = _orb.DialUnitWidth;
        }

        var center = new PointD(ctx.CenterX, ctx.CenterY);
        var readout = new PointD(ctx.CenterX, ctx.CenterY + _orb.ReadoutShift);
        return width > 0 && OrbUnitToggle.Contains(new PointD(x, y), OrbUnitToggle.HitRect(layout, ctx.Size, readout, width), center, ctx.Size);
    }

    /// <summary>The unit's hover affordance (55 % → 85 % with an underline, 0.15 s).</summary>
    public void SetUnitHover(bool hovering)
    {
        var now = Now();
        _orb.Scene.Update(Math.Max(_orb.Scene.Time, now));
        _orb.Scene.SetUnitHover(hovering);
        _unitHoverUntil = now + OrbScene.UnitHoverDuration + 0.05;
        _clock.Kick(this);
    }

    public void Render(double now)
    {
        if (!_orb.IsConfigured || Hwnd == 0)
        {
            return;
        }

        _device.Atlas.BeginFrame();
        var motion = Motion?.Invoke(now) ?? new OrbWindowMotion(1, 1, FrameClass.None);
        var blankMisses = _device.Atlas.BlankMisses;
        _orb.Render(now, motion.Scale, motion.Opacity);
        _glyphRetry = _device.Atlas.BlankMisses != blankMisses ? now + (_device.Atlas.SecondsToRetry ?? OrbBlankGlyphs.FirstDelay) : null;
        var demand = FramePolicy.Max(_orb.Demand, motion.Demand);
        if (now < _unitHoverUntil)
        {
            demand = FramePolicy.Max(demand, FrameClass.Fps60);
        }

        if (_device.Atlas.NeedsRebuild)
        {
            demand = FramePolicy.Max(demand, FrameClass.Fps30);
        }

        _demand = demand;
        var fingerprint = _orb.List.Fingerprint() ^ (ulong)_pixels;
        if (_presentedGeneration != _device.Generation)
        {
            _forcePresent = true;
        }

        if (fingerprint == _presented && !_forcePresent)
        {
            FramesSkipped++;
            return;
        }

        if (!Draw())
        {
            return;
        }

        _presented = fingerprint;
        _presentedGeneration = _device.Generation;
        _forcePresent = false;
        FramesPresented++;
    }

    /// <summary>The orb as drawn now (developer capture).</summary>
    public BitmapSource? Snapshot()
    {
        if (!_orb.IsConfigured)
        {
            return null;
        }

        using var target = new OffscreenTarget();
        var pixels = target.Render(_device, _orb.List, _pixels, _pixels);
        return pixels is null ? null : OffscreenTarget.ToBitmap(pixels, _pixels, _pixels);
    }

    public void Dispose()
    {
        _device.Recovered -= OnRecovered;
        CharacterGpu.AssetReady -= OnCharacterAsset;
        _clock.Unregister(this);
        _surface?.Dispose();
        _surface = null;
    }

    private bool Draw()
    {
        if (_device.IsLost && !Recover())
        {
            return false;
        }

        _surface ??= new CompositionSurface(Hwnd);
        try
        {
            if (!_device.CanSubmit)
            {
                FrameStats.AddSkip();
                return Skip();
            }

            var target = _surface.Begin(_device, _pixels, _pixels);
            if (target is not null && OrbGpuRenderer.Draw(_device, _orb.List, target, _pixels, _pixels))
            {
                switch (_surface.Present())
                {
                    case PresentResult.Shown:
                        return true;
                    case PresentResult.Busy:
                        return Skip();
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            AppLog.Warn("An orb frame failed on the GPU.", ex);
        }

        if (_device.IsLost)
        {
            Recover();
        }

        return false;
    }

    /// <summary>
    /// The compositor has not taken the previous frame yet, or the GPU is still busy: the next frame presents instead
    /// (asked for now only when the orb would otherwise stay static).
    /// </summary>
    private bool Skip()
    {
        FramesBusy++;
        _forcePresent = true;
        if (_demand == FrameClass.None)
        {
            _clock.Kick(this);
        }

        return false;
    }

    private bool Recover()
    {
        if (_device.Recover())
        {
            _forcePresent = true;
            _clock.Kick(this);
            return false;
        }

        RendererFailed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private void Resumed(bool active)
    {
        if (active)
        {
            _orb.SnapNext();
            _forcePresent = true;
            _clock.Kick(this);
        }
        else
        {
            _clock.Reschedule();
        }
    }

    private double Now() => Math.Max(_clock.Now, _orb.Scene.Time);

    private static Rgba Accent()
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

    /// <summary>The culture text is shaped for (font fallback of CJK titles).</summary>
    public static void SetLanguage(GpuDevice device, CultureInfo culture) => device.Atlas.Culture = culture;
}
