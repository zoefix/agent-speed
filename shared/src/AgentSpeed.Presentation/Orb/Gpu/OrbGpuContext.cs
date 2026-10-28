using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Colors of the Windows high-contrast theme the orb follows (DESIGN 2a, per style).</summary>
public readonly record struct OrbContrastColors(Rgba WindowText, Rgba Window, Rgba Highlight, Rgba GrayText)
{
    public static OrbContrastColors Default { get; } = new(Rgba.White, Rgba.Black, Rgba.FromHex("#1AEBFF"), Rgba.FromHex("#A6A6A6"));
}

/// <summary>
/// Everything a draw builder needs that changes only with the size, the appearance, the effects or the display scale.
/// Builders place shapes in canvas points: the canvas is the 1.5 S window, its center the disc center.
/// </summary>
public sealed class OrbGpuContext
{
    private const int LabelBuckets = 128;
    private readonly Rgba?[] _labels = new Rgba?[LabelBuckets + 1];

    /// <param name="size">Diameter S in points (DIP).</param>
    /// <param name="scale">Pixels per point of the target.</param>
    /// <param name="highContrast">The Windows high-contrast colors, or null when high contrast is off.</param>
    /// <param name="solidBody">Reduce Transparency: no translucent body.</param>
    /// <param name="bodyOpacity">The orb's opacity setting (0.4..1) applied to the body.</param>
    public OrbGpuContext(double size, double scale, OrbTokens tokens, EffectsPolicy effects, OrbContrastColors? highContrast = null,
        bool solidBody = false, double bodyOpacity = 1)
    {
        Size = size;
        Scale = scale > 0 ? scale : 1;
        Tokens = tokens;
        Effects = effects;
        HighContrast = highContrast is not null;
        Contrast = highContrast ?? OrbContrastColors.Default;
        SolidBody = solidBody;
        BodyOpacity = bodyOpacity;
    }

    public double Size { get; }

    public double Scale { get; }

    public OrbTokens Tokens { get; }

    public EffectsPolicy Effects { get; }

    public bool HighContrast { get; }

    public OrbContrastColors Contrast { get; }

    public bool SolidBody { get; }

    public double BodyOpacity { get; }

    public bool Dark => Tokens.Appearance == OrbAppearance.Dark;

    /// <summary>The window side (1.5 S).</summary>
    public double Canvas => Size * 1.5;

    public double CenterX => Size * 0.75;

    public double CenterY => Size * 0.75;

    /// <summary>Scale relative to the default 148 pt orb, for point-sized details.</summary>
    public double Unit => Size / 148;

    /// <summary>One device pixel in points.</summary>
    public double Pixel => 1 / Scale;

    /// <summary>Light adds up on dark glass and blends normally on porcelain.</summary>
    public OrbBlend GlowBlend => Dark ? OrbBlend.Additive : OrbBlend.Over;

    /// <summary>The canvas point at <paramref name="degrees"/> (clockwise from 3 o'clock) and <paramref name="radius"/> × S from the center.</summary>
    public (double X, double Y) Polar(double degrees, double radius)
    {
        var (x, y) = StyleMath.Polar(radius * Size, degrees);
        return (CenterX + x, CenterY + y);
    }

    /// <summary>A point given in units of S relative to the disc center, in canvas points.</summary>
    public (double X, double Y) At(double x, double y) => (CenterX + x * Size, CenterY + y * Size);

    /// <summary>The title label color of DESIGN 2 (dark: heat; light: heat darkened for contrast), cached per heat bucket.</summary>
    public Rgba Label(double heat)
    {
        var bucket = HeatPalette.Bucket(heat, LabelBuckets + 1);
        if (_labels[bucket] is { } cached)
        {
            return cached;
        }

        var color = Tokens.TierLabel(bucket / (double)LabelBuckets);
        _labels[bucket] = color;
        return color;
    }
}

/// <summary>Body tokens a style overrides (DESIGN 2a: liquid and reactor bodies, no lens highlight in some styles).</summary>
/// <param name="Top">Gradient top, or null for the appearance token.</param>
/// <param name="Bottom">Gradient bottom, or null for the appearance token.</param>
/// <param name="Lens">Draws the faint top-left lens highlight.</param>
public readonly record struct OrbBodyLook(Rgba? Top, Rgba? Bottom, bool Lens)
{
    public static OrbBodyLook Standard => new(null, null, true);
}

/// <summary>
/// The readout's colors where a style recolors it (DESIGN 2a.3: the liquid draws the part below its surface in white with a
/// dark shadow). Null entries keep the standard tokens.
/// </summary>
/// <param name="ShadowSigma">σ of the shadow in points (it replaces the value's heat glow).</param>
public readonly record struct OrbReadoutColors(Rgba? Value, Rgba? Unit, Rgba? Title, Rgba? Shadow, double ShadowSigma, bool ValueGlow = true)
{
    public static OrbReadoutColors Standard => new(null, null, null, null, 0);
}

/// <summary>
/// One style's instrument on the GPU (DESIGN 2a): it owns the style's model and adds its shapes between the shared body and
/// the shared readout. Implementations allocate nothing per frame.
/// </summary>
public interface IOrbStyleDraw
{
    OrbStyle Style { get; }

    IOrbStyleModel Model { get; }

    OrbReadoutLayout Readout { get; }

    OrbBodyLook Body { get; }

    /// <summary>The shared aura, response wave and new-peak rays (every style except <c>minimal</c>).</summary>
    bool SharedEffects { get; }

    /// <summary>
    /// The liquid splits the readout at its surface (<see cref="OrbDrawList.Liquid"/>) and draws the part below it in these
    /// colors; null for the other styles.
    /// </summary>
    OrbReadoutColors? BelowSurface { get; }

    /// <summary>Rebuilds cached geometry and colors (size, appearance, effects, display scale).</summary>
    void Configure(OrbGpuContext context);

    /// <summary>Adds the instrument for this frame (after the model's Update).</summary>
    void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame);
}

/// <summary>
/// A style that also draws outside its instrument slot (DESIGN 2.1, the floating <c>led</c>): layers behind the body, its own
/// response wave and new-peak burst instead of the shared ones, and layers above the readout.
/// </summary>
public interface IOrbStyleLayers
{
    /// <summary>The shared aura's strength (× its standard opacity).</summary>
    double AuraScale { get; }

    /// <summary>Per unit of overdrive, how far the aura's color moves toward white-hot.</summary>
    double AuraHotMix { get; }

    /// <summary>Draws the response wave and the new-peak rays itself (the shared ones are skipped).</summary>
    bool OwnsPulseAndPeak { get; }

    /// <summary>Adds what lies behind the body (after the aura).</summary>
    void BuildBehindBody(OrbDrawList list, OrbScene scene, in OrbFrame frame);

    /// <summary>
    /// Adds what lies above the readout (after <see cref="OrbDrawList.BeginOverlay"/>); <paramref name="readout"/> was just drawn in
    /// this style's layout (null for the outgoing style of a crossfade).
    /// </summary>
    void BuildOverReadout(OrbDrawList list, OrbScene scene, in OrbFrame frame, OrbReadoutDraw? readout);
}
