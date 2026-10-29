using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Perf;

/// <summary>
/// The performance score's own colors (DESIGN 12, <i>Colors</i> and <i>Chips</i>). Text uses the system label colors;
/// high contrast replaces all of these with system colors.
/// </summary>
public static class PerfColors
{
    /// <summary>The score ink of mini meters and History meters: a quiet slate blue, distinct from every source color.</summary>
    public static Rgba ScoreInk(bool dark) => dark ? Rgba.FromHex("#93A4D6") : Rgba.FromHex("#4B5FA6");

    /// <summary>Tracks: white 9 % on dark, black 7 % on light.</summary>
    public static Rgba Track(bool dark) => dark ? new Rgba(1, 1, 1, 0.09) : new Rgba(0, 0, 0, 0.07);

    /// <summary>The usual chip's text above the usual value; null for the neutral chips, which take the secondary text color.</summary>
    public static Rgba? ChipText(PerfChipTone tone, bool dark) => tone == PerfChipTone.Mint
        ? dark ? Rgba.FromHex("#6EE7B7") : Rgba.FromHex("#047857")
        : null;

    /// <summary>The chip's fill: mint above usual, the label color at 6 % at or below it, none for the outlined chips.</summary>
    public static Rgba ChipFill(PerfChipTone tone, bool dark) => tone switch
    {
        PerfChipTone.Mint => dark ? Rgba255(52, 211, 153, 0.14) : Rgba255(16, 185, 129, 0.12),
        PerfChipTone.Neutral => dark ? new Rgba(1, 1, 1, 0.06) : new Rgba(0, 0, 0, 0.06),
        _ => Rgba.Transparent,
    };

    /// <summary>The outlined chips' dotted outline: the label color at 35 %.</summary>
    public const double OutlineOpacity = 0.35;

    /// <summary>The card scale's pearl gradient (the board glass arc's colors), from its start to its end.</summary>
    public static (Rgba C, Rgba A, Rgba B) Pearl(bool dark) => dark
        ? (Rgba.FromHex("#A5F3FC"), Rgba.FromHex("#C7D2FE"), Rgba.FromHex("#F5D0FE"))
        : (Rgba.FromHex("#1AA3C4"), Rgba.FromHex("#6D7CF0"), Rgba.FromHex("#C05BD9"));

    /// <summary>
    /// The card's reading text — the components' meanings, hints and the footnote: a little darker than tertiary, so two
    /// lines at 10.5 DIP keep 4.5:1 on the card (DESIGN 7). High contrast uses the system text color.
    /// </summary>
    public static Rgba CaptionInk(bool dark) => dark ? Rgba255(235, 235, 245, 0.56) : Rgba255(60, 60, 67, 0.72);

    /// <summary>The card's solid fill with transparency effects off.</summary>
    public static Rgba SolidCard(bool dark) => dark ? Rgba.FromHex("#26272B") : Rgba.FromHex("#FFFFFF");

    /// <summary>The scale's ticks: 500 and 2000 in the label color at 18 %, the reference 1000 at 45 %.</summary>
    public const double TickOpacity = 0.18;

    public const double ReferenceTickOpacity = 0.45;

    /// <summary>A resting fill is drawn at 45 %.</summary>
    public const double RestingFill = 0.45;

    private static Rgba Rgba255(int r, int g, int b, double a) => new(r / 255.0, g / 255.0, b / 255.0, a);
}
