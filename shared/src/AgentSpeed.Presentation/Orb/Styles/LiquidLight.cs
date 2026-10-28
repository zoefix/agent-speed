using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Which layers lie under a light layer of the liquid (back to front, DESIGN 2a.3).</summary>
public enum LiquidLightStage
{
    /// <summary>The inner glow: on the liquid body and its spherical shade.</summary>
    InnerGlow,

    /// <summary>Currents, the gold band and particles: also on the inner glow and the readout scrim.</summary>
    AboveScrim,
}

/// <summary>
/// Additive light on the liquid without a blend mode (DESIGN 2a.3: spill, inner glow, currents, motes, splashes, steam,
/// droplets and the gold band are additive in dark). Retained-mode WPF only blends "over", which mixes a light toward its
/// own color instead of adding it, so a gold mote on gold liquid read as a pale dot and the liquid as muddy. Drawing a
/// light <c>c</c> at alpha <c>a</c> "over" in the color <c>b + c / k</c> at alpha <c>k a</c>, where <c>b</c> is what lies
/// under it, gives <c>b (1 − k a) + k a (b + c / k) = b + a c</c>, the additive result; <c>k ≥ 1</c> is the smallest
/// factor that keeps every channel of the color within 1. <see cref="LightBase"/> evaluates <c>b</c> from the same layer
/// formulas the renderer draws.
/// </summary>
public sealed partial class LiquidModel
{
    /// <summary>The dark body (layer 2): <c>#181D2B</c> at the top of the disc to <c>#07090D</c> at the bottom.</summary>
    public static readonly Rgba DarkBodyTop = Rgba.FromHex("#181D2B");

    public static readonly Rgba DarkBodyBottom = Rgba.FromHex("#07090D");

    public static readonly Rgba LightBodyTop = Rgba.FromHex("#FFFFFF");

    public static readonly Rgba LightBodyBottom = Rgba.FromHex("#E4E8F0");

    /// <summary>
    /// The color and alpha to draw a light (<paramref name="light"/> with its alpha) "over" so it adds like the
    /// prototype's additive blending at (x, y) (S, from the center). The alpha may exceed 1: scale it (a gradient, a
    /// sprite profile, an opacity) before clamping. In light appearance lights blend normally and come back unchanged.
    /// </summary>
    public Rgba LitOver(Rgba light, double x, double y, LiquidLightStage stage = LiquidLightStage.AboveScrim)
    {
        if (!_dark || _highContrast)
        {
            return light;
        }

        var b = LightBase(x, y, stage);
        var k = Math.Max(1, Math.Max(Ratio(light.R, b.R), Math.Max(Ratio(light.G, b.G), Ratio(light.B, b.B))));
        return new Rgba(b.R + light.R / k, b.G + light.G / k, b.B + light.B / k, light.A * k);

        static double Ratio(double add, double under) => add / Math.Max(1e-3, 1 - under);
    }

    /// <summary>The opaque color under a light layer at (x, y) in dark appearance (S, from the center).</summary>
    public Rgba LightBase(double x, double y, LiquidLightStage stage)
    {
        var t = Math.Clamp(y + 0.5, 0, 1);
        var color = Rgba.Lerp(DarkBodyTop, DarkBodyBottom, t).WithAlpha(1);
        color = Over(Rgba.Black, VolumeShadeAlpha(x, y), color);

        var surface = SurfaceY(x);
        if (y >= surface)
        {
            var top = BodyGradientTopY;
            var u = Math.Clamp((y - top) / Math.Max(1e-6, BodyGradientBottomY - top), 0, 1);
            var liquid = Rgba.Lerp(_bodyTop, _bodyBottom, u);
            color = Over(liquid.WithAlpha(1), liquid.A, color);
            color = Over(Rgba.Black, SphericalShadeColor.A * ShadeFraction(x, y), color);
            if (stage == LiquidLightStage.AboveScrim)
            {
                var glow = _innerGlow;
                var dx = x;
                var dy = y - InnerGlowCenterY;
                var reach = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / LiquidGeometry.InnerGlowRadius);
                color = Add(color, glow, glow.A * reach);
            }
        }
        else
        {
            var spillTop = MeanSurfaceY - LiquidGeometry.SpillHeight;
            if (y >= spillTop)
            {
                color = Add(color, _spill, _spill.A * Math.Clamp((y - spillTop) / LiquidGeometry.SpillHeight, 0, 1));
            }
        }

        if (stage == LiquidLightStage.AboveScrim)
        {
            color = Over(Rgba.Black, ScrimOpacity * ScrimFraction(x, y), color);
        }

        return color;
    }

    /// <summary>Layer 2 volume shading: clear to 75 % of 0.5 S (8 % there), black 42 % at the disc edge.</summary>
    private static double VolumeShadeAlpha(double x, double y)
    {
        var d = Math.Sqrt(x * x + y * y) / 0.5;
        return d <= 0.75 ? 0.08 * Math.Max(0, d - 0.1) / 0.65 : 0.08 + (0.42 - 0.08) * Math.Min(1, (d - 0.75) / 0.25);
    }

    private static double ShadeFraction(double x, double y)
    {
        var dx = x - LiquidGeometry.ShadeCenterX;
        var dy = y - LiquidGeometry.ShadeCenterY;
        var inner = LiquidGeometry.ShadeInner * R;
        return Math.Clamp((Math.Sqrt(dx * dx + dy * dy) - inner) / (R - inner), 0, 1);
    }

    /// <summary>The scrim's alpha profile: 35 % at the center → 30 % at 55 % → 0 at the ellipse.</summary>
    private static double ScrimFraction(double x, double y)
    {
        var kx = x / LiquidGeometry.ScrimRadiusX;
        var ky = (y - LiquidGeometry.ScrimCenterY) / LiquidGeometry.ScrimRadiusY;
        var k = Math.Sqrt(kx * kx + ky * ky);
        return k >= 1 ? 0 : k < 0.55 ? 0.35 + (0.30 - 0.35) * k / 0.55 : 0.30 * (1 - (k - 0.55) / 0.45);
    }

    private static Rgba Over(Rgba top, double alpha, Rgba under)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return new Rgba(
            top.R * alpha + under.R * (1 - alpha),
            top.G * alpha + under.G * (1 - alpha),
            top.B * alpha + under.B * (1 - alpha));
    }

    private static Rgba Add(Rgba under, Rgba light, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return new Rgba(
            Math.Min(1, under.R + light.R * alpha),
            Math.Min(1, under.G + light.G * alpha),
            Math.Min(1, under.B + light.B * alpha));
    }
}
