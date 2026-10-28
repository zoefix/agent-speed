using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>The liquid's particles (DESIGN 2a.3 layer 7) and its glass (layer 9).</summary>
public sealed partial class LiquidDraw
{
    /// <summary>
    /// Cool motes are glass bubbles (a ring and a highlight dot, normal blending), hot ones luminous motes (a core with a
    /// Gaussian halo, additive in dark); bubbles go first so a frame needs two batches whatever the mix.
    /// </summary>
    private void AddMotes(OrbDrawList list, OrbGpuContext ctx, LiquidModel m)
    {
        var motes = m.Motes;
        if (motes.Length == 0)
        {
            return;
        }

        var s = ctx.Size;
        var dark = ctx.Dark;
        var ringAlpha = dark ? 0.5 : 0.85;
        var spotAlpha = dark ? 0.6 : 0.95;
        for (var i = 0; i < motes.Length; i++)
        {
            ref readonly var p = ref motes[i];
            var hot = p.Kind == LiquidSpriteKind.GoldMote ? 1 : p.Hot;
            if (hot >= 1)
            {
                continue;
            }

            var (x, y) = ctx.At(p.X, p.Y);
            var size = p.Radius * s;
            var cool = (1 - hot) * p.Alpha;
            list.Add(OrbGpuInstance.Ring(x, y, size, Math.Max(0.6, size * 0.28), Rgba.White.WithAlpha(ringAlpha * cool)));
            list.Add(OrbGpuInstance.Circle(x - size * 0.35, y - size * 0.35, Math.Max(0.4, size * 0.28), Rgba.White.WithAlpha(spotAlpha * cool)));
        }

        var peakAlpha = dark ? 0.95 : 0.9;
        var rimAlpha = dark ? 0.4 : 0.45;
        for (var i = 0; i < motes.Length; i++)
        {
            ref readonly var p = ref motes[i];
            var gold = p.Kind == LiquidSpriteKind.GoldMote;
            var hot = gold ? 1 : p.Hot;
            if (hot <= 0)
            {
                continue;
            }

            var (x, y) = ctx.At(p.X, p.Y);
            var size = p.Radius * s;
            var color = gold ? HeatPalette.Gold : _moteColor;
            var strength = hot * p.Alpha;
            list.Add(OrbGpuInstance.Circle(x, y, size * 0.7, color.WithAlpha(peakAlpha * strength), glow: size * 0.9, glowAlpha: 1)
                .RadialGradient(color.WithAlpha(rimAlpha * strength), size * 0.7), ctx.GlowBlend);
        }
    }

    /// <summary>Pops at the surface, splash droplets, steam wisps and the falling pulse droplets.</summary>
    private static void AddSurfaceParticles(OrbDrawList list, OrbGpuContext ctx, LiquidModel m)
    {
        var s = ctx.Size;
        var blend = ctx.GlowBlend;
        foreach (ref readonly var p in m.Pops)
        {
            var (x, y) = ctx.At(p.X, p.Y);
            list.Add(OrbGpuInstance.Ring(x, y, p.Radius * s, 0.8, p.Color.WithAlpha(p.Alpha)));
        }

        foreach (ref readonly var p in m.Splashes)
        {
            var (x, y) = ctx.At(p.X, p.Y);
            list.Add(OrbGpuInstance.Circle(x, y, p.Radius * s, p.Color.WithAlpha(p.Alpha)), blend);
        }

        foreach (ref readonly var p in m.Steam)
        {
            var (x, y) = ctx.At(p.X, p.Y);
            var radius = p.Radius * s;
            list.Add(OrbGpuInstance.Circle(x, y, radius, p.Color.WithAlpha(p.Alpha)).RadialGradient(p.Color.WithAlpha(0), radius), blend);
        }

        foreach (ref readonly var p in m.Droplets)
        {
            var (x, y) = ctx.At(p.X, p.Y);
            var radius = p.Radius * s;
            list.Add(OrbGpuInstance.Blade(x, y, -90, radius * 2.2, 0, radius, radius * 0.12, radius, p.Color.WithAlpha(p.Alpha)), blend);
        }
    }

    /// <summary>The glass wall, the bottom caustic, the specular highlight and the rim glint.</summary>
    private static void AddGlass(OrbDrawList list, OrbGpuContext ctx, LiquidModel m, bool hc, bool thumbnail)
    {
        var s = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY;
        list.Add(OrbGpuInstance.Ring(cx, cy, R * s, Math.Max(1, LiquidGeometry.WallWidth * s), hc ? ctx.Contrast.GrayText : m.GlassWallColor));
        if (hc || thumbnail)
        {
            return;
        }

        var caustic = m.CausticColor;
        if (caustic.A > 0.004)
        {
            list.Add(OrbGpuInstance.BandArc(cx, cy, LiquidGeometry.CausticRadius * s, LiquidGeometry.CausticWidth * s,
                LiquidGeometry.CausticStartDegrees, LiquidGeometry.CausticEndDegrees - LiquidGeometry.CausticStartDegrees, caustic, core: 0.55,
                glow: OrbGpuInstance.Glow(LiquidGeometry.CausticBlur * s), glowAlpha: 0.7), ctx.GlowBlend);
        }

        var specular = m.SpecularColor;
        var (sx, sy) = ctx.At(LiquidGeometry.SpecularX, LiquidGeometry.SpecularY);
        list.Add(OrbGpuInstance.Ellipse(sx, sy, LiquidGeometry.SpecularRadiusX * s, LiquidGeometry.SpecularRadiusY * s,
            LiquidGeometry.SpecularRotationDegrees, specular).RadialGradient(specular.WithAlpha(0), 0.24 * s));

        list.Add(OrbGpuInstance.BandArc(cx, cy, LiquidGeometry.GlintRadius * s, Math.Max(1, LiquidGeometry.GlintWidth * s),
            LiquidGeometry.GlintStartDegrees, LiquidGeometry.GlintEndDegrees - LiquidGeometry.GlintStartDegrees, m.GlintColor, roundCaps: true));
    }
}
