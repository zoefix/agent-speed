using System.Numerics;
using System.Runtime.InteropServices;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>The faces of the orb readout (the app maps them to Segoe UI Variable typefaces).</summary>
public enum OrbFont
{
    /// <summary>Bold display face with tabular digits (the value).</summary>
    Value,

    /// <summary>Medium text face (the unit and the agents overflow).</summary>
    Unit,

    /// <summary>Semibold text face with CJK fallbacks (the speed title).</summary>
    Title,

    /// <summary>Medium text face with CJK fallbacks (the hover info line).</summary>
    Info,

    /// <summary>Heavy face of the new-peak pill.</summary>
    Pill,

    /// <summary>Semibold text face with tabular cells (the traffic readout's numbers, DESIGN 10).</summary>
    TrafficNumber,

    /// <summary>
    /// The traffic readout's vector arrows (DESIGN 10): <c>↓</c> or <c>↑</c>, 0.50 × size wide and 0.66 × size tall on the
    /// baseline, stroke max(0.9 pt, 0.11 × size) with round caps.
    /// </summary>
    Arrow,
}

/// <summary>
/// A string at one size, as the glyph atlas rasterizes it. Sizes and tracking are in points; <see cref="GlowSigma"/> is the
/// standard deviation (points) of the blurred copy kept beside the coverage for the text's glow (0: none).
/// </summary>
public readonly record struct OrbTextKey(string Text, OrbFont Font, float Size, float Tracking = 0, float GlowSigma = 0)
{
    /// <summary>Quantizes size and tracking so that animated fits reuse a handful of atlas entries.</summary>
    public static OrbTextKey Create(string text, OrbFont font, double size, double tracking = 0, double glowSigma = 0) =>
        new(text, font, (float)(Math.Round(Math.Max(1, size) * 4) / 4), (float)(Math.Round(tracking * 20) / 20),
            (float)(Math.Round(glowSigma * 8) / 8));
}

/// <summary>
/// A rasterized string in the atlas. The quad is relative to the text origin (the left end of the baseline), in points,
/// and includes the padding for the glow; <see cref="Advance"/> is the width of the text itself (with tracking).
/// </summary>
public readonly record struct OrbGlyph(
    float Advance,
    float Ascent,
    float QuadLeft,
    float QuadTop,
    float QuadWidth,
    float QuadHeight,
    float U0,
    float V0,
    float U1,
    float V1)
{
    public bool IsEmpty => QuadWidth <= 0 || QuadHeight <= 0;
}

/// <summary>Rasterizes strings into the shared glyph atlas (the app implements it with DirectWrite through WPF).</summary>
public interface IOrbGlyphs
{
    /// <summary>The rasterized string; false when it cannot be drawn (the atlas then skips it this frame).</summary>
    bool TryGet(in OrbTextKey key, out OrbGlyph glyph);

    /// <summary>Width of the widest digit of a face at a size (tabular cells), in points.</summary>
    double DigitCell(OrbFont font, double size);

    /// <summary>Cap height of a face at a size, in points.</summary>
    double CapHeight(OrbFont font, double size);
}

/// <summary>How a text quad is clipped.</summary>
public enum OrbTextClipMode
{
    None = 0,
    Rect = 1,

    /// <summary>Only above the liquid surface (and inside the rect).</summary>
    AboveLiquid = 2,

    /// <summary>Only below the liquid surface (and inside the rect).</summary>
    BelowLiquid = 3,

    /// <summary>A moving slanted band: center x, center y, half width, slope dx/dy.</summary>
    Band = 4,
}

/// <summary>A text clip: a rectangle (canvas points), the liquid surface, or a band.</summary>
public readonly record struct OrbTextClip(OrbTextClipMode Mode, float X0, float Y0, float X1, float Y1)
{
    private const float Far = 1e6f;

    public static OrbTextClip None => default;

    public static OrbTextClip Rect(double left, double top, double right, double bottom) =>
        new(OrbTextClipMode.Rect, (float)left, (float)top, (float)right, (float)bottom);

    public static OrbTextClip Liquid(bool below) =>
        new(below ? OrbTextClipMode.BelowLiquid : OrbTextClipMode.AboveLiquid, -Far, -Far, Far, Far);

    /// <summary>This clip narrowed to a rectangle (a band keeps its own shape).</summary>
    public OrbTextClip Intersect(double left, double top, double right, double bottom) => Mode switch
    {
        OrbTextClipMode.None => Rect(left, top, right, bottom),
        OrbTextClipMode.Band => this,
        _ => this with
        {
            X0 = Math.Max(X0, (float)left),
            Y0 = Math.Max(Y0, (float)top),
            X1 = Math.Min(X1, (float)right),
            Y1 = Math.Min(Y1, (float)bottom),
        },
    };
}

/// <summary>One text quad of the atlas pipeline (128 bytes, the layout of <c>TextInstance</c> in the HLSL source).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbTextInstance
{
    /// <summary>Quad x, y (top-left), width, height in canvas points.</summary>
    public Vector4 Rect;

    /// <summary>Atlas u0, v0, u1, v1.</summary>
    public Vector4 Uv;

    /// <summary>Straight-alpha fill.</summary>
    public Vector4 Color;

    /// <summary>Glow rgb and strength (0: none), from the atlas's blurred channel.</summary>
    public Vector4 Glow;

    /// <summary>Shear (dx per dy), shear pivot y, unused, clip mode.</summary>
    public Vector4 Params;

    /// <summary>Clip rect (min x, min y, max x, max y) or band (center x, center y, half width, slope).</summary>
    public Vector4 Clip;

    /// <summary>Bottom color of a vertical gradient fill (straight alpha).</summary>
    public Vector4 Fill2;

    /// <summary>Gradient top y, bottom y, enabled, unused.</summary>
    public Vector4 Gradient;

    public const int Stride = 128;
}
