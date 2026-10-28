using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>The moving parts of the <c>led</c> instrument: redline edge glow and heat haze, the comets and the response waves.</summary>
public sealed partial class LedDraw
{
    private const int HazeStrands = 9;
    private const int HazeSteps = 6;
    private const int MaxWaves = 4;

    private static readonly double[] HazeSeeds = BuildHazeSeeds();

    /// <summary>
    /// The scene's eased redline amount (0..1), recovered from <see cref="OrbScene.EdgeGlow"/>, which is that amount times
    /// <c>0.3 + 0.2 sin(2π 2.2 t)</c> while the aura breathes and times 0.25 otherwise.
    /// </summary>
    internal static double RedlineAmount(OrbScene scene)
    {
        var glow = scene.EdgeGlow;
        if (glow <= 0)
        {
            return 0;
        }

        var wave = scene.Effects.AuraBreathing ? 0.3 + 0.2 * Math.Sin(2 * Math.PI * 2.2 * scene.Time) : 0.25;
        return StyleMath.Clamp01(glow / wave);
    }

    /// <summary>Max effects at redline: faint rising heat waves above the ring.</summary>
    private void AddHeatHaze(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, double redline)
    {
        if (!frame.Effects.HeatDistortion || redline <= 0.01)
        {
            return;
        }

        var s = ctx.Size;
        var t = frame.Time;
        var color = _dark ? HeatPalette.WhiteHot : HeatPalette.LightSparkHot;
        var width = 1.4 * ctx.Unit;
        var top = ctx.CenterY - 0.74 * s;
        const double step = 1.0 / HazeSteps;
        for (var index = 0; index < HazeStrands; index++)
        {
            var seed = HazeSeeds[index];
            var offset = (index / (double)(HazeStrands - 1) - 0.5) * 0.62;
            var x0 = ctx.CenterX + offset * s;
            var bottom = ctx.CenterY - Math.Sqrt(Math.Max(0, 0.25 - offset * offset)) * s - 2;
            if (bottom <= top + 4)
            {
                continue;
            }

            var flicker = 0.5 + 0.5 * Math.Sin(t * (5 + seed * 3) + seed * 20);
            var alpha = (_dark ? 0.08 : 0.1) * redline * (0.4 + 0.6 * flicker);
            var previousX = x0;
            var previousY = bottom;
            for (var k = 1; k <= HazeSteps; k++)
            {
                var v = k * step;
                var y = bottom - (bottom - top) * v;
                var x = x0 + Math.Sin(t * (3.1 + seed * 2) + v * 9 + seed * 12) * 0.018 * s * v;
                var fade = 1 - (v - step);
                list.Add(OrbGpuInstance.Capsule(previousX, previousY, x, y, width, color.WithAlpha(alpha * fade),
                    color.WithAlpha(alpha * (fade + step))), ctx.GlowBlend);
                previousX = x;
                previousY = y;
            }
        }
    }

    /// <summary>Redline: a pulsing glow along the outer edge of the lit arc, brightest toward the leading edge.</summary>
    private void AddEdgeGlow(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbFrame frame, double shown, double overdrive,
        double redline)
    {
        var alpha = scene.EdgeGlow * frame.Effects.Bloom;
        if (redline <= 0.01 || alpha <= 0.004 || shown <= 0.002)
        {
            return;
        }

        var heat = HeatPalette.Sample(frame.Heat);
        var color = _dark
            ? OkLab.Mix(heat, HeatPalette.WhiteHot, 0.7 + 0.3 * overdrive)
            : OkLab.Mix(heat, HeatPalette.LightSparkHot, 0.25 * overdrive);
        color = color.WithAlpha(alpha);
        var s = ctx.Size;
        var sweep = GaugeLayout.AngleForFraction(shown) - GaugeLayout.StartDegrees;
        list.Add(OrbGpuInstance.Arc(ctx.CenterX, ctx.CenterY, 0.447 * s, 0.012 * s, GaugeLayout.StartDegrees, sweep, color, color,
            glow: 0.02 * s, glowAlpha: 0.8), ctx.GlowBlend);
    }

    /// <summary>Dims the comet while it passes the gap at the bottom of the gauge.</summary>
    private static double CometGapFade(double degrees)
    {
        var delta = (degrees - 90) % 360;
        if (delta > 180)
        {
            delta -= 360;
        }
        else if (delta < -180)
        {
            delta += 360;
        }

        return 0.3 + 0.7 * StyleMath.SmoothStep(18, 52, Math.Abs(delta));
    }

    /// <summary>
    /// The comet (DESIGN 2.1): a tapered arc of <c>40° + 60° h</c> (heat → white-hot head) travelling just outside the gauge, and
    /// from <c>h &gt; 0.55</c> (Standard and Max) a second one half a turn behind at 70 %.
    /// </summary>
    private void AddComets(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbFrame frame)
    {
        var opacity = scene.CometOpacity;
        if (opacity <= 0.003)
        {
            return;
        }

        var tail = 40 + 60 * StyleMath.Clamp01(frame.Heat);
        AddComet(list, ctx, frame, scene.CometDegrees, tail, opacity);
        if (_effects.Gates.Motion && frame.Heat > 0.55)
        {
            AddComet(list, ctx, frame, scene.CometDegrees - 180, tail, opacity * 0.7);
        }
    }

    private void AddComet(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, double head, double tail, double opacity)
    {
        var alpha = opacity * CometGapFade(head);
        var s = ctx.Size;
        var bloom = frame.Effects.Bloom;
        var heat = ctx.Label(Math.Max(frame.Heat, 0.1));
        var hot = _dark ? HeatPalette.WhiteHot : heat;
        var radius = GaugeLayout.CometRadius * s;
        list.Add(OrbGpuInstance.Arc(ctx.CenterX, ctx.CenterY, radius, 0.0125 * s, head - tail, tail, hot.WithAlpha(alpha),
            heat.WithAlpha(alpha * 0.6), glow: 0.012 * s * bloom, glowAlpha: 0.55), ctx.GlowBlend);
        var (x, y) = ctx.Polar(head, GaugeLayout.CometRadius);
        list.Add(OrbGpuInstance.Circle(x, y, 0.0085 * s, hot.WithAlpha(alpha), glow: 0.022 * s * bloom, glowAlpha: 0.8), ctx.GlowBlend);
    }

    /// <summary>
    /// The response waves (DESIGN 2.1, layer 7; prototype "shockwaves"): a Gaussian ring from the rim to 0.74 S in the heat of the
    /// response, mixed 30 % toward white-hot in dark, and a soft wake ring 3.5 × wider to 0.66 S, over 0.85 s; the newest four.
    /// With Reduce Motion the ring fades in place.
    /// </summary>
    private void AddWaves(OrbDrawList list, OrbGpuContext ctx, OrbScene scene)
    {
        var count = scene.PulseCount;
        var s = ctx.Size;
        var expands = scene.Effects.PulseExpands;
        for (var i = Math.Max(0, count - MaxWaves); i < count; i++)
        {
            var wave = scene.PulseAt(i);
            var x = wave.Progress;
            if (x >= 1)
            {
                continue;
            }

            var strength = Math.Clamp(wave.Strength, 0.3, 1);
            var heat = HeatPalette.Sample(wave.Heat);
            var ring = _dark ? Rgba.Lerp(heat, HeatPalette.WhiteHot, 0.3) : Ink(heat);
            var width = (0.005 + 0.005 * strength) * (1 - x) + 0.001;
            if (!expands)
            {
                var fade = Math.Sin(Math.PI * x) * 0.5 * strength * (_dark ? 1 : 0.6);
                list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, 0.53 * s, 0, ring.WithAlpha(fade), core: 0, glow: 0.006 * s, glowAlpha: 1),
                    ctx.GlowBlend);
                continue;
            }

            var radius = 0.5 + 0.24 * Easing.OutCubic(x);
            list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, radius * s, 0, ring.WithAlpha((1 - x) * 0.7 * strength * (_dark ? 1 : 0.6)),
                core: 0, glow: width * s, glowAlpha: 1), ctx.GlowBlend);
            var wake = 0.5 + 0.16 * Easing.OutCubic(Math.Min(1, x * 1.3));
            list.Add(OrbGpuInstance.Ring(ctx.CenterX, ctx.CenterY, wake * s, 0, (_dark ? heat : Ink(heat)).WithAlpha((1 - x) * 0.3 * strength),
                core: 0, glow: width * 3.5 * s, glowAlpha: 1), ctx.GlowBlend);
        }
    }

    /// <summary>The prototype's porcelain ink: <c>pow(c, 1.55)</c> deepens a color without muddying it.</summary>
    internal static Rgba Ink(Rgba color) => new(Math.Pow(color.R, 1.55), Math.Pow(color.G, 1.55), Math.Pow(color.B, 1.55), color.A);

    private static double[] BuildHazeSeeds()
    {
        var seeds = new double[HazeStrands];
        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = Hash(i, 91);
        }

        return seeds;
    }

    /// <summary>Deterministic noise in 0..1 for a pair of keys (the macOS <c>Noise.hash</c>).</summary>
    private static double Hash(int a, int b)
    {
        var x = unchecked((ulong)((long)a * 73_856_093 ^ (long)b * 19_349_663));
        x ^= x >> 33;
        x = unchecked(x * 0xff51afd7ed558ccdUL);
        x ^= x >> 33;
        x = unchecked(x * 0xc4ceb9fe1a85ec53UL);
        x ^= x >> 33;
        return x % 10_000 / 10_000.0;
    }
}
