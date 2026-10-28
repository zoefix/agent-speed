namespace AgentSpeed.Presentation.Colors;

/// <summary>OKLab color space (Björn Ottosson, 2020) for perceptually even gradients.</summary>
public readonly record struct OkLab(double L, double A, double B)
{
    public static OkLab FromRgb(Rgba color)
    {
        var r = ToLinear(color.R);
        var g = ToLinear(color.G);
        var b = ToLinear(color.B);

        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        return new OkLab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public Rgba ToRgb(double alpha = 1)
    {
        var l = L + 0.3963377774 * A + 0.2158037573 * B;
        var m = L - 0.1055613458 * A - 0.0638541728 * B;
        var s = L - 0.0894841775 * A - 1.2914855480 * B;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;

        var r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return new Rgba(
            Math.Clamp(ToGamma(r), 0, 1),
            Math.Clamp(ToGamma(g), 0, 1),
            Math.Clamp(ToGamma(b), 0, 1),
            alpha);
    }

    public static OkLab Lerp(OkLab a, OkLab b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new OkLab(a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);
    }

    public static Rgba Mix(Rgba a, Rgba b, double t)
    {
        var alpha = a.A + (b.A - a.A) * Math.Clamp(t, 0, 1);
        return Lerp(FromRgb(a), FromRgb(b), t).ToRgb(alpha);
    }

    /// <summary>Scales lightness by (1 - amount), keeping hue and chroma.</summary>
    public static Rgba Darken(Rgba color, double amount)
    {
        var lab = FromRgb(color);
        return (lab with { L = lab.L * (1 - Math.Clamp(amount, 0, 1)) }).ToRgb(color.A);
    }

    internal static double ToLinear(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    internal static double ToGamma(double c)
    {
        if (c <= 0)
        {
            return 0;
        }

        return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }
}
