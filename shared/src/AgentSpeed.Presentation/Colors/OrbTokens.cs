namespace AgentSpeed.Presentation.Colors;

public enum OrbAppearance
{
    Dark,
    Light,
}

/// <summary>Orb color tokens for the dark and light ("porcelain") variants (DESIGN section 2).</summary>
public sealed record OrbTokens(
    OrbAppearance Appearance,
    Rgba BodyTop,
    Rgba BodyBottom,
    double BodyAlpha,
    Rgba RimTop,
    Rgba? OuterStroke,
    Rgba ShadowColor,
    double ShadowOffset,
    double ShadowBlur,
    Rgba UnlitSegment,
    Rgba ValueText,
    Rgba SecondaryText,
    Rgba IdleValueText,
    double AuraOpacityScale,
    bool AdditiveParticles,
    double ParticleSizeScale,
    Rgba ParticleHot,
    double TierLabelDarken)
{
    public static OrbTokens Dark { get; } = new(
        OrbAppearance.Dark,
        Rgba.FromHex("#1B2030"),
        Rgba.FromHex("#090B10"),
        0.88,
        Rgba.White.WithAlpha(0.14),
        null,
        Rgba.Black.WithAlpha(0.45),
        0.03,
        0.10,
        Rgba.White.WithAlpha(0.07),
        Rgba.White.WithAlpha(0.96),
        Rgba.White.WithAlpha(0.55),
        Rgba.White.WithAlpha(0.60),
        0.55,
        true,
        1.0,
        HeatPalette.WhiteHot,
        0);

    public static OrbTokens Light { get; } = new(
        OrbAppearance.Light,
        Rgba.FromHex("#FFFFFF"),
        Rgba.FromHex("#E7EBF3"),
        0.94,
        Rgba.White.WithAlpha(0.95),
        Rgba.Black.WithAlpha(0.08),
        Rgba.Black.WithAlpha(0.16),
        0.04,
        0.12,
        Rgba.Black.WithAlpha(0.08),
        Rgba.FromHex("#0B0D12").WithAlpha(0.92),
        Rgba.Black.WithAlpha(0.50),
        Rgba.FromHex("#0B0D12").WithAlpha(0.55),
        0.32,
        false,
        1.2,
        HeatPalette.LightSparkHot,
        0.18);

    public static OrbTokens For(OrbAppearance appearance) => appearance == OrbAppearance.Light ? Light : Dark;

    public const double TierLabelMinContrast = 3.0;

    /// <summary>Tier label color: the heat color, darkened on the light body until it reaches 3:1 contrast.</summary>
    public Rgba TierLabel(double heat)
    {
        var color = HeatPalette.Sample(heat);
        if (TierLabelDarken <= 0)
        {
            return color;
        }

        for (var amount = TierLabelDarken; amount < 0.9; amount += 0.02)
        {
            var candidate = OkLab.Darken(color, amount);
            if (Rgba.ContrastRatio(candidate, BodyTop) >= TierLabelMinContrast
                && Rgba.ContrastRatio(candidate, BodyBottom) >= TierLabelMinContrast)
            {
                return candidate;
            }
        }

        return OkLab.Darken(color, 0.9);
    }

    public Rgba LeadingSegment(double heat) => Appearance == OrbAppearance.Dark ? HeatPalette.WhiteHot : HeatPalette.Sample(heat);
}
