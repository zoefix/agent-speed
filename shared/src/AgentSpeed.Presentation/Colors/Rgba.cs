using System.Globalization;

namespace AgentSpeed.Presentation.Colors;

/// <summary>Straight (non-premultiplied) sRGB color with components in 0..1.</summary>
public readonly record struct Rgba(double R, double G, double B, double A = 1)
{
    public static Rgba White => new(1, 1, 1);

    public static Rgba Black => new(0, 0, 0);

    public static Rgba Transparent => new(0, 0, 0, 0);

    public byte R8 => ToByte(R);

    public byte G8 => ToByte(G);

    public byte B8 => ToByte(B);

    public byte A8 => ToByte(A);

    public static Rgba FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length is not (6 or 8))
        {
            throw new FormatException($"Invalid color '{hex}'.");
        }

        var value = uint.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (span.Length == 6)
        {
            return new Rgba(((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
        }

        return new Rgba(
            ((value >> 16) & 0xFF) / 255.0,
            ((value >> 8) & 0xFF) / 255.0,
            (value & 0xFF) / 255.0,
            ((value >> 24) & 0xFF) / 255.0);
    }

    public Rgba WithAlpha(double alpha) => this with { A = Math.Clamp(alpha, 0, 1) };

    public Rgba MultiplyAlpha(double factor) => this with { A = Math.Clamp(A * factor, 0, 1) };

    /// <summary>Linear interpolation in gamma-encoded sRGB (cheap; use <see cref="OkLab"/> for perceptual mixing).</summary>
    public static Rgba Lerp(Rgba a, Rgba b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgba(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }

    public string ToHex() => A8 == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{R8:X2}{G8:X2}{B8:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{A8:X2}{R8:X2}{G8:X2}{B8:X2}");

    /// <summary>WCAG relative luminance.</summary>
    public double RelativeLuminance()
    {
        return 0.2126 * OkLab.ToLinear(R) + 0.7152 * OkLab.ToLinear(G) + 0.0722 * OkLab.ToLinear(B);
    }

    public static double ContrastRatio(Rgba foreground, Rgba background)
    {
        var composite = foreground.A >= 1 ? foreground : CompositeOver(foreground, background);
        var l1 = composite.RelativeLuminance();
        var l2 = background.RelativeLuminance();
        var (hi, lo) = l1 > l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static Rgba CompositeOver(Rgba top, Rgba bottom)
    {
        var a = top.A + bottom.A * (1 - top.A);
        if (a <= 0)
        {
            return Transparent;
        }

        return new Rgba(
            (top.R * top.A + bottom.R * bottom.A * (1 - top.A)) / a,
            (top.G * top.A + bottom.G * bottom.A * (1 - top.A)) / a,
            (top.B * top.A + bottom.B * bottom.A * (1 - top.A)) / a,
            a);
    }

    private static byte ToByte(double component) =>
        (byte)Math.Clamp((int)Math.Round(component * 255, MidpointRounding.AwayFromZero), 0, 255);
}
