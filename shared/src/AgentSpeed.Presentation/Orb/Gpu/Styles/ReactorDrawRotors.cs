using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>The moving parts of the <c>reactor</c>: rotors A–C (layers 5–7), the pulse flare (9) and lightning (8).</summary>
public sealed partial class ReactorDraw
{
    private const int BoltSegments = ReactorModel.BoltPointCount - 1;

    /// <summary>Rotor A: three arcs 120° apart, a tail-to-head gradient each, over a glow in dark.</summary>
    private void AddRotorA(OrbDrawList list, OrbGpuContext ctx, bool effects, double glowScale)
    {
        var m = _model;
        var s = ctx.Size;
        var width = m.ArcAWidth * s;
        var length = m.ArcADegrees;
        var head = m.ArcAHeadColor;
        var tail = m.ArcATailColor;
        var glow = m.ArcAGlowColor;
        var floor = TailFloor(head, tail);
        var glowAlpha = 0.0;
        var glowRadius = 0.0;
        if (effects && glow.A > 0.004)
        {
            var sigma = m.ArcAGlowSigma * s;
            glowAlpha = glow.A * glowScale * StrokeGlow(width, sigma);
            glowRadius = OrbGpuInstance.Glow(sigma);
        }

        for (var k = 0; k < ReactorGeometry.RotorAArcs; k++)
        {
            var end = m.RotorADegrees + 120.0 * k;
            if (glowAlpha > 0.004)
            {
                var color = glow.WithAlpha(glowAlpha);
                list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, ReactorGeometry.RotorARadius * s, width, end - length, length,
                    color, color, startAlpha: floor, core: 0, glow: glowRadius, glowAlpha: 1), ctx.GlowBlend);
            }

            AddArc(list, ctx, ReactorGeometry.RotorARadius * s, width, end, length, head, tail);
        }
    }

    /// <summary>Rotor C: two thin arcs 180° apart, no glow.</summary>
    private void AddRotorC(OrbDrawList list, OrbGpuContext ctx)
    {
        var m = _model;
        var s = ctx.Size;
        for (var k = 0; k < ReactorGeometry.RotorCArcs; k++)
        {
            AddArc(list, ctx, ReactorGeometry.RotorCRadius * s, m.ArcCWidth * s, m.RotorCDegrees + 180.0 * k, m.ArcCDegrees,
                m.ArcCHeadColor, m.ArcCTailColor);
        }
    }

    /// <summary>
    /// One rotor arc ending at <paramref name="end"/>: the model's tail color already carries the tail's alpha floor, so the
    /// band's own alpha ramp stays at 1. High contrast: the arc in the text color with its head in the highlight color.
    /// </summary>
    private static void AddArc(OrbDrawList list, OrbGpuContext ctx, double radius, double width, double end, double length, Rgba head,
        Rgba tail)
    {
        if (ctx.HighContrast)
        {
            var lead = Math.Min(length, Math.Max(4, length * 0.25));
            list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, end - length, length, ctx.Contrast.WindowText));
            list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, end - lead, lead, ctx.Contrast.Highlight));
            return;
        }

        list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, end - length, length, head, tail), ctx.GlowBlend);
    }

    /// <summary>Rotor B: the coils, each in its base color blended toward hot by its energy, at its opacity.</summary>
    private void AddRotorB(OrbDrawList list, OrbGpuContext ctx)
    {
        var m = _model;
        var s = ctx.Size;
        var radius = ReactorGeometry.RotorBRadius * s;
        var width = ReactorGeometry.RotorBWidth * s;
        var length = m.CoilArcDegrees;
        var opacities = m.CoilOpacities;
        var hotness = m.CoilHotness;
        var hc = ctx.HighContrast;
        var baseColor = m.CoilColor;
        var hotColor = m.CoilHotColor;
        for (var i = 0; i < opacities.Length; i++)
        {
            var opacity = hc ? Math.Max(0.45, opacities[i]) : opacities[i];
            if (opacity <= 0.004)
            {
                continue;
            }

            var color = hc ? ctx.Contrast.WindowText : hotness[i] > 0 ? Rgba.Lerp(baseColor, hotColor, hotness[i]) : baseColor;
            color = color.WithAlpha(color.A * Math.Min(1, opacity));
            var center = m.CoilAngleDegrees(i);
            list.Add(OrbGpuInstance.BandArc(ctx.CenterX, ctx.CenterY, radius, width, center - length / 2, length, color, color),
                hc ? OrbBlend.Over : ctx.GlowBlend);
        }
    }

    /// <summary>Layer 9: the pulse flare ring from the core rim to the disc edge.</summary>
    private void AddFlare(OrbDrawList list, OrbGpuContext ctx)
    {
        var m = _model;
        if (!m.FlareVisible)
        {
            return;
        }

        var color = m.FlareColor;
        if (color.A <= 0.004)
        {
            return;
        }

        var s = ctx.Size;
        list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, m.FlareRadius * s, m.FlareWidth * s, color), ctx.GlowBlend);
    }

    /// <summary>
    /// Layer 8: each bolt is its 8-segment path stroked three times (glow, body, core), then the sparks at the bolts' far ends.
    /// </summary>
    private void AddLightning(OrbDrawList list, OrbGpuContext ctx)
    {
        var m = _model;
        var bolts = m.Bolts;
        var sparks = m.Sparks;
        if (bolts.IsEmpty && sparks.IsEmpty)
        {
            return;
        }

        var s = ctx.Size;
        var blend = ctx.GlowBlend;
        var color = m.BoltColor;
        var core = m.BoltCoreColor;
        var coreWidth = Math.Max(0.6, ReactorGeometry.BoltCoreWidth * s);
        foreach (var bolt in bolts)
        {
            var alpha = bolt.Alpha;
            if (alpha <= 0.01)
            {
                continue;
            }

            var path = m.BoltPath(bolt);
            AddBoltStroke(list, ctx, path, ReactorGeometry.BoltGlowWidth * s, color.WithAlpha(color.A * 0.28 * alpha), blend);
            AddBoltStroke(list, ctx, path, ReactorGeometry.BoltBodyWidth * s, color.WithAlpha(color.A * 0.75 * alpha), blend);
            AddBoltStroke(list, ctx, path, coreWidth, core.WithAlpha(core.A * alpha), blend);
        }

        foreach (var spark in sparks)
        {
            if (spark.Alpha <= 0.004)
            {
                continue;
            }

            list.Add(OrbGpuInstance.Circle(ctx.CenterX + spark.X * s, ctx.CenterY + spark.Y * s, spark.Radius * s,
                spark.Color.WithAlpha(spark.Color.A * spark.Alpha)), blend);
        }
    }

    private static void AddBoltStroke(OrbDrawList list, OrbGpuContext ctx, ReadOnlySpan<ReactorPoint> path, double width, Rgba color,
        OrbBlend blend)
    {
        var s = ctx.Size;
        var x0 = ctx.CenterX + path[0].X * s;
        var y0 = ctx.CenterY + path[0].Y * s;
        for (var i = 1; i <= BoltSegments; i++)
        {
            var x1 = ctx.CenterX + path[i].X * s;
            var y1 = ctx.CenterY + path[i].Y * s;
            list.Add(OrbGpuInstance.Capsule(x0, y0, x1, y1, width, color), blend);
            x0 = x1;
            y0 = y1;
        }
    }

    /// <summary>The tail's share of the head's alpha (the band's alpha ramp for a glow drawn in one color).</summary>
    private static double TailFloor(Rgba head, Rgba tail) => head.A > 0.001 ? Math.Clamp(tail.A / head.A, 0, 1) : 1;
}
