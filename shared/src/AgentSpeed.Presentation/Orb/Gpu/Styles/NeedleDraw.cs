using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>needle</c> instrument on the GPU (DESIGN 2a.2), back to front: dial band, red-zone glow and red zone, graduations,
/// trace, stop pins, shift lights, wake, needle (glow or drop shadow, blade, spine, tip flare), peg sparks and hub. Every
/// value comes from <see cref="NeedleModel"/>; glows are the analytic Gaussian of the shape pipeline, scaled to the peak of a
/// blurred stroke of the same width.
/// </summary>
public sealed partial class NeedleDraw : IOrbStyleDraw
{
    private const double MaxGlowScale = 1.25;
    private const double Sqrt2 = 1.4142135623730951;

    private readonly NeedleModel _model;
    private readonly double[] _ticks = new double[NeedleGeometry.TickCount * 4];
    private OrbGpuContext? _context;
    private double _redStart;
    private double _redSweep;

    public NeedleDraw(uint seed)
    {
        _model = new NeedleModel(seed);
    }

    public OrbStyle Style => OrbStyle.Needle;

    public IOrbStyleModel Model => _model;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Needle;

    public OrbBodyLook Body => OrbBodyLook.Standard;

    public bool SharedEffects => true;

    public OrbReadoutColors? BelowSurface => null;

    /// <summary>
    /// The unit as the legend of a real tachometer while the traffic readout is on (DESIGN 10): its glyph and color, drawn on
    /// the dial face under the graduations and the needle; null keeps the unit in the hub. Set by the orb before each frame.
    /// </summary>
    public (OrbGlyph Glyph, Rgba Color)? DialUnit { get; set; }

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        for (var k = 0; k < NeedleGeometry.TickCount; k++)
        {
            var major = NeedleGeometry.IsMajor(k);
            var degrees = StyleMath.DialDegrees(NeedleGeometry.TickDial(k));
            var outer = NeedleGeometry.TickOuterRadius;
            var inner = outer - (major ? NeedleGeometry.MajorLength : NeedleGeometry.MinorLength);
            (_ticks[4 * k], _ticks[4 * k + 1]) = context.Polar(degrees, inner);
            (_ticks[4 * k + 2], _ticks[4 * k + 3]) = context.Polar(degrees, outer);
        }

        _redStart = StyleMath.DialDegrees(NeedleGeometry.RedZoneStart);
        _redSweep = StyleMath.DialSweepDegrees * (NeedleGeometry.RedZoneEnd - NeedleGeometry.RedZoneStart);
    }

    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_context is not { } ctx)
        {
            return;
        }

        var m = _model;
        var hc = ctx.HighContrast || m.HighContrast;
        var glowScale = frame.Effects.Level == EffectsIntensity.Max ? MaxGlowScale : 1;
        AddDial(list, ctx, m, hc);
        if (DialUnit is { } legend && !frame.Thumbnail)
        {
            var baseline = ctx.CenterY + Traffic.OrbTrafficPlacement.NeedleDialUnitBaseline * ctx.Size;
            list.AddInlineText(legend.Glyph, ctx.CenterX - legend.Glyph.Advance / 2, baseline, legend.Color);
        }

        AddGraduations(list, ctx, m, hc, glowScale);
        AddTrace(list, ctx, m, hc);
        AddPins(list, ctx, m, hc);
        AddLamps(list, ctx, m, hc, glowScale);
        AddWake(list, ctx, m, hc);
        AddNeedle(list, ctx, m, hc, frame.Thumbnail);
        if (!hc && !frame.Thumbnail)
        {
            AddSparks(list, ctx, m);
        }

        if (m.ShowHub)
        {
            AddHub(list, ctx, m.Palette, hc);
        }
    }

    /// <summary>Peak of a stroke <paramref name="width"/> wide blurred by σ, relative to the analytic glow (full at the edge).</summary>
    private static double StrokeGlow(double width, double sigma) => sigma > 0 ? NeedleGlow.Erf(width / (2 * Sqrt2 * sigma)) : 1;

    /// <summary>Peak of a blurred dot of <paramref name="radius"/>, relative to the analytic glow.</summary>
    private static double DotGlow(double radius, double sigma) => sigma > 0 ? 1 - Math.Exp(-radius * radius / (2 * sigma * sigma)) : 1;

    /// <summary>Layers 2 and 3: the dial band, then the red zone with its redline glow.</summary>
    private void AddDial(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc)
    {
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var palette = m.Palette;
        if (!hc)
        {
            var start = StyleMath.DialDegrees(NeedleGeometry.DialBandStart);
            var sweep = StyleMath.DialSweepDegrees * (NeedleGeometry.DialBandEnd - NeedleGeometry.DialBandStart);
            list.Add(OrbGpuInstance.BandArc(cx, cy, NeedleGeometry.DialBandRadius * s, NeedleGeometry.DialBandWidth * s, start, sweep,
                palette.DialBand));
        }

        var radius = NeedleGeometry.RedZoneRadius * s;
        var width = NeedleGeometry.RedZoneWidth * s;
        var red = hc ? ctx.Contrast.Highlight : palette.RedZone;
        if (!hc && m.RedZoneGlowSigma > 0 && m.RedZoneGlowOpacity > 0.002)
        {
            var sigma = m.RedZoneGlowSigma * s;
            var glow = red.WithAlpha(m.RedZoneGlowOpacity * StrokeGlow(width, sigma));
            list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, _redStart, _redSweep, glow, core: 0, glow: OrbGpuInstance.Glow(sigma),
                glowAlpha: 1), ctx.GlowBlend);
        }

        list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, _redStart, _redSweep, red.WithAlpha(hc ? 1 : m.RedZoneOpacity)));
    }

    /// <summary>Layer 4: 41 round-capped ticks (minors hidden below S 120); lit majors glow in dark.</summary>
    private void AddGraduations(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc, double glowScale)
    {
        var s = ctx.Size;
        var ticks = m.Ticks;
        var text = ctx.Contrast.WindowText;
        for (var k = 0; k < ticks.Length; k++)
        {
            ref readonly var tick = ref ticks[k];
            if (!tick.Visible)
            {
                continue;
            }

            var width = tick.Width * s;
            var glow = 0.0;
            var glowAlpha = 0.0;
            if (!hc && tick.GlowSigma > 0 && tick.GlowOpacity > 0)
            {
                var sigma = tick.GlowSigma * s;
                glow = OrbGpuInstance.Glow(sigma);
                glowAlpha = Math.Min(1, tick.GlowOpacity / Math.Max(0.05, tick.Color.A) * glowScale * StrokeGlow(width, sigma));
            }

            var i = 4 * k;
            list.Add(OrbGpuInstance.Capsule(_ticks[i], _ticks[i + 1], _ticks[i + 2], _ticks[i + 3], width, hc ? text : tick.Color, glow: glow,
                glowAlpha: glowAlpha));
        }
    }

    /// <summary>
    /// Layer 5: the trace from n = 0 to the needle, its opacity rising toward the needle. Dark samples the heat palette along
    /// the arc (additive); light joins short runs between label colors.
    /// </summary>
    private static void AddTrace(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc)
    {
        if (!m.TraceVisible)
        {
            return;
        }

        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var radius = NeedleGeometry.TraceRadius * s;
        var width = NeedleGeometry.TraceWidth * s;
        var end = m.TraceEnd;
        var start = StyleMath.DialDegrees(0);
        if (hc)
        {
            list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, start, StyleMath.DialSweepDegrees * end, ctx.Contrast.WindowText));
            return;
        }

        var first = m.TraceOpacityStart;
        var last = m.TraceOpacityEnd;
        if (ctx.Dark)
        {
            list.Add(OrbGpuInstance.PaletteArc(cx, cy, radius, width, start, StyleMath.DialSweepDegrees * end, 0, end, last,
                startAlpha: last > 0 ? first / last : 1), OrbBlend.Additive);
            return;
        }

        var runs = Math.Max(2, (int)Math.Ceiling(end * 16));
        for (var i = 0; i < runs; i++)
        {
            var p0 = end * i / runs;
            var p1 = end * (i + 1) / runs;
            var a0 = first + (last - first) * (p0 / end);
            var a1 = first + (last - first) * (p1 / end);
            list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, StyleMath.DialDegrees(p0), StyleMath.DialSweepDegrees * (p1 - p0),
                m.TraceColor(p1).WithAlpha(a1), m.TraceColor(p0).WithAlpha(a0)));
        }
    }

    /// <summary>Layer 6: the zero stop and the peg.</summary>
    private static void AddPins(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc)
    {
        if (!m.ShowStopPins)
        {
            return;
        }

        var color = hc ? ctx.Contrast.WindowText : m.Palette.Pin;
        var radius = NeedleGeometry.PinSize * ctx.Size;
        var (zx, zy) = ctx.Polar(StyleMath.DialDegrees(NeedleGeometry.ZeroPin), NeedleGeometry.PinRadius);
        var (px, py) = ctx.Polar(StyleMath.DialDegrees(NeedleGeometry.PegPin), NeedleGeometry.PinRadius);
        list.Add(OrbGpuInstance.Circle(zx, zy, radius, color));
        list.Add(OrbGpuInstance.Circle(px, py, radius, color));
    }

    /// <summary>Layer 7 (S ≥ 120): five housings and their lamps, glowing in dark.</summary>
    private static void AddLamps(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc, double glowScale)
    {
        if (!m.ShowLamps)
        {
            return;
        }

        var s = ctx.Size;
        var size = NeedleGeometry.LampSize * s;
        var radius = size * NeedleGeometry.LampLitScale;
        var housing = m.Palette.LampHousing;
        var lamps = m.Lamps;
        for (var i = 0; i < lamps.Length; i++)
        {
            ref readonly var lamp = ref lamps[i];
            var (x, y) = ctx.At(lamp.X, lamp.Y);
            list.Add(hc
                ? OrbGpuInstance.Ring(x, y, size - 0.5, 1, ctx.Contrast.GrayText)
                : OrbGpuInstance.Circle(x, y, size, housing));
            if (lamp.Opacity <= 0.001)
            {
                continue;
            }

            var color = (hc ? ctx.Contrast.Highlight : lamp.Color).WithAlpha(lamp.Opacity);
            var glow = 0.0;
            var glowAlpha = 0.0;
            if (!hc && lamp.GlowSigma > 0)
            {
                var sigma = lamp.GlowSigma * s;
                glow = OrbGpuInstance.Glow(sigma);
                glowAlpha = Math.Min(1, 0.9 * glowScale * DotGlow(radius, sigma));
            }

            list.Add(OrbGpuInstance.Circle(x, y, radius, color, glow: glow, glowAlpha: glowAlpha));
        }
    }

    /// <summary>Layer 8: the wedge trailing a fast needle, bright at the needle and clear at its tail.</summary>
    private static void AddWake(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc)
    {
        if (hc || !m.WakeVisible || m.WakeHeadOpacity <= 0.002)
        {
            return;
        }

        var s = ctx.Size;
        var head = m.WakeHeadDegrees;
        var tail = m.WakeTailDegrees;
        var length = Math.Abs(head - tail);
        if (length <= 0.01)
        {
            return;
        }

        var radius = (NeedleGeometry.WakeInnerRadius + NeedleGeometry.WakeOuterRadius) / 2 * s;
        var width = (NeedleGeometry.WakeOuterRadius - NeedleGeometry.WakeInnerRadius) * s;
        var bright = m.WakeColor.WithAlpha(m.WakeHeadOpacity);
        var clear = m.WakeColor.WithAlpha(0);
        list.Add(tail < head
            ? OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, tail, length, bright, clear)
            : OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, head, length, clear, bright), ctx.GlowBlend);
    }
}
