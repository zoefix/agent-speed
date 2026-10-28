using System.Numerics;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Primitive of the orb's instanced signed-distance-field pipeline (DESIGN 2a, Techniques).</summary>
public enum OrbShape
{
    RoundedRect = 0,
    Circle = 1,
    Ring = 2,
    Capsule = 3,
    Radial = 4,
    Arc = 5,
    BandArc = 6,
    Ellipse = 7,
    Blade = 8,

    /// <summary>One segment of a stroked polyline (DESIGN 2.1 bolts and arcs), see <see cref="OrbGpuInstance.Stroke"/>.</summary>
    Stroke = 9,
}

/// <summary>How a shape is colored across its area.</summary>
public enum OrbFill
{
    Solid = 0,
    Linear = 1,
    Radial = 2,
    Palette = 3,

    /// <summary>Circle: color at the center → color2 at q0 × the radius → transparent at the radius.</summary>
    RadialStops = 4,
}

/// <summary>
/// One instanced shape (96 bytes, the layout of <c>Instance</c> in the HLSL source and of the macOS <c>OrbInstance</c>).
/// Coordinates are canvas points (DIP, y down, origin at the top-left of the 1.5 S window); angles given to the factory
/// methods are degrees clockwise from 3 o'clock; glows take the Gaussian falloff radius of the shader
/// (<see cref="Glow"/> converts a spec σ). Colors are straight-alpha sRGB.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbGpuInstance
{
    /// <summary>Center x, y, rotation (radians), shape.</summary>
    public Vector4 A;

    /// <summary>Size x, y, corner radius, glow radius.</summary>
    public Vector4 B;

    public Vector4 Color;

    /// <summary>Gradient tail color.</summary>
    public Vector4 Color2;

    /// <summary>Core alpha, glow alpha, p0, p1.</summary>
    public Vector4 Extra;

    /// <summary>Fill mode, q0, q1, q2.</summary>
    public Vector4 Extra2;

    public const int Stride = 96;

    private const double DegreesToRadians = Math.PI / 180;

    private OrbGpuInstance(OrbShape shape, double x, double y, double rotation, float sizeX, float sizeY, float corner, double glow,
        Rgba color, Rgba? tail, double core, double glowAlpha, float p0 = 0, float p1 = 0, Vector4? extra2 = null)
    {
        A = new Vector4((float)x, (float)y, (float)rotation, (float)shape);
        B = new Vector4(sizeX, sizeY, corner, (float)Math.Max(0, glow));
        Color = ToVector(color);
        Color2 = ToVector(tail ?? color);
        Extra = new Vector4((float)core, (float)glowAlpha, p0, p1);
        Extra2 = extra2 ?? new Vector4(0, 0, 0, 1);
    }

    public readonly OrbShape Shape => (OrbShape)(int)A.W;

    /// <summary>The shader glow radius of a Gaussian glow with standard deviation <paramref name="sigma"/> (a canvas shadowBlur of 2σ).</summary>
    public static double Glow(double sigma) => sigma * 1.414;

    public static Vector4 ToVector(Rgba color) =>
        new((float)color.R, (float)color.G, (float)color.B, (float)Math.Clamp(color.A, 0, 1));

    public static OrbGpuInstance RoundedRect(double x, double y, double halfWidth, double halfHeight, double rotationDegrees, double corner,
        Rgba color, double core = 1, double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.RoundedRect, x, y, rotationDegrees * DegreesToRadians, (float)halfWidth, (float)halfHeight, (float)corner, glow, color,
            null, core, glowAlpha);

    public static OrbGpuInstance Circle(double x, double y, double radius, Rgba color, double core = 1, double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.Circle, x, y, 0, (float)radius, 0, 0, glow, color, null, core, glowAlpha);

    public static OrbGpuInstance Ring(double x, double y, double radius, double width, Rgba color, double core = 1, double glow = 0,
        double glowAlpha = 0) =>
        new(OrbShape.Ring, x, y, 0, (float)radius, (float)width, 0, glow, color, null, core, glowAlpha);

    /// <summary>A round-capped line from the tail to the head, colored <paramref name="tailColor"/> → <paramref name="color"/>.</summary>
    public static OrbGpuInstance Capsule(double tailX, double tailY, double headX, double headY, double width, Rgba color,
        Rgba? tailColor = null, double core = 1, double glow = 0, double glowAlpha = 0)
    {
        var dx = headX - tailX;
        var dy = headY - tailY;
        var length = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
        return new OrbGpuInstance(OrbShape.Capsule, (headX + tailX) / 2, (headY + tailY) / 2, Math.Atan2(dy, dx), (float)length, (float)width,
            0, glow, color, tailColor, core, glowAlpha);
    }

    /// <summary>A soft radial halo between two radii (nothing inside the inner one), at <paramref name="alpha"/>.</summary>
    public static OrbGpuInstance Radial(double x, double y, double inner, double outer, Rgba color, double alpha) =>
        new(OrbShape.Radial, x, y, 0, 0, 0, 0, 0, color, null, alpha, 0, (float)inner, (float)outer);

    /// <summary>A tapered arc (the comet) from <paramref name="startDegrees"/> over <paramref name="sweepDegrees"/>, brightest at the head.</summary>
    public static OrbGpuInstance Arc(double x, double y, double radius, double width, double startDegrees, double sweepDegrees, Rgba color,
        Rgba tailColor, double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.Arc, x, y, 0, (float)radius, (float)width, 0, glow, color, tailColor, 1, glowAlpha,
            (float)(startDegrees * DegreesToRadians), (float)(Math.Max(0.0001, sweepDegrees) * DegreesToRadians));

    /// <summary>
    /// A constant-width arc from <paramref name="startDegrees"/> over <paramref name="sweepDegrees"/> (clockwise, 0..360), colored
    /// <paramref name="startColor"/> → <paramref name="color"/> with its alpha rising from <paramref name="startAlpha"/> × to 1 × at the
    /// end; butt caps unless <paramref name="roundCaps"/>.
    /// </summary>
    public static OrbGpuInstance BandArc(double x, double y, double radius, double width, double startDegrees, double sweepDegrees, Rgba color,
        Rgba? startColor = null, double startAlpha = 1, bool roundCaps = false, double core = 1, double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.BandArc, x, y, 0, (float)radius, (float)width, roundCaps ? 1 : 0, glow, color, startColor, core, glowAlpha,
            (float)(startDegrees * DegreesToRadians), (float)(Math.Clamp(sweepDegrees, 0.0001, 360) * DegreesToRadians),
            new Vector4((float)OrbFill.Solid, 0, 0, (float)startAlpha));

    /// <summary>
    /// A band arc colored by the heat palette from <paramref name="heatFrom"/> at the start to <paramref name="heatTo"/> at the end (the
    /// spectrum of a dial), at <paramref name="alpha"/>, with its alpha ramping like <see cref="BandArc"/>.
    /// </summary>
    public static OrbGpuInstance PaletteArc(double x, double y, double radius, double width, double startDegrees, double sweepDegrees,
        double heatFrom, double heatTo, double alpha, double startAlpha = 1, bool roundCaps = false, double core = 1, double glow = 0,
        double glowAlpha = 0) =>
        new(OrbShape.BandArc, x, y, 0, (float)radius, (float)width, roundCaps ? 1 : 0, glow, Rgba.White.WithAlpha(alpha), null, core,
            glowAlpha, (float)(startDegrees * DegreesToRadians), (float)(Math.Clamp(sweepDegrees, 0.0001, 360) * DegreesToRadians),
            new Vector4((float)OrbFill.Palette, (float)heatFrom, (float)heatTo, (float)startAlpha));

    /// <summary>An ellipse with radii (<paramref name="radiusX"/>, <paramref name="radiusY"/>) rotated by <paramref name="rotationDegrees"/>.</summary>
    public static OrbGpuInstance Ellipse(double x, double y, double radiusX, double radiusY, double rotationDegrees, Rgba color, double core = 1,
        double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.Ellipse, x, y, rotationDegrees * DegreesToRadians, (float)radiusX, (float)radiusY, 0, glow, color, null, core, glowAlpha);

    /// <summary>
    /// The needle: pivot at (<paramref name="x"/>, <paramref name="y"/>) pointing at <paramref name="angleDegrees"/>, a tapered blade to a
    /// round tip at <paramref name="tipLength"/> (half widths <paramref name="pivotHalfWidth"/> → <paramref name="tipHalfWidth"/>) and a
    /// counterweight <paramref name="tailLength"/> behind the pivot (half width → <paramref name="tailHalfWidth"/>).
    /// </summary>
    public static OrbGpuInstance Blade(double x, double y, double angleDegrees, double tipLength, double tailLength, double pivotHalfWidth,
        double tipHalfWidth, double tailHalfWidth, Rgba color, double core = 1, double glow = 0, double glowAlpha = 0) =>
        new(OrbShape.Blade, x, y, angleDegrees * DegreesToRadians, (float)tipLength, (float)tailLength, (float)tailHalfWidth, glow, color, null,
            core, glowAlpha, (float)pivotHalfWidth, (float)tipHalfWidth);

    /// <summary>
    /// Linear gradient fill (circle, ring, rounded rect, ellipse, blade): this color at −<paramref name="halfLength"/> along
    /// <paramref name="angleDegrees"/> (90 = top to bottom) → <paramref name="end"/> at +<paramref name="halfLength"/>.
    /// </summary>
    public readonly OrbGpuInstance LinearGradient(Rgba end, double halfLength, double angleDegrees = 90)
    {
        var copy = this;
        copy.Color2 = ToVector(end);
        copy.Extra2 = new Vector4((float)OrbFill.Linear, (float)(angleDegrees * DegreesToRadians), (float)halfLength, 1);
        return copy;
    }

    /// <summary>Radial gradient fill: this color at the offset (from the shape center) → <paramref name="end"/> at <paramref name="radius"/>.</summary>
    public readonly OrbGpuInstance RadialGradient(Rgba end, double radius, double offsetX = 0, double offsetY = 0)
    {
        var copy = this;
        copy.Color2 = ToVector(end);
        copy.Extra2 = new Vector4((float)OrbFill.Radial, (float)offsetX, (float)offsetY, (float)radius);
        return copy;
    }

    /// <summary>
    /// One segment of a stroked polyline from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), drawn as up to three concentric round-capped layers like canvas strokes: the glow and the body in
    /// <paramref name="color"/>, the core in <paramref name="core"/>, each with its half width and alpha, summed when
    /// <paramref name="additive"/> and laid over each other otherwise. At a joint the segment keeps only its side of the
    /// bisector with its neighbor (<paramref name="startCut"/> and <paramref name="endCut"/>: the bisector's direction along the
    /// path, zero at the ends of the path), so a jagged path is covered once.
    /// </summary>
    /// <remarks>
    /// Layout: a = (mid x, mid y, direction (rad), 9); b = (half length, glow, body and core half widths); color = glow and body
    /// color with the stroke's alpha; color2 = core color; extra = (glow, body, core alpha, additive); extra2 = (start cut x, y,
    /// end cut x, y).
    /// </remarks>
    public static OrbGpuInstance Stroke(double x0, double y0, double x1, double y1, double glowHalfWidth, double bodyHalfWidth,
        double coreHalfWidth, Rgba color, Rgba core, double glowAlpha, double bodyAlpha, double coreAlpha, bool additive, Vector2 startCut,
        Vector2 endCut)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        return new OrbGpuInstance
        {
            A = new Vector4((float)((x0 + x1) / 2), (float)((y0 + y1) / 2), (float)Math.Atan2(dy, dx), (float)OrbShape.Stroke),
            B = new Vector4((float)(Math.Sqrt(dx * dx + dy * dy) / 2), (float)Math.Max(0, glowHalfWidth), (float)Math.Max(0, bodyHalfWidth),
                (float)Math.Max(0, coreHalfWidth)),
            Color = ToVector(color),
            Color2 = ToVector(core),
            Extra = new Vector4((float)glowAlpha, (float)bodyAlpha, (float)coreAlpha, additive ? 1 : 0),
            Extra2 = new Vector4(startCut.X, startCut.Y, endCut.X, endCut.Y),
        };
    }

    /// <summary>
    /// Three-stop radial fill of a circle (a canvas radial gradient with stops 0, <paramref name="stop"/>, 1): this color at the
    /// center → <paramref name="middle"/> at <paramref name="stop"/> × the radius → <paramref name="middle"/> transparent at the
    /// radius.
    /// </summary>
    public readonly OrbGpuInstance RadialStops(Rgba middle, double stop)
    {
        var copy = this;
        copy.Color2 = ToVector(middle);
        copy.Extra2 = new Vector4((float)OrbFill.RadialStops, (float)Math.Clamp(stop, 0.001, 0.999), 0, 1);
        return copy;
    }

    /// <summary>Multiplies every alpha (crossfades).</summary>
    public void ScaleAlpha(float factor)
    {
        Color.W *= factor;
        Color2.W *= factor;
    }
}
