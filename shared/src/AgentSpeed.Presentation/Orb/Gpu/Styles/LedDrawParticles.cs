using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>led</c> style's sparks (streaks), embers and new-peak burst (DESIGN 2.1, layer 5; prototype render loop). Sizes are
/// points at every S, as in the prototype.
/// </summary>
public sealed partial class LedDraw
{
    private static readonly Rgba GoldSpark = new(1, 0.82, 0.35);

    private void AddParticles(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame)
    {
        var particles = _effects.Particles.Particles;
        if (particles.IsEmpty)
        {
            return;
        }

        var s = ctx.Size;
        var blend = ctx.GlowBlend;
        var dark = _dark;
        for (var i = 0; i < particles.Length; i++)
        {
            ref readonly var p = ref particles[i];
            var life = (double)p.Remaining;
            var r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            var fade = StyleMath.SmoothStep(OuterRadius, 0.66, r) * ReadoutZone.Fade(p.X, p.Y - frame.ReadoutShift);
            var a = Math.Min(1, life * 1.4) * (p.Kind == LedParticleKind.Ember ? 0.8 : 1) * fade;
            if (a <= 0.004)
            {
                continue;
            }

            var color = ParticleColor(p, life, dark);
            var x = ctx.CenterX + p.X * s;
            var y = ctx.CenterY + p.Y * s;
            if (p.Streak)
            {
                var speed = Math.Sqrt(p.VelocityX * p.VelocityX + p.VelocityY * p.VelocityY);
                var fadeV = Math.Min(1, speed / 0.12);
                var seconds = Math.Max(0.02, Math.Min(0.06, 0.06 * fadeV));
                var alpha = a * (0.25 + 0.75 * fadeV);
                var tailX = x - p.VelocityX * seconds * s;
                var tailY = y - p.VelocityY * seconds * s;
                list.Add(OrbGpuInstance.Capsule(tailX, tailY, x, y, p.Size * (dark ? 0.75 : 1), color.WithAlpha(alpha), color.WithAlpha(0)),
                    blend);
                if (dark)
                {
                    list.Add(OrbGpuInstance.Circle(x, y, p.Size * 0.55, Rgba.Lerp(color, Rgba.White, 0.6).WithAlpha(alpha)), blend);
                }

                continue;
            }

            var radius = p.Size * (dark ? 0.8 : 1) * (0.55 + 0.45 * Math.Sin(p.Age * 18 + p.Seed));
            var center = dark ? Rgba.Lerp(color, Rgba.White, 0.5) : color;
            list.Add(OrbGpuInstance.Circle(x, y, radius * 2.2, center.WithAlpha(a)).RadialStops(color.WithAlpha(a * 0.55), 0.3), blend);
        }
    }

    /// <summary>Dark: heat toward white-hot as the particle ages; light: heat darkened 10 %, amber for the hottest.</summary>
    private static Rgba ParticleColor(in LedParticle p, double life, bool dark)
    {
        if (p.Kind == LedParticleKind.Gold)
        {
            return GoldSpark;
        }

        var heat = HeatPalette.Sample(p.Heat);
        if (dark)
        {
            return Rgba.Lerp(heat, HeatPalette.WhiteHot, Math.Min(1, (1 - life) * 0.3 + 0.4));
        }

        return p.Heat > 0.92 ? HeatPalette.LightSparkHot : Scaled(heat, 0.9);
    }
}
