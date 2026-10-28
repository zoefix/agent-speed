using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>reactor</c> instrument on the GPU (DESIGN 2a.4), the macOS look on <see cref="ReactorModel"/>: stator and
/// tracks, the corona, three counter-rotating rotors, the pulse flare ring, lightning with its sparks and the core window.
/// Thumbnails draw rotors A and C and the core only; Windows high contrast draws the rings in the theme's text color with
/// highlighted heads and no corona, glow or lightning.
/// </summary>
public sealed partial class ReactorDraw : IOrbStyleDraw
{
    private static readonly OrbBodyLook DarkBody = new(Rgba.FromHex("#141824"), Rgba.FromHex("#06070B"), Lens: true);
    private static readonly OrbBodyLook LightBody = new(null, Rgba.FromHex("#E5E9F1"), Lens: true);

    private readonly ReactorModel _model;
    private readonly double[] _notchX0 = new double[ReactorModel.MaxCoils];
    private readonly double[] _notchY0 = new double[ReactorModel.MaxCoils];
    private readonly double[] _notchX1 = new double[ReactorModel.MaxCoils];
    private readonly double[] _notchY1 = new double[ReactorModel.MaxCoils];
    private OrbGpuContext? _ctx;
    private int _notches;

    public ReactorDraw(int seed)
    {
        _model = new ReactorModel(seed);
    }

    public OrbStyle Style => OrbStyle.Reactor;

    public IOrbStyleModel Model => _model;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Reactor;

    public OrbBodyLook Body { get; private set; } = DarkBody;

    public bool SharedEffects => true;

    public OrbReadoutColors? BelowSurface => null;

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        Body = context.Dark ? DarkBody : LightBody;
        _notches = context.Size < 120 ? 4 : 24;
        for (var i = 0; i < _notches; i++)
        {
            var angle = 360.0 * i / _notches;
            (_notchX0[i], _notchY0[i]) = context.Polar(angle, ReactorGeometry.NotchInner);
            (_notchX1[i], _notchY1[i]) = context.Polar(angle, ReactorGeometry.NotchOuter);
        }
    }

    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_ctx is not { } ctx)
        {
            return;
        }

        var thumbnail = frame.Thumbnail;
        var effects = !thumbnail && !ctx.HighContrast;
        var glowScale = frame.Effects.Level == EffectsIntensity.Max ? 1.3 : 1;
        if (!thumbnail)
        {
            AddStator(list, ctx);
            AddTracks(list, ctx);
        }

        if (effects)
        {
            AddCorona(list, ctx);
        }

        AddRotorA(list, ctx, effects, glowScale);
        if (!thumbnail)
        {
            AddRotorB(list, ctx);
        }

        AddRotorC(list, ctx);
        if (effects)
        {
            AddFlare(list, ctx);
            AddLightning(list, ctx);
        }

        AddCore(list, ctx, effects, glowScale);
    }

    /// <summary>Layer 2: the stator ring and its notches (every 6th brighter; 4 below S 120).</summary>
    private void AddStator(OrbDrawList list, OrbGpuContext ctx)
    {
        var m = _model;
        var s = ctx.Size;
        var hc = ctx.HighContrast;
        var text = ctx.Contrast.WindowText;
        list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, ReactorGeometry.StatorRadius * s,
            Math.Max(1, ReactorGeometry.StatorWidth * s), hc ? text : m.StatorColor));
        var width = Math.Max(0.8, ReactorGeometry.NotchWidth * s);
        for (var i = 0; i < _notches; i++)
        {
            var color = hc ? text.WithAlpha(_notches == 4 || i % 6 == 0 ? 1 : 0.5) : m.NotchColor(i);
            list.Add(OrbGpuInstance.Capsule(_notchX0[i], _notchY0[i], _notchX1[i], _notchY1[i], width, color));
        }
    }

    /// <summary>Layer 3: the unlit tracks under rotors A and C.</summary>
    private void AddTracks(OrbDrawList list, OrbGpuContext ctx)
    {
        var s = ctx.Size;
        var color = ctx.HighContrast ? ctx.Contrast.GrayText.WithAlpha(0.6) : _model.TrackColor;
        list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, ReactorGeometry.RotorARadius * s, ReactorGeometry.RotorAWidth * s, color));
        list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, ReactorGeometry.RotorCRadius * s, ReactorGeometry.RotorCWidth * s, color));
    }

    /// <summary>
    /// Layer 4: a soft radial falloff from the core rim (its curve concentrates light near the rim, so it reaches 0.42 S
    /// rather than the nominal 0.40 S), additive in dark.
    /// </summary>
    private void AddCorona(OrbDrawList list, OrbGpuContext ctx)
    {
        var opacity = _model.CoronaOpacity;
        if (opacity <= 0.004)
        {
            return;
        }

        list.Add(OrbGpuInstance.Radial(ctx.CenterX, ctx.CenterY, ReactorGeometry.CoreRadius * ctx.Size, 0.42 * ctx.Size,
            _model.CoronaColor, Math.Min(1, opacity)), ctx.GlowBlend);
    }

    /// <summary>Layer 10: the core window disc and its rim (glowing in dark; unlit at idle).</summary>
    private void AddCore(OrbDrawList list, OrbGpuContext ctx, bool effects, double glowScale)
    {
        var m = _model;
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var radius = ReactorGeometry.CoreRadius * s;
        if (ctx.HighContrast)
        {
            list.Add(OrbGpuInstance.Circle(cx, cy, radius, ctx.Contrast.Window));
        }
        else
        {
            var offset = ReactorGeometry.CoreGradientOffsetY * s;
            list.Add(OrbGpuInstance.Circle(cx, cy, radius, m.CoreTopColor).RadialGradient(m.CoreBottomColor, radius - offset, 0, offset));
        }

        var width = m.RimWidth * s;
        var glow = m.RimGlowColor;
        if (effects && glow.A > 0.004)
        {
            var sigma = m.RimGlowSigma * s;
            var alpha = glow.A * glowScale * StrokeGlow(width, sigma);
            list.Add(OrbGpuInstance.Ring(cx, cy, radius, width, glow.WithAlpha(alpha), core: 0, glow: OrbGpuInstance.Glow(sigma), glowAlpha: 1),
                ctx.GlowBlend);
        }

        list.Add(OrbGpuInstance.Ring(cx, cy, radius, width, ctx.HighContrast ? ctx.Contrast.WindowText : m.RimColor));
    }

    /// <summary>Peak strength of a canvas shadow of σ around a stroke <paramref name="width"/> wide, relative to the analytic glow.</summary>
    private static double StrokeGlow(double width, double sigma) =>
        sigma > 0 ? NeedleGlow.Erf(width / (2 * Math.Sqrt(2) * sigma)) : 1;
}
