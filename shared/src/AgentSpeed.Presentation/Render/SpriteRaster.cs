using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Render;

public enum SpriteBlend
{
    /// <summary>Saturating add (dark appearance: sparks and glows light each other up).</summary>
    Additive,

    /// <summary>Normal "over" blending (light appearance).</summary>
    Over,
}

public enum DotShape
{
    /// <summary>Full core up to 35 % of the radius, then a smooth falloff to 0 at the radius (a glowing mote or spark).</summary>
    Soft,

    /// <summary>A solid disc with a one-pixel anti-aliased edge.</summary>
    Solid,

    /// <summary>A Gaussian blob (σ = radius / 2.5), for steam and halos.</summary>
    Glow,

    /// <summary>
    /// A spark with its halo in one pass: a solid core to 15 % of the radius falling to 35 % at 42 %, then a soft halo
    /// to 0 at the radius (a dot of radius r with a 2.4 r glow at 22 %).
    /// </summary>
    Spark,

    /// <summary>A luminous mote (liquid, dark): linear from full at the center to 42 % at 35 % of the radius, then to 0.</summary>
    Mote,

    /// <summary>The light appearance's mote: linear from full to 50 % at 35 % of the radius, then to 0.</summary>
    MoteLight,
}

/// <summary>Device-pixel rectangle.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

/// <summary>
/// CPU splatting of small particles into a premultiplied BGRA buffer (one <c>uint</c> per pixel, the layout of WPF's
/// <c>Pbgra32</c>). Drawing hundreds of tiny ellipses through a retained scene costs far more than writing their pixels
/// and uploading one dirty rectangle, so every particle system of every orb style draws through this. Coordinates
/// are in device pixels. Never allocates after construction; not thread-safe.
/// </summary>
public sealed class SpriteRaster
{
    private const int ProfileSteps = 256;
    private static readonly float[] SoftProfile = BuildProfile(DotShape.Soft);
    private static readonly float[] GlowProfile = BuildProfile(DotShape.Glow);
    private static readonly float[] SparkProfile = BuildProfile(DotShape.Spark);
    private static readonly float[] MoteProfile = BuildProfile(DotShape.Mote);
    private static readonly float[] MoteLightProfile = BuildProfile(DotShape.MoteLight);
    private const int BandSteps = 64;
    private readonly float[] _band = new float[BandSteps + 1];

    private PixelRect _shown;
    private PixelRect _current;

    public SpriteRaster(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new uint[Width * Height];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Premultiplied BGRA pixels, row-major, stride <c>Width * 4</c> bytes.</summary>
    public uint[] Pixels { get; }

    /// <summary>
    /// Restricts drawing to a circle (device pixels; radius ≤ 0 removes it): the liquid's particles stay inside the glass
    /// without a geometry clip on the layer.
    /// </summary>
    public void SetClipCircle(double x, double y, double radius)
    {
        _clipX = x;
        _clipY = y;
        _clipR2 = radius > 0 ? radius * radius : 0;
    }

    private double _clipX;
    private double _clipY;
    private double _clipR2;

    private bool Outside(int px, int py)
    {
        if (_clipR2 <= 0)
        {
            return false;
        }

        var dx = px + 0.5 - _clipX;
        var dy = py + 0.5 - _clipY;
        return dx * dx + dy * dy > _clipR2;
    }

    /// <summary>The area drawn since <see cref="Begin"/>.</summary>
    public PixelRect Drawn => _current;

    /// <summary>Starts a frame: clears what the previous frame drew.</summary>
    public void Begin()
    {
        ClearRect(_shown);
        ClearRect(_current);
        _current = default;
    }

    /// <summary>Ends a frame; returns the rectangle that changed since the previous frame (empty when nothing did).</summary>
    public PixelRect End()
    {
        var dirty = PixelRect.Union(_shown, _current);
        _shown = _current;
        return dirty;
    }

    /// <summary>Clears everything (for example after a size change of the owner).</summary>
    public void Clear()
    {
        Array.Clear(Pixels);
        _shown = default;
        _current = default;
    }

    public void Dot(double x, double y, double radius, Rgba color, double alpha, DotShape shape = DotShape.Soft,
        SpriteBlend blend = SpriteBlend.Additive)
    {
        alpha *= color.A;
        if (!(alpha > 0.002) || !(radius > 0.05) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        // A dot smaller than a pixel keeps its energy: shrink the alpha instead of the footprint.
        if (radius < 0.7)
        {
            alpha *= radius / 0.7;
            radius = 0.7;
        }

        var y0 = Math.Max(0, (int)Math.Floor(y - radius));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(y + radius));
        var xMin = Math.Max(0, (int)Math.Floor(x - radius));
        var xMax = Math.Min(Width - 1, (int)Math.Ceiling(x + radius));
        if (xMin > xMax || y0 > y1)
        {
            return;
        }

        var profile = shape switch
        {
            DotShape.Glow => GlowProfile,
            DotShape.Spark => SparkProfile,
            DotShape.Mote => MoteProfile,
            DotShape.MoteLight => MoteLightProfile,
            _ => SoftProfile,
        };
        var fx = (float)x;
        var fy = (float)y;
        var r2 = (float)(radius * radius);
        var invR2 = (ProfileSteps - 1) / r2;
        // An alpha above 1 (a light drawn "over" in its additive equivalent) saturates per pixel, after the profile.
        var a255 = (float)(Math.Min(8, alpha) * 255);
        var cr = (int)(color.R * 255 + 0.5);
        var cg = (int)(color.G * 255 + 0.5);
        var cb = (int)(color.B * 255 + 0.5);
        var solid = shape == DotShape.Solid;
        var edge = (float)(radius - 1);
        var pixels = Pixels;
        for (var py = y0; py <= y1; py++)
        {
            var dy = py + 0.5f - fy;
            var dy2 = dy * dy;
            if (dy2 >= r2)
            {
                continue;
            }

            var half = MathF.Sqrt(r2 - dy2);
            var px0 = Math.Max(xMin, (int)(fx - half));
            var px1 = Math.Min(xMax, (int)(fx + half));
            var row = py * Width;
            for (var px = px0; px <= px1; px++)
            {
                var dx = px + 0.5f - fx;
                var d2 = dx * dx + dy2;
                if (d2 >= r2)
                {
                    continue;
                }

                float coverage;
                if (solid)
                {
                    coverage = d2 <= edge * edge || edge <= 0 ? 1f : Math.Clamp((float)radius - MathF.Sqrt(d2), 0, 1);
                }
                else
                {
                    coverage = profile[(int)(d2 * invR2)];
                }

                var a = Math.Min(255, (int)(a255 * coverage + 0.5f));
                if (a > 0 && !Outside(px, py))
                {
                    BlendInt(ref pixels[row + px], cr, cg, cb, a, blend);
                }
            }
        }

        Grow(xMin, y0, xMax, y1);
    }

    /// <summary>A thin ring (a bubble): stroke <paramref name="width"/> centered on <paramref name="radius"/>.</summary>
    public void Ring(double x, double y, double radius, double width, Rgba color, double alpha, SpriteBlend blend = SpriteBlend.Over)
    {
        alpha *= color.A;
        if (!(alpha > 0.002) || !(radius > 0.2) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        width = Math.Max(0.6, width);
        var outer = radius + width / 2 + 1;
        var x0 = Math.Max(0, (int)Math.Floor(x - outer));
        var y0 = Math.Max(0, (int)Math.Floor(y - outer));
        var x1 = Math.Min(Width - 1, (int)Math.Ceiling(x + outer));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(y + outer));
        if (x0 > x1 || y0 > y1)
        {
            return;
        }

        var cr = (float)(color.R * 255);
        var cg = (float)(color.G * 255);
        var cb = (float)(color.B * 255);
        var a = (float)Math.Min(1, alpha);
        var half = width / 2;
        var outer2 = outer * outer;
        for (var py = y0; py <= y1; py++)
        {
            var dy = py + 0.5 - y;
            var row = py * Width;
            for (var px = x0; px <= x1; px++)
            {
                var dx = px + 0.5 - x;
                var d2 = dx * dx + dy * dy;
                if (d2 >= outer2)
                {
                    continue;
                }

                var distance = Math.Abs(Math.Sqrt(d2) - radius);
                var coverage = (float)Math.Clamp(half + 0.5 - distance, 0, 1);
                if (coverage > 0 && !Outside(px, py))
                {
                    Blend(ref Pixels[row + px], cr, cg, cb, a * coverage, blend);
                }
            }
        }

        Grow(x0, y0, x1, y1);
    }

    /// <summary>
    /// A streak from (<paramref name="x0"/>, <paramref name="y0"/>) (tail) to (<paramref name="x1"/>, <paramref name="y1"/>)
    /// (head), <paramref name="width"/> wide with round ends; the alpha ramps from <paramref name="tailAlpha"/> × alpha at
    /// the tail to alpha at the head.
    /// </summary>
    public void Streak(double x0, double y0, double x1, double y1, double width, Rgba color, double alpha,
        double tailAlpha = 0.2, SpriteBlend blend = SpriteBlend.Additive)
    {
        alpha *= color.A;
        if (!(alpha > 0.002) || !double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1))
        {
            return;
        }

        width = Math.Max(0.7, width);
        var half = width / 2;
        var reach = half + 1;
        var bx0 = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - reach));
        var by0 = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - reach));
        var bx1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + reach));
        var by1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + reach));
        if (bx0 > bx1 || by0 > by1)
        {
            return;
        }

        var vx = x1 - x0;
        var vy = y1 - y0;
        var length2 = vx * vx + vy * vy;
        var cr = (float)(color.R * 255);
        var cg = (float)(color.G * 255);
        var cb = (float)(color.B * 255);
        for (var py = by0; py <= by1; py++)
        {
            var row = py * Width;
            var wy = py + 0.5 - y0;
            for (var px = bx0; px <= bx1; px++)
            {
                var wx = px + 0.5 - x0;
                var t = length2 > 1e-9 ? Math.Clamp((wx * vx + wy * vy) / length2, 0, 1) : 1;
                var ex = wx - t * vx;
                var ey = wy - t * vy;
                var distance = Math.Sqrt(ex * ex + ey * ey);
                var coverage = Math.Clamp(half + 0.5 - distance, 0, 1);
                if (coverage <= 0 || Outside(px, py))
                {
                    continue;
                }

                var a = alpha * (tailAlpha + (1 - tailAlpha) * t) * coverage;
                Blend(ref Pixels[row + px], cr, cg, cb, (float)Math.Min(1, a), blend);
            }
        }

        Grow(bx0, by0, bx1, by1);
    }

    private static void BlendInt(ref uint pixel, int r, int g, int b, int a, SpriteBlend blend)
    {
        var p = pixel;
        var da = (int)(p >> 24);
        var dr = (int)((p >> 16) & 0xFF);
        var dg = (int)((p >> 8) & 0xFF);
        var db = (int)(p & 0xFF);
        var sr = (r * a + 127) / 255;
        var sg = (g * a + 127) / 255;
        var sb = (b * a + 127) / 255;
        if (blend == SpriteBlend.Additive)
        {
            da = Math.Min(255, da + a);
            dr = Math.Min(da, dr + sr);
            dg = Math.Min(da, dg + sg);
            db = Math.Min(da, db + sb);
        }
        else
        {
            var keep = 255 - a;
            da = a + (da * keep + 127) / 255;
            dr = sr + (dr * keep + 127) / 255;
            dg = sg + (dg * keep + 127) / 255;
            db = sb + (db * keep + 127) / 255;
        }

        pixel = (uint)(da << 24 | dr << 16 | dg << 8 | db);
    }

    private static void Blend(ref uint pixel, float r, float g, float b, float alpha, SpriteBlend blend)
    {
        if (alpha <= 0.0005f)
        {
            return;
        }

        var p = pixel;
        var da = (int)(p >> 24);
        var dr = (int)((p >> 16) & 0xFF);
        var dg = (int)((p >> 8) & 0xFF);
        var db = (int)(p & 0xFF);
        var sa = (int)(alpha * 255 + 0.5f);
        var sr = (int)(r * alpha + 0.5f);
        var sg = (int)(g * alpha + 0.5f);
        var sb = (int)(b * alpha + 0.5f);
        if (blend == SpriteBlend.Additive)
        {
            da = Math.Min(255, da + sa);
            dr = Math.Min(da, dr + sr);
            dg = Math.Min(da, dg + sg);
            db = Math.Min(da, db + sb);
        }
        else
        {
            var keep = 255 - sa;
            da = sa + (da * keep + 127) / 255;
            dr = sr + (dr * keep + 127) / 255;
            dg = sg + (dg * keep + 127) / 255;
            db = sb + (db * keep + 127) / 255;
        }

        pixel = (uint)(da << 24 | dr << 16 | dg << 8 | db);
    }

    private void Grow(int x0, int y0, int x1, int y1) =>
        _current = PixelRect.Union(_current, new PixelRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));

    private void ClearRect(PixelRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            Array.Clear(Pixels, y * Width + rect.X, rect.Width);
        }
    }

    private static double Easing(double t) => t * t * (3 - 2 * t);

    private static double MoteFalloff(double d, double rim) => d <= 0.35 ? 1 - (1 - rim) * d / 0.35 : rim * (1 - d) / 0.65;

    /// <summary>
    /// The blurred shadow of a stroke along a surface <c>y(x)</c> (the liquid's meniscus glow, a canvas
    /// <c>shadowBlur</c> of 2σ): for every pixel column between the first and last sample, the alpha across the line is
    /// a <paramref name="width"/>-wide box convolved with a Gaussian of <paramref name="sigma"/>, times
    /// <paramref name="alpha"/>. Samples are in device pixels with ascending x. Normal blending, inside the clip circle.
    /// </summary>
    public void SurfaceGlow(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double width, double sigma, Rgba color, double alpha)
    {
        alpha *= color.A;
        var count = Math.Min(xs.Length, ys.Length);
        if (count < 2 || !(alpha > 0.002) || !(sigma > 0.05))
        {
            return;
        }

        width = Math.Max(0, width);
        var reach = width / 2 + 3 * sigma;
        var step = reach / BandSteps;
        var peak = 0f;
        for (var i = 0; i <= BandSteps; i++)
        {
            var d = i * step;
            var value = (float)(alpha * (Phi((d + width / 2) / sigma) - Phi((d - width / 2) / sigma)));
            _band[i] = value;
            peak = Math.Max(peak, value);
        }

        if (peak * 255 < 0.5)
        {
            return;
        }

        var cr = (int)(color.R * 255 + 0.5);
        var cg = (int)(color.G * 255 + 0.5);
        var cb = (int)(color.B * 255 + 0.5);
        var px0 = Math.Max(0, (int)Math.Ceiling(xs[0] - 0.5));
        var px1 = Math.Min(Width - 1, (int)Math.Floor(xs[count - 1] - 0.5));
        int top = int.MaxValue, bottom = int.MinValue;
        var segment = 0;
        var pixels = Pixels;
        var invStep = 1 / step;
        for (var px = px0; px <= px1; px++)
        {
            var x = px + 0.5;
            while (segment < count - 2 && xs[segment + 1] < x)
            {
                segment++;
            }

            var span = xs[segment + 1] - xs[segment];
            var t = span > 1e-9 ? Math.Clamp((x - xs[segment]) / span, 0, 1) : 0;
            var y = ys[segment] + (ys[segment + 1] - ys[segment]) * t;
            if (!double.IsFinite(y))
            {
                continue;
            }

            var py0 = Math.Max(0, (int)Math.Floor(y - reach));
            var py1 = Math.Min(Height - 1, (int)Math.Ceiling(y + reach));
            for (var py = py0; py <= py1; py++)
            {
                var index = (int)(Math.Abs(py + 0.5 - y) * invStep + 0.5);
                if (index > BandSteps || Outside(px, py))
                {
                    continue;
                }

                var a = (int)(_band[index] * 255 + 0.5f);
                if (a > 0)
                {
                    BlendInt(ref pixels[py * Width + px], cr, cg, cb, a, SpriteBlend.Over);
                }
            }

            top = Math.Min(top, py0);
            bottom = Math.Max(bottom, py1);
        }

        if (top <= bottom)
        {
            Grow(px0, top, px1, bottom);
        }
    }

    /// <summary>The standard normal CDF (Abramowitz–Stegun 7.1.26, error below 1.5e-7).</summary>
    private static double Phi(double x)
    {
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * z);
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1 + (x >= 0 ? erf : -erf));
    }

    private static float[] BuildProfile(DotShape shape)
    {
        var table = new float[ProfileSteps];
        for (var i = 0; i < ProfileSteps; i++)
        {
            var d = Math.Sqrt(i / (double)(ProfileSteps - 1));
            table[i] = shape switch
            {
                DotShape.Glow => (float)Math.Exp(-0.5 * (d * 2.5) * (d * 2.5)),
                DotShape.Mote => (float)MoteFalloff(d, 0.4 / 0.95),
                DotShape.MoteLight => (float)MoteFalloff(d, 0.5),
                DotShape.Spark => (float)(d <= 0.15 ? 1
                    : d <= 0.42 ? 1 - 0.65 * Easing(( d - 0.15) / 0.27)
                    : 0.35 * Math.Pow(1 - (d - 0.42) / 0.58, 2.2)),
                _ => (float)(d <= 0.35 ? 1 : Math.Pow(1 - (d - 0.35) / 0.65, 1.6)),
            };
        }

        return table;
    }
}
