using System.Numerics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>led</c> style's electric arcs (redline) and digit lightning (DESIGN 2.1): paths stroked like canvas lines in three
/// layers (glow, body, core) with <see cref="OrbShape.Stroke"/> segments, and the radial flash over a crackling digit.
/// </summary>
public sealed partial class LedDraw
{
    private static readonly Rgba ArcBlue = new(0.75, 0.85, 1);
    private static readonly Rgba BoltBlue = new(0.7, 0.82, 1);
    private static readonly Rgba LightBoltGlow = new(0.42, 0.5, 1);
    private static readonly Rgba LightBoltCore = new(0.2, 0.24, 0.85);

    /// <summary>Arcs: 0.022 S at 18 % and 0.008 S at 50 % in the arc color, 0.0032 S at 95 % white (light: the arc color, no white core).</summary>
    private void AddArcs(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame)
    {
        var arcs = _effects.Arcs;
        if (arcs.Count == 0)
        {
            return;
        }

        var heat = HeatPalette.Sample(frame.Heat);
        var color = _dark ? Rgba.Lerp(heat, ArcBlue, 0.5) : Scaled(heat, 0.85);
        var core = _dark ? Rgba.White : color;
        var s = ctx.Size;
        for (var i = 0; i < arcs.Count; i++)
        {
            var fade = arcs.Fade(i);
            if (fade <= 0.004)
            {
                continue;
            }

            AddPath(list, ctx, arcs.Path(i), 0.011 * s, 0.004 * s, 0.0016 * s, color.WithAlpha(fade), core.WithAlpha(fade), 0.18, 0.5, 0.95);
        }
    }

    /// <summary>
    /// Above the readout: each crackling digit's flash (radius 1.44 × its cell width, white in dark, <c>#6B80FF</c> at half
    /// strength in light) and the bolts (5 w at 16 %, 2 w at 45 % in the glow color, 0.8 w at 95 % in the core color; forks
    /// 0.6 w at 80 %).
    /// </summary>
    private void AddLightning(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame)
    {
        var lightning = _effects.Lightning;
        if (lightning.ZapCount == 0 && lightning.BoltCount == 0)
        {
            return;
        }

        var s = ctx.Size;
        var glow = _dark ? Rgba.Lerp(HeatPalette.Sample(Math.Max(frame.Heat, 0.3)), BoltBlue, 0.45) : LightBoltGlow;
        var core = _dark ? Rgba.White : LightBoltCore;
        foreach (var zap in lightning.Zaps)
        {
            var k = zap.Fade;
            var center = (_dark ? Rgba.White : glow).WithAlpha(k * Math.Min(0.8, 0.35 * zap.Power) * (_dark ? 1 : 0.5));
            var middle = glow.WithAlpha(k * 0.3 * Math.Min(1, zap.Power));
            list.Add(OrbGpuInstance.Circle(ctx.CenterX + zap.X * s, ctx.CenterY + zap.Y * s, zap.Radius * 1.6 * s, center).RadialStops(middle, 0.4),
                ctx.GlowBlend);
        }

        var light = _dark ? 1 : 0.9;
        for (var b = 0; b < lightning.BoltCount; b++)
        {
            var alpha = lightning.Alpha(b) * light;
            var w = lightning.Width(b) * s;
            AddPath(list, ctx, lightning.MainPath(b), 2.5 * w, w, 0.4 * w, glow.WithAlpha(alpha), core.WithAlpha(alpha), 0.16, 0.45, 0.95);
            for (var f = 0; f < lightning.ForkCount(b); f++)
            {
                var forkAlpha = alpha * 0.8;
                var fw = w * 0.6;
                AddPath(list, ctx, lightning.ForkPath(b, f), 2.5 * fw, fw, 0.4 * fw, glow.WithAlpha(forkAlpha), core.WithAlpha(forkAlpha), 0.16, 0.45,
                    0.95);
            }
        }
    }

    /// <summary>Strokes a path (S from the center) segment by segment, each cut at the bisectors of its joints.</summary>
    private void AddPath(OrbDrawList list, OrbGpuContext ctx, ReadOnlySpan<LedPoint> path, double glowHalf, double bodyHalf, double coreHalf,
        Rgba color, Rgba core, double glowAlpha, double bodyAlpha, double coreAlpha)
    {
        var s = ctx.Size;
        var additive = _dark;
        var blend = ctx.GlowBlend;
        var last = path.Length - 1;
        for (var i = 0; i < last; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var startCut = i == 0 ? Vector2.Zero : Bisector(path[i - 1], a, b);
            var endCut = i == last - 1 ? Vector2.Zero : Bisector(a, b, path[i + 2]);
            list.Add(OrbGpuInstance.Stroke(ctx.CenterX + a.X * s, ctx.CenterY + a.Y * s, ctx.CenterX + b.X * s, ctx.CenterY + b.Y * s, glowHalf,
                bodyHalf, coreHalf, color, core, glowAlpha, bodyAlpha, coreAlpha, additive, startCut, endCut), blend);
        }
    }

    /// <summary>The direction of the bisector at <paramref name="joint"/>: the mean of the incoming and outgoing directions.</summary>
    internal static Vector2 Bisector(LedPoint before, LedPoint joint, LedPoint after)
    {
        var incoming = Direction(before, joint);
        var outgoing = Direction(joint, after);
        if (incoming == Vector2.Zero || outgoing == Vector2.Zero)
        {
            return incoming == Vector2.Zero ? outgoing : incoming;
        }

        var mean = incoming + outgoing;
        return mean.LengthSquared() < 1e-8f ? outgoing : Vector2.Normalize(mean);
    }

    private static Vector2 Direction(LedPoint from, LedPoint to)
    {
        var d = new Vector2(to.X - from.X, to.Y - from.Y);
        return d.LengthSquared() < 1e-14f ? Vector2.Zero : Vector2.Normalize(d);
    }
}
