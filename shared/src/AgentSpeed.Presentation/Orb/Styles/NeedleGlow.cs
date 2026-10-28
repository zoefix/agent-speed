namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// One translucent stroke of a stacked glow: it reaches <see cref="Extent"/> (in σ) beyond the edge of the glowing
/// stroke (negative: inside it) and is drawn at <see cref="Alpha"/> in the glow color. Layers are drawn widest first.
/// </summary>
public readonly record struct NeedleGlowLayer(double Extent, double Alpha);

/// <summary>
/// Gaussian glows and soft shadows for retained drawings without a blur pass (DESIGN 2a, Frame policy: glows are the
/// analytic Gaussian of the distance, never a blur). A glow of σ around a stroke becomes a few stacked, widening
/// translucent strokes whose composite follows the blurred stroke's profile; a blurred disc becomes radial-gradient
/// stops. Shared by the <c>needle</c> and <c>minimal</c> renderers. Lengths are in units of σ.
/// </summary>
public static class NeedleGlow
{
    public const int MaxLayers = 9;

    /// <summary>The glow reaches this far beyond the stroke edge (the profile is below 0.3 % there).</summary>
    public const double Reach = 2.8;

    private const double Sqrt2 = 1.4142135623730951;

    private static readonly double[] OuterExtents = [Reach, 2.2, 1.65, 1.15, 0.7, 0.3];
    private static readonly double[] InnerExtents = [-0.3, -0.8, -1.3];

    /// <summary>
    /// Opacity of a stroke <paramref name="width"/> wide blurred by a unit Gaussian, at <paramref name="distance"/> from
    /// its center line: <c>½ (erf((x + w/2)/√2) − erf((x − w/2)/√2))</c>.
    /// </summary>
    public static double StrokeProfile(double width, double distance)
    {
        var half = Math.Max(0, width) / 2;
        var x = Math.Abs(distance);
        return 0.5 * (Erf((x + half) / Sqrt2) - Erf((x - half) / Sqrt2));
    }

    /// <summary>
    /// The layers of a glow of <paramref name="opacity"/> around a stroke <paramref name="width"/> σ wide. Without
    /// <paramref name="core"/> the composite matches the profile outside the stroke (an outer glow under an opaque
    /// stroke); with it, inner layers make it match inside the stroke too (a soft shadow, or a glow under a translucent
    /// stroke). Returns the number of layers written.
    /// </summary>
    public static int StrokeLayers(double width, double opacity, bool core, Span<NeedleGlowLayer> layers)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        opacity = StyleMath.Clamp01(opacity);
        var half = width / 2;
        Span<double> extents = stackalloc double[MaxLayers];
        var n = 0;
        foreach (var extent in OuterExtents)
        {
            extents[n++] = extent;
        }

        if (core)
        {
            foreach (var extent in InnerExtents)
            {
                if (half + extent > 0.15)
                {
                    extents[n++] = extent;
                }
            }
        }

        if (layers.Length < n)
        {
            throw new ArgumentException($"At least {n} layers are needed.", nameof(layers));
        }

        Span<double> targets = stackalloc double[MaxLayers];
        for (var j = 0; j < n; j++)
        {
            var outer = half + extents[j];
            var inner = j + 1 < n ? half + extents[j + 1] : core ? 0 : half;
            targets[j] = opacity * StrokeProfile(width, (outer + inner) / 2);
        }

        Span<double> alphas = stackalloc double[MaxLayers];
        StackAlphas(targets[..n], alphas);
        for (var j = 0; j < n; j++)
        {
            layers[j] = new NeedleGlowLayer(extents[j], alphas[j]);
        }

        return n;
    }

    /// <summary>
    /// Alphas of same-colored layers drawn over each other where layer <c>j</c> covers band <c>j</c> and every band
    /// after it (the widest first), so that the composite over band <c>j</c> equals <c>targets[j]</c>. Targets are
    /// clamped to be non-decreasing.
    /// </summary>
    public static void StackAlphas(ReadOnlySpan<double> targets, Span<double> alphas)
    {
        var below = 0.0;
        for (var j = 0; j < targets.Length; j++)
        {
            var target = Math.Clamp(StyleMath.Finite(targets[j]), below, 0.999999);
            alphas[j] = (target - below) / (1 - below);
            below = target;
        }
    }

    /// <summary>The composite alpha where every layer overlaps (inside the narrowest one).</summary>
    public static double Composite(ReadOnlySpan<NeedleGlowLayer> layers)
    {
        var keep = 1.0;
        foreach (var layer in layers)
        {
            keep *= 1 - layer.Alpha;
        }

        return 1 - keep;
    }

    /// <summary>
    /// Opacity of a disc of <paramref name="radius"/> blurred by a unit Gaussian, at <paramref name="distance"/> from its
    /// center: <c>∫₀ᵃ r e^{−(ρ − r)²/2} I0e(ρ r) dr</c> (Simpson's rule; exact at the center, <c>1 − e^{−a²/2}</c>).
    /// </summary>
    public static double BlurredDisc(double radius, double distance)
    {
        if (!(radius > 0) || !double.IsFinite(radius) || !double.IsFinite(distance))
        {
            return 0;
        }

        var rho = Math.Abs(distance);
        if (rho > radius + 9)
        {
            return 0;
        }

        const int Steps = 256;
        var h = radius / Steps;
        var sum = 0.0;
        for (var i = 0; i <= Steps; i++)
        {
            var r = i * h;
            var d = rho - r;
            var f = r * Math.Exp(-0.5 * d * d) * ScaledBesselI0(rho * r);
            sum += i == 0 || i == Steps ? f : i % 2 == 1 ? 4 * f : 2 * f;
        }

        return Math.Clamp(sum * h / 3, 0, 1);
    }

    /// <summary>The error function (Abramowitz and Stegun 7.1.26, |error| ≤ 1.5e-7).</summary>
    public static double Erf(double x)
    {
        if (double.IsNaN(x))
        {
            return 0;
        }

        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var poly = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        return sign * (1 - poly * Math.Exp(-x * x));
    }

    /// <summary><c>e^{−x} I0(x)</c> for x ≥ 0 (Abramowitz and Stegun 9.8.1 and 9.8.2).</summary>
    internal static double ScaledBesselI0(double x)
    {
        x = Math.Abs(x);
        if (x <= 3.75)
        {
            var t = x / 3.75;
            t *= t;
            var i0 = 1 + t * (3.5156229 + t * (3.0899424 + t * (1.2067492 + t * (0.2659732 + t * (0.0360768 + t * 0.0045813)))));
            return i0 * Math.Exp(-x);
        }

        var u = 3.75 / x;
        var p = 0.39894228 + u * (0.01328592 + u * (0.00225319 + u * (-0.00157565 + u * (0.00916281
            + u * (-0.02057706 + u * (0.02635537 + u * (-0.01647633 + u * 0.00392377)))))));
        return p / Math.Sqrt(x);
    }
}
