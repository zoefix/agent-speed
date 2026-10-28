using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>
/// The layers every style shares (DESIGN 2 and 2a, Shared foundation): the body with its shadow, rim and lens, the aura,
/// the response wave, the new-peak rays, the source bezel (2b), the agent dots and the Settings highlight ring.
/// </summary>
public static class OrbSharedDraw
{
    private static readonly Rgba DarkInnerShadow = Rgba.Black.WithAlpha(0.55);
    private static readonly Rgba LightInnerShadow = Rgba.FromHex("#1B2340").WithAlpha(0.16);
    private static readonly Rgba DarkLens = Rgba.White.WithAlpha(0.06);
    private static readonly Rgba LightLens = Rgba.White.WithAlpha(0.7);
    private static readonly Rgba DarkDotHalo = new(6 / 255.0, 8 / 255.0, 12 / 255.0, 0.55);
    private static readonly Rgba LightDotHalo = Rgba.White.WithAlpha(0.75);
    private static readonly Rgba DarkPulseMix = HeatPalette.WhiteHot;
    private static readonly Rgba DarkGold = OkLab.Mix(HeatPalette.Gold, HeatPalette.WhiteHot, 0.25);
    private static readonly Rgba LightGold = OkLab.Darken(HeatPalette.Gold, 0.1);

    /// <summary>
    /// A Gaussian-blurred disc (σ <paramref name="sigma"/>) as one SDF circle: the radius shrinks by 1.59 σ and the glow
    /// radius is 1.91 σ, which matches erfc's profile within 1 % of the peak from the edge out to 2 σ.
    /// </summary>
    public static OrbGpuInstance BlurredDisc(double x, double y, double radius, double sigma, Rgba color) =>
        OrbGpuInstance.Circle(x, y, Math.Max(0, radius - 1.59 * sigma), color, core: 1, glow: 1.91 * sigma, glowAlpha: 1);

    /// <summary>
    /// The disc (DESIGN 2, layer 2): drop shadow outside the glass, the body gradient at the opacity setting, the inner shadow,
    /// the lens highlight, the rim (or nothing where the source bezel replaces it) and the light outer stroke.
    /// </summary>
    public static void Body(OrbDrawList list, OrbGpuContext ctx, OrbBodyLook look, bool bezel)
    {
        var s = ctx.Size;
        var r = s / 2;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var t = ctx.Tokens;
        if (ctx.HighContrast)
        {
            list.Add(OrbGpuInstance.Circle(cx, cy, r, ctx.Contrast.Window));
            list.Add(OrbGpuInstance.Ring(cx, cy, r - 0.75, 1.5, ctx.Contrast.WindowText));
            return;
        }

        list.ClipRadius = -(r - 0.5 * ctx.Pixel);
        list.Add(BlurredDisc(cx, cy + t.ShadowOffset * s, r, t.ShadowBlur * s / 2, t.ShadowColor));
        list.ClipRadius = 0;

        var alpha = ctx.SolidBody ? 1 : t.BodyAlpha * Math.Clamp(ctx.BodyOpacity, 0.4, 1);
        var top = (look.Top ?? t.BodyTop).WithAlpha(alpha);
        var bottom = (look.Bottom ?? t.BodyBottom).WithAlpha(alpha);
        list.Add(OrbGpuInstance.Circle(cx, cy, r, top).LinearGradient(bottom, r));

        list.ClipRadius = r;
        var inner = ctx.Dark ? DarkInnerShadow : LightInnerShadow;
        var sigma = 0.03 * s;
        list.Add(OrbGpuInstance.Ring(cx, cy, r, 0.1 * s - 2 * 1.59 * sigma, inner.MultiplyAlpha(0.8), core: 1, glow: 1.91 * sigma, glowAlpha: 1));
        if (look.Lens)
        {
            var lens = ctx.Dark ? DarkLens : LightLens;
            list.Add(OrbGpuInstance.Circle(cx, cy, r, lens).RadialGradient(lens.WithAlpha(0), 0.55 * s, -0.2 * s, -0.3 * s));
        }

        list.ClipRadius = 0;
        if (!bezel)
        {
            list.Add(OrbGpuInstance.Ring(cx, cy, r - 0.5, 1, t.RimTop).LinearGradient(t.RimTop.WithAlpha(0), 0.75 * r));
        }

        if (t.OuterStroke is { } outer)
        {
            list.Add(OrbGpuInstance.Ring(cx, cy, r + 0.5, 1, outer));
        }
    }

    /// <summary>
    /// The aura (DESIGN 2, layer 1): a radial halo in the heat color from the disc edge to 0.75 S, at <paramref name="scale"/> ×
    /// its opacity and moved toward white-hot by <paramref name="hotMix"/> (the floating <c>led</c>, DESIGN 2.1).
    /// </summary>
    public static void Aura(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, double heat, double scale = 1, double hotMix = 0)
    {
        if (ctx.HighContrast)
        {
            return;
        }

        var alpha = Math.Min(1, scene.AuraIntensity * ctx.Tokens.AuraOpacityScale * scale);
        if (alpha <= 0.004)
        {
            return;
        }

        var h = Math.Max(heat, 0.12);
        var color = ctx.Dark ? HeatPalette.Sample(h) : ctx.Label(h);
        if (hotMix > 0.001)
        {
            color = OkLab.Mix(color, HeatPalette.WhiteHot, Math.Min(1, hotMix));
        }

        list.Add(OrbGpuInstance.Radial(ctx.CenterX, ctx.CenterY, 0.5 * ctx.Size, 0.75 * ctx.Size, color, alpha), ctx.GlowBlend);
    }

    /// <summary>The response wave (DESIGN 2, layer 7): a ring from the disc edge to 0.74 S, fading as it expands.</summary>
    public static void Pulses(OrbDrawList list, OrbGpuContext ctx, OrbScene scene)
    {
        if (ctx.HighContrast)
        {
            return;
        }

        var s = ctx.Size;
        var unit = ctx.Unit;
        for (var i = 0; i < scene.PulseCount; i++)
        {
            var wave = scene.PulseAt(i);
            var p = wave.Progress;
            var eased = Easing.OutCubic(p);
            var strength = Math.Clamp(wave.Strength, 0.3, 1);
            var color = ctx.Dark ? OkLab.Mix(HeatPalette.Sample(Math.Max(0.15, wave.Heat)), DarkPulseMix, 0.18) : ctx.Label(Math.Max(0.15, wave.Heat));
            double radius;
            double alpha;
            if (scene.Effects.PulseExpands)
            {
                radius = (0.5 + 0.24 * eased) * s;
                alpha = 0.6 * (1 - eased) * (0.45 + 0.55 * strength);
            }
            else
            {
                radius = 0.53 * s;
                alpha = 0.5 * Math.Sin(Math.PI * p) * strength;
            }

            var width = Math.Max(0.3, 2 * unit * (1 - eased) * (0.55 + 0.45 * strength));
            list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, radius, width, color.WithAlpha(alpha), glow: 3 * unit, glowAlpha: 0.35),
                ctx.GlowBlend);
        }
    }

    /// <summary>The new-peak burst (DESIGN 2, layer 8): 24 gold rays from the disc edge.</summary>
    public static void PeakRays(OrbDrawList list, OrbGpuContext ctx, OrbScene scene)
    {
        if (ctx.HighContrast || scene.PeakRays is not { } p)
        {
            return;
        }

        var unit = ctx.Unit;
        var gold = ctx.Dark ? DarkGold : LightGold;
        if (!scene.Effects.SlideTransitions)
        {
            list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, 0.54 * ctx.Size, 3 * unit, gold.WithAlpha(0.45 * Math.Sin(Math.PI * p)),
                glow: 4 * unit, glowAlpha: 0.4), ctx.GlowBlend);
            return;
        }

        var travel = Easing.OutCubic(p);
        var alpha = Math.Pow(1 - p, 1.3);
        for (var i = 0; i < 24; i++)
        {
            var longRay = i % 2 == 0;
            var angle = i * 15 + 3.75;
            var reach = longRay ? 0.23 : 0.16;
            var head = 0.515 + reach * travel;
            var tail = 0.505 + reach * Math.Max(0, travel - 0.42) * 1.25;
            var (tx, ty) = ctx.Polar(angle, tail);
            var (hx, hy) = ctx.Polar(angle, head);
            list.Add(OrbGpuInstance.Capsule(tx, ty, hx, hy, (longRay ? 1.7 : 1.2) * unit, gold.WithAlpha(alpha), gold.WithAlpha(alpha * 0.25),
                glow: 2.2 * unit, glowAlpha: 0.45), ctx.GlowBlend);
        }
    }

    /// <summary>
    /// The source bezel of an orb tracking a subset (DESIGN 2b, Identity at a glance): equal arcs clockwise from 12 o'clock in
    /// source order with 4° gaps, max(1 pt, 0.0105 S) wide; unavailable sources dashed in the unlit token × 2.5.
    /// </summary>
    public static void Bezel(OrbDrawList list, OrbGpuContext ctx, IReadOnlyList<SourceKind>? sources, SourceSet unavailable)
    {
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        var s = ctx.Size;
        var width = s < 64 ? ctx.Pixel : Math.Max(1, 0.0105 * s);
        var radius = s / 2 - width / 2;
        var count = sources.Count;
        var gap = count > 1 ? 4.0 : 0;
        var span = 360.0 / count;
        var unlit = ctx.Tokens.UnlitSegment.WithAlpha(Math.Min(1, ctx.Tokens.UnlitSegment.A * 2.5));
        for (var i = 0; i < count; i++)
        {
            var source = sources[i];
            var off = unavailable.Contains(source);
            var start = -90 + span * i + gap / 2;
            var sweep = span - gap;
            if (!off)
            {
                var color = ctx.HighContrast ? ctx.Contrast.Highlight
                    : ctx.Dark ? HeatPalette.SourceColor(source).WithAlpha(0.85)
                    : OkLab.Darken(HeatPalette.SourceColor(source), 0.1).WithAlpha(0.9);
                list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, start, count == 1 ? 360 : sweep, color));
                continue;
            }

            var dash = ctx.HighContrast ? ctx.Contrast.GrayText : unlit;
            var degreesPerPoint = 180 / (Math.PI * radius);
            var on = width * 1.2 * degreesPerPoint;
            var period = width * 2.6 * degreesPerPoint;
            for (var angle = start; angle < start + sweep; angle += period)
            {
                list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, angle, Math.Min(on, start + sweep - angle), dash));
            }
        }
    }

    /// <summary>
    /// The agent dots (DESIGN 2, layer 6): one 4 pt dot per generating agent in its source color at the style's line, gently
    /// pulsing; at most six (five and <c>+n</c> beyond, the label is the readout's).
    /// </summary>
    public static void AgentDots(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, OrbReadoutLayout layout, double time, bool unavailable)
    {
        var agents = scene.AgentDots;
        if (layout.DotsY is not { } offset || ctx.Size < 64 || agents.Count == 0 || scene.Tier == SpeedTier.Idle || unavailable)
        {
            return;
        }

        var dots = OrbDotsLayout.Create(agents.Count + scene.AgentOverflow, ctx, offset);
        var pulse = scene.Effects.AuraBreathing && scene.Effects.SlideTransitions;
        for (var i = 0; i < Math.Min(dots.DotCount, agents.Count); i++)
        {
            var source = agents[i];
            var color = ctx.HighContrast ? ctx.Contrast.WindowText
                : ctx.Dark ? HeatPalette.SourceColor(source) : OkLab.Darken(HeatPalette.SourceColor(source), 0.12);
            var alpha = pulse ? 0.62 + 0.38 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (time * 0.9 + i * 0.19))) : 1;
            var x = dots.CenterX(i);
            if (layout.DotHalo && !ctx.HighContrast)
            {
                list.Add(OrbGpuInstance.Circle(x, dots.Y, dots.Diameter / 2 + 1.2 * ctx.Unit, ctx.Dark ? DarkDotHalo : LightDotHalo));
            }

            list.Add(OrbGpuInstance.Circle(x, dots.Y, dots.Diameter / 2, color.WithAlpha(alpha), glow: ctx.Dark && !ctx.HighContrast ? dots.Diameter * 0.7 : 0,
                glowAlpha: 0.45));
        }
    }

    /// <summary>The ring Settings → 懸浮窗 draws around an orb whose row is hovered, selected or focused (DESIGN 2b).</summary>
    public static void Highlight(OrbDrawList list, OrbGpuContext ctx, Rgba accent, double amount)
    {
        if (amount <= 0.004)
        {
            return;
        }

        list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, ctx.Size / 2 + 5, 2, accent.WithAlpha(0.6 * Math.Clamp(amount, 0, 1))));
    }
}

/// <summary>Positions of the agent dots (max 6, then 5 dots and <c>+n</c>), in canvas points.</summary>
public readonly record struct OrbDotsLayout(int DotCount, int Overflow, double Diameter, double Spacing, double Y, double StartX)
{
    /// <summary>Room reserved for the <c>+n</c> label.</summary>
    public static double OverflowWidth(double size) => 0.075 * size;

    public static OrbDotsLayout Create(int count, OrbGpuContext ctx, double offset)
    {
        var overflow = count > 6 ? count - 5 : 0;
        var dots = overflow > 0 ? 5 : count;
        var unit = ctx.Unit;
        var diameter = 4 * unit;
        var spacing = 3.2 * unit;
        var total = dots * diameter + Math.Max(0, dots - 1) * spacing + (overflow > 0 ? spacing + OverflowWidth(ctx.Size) : 0);
        return new OrbDotsLayout(dots, overflow, diameter, spacing, ctx.CenterY + offset * ctx.Size, ctx.CenterX - total / 2);
    }

    public double CenterX(int index) => StartX + index * (Diameter + Spacing) + Diameter / 2;

    /// <summary>Leading edge of the <c>+n</c> label.</summary>
    public double OverflowX => StartX + DotCount * (Diameter + Spacing);
}
