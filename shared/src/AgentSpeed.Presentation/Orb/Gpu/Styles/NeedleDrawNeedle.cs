using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>The needle, its peg sparks and the hub (DESIGN 2a.2 layers 9–11).</summary>
public sealed partial class NeedleDraw
{
    private const double SparkTailAlpha = 0.35;
    private const double HubShadowRadius = 0.96;

    private static readonly Rgba DarkHubRimTop = Rgba.White.WithAlpha(0.2);
    private static readonly Rgba DarkHubRimBottom = Rgba.White.WithAlpha(0.03);
    private static readonly Rgba LightHubRimTop = Rgba.White;
    private static readonly Rgba LightHubRimBottom = Rgba.Black.WithAlpha(0.1);

    /// <summary>
    /// Layer 9: the glow (dark) or drop shadow (light), the tapered blade, its spine and the pulse flare at the tip. The
    /// blade's counterweight hides under the hub.
    /// </summary>
    private static void AddNeedle(OrbDrawList list, OrbGpuContext ctx, NeedleModel m, bool hc, bool thumbnail)
    {
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var angle = m.NeedleDegrees;
        var tip = NeedleGeometry.NeedleTip * s;
        var tail = -NeedleGeometry.NeedleTail * s;
        var pivotHalf = NeedleGeometry.NeedleBaseWidth / 2 * s;
        var tipHalf = NeedleGeometry.NeedleTipWidth / 2 * s;
        var tailHalf = NeedleGeometry.NeedleTailWidthScale * pivotHalf;
        var palette = m.Palette;
        if (hc)
        {
            list.Add(OrbGpuInstance.Blade(cx, cy, angle, tip, tail, pivotHalf, tipHalf, tailHalf, ctx.Contrast.WindowText));
            return;
        }

        if (m.NeedleGlowSigma > 0 && m.NeedleGlowOpacity > 0.002)
        {
            var sigma = m.NeedleGlowSigma * s;
            var glow = m.NeedleGlowColor.WithAlpha(Math.Min(1, m.NeedleGlowOpacity) * StrokeGlow(pivotHalf + tipHalf, sigma));
            list.Add(OrbGpuInstance.Blade(cx, cy, angle, tip, tail, pivotHalf, tipHalf, tailHalf, glow, core: 0, glow: OrbGpuInstance.Glow(sigma),
                glowAlpha: 1), ctx.GlowBlend);
        }
        else if (m.NeedleShadowVisible)
        {
            list.Add(OrbGpuInstance.Blade(cx + palette.NeedleShadowOffsetX * s, cy + palette.NeedleShadowOffsetY * s, angle, tip, tail, pivotHalf,
                tipHalf, tailHalf, palette.NeedleShadow, glow: OrbGpuInstance.Glow(palette.NeedleShadowSigma * s), glowAlpha: 1));
        }

        list.Add(OrbGpuInstance.Blade(cx, cy, angle, tip, tail, pivotHalf, tipHalf, tailHalf, m.NeedleFill));

        var radians = angle * Math.PI / 180;
        var ux = Math.Cos(radians);
        var uy = Math.Sin(radians);
        var from = NeedleGeometry.SpineStart * s;
        var to = tip - tipHalf;
        list.Add(OrbGpuInstance.Capsule(cx + ux * from, cy + uy * from, cx + ux * to, cy + uy * to, Math.Max(0.6, NeedleGeometry.SpineWidth * s),
            m.Spine));

        if (!thumbnail && m.FlareSpotOpacity > 0.004)
        {
            var radius = NeedleGeometry.FlareSpotRadius * s;
            var spot = palette.FlareSpot;
            list.Add(OrbGpuInstance.Circle(cx + ux * tip, cy + uy * tip, radius, spot.WithAlpha(m.FlareSpotOpacity))
                .RadialGradient(spot.WithAlpha(0), radius), ctx.GlowBlend);
        }
    }

    /// <summary>Layer 10: streaks from the peg contact, white-hot (light amber) cooling to heat(0.95).</summary>
    private static void AddSparks(OrbDrawList list, OrbGpuContext ctx, NeedleModel m)
    {
        var sparks = m.Sparks;
        var s = ctx.Size;
        for (var i = 0; i < sparks.Length; i++)
        {
            ref readonly var spark = ref sparks[i];
            var (hx, hy) = ctx.At(spark.X, spark.Y);
            var color = spark.Color.WithAlpha(spark.Alpha);
            list.Add(OrbGpuInstance.Capsule(hx + spark.StreakX * s, hy + spark.StreakY * s, hx, hy, spark.Width, color,
                color.WithAlpha(spark.Alpha * SparkTailAlpha)), ctx.GlowBlend);
        }
    }

    /// <summary>Layer 11: drop shadow, gradient disc, inner rim light and the 135° chrome bezel as four quarter arcs.</summary>
    private static void AddHub(OrbDrawList list, OrbGpuContext ctx, NeedlePalette palette, bool hc)
    {
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        var r = NeedleGeometry.HubRadius * s;
        if (hc)
        {
            list.Add(OrbGpuInstance.Circle(cx, cy, r, ctx.Contrast.Window));
            list.Add(OrbGpuInstance.Ring(cx, cy, r, 1.5, ctx.Contrast.WindowText));
            return;
        }

        list.Add(OrbGpuInstance.Circle(cx, cy + NeedleGeometry.HubShadowOffsetY * s, r * HubShadowRadius, palette.HubShadow,
            glow: OrbGpuInstance.Glow(NeedleGeometry.HubShadowSigma * s), glowAlpha: 1));
        list.Add(OrbGpuInstance.Circle(cx, cy, r, palette.HubTop).LinearGradient(palette.HubBottom, r));

        var dark = palette.Dark;
        list.Add(OrbGpuInstance.Ring(cx, cy, r - 0.004 * s, Math.Max(1, 0.008 * s), dark ? DarkHubRimTop : LightHubRimTop)
            .LinearGradient(dark ? DarkHubRimBottom : LightHubRimBottom, r));

        var width = Math.Max(1.2, NeedleGeometry.HubBezelWidth * s);
        var first = palette.BezelStart;
        var middle = palette.BezelMiddle;
        var last = palette.BezelEnd;
        list.Add(OrbGpuInstance.BandArc(cx, cy, r, width, 225, 90, middle, first));
        list.Add(OrbGpuInstance.BandArc(cx, cy, r, width, 315, 90, last, middle));
        list.Add(OrbGpuInstance.BandArc(cx, cy, r, width, 45, 90, middle, last));
        list.Add(OrbGpuInstance.BandArc(cx, cy, r, width, 135, 90, first, middle));
    }
}
