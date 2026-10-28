using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>
/// The plate's material on Windows (DESIGN 2b, Name plate, Material): layered windows have no acrylic, so the fill is
/// solid, as for Reduce Transparency. Windows high contrast uses system colours instead (the app's painter).
/// </summary>
/// <param name="FillTop">Fill at the top of the vertical gradient.</param>
/// <param name="FillBottom">Fill at the bottom.</param>
/// <param name="RimTop">1 pt inner stroke at the top.</param>
/// <param name="RimBottom">1 pt inner stroke at the bottom.</param>
/// <param name="Outer">0.5 pt outer stroke.</param>
/// <param name="Shadow">Drop shadow (y 1, blur 6).</param>
/// <param name="Text">Neutral ink of the name.</param>
/// <param name="Secondary">Statuses (<c>source.notFound</c>, <c>source.disabled</c>) and hollow dots.</param>
/// <param name="Accent">The undo action.</param>
public sealed record NamePlateMaterial(
    Rgba FillTop,
    Rgba FillBottom,
    Rgba RimTop,
    Rgba RimBottom,
    Rgba Outer,
    Rgba Shadow,
    Rgba Text,
    Rgba Secondary,
    Rgba Accent)
{
    public const double OuterWidth = 0.5;
    public const double RimWidth = 1;
    public const double ShadowOffsetY = 1;
    public const double ShadowBlur = 6;

    public static NamePlateMaterial Dark { get; } = new(
        Rgba.FromHex("#1F2432"),
        Rgba.FromHex("#0E1016"),
        Rgba.White.WithAlpha(0.16),
        Rgba.White.WithAlpha(0.04),
        Rgba.Black.WithAlpha(0.35),
        Rgba.Black.WithAlpha(0.38),
        Rgba.White.WithAlpha(0.92),
        Rgba.White.WithAlpha(0.66),
        Rgba.FromHex("#4D9CFF"));

    public static NamePlateMaterial Light { get; } = new(
        Rgba.FromHex("#FFFFFF"),
        Rgba.FromHex("#F0F2F6"),
        Rgba.White.WithAlpha(0.95),
        Rgba.White.WithAlpha(0),
        Rgba.Black.WithAlpha(0.10),
        Rgba.Black.WithAlpha(0.14),
        Rgba.FromHex("#0B0D12").WithAlpha(0.88),
        Rgba.Black.WithAlpha(0.60),
        Rgba.FromHex("#0A64D6"));

    public static NamePlateMaterial For(bool dark) => dark ? Dark : Light;

    /// <summary>The fill at the plate's vertical center: the 1 pt ring that separates overlapping dots.</summary>
    public Rgba Separator => Rgba.Lerp(FillTop, FillBottom, 0.5);

    /// <summary>A source colour on this material: as the bezel, darkened 10 % on the light plate.</summary>
    public static Rgba SourceColor(Rgba color, bool dark) => dark ? color : OkLab.Darken(color, 0.1);

    public static Rgba SourceColor(SourceKind source, bool dark) => SourceColor(HeatPalette.SourceColor(source), dark);

    /// <summary>
    /// The all-sources dot: a conic gradient through every source colour in source order, clockwise from 12 o'clock and
    /// back to the first; <paramref name="turn"/> is the angle in turns (0..1).
    /// </summary>
    public static Rgba SpectrumAt(double turn, bool dark)
    {
        var kinds = Enum.GetValues<SourceKind>();
        var position = (turn % 1 + 1) % 1 * kinds.Length;
        var index = (int)Math.Floor(position);
        var from = SourceColor(kinds[index % kinds.Length], dark);
        var to = SourceColor(kinds[(index + 1) % kinds.Length], dark);
        return OkLab.Mix(from, to, position - index);
    }
}
