using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>What a <see cref="LiquidSprite"/> draws (DESIGN 2a.3, Particles and Events).</summary>
public enum LiquidSpriteKind : byte
{
    /// <summary>
    /// A rising mote or bubble. <see cref="LiquidSprite.Hot"/> 0 draws a glass bubble (a ring of
    /// <see cref="LiquidSprite.Radius"/>, width 0.28 × the radius, in <see cref="LiquidModel.BubbleRingColor"/>, plus a
    /// highlight dot of 0.28 × the radius at (−0.35, −0.35) × the radius in <see cref="LiquidModel.BubbleHighlightColor"/>),
    /// 1 a luminous mote (a soft dot of 2 × the radius in <see cref="LiquidSprite.Color"/> at 95 %, additive in dark); in
    /// between, the bubble at (1 − Hot) and the mote at Hot.
    /// </summary>
    Mote,

    /// <summary>A gold mote of the new-peak accent (always luminous, <c>#FACC15</c>).</summary>
    GoldMote,

    /// <summary>A bubble popping at the surface: a ring of <see cref="LiquidSprite.Radius"/>, about 0.8 pt wide.</summary>
    Pop,

    /// <summary>A splash droplet: a solid dot.</summary>
    Splash,

    /// <summary>A steam wisp: a Gaussian disc.</summary>
    Steam,

    /// <summary>
    /// A falling droplet of the pulse accent: a teardrop whose round end (radius <see cref="LiquidSprite.Radius"/>) is
    /// centered at (X, Y) and whose tip is 2.2 × the radius above it (control points ± 1.1 × the radius at Y).
    /// </summary>
    Droplet,
}

/// <summary>
/// One particle of the liquid style, ready to draw. Positions and radius are in units of S relative to the disc
/// center (y down); <see cref="Color"/> is opaque and <see cref="Alpha"/> is the final opacity (readout-zone fade and
/// lifetime included). Everything is clipped to the inner glass (r 0.455 S).
/// </summary>
public readonly record struct LiquidSprite(
    LiquidSpriteKind Kind,
    double X,
    double Y,
    double Radius,
    Rgba Color,
    double Alpha,
    double Hot);

/// <summary>Fixed geometry of the liquid style in units of S (DESIGN 2a.3); the renderer caches these once per size.</summary>
public static class LiquidGeometry
{
    /// <summary>Inner glass radius: layers 3–9 are clipped to it; the level is a fraction of its diameter.</summary>
    public const double GlassRadius = 0.455;

    public const double IdleLevel = 0.08;

    /// <summary>Volume shading of the body: radial gradient centered here, clear to 75 % of 0.5 S, black 42 % (light <c>#1E283C</c> 12 %) at the edge.</summary>
    public const double VolumeCenterX = -0.12;
    public const double VolumeCenterY = -0.16;

    /// <summary>Light spill: a band of this height above the surface.</summary>
    public const double SpillHeight = 0.22;

    /// <summary>Spherical shade over the liquid: from 35 % of the glass radius around this center to black 50 % (light <c>#283042</c> 14 %) at the glass.</summary>
    public const double ShadeCenterX = -0.06;
    public const double ShadeCenterY = -0.08;
    public const double ShadeInner = 0.35;

    public const double InnerGlowRadius = 0.32;

    /// <summary>Readout scrim (dark only): ellipse at c + (0, 0.1 S), radii 0.36 × 0.26 S, black 35 % → 30 % at 55 % → 0.</summary>
    public const double ScrimCenterY = 0.1;
    public const double ScrimRadiusX = 0.36;
    public const double ScrimRadiusY = 0.26;

    /// <summary>Energy current widths (filaments 0, 1, 2 at 25 / 50 / 75 % of the depth), blurred σ 0.006 S.</summary>
    public static ReadOnlySpan<double> CurrentWidths => [0.018, 0.014, 0.010];

    public const double CurrentBlur = 0.006;

    /// <summary>Glass wall: stroke 0.006 S at the glass radius, white 9 % / black 6 %.</summary>
    public const double WallWidth = 0.006;

    /// <summary>Bottom caustic: arc r 0.475 S from 55° to 125°, width 0.02 S, σ 0.008 S.</summary>
    public const double CausticRadius = 0.475;
    public const double CausticStartDegrees = 55;
    public const double CausticEndDegrees = 125;
    public const double CausticWidth = 0.02;
    public const double CausticBlur = 0.008;

    /// <summary>Specular: ellipse 0.2 × 0.12 S rotated −34° at c − (0.17, 0.22) S, white 13 % / 75 % → 0.</summary>
    public const double SpecularX = -0.17;
    public const double SpecularY = -0.22;
    public const double SpecularRadiusX = 0.2;
    public const double SpecularRadiusY = 0.12;
    public const double SpecularRotationDegrees = -34;

    /// <summary>Rim glint: arc r 0.42 S from 200° to 236°, width 0.012 S, white 22 % / 95 %.</summary>
    public const double GlintRadius = 0.42;
    public const double GlintStartDegrees = 200;
    public const double GlintEndDegrees = 236;
    public const double GlintWidth = 0.012;

    /// <summary>The new-peak gold band: <c>#FACC15</c>, ± this half height around <see cref="LiquidModel.GoldBandY"/>.</summary>
    public const double GoldBandHalfHeight = 0.06;

    /// <summary>The x of sample <paramref name="index"/> of <paramref name="count"/> evenly spaced across the glass (both ends included).</summary>
    public static double SampleX(int index, int count) =>
        count <= 1 ? 0 : -GlassRadius + 2 * GlassRadius * index / (count - 1);
}
