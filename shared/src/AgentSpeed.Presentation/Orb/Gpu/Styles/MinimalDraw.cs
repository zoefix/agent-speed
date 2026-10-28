using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>minimal</c> instrument on the GPU (DESIGN 2a.5), the macOS look on <see cref="MinimalModel"/>: the track, the
/// progress ring (a conic gradient over the first lap, the second lap over it with its head's shadow), its glow and the
/// head dot. An idle orb draws the track only.
/// </summary>
public sealed class MinimalDraw : IOrbStyleDraw
{
    private const double Top = StyleMath.TopDegrees;
    private const double MinSweep = 0.18;

    private static readonly OrbBodyLook NoLens = new(null, null, Lens: false);

    private readonly MinimalModel _model;
    private OrbGpuContext? _ctx;

    public MinimalDraw()
    {
        _model = new MinimalModel();
    }

    public OrbStyle Style => OrbStyle.Minimal;

    public IOrbStyleModel Model => _model;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Minimal;

    public OrbBodyLook Body => NoLens;

    public bool SharedEffects => false;

    public OrbReadoutColors? BelowSurface => null;

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
    }

    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_ctx is not { } ctx)
        {
            return;
        }

        var m = _model;
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var radius = MinimalModel.TrackRadius * s;
        var width = m.RingWidth * s;
        var hc = ctx.HighContrast;
        list.Add(OrbGpuInstance.Ring(cx, cy, radius, width, hc ? ctx.Contrast.GrayText : m.TrackColor));
        if (!m.RingVisible)
        {
            return;
        }

        var lap1 = Math.Clamp(m.Lap1EndDegrees - Top, 0, 360);
        var lap2 = m.HasSecondLap ? Math.Clamp(m.Lap2EndDegrees - Top, 0, 360) : 0;
        if (hc)
        {
            // The second lap in the text color: in the highlight color it would vanish into the first.
            list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, Top, lap1, ctx.Contrast.Highlight, roundCaps: true));
            if (lap2 > MinSweep)
            {
                list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, Top, lap2, ctx.Contrast.WindowText, roundCaps: true));
            }

            AddHeadDot(list, ctx, ctx.Contrast.Window);
            return;
        }

        var head = m.HeadColor;
        var glow = Rgba.Transparent;
        var glowRadius = 0.0;
        if (!frame.Thumbnail && m.RingGlowSigma > 0 && m.RingGlowOpacity > 0.002)
        {
            // An analytic glow is full strength at the edge; a canvas shadow of a thin stroke only reaches erf(w / 2√2σ).
            var sigma = m.RingGlowSigma * s;
            glow = head.WithAlpha(m.RingGlowOpacity * NeedleGlow.Erf(width / (2 * Math.Sqrt(2) * sigma)));
            glowRadius = OrbGpuInstance.Glow(sigma);
        }

        AddGlow(list, ctx, radius, width, lap1, glow, glowRadius);
        list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, Top, lap1, head, m.StartColor, roundCaps: true));
        if (lap2 > MinSweep)
        {
            if (m.ShadowVisible)
            {
                var shadow = m.ShadowColor;
                list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, m.ShadowStartDegrees, m.ShadowEndDegrees - m.ShadowStartDegrees,
                    shadow, shadow, core: 0, glow: OrbGpuInstance.Glow(m.ShadowSigma * s), glowAlpha: 1));
            }

            AddGlow(list, ctx, radius, width, lap2, glow, glowRadius);
            list.Add(OrbGpuInstance.BandArc(cx, cy, radius, width, Top, lap2, head, head, roundCaps: true));
        }

        AddHeadDot(list, ctx, m.HeadDotColor);
    }

    private static void AddGlow(OrbDrawList list, OrbGpuContext ctx, double radius, double width, double sweep, Rgba glow, double glowRadius)
    {
        if (glow.A <= 0.002)
        {
            return;
        }

        list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, Top, sweep, glow, glow, roundCaps: true, core: 0,
            glow: glowRadius, glowAlpha: 1));
    }

    private void AddHeadDot(OrbDrawList list, OrbGpuContext ctx, Rgba color)
    {
        var m = _model;
        if (!m.HeadDotVisible)
        {
            return;
        }

        var (x, y) = ctx.Polar(m.HeadDegrees, MinimalModel.TrackRadius);
        list.Add(OrbGpuInstance.Circle(x, y, m.HeadDotRadius * ctx.Size, color.WithAlpha(color.A * m.HeadDotOpacity)));
    }
}
