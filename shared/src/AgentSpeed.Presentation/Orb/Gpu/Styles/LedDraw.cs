using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The floating <c>led</c> instrument on the GPU (DESIGN 2 layers 2–8, 2.1, 2a.1): the silk core over the body; 36 rounded
/// segments lit in the heat color of their own position with energy flowing to a white-hot leading edge and its bloom; the
/// tier tick dots, the redline edge glow and heat haze; one or two comets; the tip flare and the new-peak flash, ring and
/// rays; the response waves; sparks, embers and electric arcs; behind the body the corona and warp streaks, and above the
/// readout the digit lightning.
/// </summary>
public sealed partial class LedDraw : IOrbStyleDraw, IOrbStyleLayers
{
    /// <summary>
    /// Most instances one frame can add: the fixed layers (≤ 150 with the Max heat haze and four waves), two per particle,
    /// nine strokes per arc, 64 per bolt and the flashes.
    /// </summary>
    internal const int Budget = 150 + 2 * LedParticles.Capacity + LedArcs.Capacity * (LedArcs.PointCount - 1)
        + LedLightning.MaxBolts * (LedLightning.MainPoints - 1 + LedLightning.MaxForks * (LedLightning.ForkPoints - 1)) + LedLightning.MaxZaps;

    private const int Segments = GaugeLayout.SegmentCount;
    private const double LastSegment = Segments - 1;

    /// <summary>The lit arc's outer glow is 30 % stronger than in v1.3 (DESIGN 2.1, Gauge energy flow; the prototype's 1.3).</summary>
    private const double ArcGlowBoost = 1.3;

    /// <summary>The shader's glows are Gaussian; this widens one to cover the prototype's exponential falloff.</summary>
    private const double ArcGlowSpread = 1.25;

    private static readonly SpeedTier[] TickTiers = [SpeedTier.Cruise, SpeedTier.Boost, SpeedTier.Overdrive, SpeedTier.Redline];

    private readonly double[] _segmentX = new double[Segments];
    private readonly double[] _segmentY = new double[Segments];
    private readonly double[] _segmentAngle = new double[Segments];
    private readonly Rgba[] _segmentColor = new Rgba[Segments];
    private readonly double[] _flow = new double[Segments];
    private readonly double[] _tickX = new double[TickTiers.Length];
    private readonly double[] _tickY = new double[TickTiers.Length];
    private readonly double[] _tickFraction = new double[TickTiers.Length];
    private readonly LedEffects _effects;

    private OrbGpuContext? _ctx;
    private double _tickRedline = double.NaN;
    private double _halfLength;
    private double _halfWidth;
    private double _corner;
    private bool _dark = true;

    public LedDraw(OrbScene scene, uint seed = 1)
    {
        _effects = new LedEffects(seed);
        Model = new LedModel(scene, _effects);
    }

    public OrbStyle Style => OrbStyle.Led;

    public IOrbStyleModel Model { get; }

    /// <summary>The v1.4 effect state the builder draws.</summary>
    public LedEffects Effects => _effects;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Led;

    public OrbBodyLook Body => OrbBodyLook.Standard;

    public bool SharedEffects => true;

    public OrbReadoutColors? BelowSurface => null;

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _dark = context.Dark;
        var s = context.Size;
        _halfLength = GaugeLayout.SegmentLength * s / 2;
        _halfWidth = GaugeLayout.Radius * s * GaugeLayout.SegmentSpanDegrees * Math.PI / 180 / 2;
        _corner = Math.Min(2 * _halfLength, 2 * _halfWidth) * 0.24;
        for (var i = 0; i < Segments; i++)
        {
            var angle = GaugeLayout.Segment(i).MidDegrees;
            (_segmentX[i], _segmentY[i]) = context.Polar(angle, GaugeLayout.Radius);
            _segmentAngle[i] = angle;
            var color = HeatPalette.Sample(Math.Max(0.07, i / LastSegment));
            _segmentColor[i] = _dark ? color : OkLab.Darken(color, 0.06);
        }

        _tickRedline = double.NaN;
    }

    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scene);
        if (_ctx is not { } ctx)
        {
            return;
        }

        var plain = frame.Thumbnail || ctx.HighContrast;
        var shown = frame.Unavailable ? 0 : StyleMath.Clamp01(scene.GaugeHeat);
        var overdrive = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Overdrive);
        var lit = shown * Segments;
        var darkLeading = _dark ? OkLab.Mix(HeatPalette.WhiteHot, Rgba.White, 0.4 * overdrive) : default;
        FillUniforms(list, ctx, scene, frame);
        if (_effects.Gates.Silk)
        {
            list.AddLed(SilkMode, OrbBlend.Over);
        }

        UpdateFlow(frame);
        if (!plain)
        {
            var redline = RedlineAmount(scene);
            AddHeatHaze(list, ctx, frame, redline);
            AddBloom(list, ctx, scene, frame, lit, overdrive, darkLeading);
            AddEdgeGlow(list, ctx, scene, frame, shown, overdrive, redline);
        }

        AddSegments(list, ctx, scene, frame, lit, overdrive, darkLeading);
        if (!plain)
        {
            AddArcGlow(list, ctx, frame, lit);
        }

        if (frame.Thumbnail)
        {
            return;
        }

        AddTicks(list, ctx, scene.Redline, shown);
        if (ctx.HighContrast)
        {
            return;
        }

        if (!frame.Unavailable)
        {
            AddComets(list, ctx, scene, frame);
        }

        AddFlarePass(list);
        AddWaves(list, ctx, scene);
        AddParticles(list, ctx, frame);
        AddArcs(list, ctx, frame);
    }

    /// <summary>Energy pulses running along the lit segments toward the leading one (Standard and Max).</summary>
    private void UpdateFlow(in OrbFrame frame)
    {
        var h = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Heat);
        var amount = _effects.Gates.Motion && !frame.Unavailable ? StyleMath.SmoothStep(0, 0.08, h) * (0.5 + 0.9 * h) : 0;
        var phase = _effects.Phases.Flow;
        for (var i = 0; i < Segments; i++)
        {
            _flow[i] = amount > 0 ? Flow(i, phase) * amount : 0;
        }
    }

    /// <summary><c>(1 − fract(2.2 i / 36 − φflow))^9</c>: a sharp pulse with a long tail behind it.</summary>
    internal static double Flow(int index, double phase)
    {
        var u = 2.2 * index / Segments - phase;
        u -= Math.Floor(u);
        return Math.Pow(1 - u, 9);
    }

    /// <summary>Brightness of a segment (the last lit one partially) and its share of white-hot near the leading edge.</summary>
    private static (double Brightness, double Whiteness) Light(int index, double lit)
    {
        var brightness = Math.Clamp(lit - index, 0, 1);
        if (brightness <= 0)
        {
            return (0, 0);
        }

        var distance = lit - (index + 1);
        return (brightness, distance < 0 ? 1 : Math.Clamp(1 - distance, 0, 1));
    }

    /// <summary>The leading-segment color (DESIGN 2 token): dark white-hot toward white with overdrive, light heat toward amber.</summary>
    private Rgba Leading(int index, double overdrive, Rgba darkLeading) =>
        _dark ? darkLeading : OkLab.Mix(HeatPalette.Sample(index / LastSegment), HeatPalette.LightSparkHot, 0.2 + 0.3 * overdrive);

    /// <summary>A lit segment's color moved by the energy flow: toward white-hot in dark, 15 % brighter in light.</summary>
    private Rgba Flowing(Rgba color, double flow)
    {
        if (flow <= 0.001)
        {
            return color;
        }

        var target = _dark ? HeatPalette.WhiteHot : Scaled(color, 1.15);
        return Rgba.Lerp(color, target, 0.75 * flow);
    }

    private OrbGpuInstance Segment(int index, Rgba color, double core = 1, double glow = 0, double glowAlpha = 0) =>
        OrbGpuInstance.RoundedRect(_segmentX[index], _segmentY[index], _halfLength, _halfWidth, _segmentAngle[index], _corner, color,
            core, glow, glowAlpha);

    /// <summary>The white-hot bloom of the leading edge (the lit arc's own glow is <see cref="AddArcGlow"/>).</summary>
    private void AddBloom(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbFrame frame, double lit, double overdrive,
        Rgba darkLeading)
    {
        var bloom = frame.Effects.Bloom;
        var glowRadius = 0.02 * ctx.Size * bloom;
        var shimmer = scene.Shimmer;
        var flash = 1 + 0.5 * frame.Flash;
        for (var i = 0; i < Segments; i++)
        {
            var (brightness, whiteness) = Light(i, lit);
            if (brightness <= 0.01 || whiteness <= 0.01)
            {
                continue;
            }

            var flow = _flow[i];
            var color = Flowing(_segmentColor[i], flow);
            if (whiteness > 0)
            {
                color = OkLab.Mix(color, Leading(i, overdrive, darkLeading), whiteness * 0.8);
            }

            var alpha = brightness * 0.45 * whiteness * shimmer[i] * flash * (1 + 0.9 * flow);
            list.Add(Segment(i, color.WithAlpha(Math.Min(1, alpha * bloom)), core: 0, glow: glowRadius * (1 + 0.7 * whiteness),
                glowAlpha: 0.6), ctx.GlowBlend);
        }
    }

    /// <summary>
    /// The lit arc's glow (prototype "gauge" glow): one band over the lit part of the gauge, in the heat of each position,
    /// <c>(0.16 + 0.5 h + 0.45 o) × 1.3 × lvl</c> × 0.55 in dark (0.22 in light) over the segments, falling off over
    /// <c>0.014 + 0.018 h + 0.02 o</c> beyond them, pulsing at the redline.
    /// </summary>
    private void AddArcGlow(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, double lit)
    {
        var h = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Heat);
        var o = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Overdrive);
        var act = StyleMath.SmoothStep(0, 0.08, h);
        if (act <= 0.001 || lit <= 0.01)
        {
            return;
        }

        var amount = (0.16 + 0.5 * h + 0.45 * o) * ArcGlowBoost * _effects.Gates.Level * act * (_dark ? 0.55 : 0.22);
        if (frame.Tier == SpeedTier.Redline && frame.Effects.AuraBreathing)
        {
            amount *= 0.85 + 0.3 * Math.Sin(frame.Time * (7 + 6 * o));
        }

        var s = ctx.Size;
        var fraction = Math.Min(1, lit / Segments);
        var sweep = GaugeLayout.SweepDegrees * fraction;
        list.Add(OrbGpuInstance.PaletteArc(ctx.CenterX, ctx.CenterY, GaugeLayout.Radius * s, GaugeLayout.SegmentLength * s, GaugeLayout.StartDegrees,
            sweep, 0, fraction, Math.Min(1, amount), glow: (0.014 + 0.018 * h + 0.02 * o) * s * ArcGlowSpread, glowAlpha: 1), ctx.GlowBlend);
    }

    /// <summary>The idle sign of life: a band of light travelling along the unlit segments.</summary>
    private double SweepBoost(int index, double? sweep, bool reduceMotion)
    {
        if (sweep is not { } u)
        {
            return 0;
        }

        if (reduceMotion)
        {
            return (_dark ? 0.1 : 0.06) * Math.Sin(Math.PI * u);
        }

        var d = index / LastSegment - (u * 1.3 - 0.15);
        return (_dark ? 0.16 : 0.1) * Math.Exp(-d * d * 90);
    }

    /// <summary>The crisp segments: unlit slate (lifted by the sweep) under the lit heat colors.</summary>
    private void AddSegments(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbFrame frame, double lit, double overdrive,
        Rgba darkLeading)
    {
        var hc = ctx.HighContrast;
        var unlit = hc ? ctx.Contrast.GrayText.WithAlpha(0.6) : ctx.Tokens.UnlitSegment;
        var sweep = frame.Unavailable ? null : scene.IdleSweep;
        var reduceMotion = frame.Effects.ReduceMotion;
        var shimmer = scene.Shimmer;
        var flash = StyleMath.Clamp01(frame.Flash);
        for (var i = 0; i < Segments; i++)
        {
            var (brightness, whiteness) = Light(i, lit);
            if (brightness < 1)
            {
                var boost = SweepBoost(i, sweep, reduceMotion);
                list.Add(Segment(i, boost > 0 ? unlit.WithAlpha(unlit.A + boost) : unlit));
            }

            if (brightness <= 0)
            {
                continue;
            }

            Rgba color;
            var flow = hc ? 0 : _flow[i];
            if (hc)
            {
                color = ctx.Contrast.Highlight;
            }
            else
            {
                color = Flowing(Scaled(_segmentColor[i], shimmer[i]), flow);
                if (whiteness > 0)
                {
                    color = OkLab.Mix(color, Leading(i, overdrive, darkLeading), whiteness);
                }

                if (flash > 0)
                {
                    color = Rgba.Lerp(color, Rgba.White, 0.25 * flash);
                }
            }

            list.Add(Segment(i, color.WithAlpha(Math.Min(1, brightness * (1 + 0.9 * flow)))));
        }
    }

    /// <summary>Four tick dots just outside the arc at the tier thresholds, in the heat color once passed.</summary>
    private void AddTicks(OrbDrawList list, OrbGpuContext ctx, double redline, double shown)
    {
        if (redline != _tickRedline)
        {
            _tickRedline = redline;
            for (var i = 0; i < TickTiers.Length; i++)
            {
                var fraction = HeatMath.HeatAtThreshold(TickTiers[i], redline);
                _tickFraction[i] = fraction;
                (_tickX[i], _tickY[i]) = ctx.Polar(GaugeLayout.AngleForFraction(fraction), GaugeLayout.TickRadius);
            }
        }

        var hc = ctx.HighContrast;
        var idle = hc ? ctx.Contrast.WindowText.WithAlpha(0.55) : _dark ? Rgba.White.WithAlpha(0.26) : Rgba.Black.WithAlpha(0.22);
        var radius = 0.0085 * ctx.Size;
        for (var i = 0; i < TickTiers.Length; i++)
        {
            var fraction = _tickFraction[i];
            var passed = shown >= fraction;
            var color = !passed ? idle : hc ? ctx.Contrast.WindowText.WithAlpha(0.95) : ctx.Label(Math.Max(fraction, 0.1));
            var glow = passed && _dark && !hc ? 1.5 * ctx.Unit : 0;
            list.Add(OrbGpuInstance.Circle(_tickX[i], _tickY[i], radius, color, glow: glow, glowAlpha: 0.5));
        }
    }

    private static Rgba Scaled(Rgba color, double factor) => factor == 1
        ? color
        : new Rgba(Math.Clamp(color.R * factor, 0, 1), Math.Clamp(color.G * factor, 0, 1), Math.Clamp(color.B * factor, 0, 1), color.A);
}
