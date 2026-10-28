namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// Where each style places the shared readout (DESIGN 2a, Shared foundation, frame table). Offsets are in units of S
/// from the disc center, y down; baselines are alphabetic.
/// </summary>
/// <param name="ValueSize">Value font size.</param>
/// <param name="ValueBaseline">Value baseline below the center.</param>
/// <param name="ValueMaxWidth">The value shrinks to this width.</param>
/// <param name="UnitSize">Unit size; null when the style has no unit.</param>
/// <param name="UnitGap">Unit baseline below the value baseline.</param>
/// <param name="TitleGap">Title baseline below the unit (or value) baseline; ignored when <paramref name="TitleFixed"/> is set.</param>
/// <param name="TitleFixed">Absolute title baseline below the center (needle: the dial gap under the hub).</param>
/// <param name="TitleSize">Title size.</param>
/// <param name="TitleMaxWidth">Titles shrink to 80 % at most to fit this width; never truncated.</param>
/// <param name="InfoMaxWidth">Hover info line width (shrinks to 70 %).</param>
/// <param name="InfoScale">Hover info line size relative to the title size.</param>
/// <param name="DotsY">Agent dots center below the center; null when the style has none.</param>
/// <param name="PillY">New-peak pill center (negative: above the center).</param>
/// <param name="Rolls">The value rolls (odometer); <c>minimal</c> simply updates.</param>
/// <param name="TitleCrossfade">Titles crossfade (0.25 s) instead of sliding.</param>
/// <param name="ValueGlow">The value glows from boost up.</param>
/// <param name="DotHalo">Agent dots get a 1.2 pt halo (liquid).</param>
/// <param name="ValueEffects">
/// The floating <c>led</c>'s value effects (DESIGN 2.1, Readout): the glow widens with overdrive and doubles from overdrive,
/// a black backing under the value in dark, and the redline's chromatic split.
/// </param>
public sealed record OrbReadoutLayout(
    double ValueSize,
    double ValueBaseline,
    double ValueMaxWidth,
    double? UnitSize,
    double UnitGap,
    double TitleGap,
    double? TitleFixed,
    double TitleSize,
    double TitleMaxWidth,
    double InfoMaxWidth,
    double InfoScale,
    double? DotsY,
    double PillY,
    bool Rolls = true,
    bool TitleCrossfade = false,
    bool ValueGlow = true,
    bool DotHalo = false,
    bool ValueEffects = false)
{
    public const double MinTextSize = 7;
    public const double TitleMinScale = 0.8;
    public const double InfoMinScale = 0.7;

    public static OrbReadoutLayout Led { get; } =
        new(0.24, 0.012, 0.62, 0.072, 0.104, 0.09, null, 0.062, 0.58, 0.62, 0.92, 0.3785, -0.232, ValueEffects: true);

    public static OrbReadoutLayout Needle { get; } = new(0.205, 0.035, 0.40, 0.062, 0.08, 0, 0.338, 0.062, 0.46, 0.42, 0.85, 0.405, -0.175);

    public static OrbReadoutLayout Liquid { get; } = Led with { DotHalo = true, ValueEffects = false };

    public static OrbReadoutLayout Reactor { get; } = new(0.205, 0.03, 0.46, 0.062, 0.08, 0.074, null, 0.058, 0.50, 0.52, 0.92, 0.252, -0.172);

    public static OrbReadoutLayout Minimal { get; } =
        new(0.30, 0.07, 0.66, null, 0, 0.098, null, 0.068, 0.62, 0.67, 0.92, null, -0.25, Rolls: false, TitleCrossfade: true, ValueGlow: false);

    public static OrbReadoutLayout For(OrbStyle style) => style switch
    {
        OrbStyle.Needle => Needle,
        OrbStyle.Liquid => Liquid,
        OrbStyle.Reactor => Reactor,
        OrbStyle.Minimal => Minimal,
        _ => Led,
    };

    /// <summary>Unit baseline below the center (S), or null.</summary>
    public double? UnitBaseline => UnitSize is null ? null : ValueBaseline + UnitGap;

    /// <summary>Title baseline below the center (S).</summary>
    public double TitleBaseline => TitleFixed ?? (UnitBaseline ?? ValueBaseline) + TitleGap;

    /// <summary>Unit and title never go below 7 pt (S = diameter in DIP).</summary>
    public static double TextSize(double fraction, double s) => Math.Max(MinTextSize, fraction * s);
}
