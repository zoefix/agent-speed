using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.TaskScore;

/// <summary>
/// The task score's own colors (DESIGN 12, <i>Colors</i> and <i>State chips</i>). Text uses the system label colors;
/// high contrast replaces all of these with system colors.
/// </summary>
public static class TaskScoreColors
{
    /// <summary>Bars, the composition and dotted outlines: the mock-up's muted slate blue.</summary>
    public static Rgba ScoreInk(bool dark) => dark ? Rgba.FromHex("#93A4D6") : Rgba.FromHex("#4B5FA6");

    /// <summary>Tracks: white 9 % on dark, black 7 % on light.</summary>
    public static Rgba Track(bool dark) => dark ? new Rgba(1, 1, 1, 0.09) : new Rgba(0, 0, 0, 0.07);

    /// <summary>The chip's text color; null for the neutral chip, which takes the secondary text color.</summary>
    public static Rgba? ChipText(TaskChipTone tone, bool dark) => tone switch
    {
        TaskChipTone.Mint => dark ? Rgba.FromHex("#6EE7B7") : Rgba.FromHex("#047857"),
        TaskChipTone.Amber => dark ? Rgba.FromHex("#FBBF24") : Rgba.FromHex("#B45309"),
        _ => null,
    };

    /// <summary>The chip's fill; transparent for the neutral chip.</summary>
    public static Rgba ChipFill(TaskChipTone tone, bool dark) => tone switch
    {
        TaskChipTone.Mint => dark ? Rgba255(52, 211, 153, 0.14) : Rgba255(16, 185, 129, 0.12),
        TaskChipTone.Amber => dark ? Rgba255(245, 165, 36, 0.14) : Rgba255(245, 158, 11, 0.14),
        _ => Rgba.Transparent,
    };

    /// <summary>The neutral chip's dotted outline: the label color at 35 %.</summary>
    public const double NeutralOutline = 0.35;

    /// <summary>A prediction cell's fill: the label color at 5 % (dark 6 %).</summary>
    public static double PredictionFill(bool dark) => dark ? 0.06 : 0.05;

    /// <summary>
    /// The card's reading text — the dimensions' meanings, the prediction note, the footnote: the prototype's description
    /// ink, a little darker than tertiary, so two lines at 10.5 DIP keep 4.5:1 on the card (DESIGN 7). High contrast uses
    /// the system text color.
    /// </summary>
    public static Rgba CaptionInk(bool dark) => dark ? Rgba255(235, 235, 245, 0.56) : Rgba255(60, 60, 67, 0.72);

    /// <summary>The card's solid fill with transparency effects off.</summary>
    public static Rgba SolidCard(bool dark) => dark ? Rgba.FromHex("#26272B") : Rgba.FromHex("#FFFFFF");

    private static Rgba Rgba255(int r, int g, int b, double a) => new(r / 255.0, g / 255.0, b / 255.0, a);
}
