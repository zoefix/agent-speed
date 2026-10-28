using System.Numerics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>liquid</c> instrument on the GPU (DESIGN 2a.3): volume shading, the volume pass of the liquid shader (spill, back
/// wave, body, spherical shade, inner glow, readout scrim, currents, gold band), the particles clipped to the glass, the
/// meniscus pass and the glass. The shader evaluates the same surface as <see cref="LiquidModel.SurfaceY"/> from
/// <see cref="OrbDrawList.Liquid"/>, which is filled on every frame (the shared readout splits at it).
/// </summary>
public sealed partial class LiquidDraw : IOrbStyleDraw
{
    private const double R = LiquidGeometry.GlassRadius;

    /// <summary>The volume shading ramps from clear at 75 % of 0.5 S to its edge alpha at 0.5 S (a Gaussian of this width).</summary>
    private const double VolumeRamp = 0.075;

    /// <summary>A canvas stroke's glow of σ peaks at width / (σ √(2π)) of the stroke's alpha; the shader's glow peaks at 1.</summary>
    private const double SqrtTau = 2.5066282746310002;

    private static readonly OrbBodyLook DarkBody = new(LiquidModel.DarkBodyTop, LiquidModel.DarkBodyBottom, false);
    private static readonly OrbBodyLook LightBody = new(LiquidModel.LightBodyTop, LiquidModel.LightBodyBottom, false);
    private static readonly Rgba DarkVolumeShade = Rgba.Black.WithAlpha(0.42);
    private static readonly Rgba LightVolumeShade = Rgba.FromHex("#1E283C").WithAlpha(0.12);

    private readonly LiquidModel _model;
    private OrbGpuContext? _context;
    private OrbBodyLook _body = DarkBody;
    private OrbReadoutColors? _below;
    private long _colorKey = long.MinValue;
    private bool _colorDark;
    private Rgba _moteColor = Rgba.White;

    public LiquidDraw(int seed)
    {
        _model = new LiquidModel(seed);
    }

    public OrbStyle Style => OrbStyle.Liquid;

    public IOrbStyleModel Model => _model;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Liquid;

    public OrbBodyLook Body => _body;

    public bool SharedEffects => true;

    /// <summary>Layer 10: the readout below the surface; none in high contrast (the solid fill keeps the system colors legible).</summary>
    public OrbReadoutColors? BelowSurface => _below;

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _body = context.Dark ? DarkBody : LightBody;
        _colorKey = long.MinValue;
        _below = null;
    }

    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_context is not { } ctx)
        {
            return;
        }

        var m = _model;
        var hc = ctx.HighContrast || frame.Effects.HighContrast;
        var thumbnail = frame.Thumbnail;
        RefreshColors(ctx, m, hc);
        if (!hc)
        {
            AddVolumeShading(list, ctx);
        }

        FillUniforms(ref list.Liquid, ctx, m, hc, thumbnail, list.AlphaScale);
        list.AddLiquid(0);
        if (!hc && !thumbnail)
        {
            var clip = list.ClipRadius;
            list.ClipRadius = R * ctx.Size;
            AddMotes(list, ctx, m);
            AddSurfaceParticles(list, ctx, m);
            list.ClipRadius = clip;
        }

        list.AddLiquid(1);
        AddGlass(list, ctx, m, hc, thumbnail);
    }

    /// <summary>Colors that change only with the model's color key: the luminous motes and the readout below the surface.</summary>
    private void RefreshColors(OrbGpuContext ctx, LiquidModel m, bool hc)
    {
        var dark = ctx.Dark;
        if (m.ColorKey == _colorKey && dark == _colorDark && (_below is null) == hc)
        {
            return;
        }

        _colorKey = m.ColorKey;
        _colorDark = dark;
        _moteColor = dark ? OkLab.Mix(m.LiquidColor, HeatPalette.WhiteHot, 0.9) : Rgba.White;
        if (hc)
        {
            _below = null;
            return;
        }

        var shadow = m.ReadoutBelowShadowColor;
        _below = shadow.A > 0
            ? new OrbReadoutColors(m.ReadoutBelowValueColor, m.ReadoutBelowUnitColor, m.ReadoutBelowTitleColor, shadow,
                LiquidModel.ReadoutBelowShadowSigma * ctx.Size)
            : new OrbReadoutColors(m.ReadoutBelowValueColor, m.ReadoutBelowUnitColor, m.ReadoutBelowTitleColor, null, 0, ValueGlow: false);
    }

    /// <summary>
    /// Layer 2's volume shading: around a point up-left of the center, clear to 75 % of 0.5 S, then black 42 % (light
    /// <c>#1E283C</c> 12 %) from 0.5 S on, clipped to the disc. A ring from 0.5 S outward with its inward glow is the ramp.
    /// </summary>
    private static void AddVolumeShading(OrbDrawList list, OrbGpuContext ctx)
    {
        var s = ctx.Size;
        var (x, y) = ctx.At(LiquidGeometry.VolumeCenterX, LiquidGeometry.VolumeCenterY);
        var edge = ctx.Dark ? DarkVolumeShade : LightVolumeShade;
        var clip = list.ClipRadius;
        list.ClipRadius = 0.5 * s;
        list.Add(OrbGpuInstance.Ring(x, y, 0.61 * s, 0.22 * s, edge, glow: VolumeRamp * s, glowAlpha: 1));
        list.ClipRadius = clip;
    }

    private void FillUniforms(ref OrbLiquidUniforms u, OrbGpuContext ctx, LiquidModel m, bool hc, bool thumbnail, float fade)
    {
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var dark = ctx.Dark;
        var still = hc || thumbnail;
        var meanY = cy + m.MeanSurfaceY * s;
        u.Frame = new Vector4((float)cx, (float)cy, (float)s, (float)(R * s));
        u.Surface = new Vector4((float)meanY, still ? 0 : (float)(m.WaveAmplitude * s), still ? 0 : (float)(m.ChopAmplitude * s),
            m.BackWaveVisible && !still ? 1 : 0);
        u.Phases = new Vector4((float)m.Phase1, (float)m.Phase2, (float)m.Phase3, (float)m.Phase4);
        for (var i = 0; i < 4; i++)
        {
            var ripple = Vector4.Zero;
            if (i < m.RippleCount && !still)
            {
                var (x, age, amplitude) = m.RippleAt(i);
                ripple = new Vector4((float)(cx + x * s), (float)(0.42 * s * age), (float)(amplitude * s * Math.Exp(-age / 0.75)), 1);
            }

            u.SetRipple(i, ripple);
        }

        var top = cy + m.BodyGradientTopY * s;
        var bottom = cy + m.BodyGradientBottomY * s;
        var width = m.MeniscusWidth * s;
        if (hc)
        {
            var highlight = ctx.Contrast.Highlight;
            u.Liquid = Color(highlight, 0.35 * fade);
            u.Deep = u.Liquid;
            u.Back = u.Spill = u.Glow = u.GlowGeom = u.Shade = u.Current = u.CurrentGeom = u.MeniscusGlow = u.Gold = Vector4.Zero;
            u.Meniscus = Color(highlight, fade);
            u.MeniscusGeom = new Vector4((float)width, 1, 0, 0);
            u.Flags = new Vector4(0, 0, (float)top, (float)bottom);
            return;
        }

        // Reduce Transparency: the liquid is 15 % more opaque.
        var solid = ctx.SolidBody ? 0.15 : 0;
        var additive = dark ? 1f : 0f;
        u.Liquid = Color(m.BodyTopColor, fade, solid);
        u.Deep = Color(m.BodyBottomColor, fade, solid);
        u.Back = Color(m.BackWaveColor, fade, solid * 0.55);
        u.Spill = thumbnail ? Vector4.Zero : Color(m.SpillColor, fade);
        u.Glow = Color(m.InnerGlowColor, fade);
        u.GlowGeom = new Vector4((float)cx, (float)(cy + m.InnerGlowCenterY * s), (float)(LiquidGeometry.InnerGlowRadius * s), additive);
        u.Shade = Color(m.SphericalShadeColor, fade);
        if (m.CurrentsVisible && !thumbnail)
        {
            u.Current = Color(m.CurrentColor(0), fade);
            u.CurrentGeom = new Vector4(0, (float)(LiquidGeometry.CurrentBlur * s), (float)m.CurrentDrift, additive);
        }
        else
        {
            u.Current = u.CurrentGeom = Vector4.Zero;
        }

        u.Meniscus = Color(m.MeniscusColor, fade);
        var glow = m.MeniscusGlowColor;
        if (dark && glow.A > 0.004)
        {
            var sigma = Math.Max(0.001, m.MeniscusGlowSigma * s);
            var strength = Math.Min(1, glow.A * Math.Min(1, width / (sigma * SqrtTau)) * 1.6);
            u.MeniscusGlow = Color(glow.WithAlpha(strength), fade);
            u.MeniscusGeom = new Vector4((float)width, (float)sigma, 1, 0);
        }
        else
        {
            u.MeniscusGlow = Vector4.Zero;
            u.MeniscusGeom = new Vector4((float)width, 1, 0, 0);
        }

        u.Gold = m.GoldBandAlpha > 0.004 && !thumbnail
            ? new Vector4((float)(cy + m.GoldBandY * s), (float)(m.GoldBandAlpha * fade), (float)(LiquidGeometry.GoldBandHalfHeight * s), additive)
            : Vector4.Zero;
        u.Flags = new Vector4(additive, (float)m.ScrimOpacity, (float)top, (float)bottom);
    }

    private static Vector4 Color(Rgba color, double fade, double extra = 0) =>
        new((float)color.R, (float)color.G, (float)color.B, (float)Math.Clamp((color.A + extra) * fade, 0, 1));
}
