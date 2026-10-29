using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Perf;

/// <summary>
/// How the score card draws (DESIGN 12, <i>Colors</i>, <i>Window</i>): the appearance, Windows high contrast, Reduce Motion
/// and transparency effects. High contrast takes system colors only: fills in the highlight color, tracks, ticks and dots in
/// the gray-text color, chips in the text color with a 1 px stroke and no fill.
/// </summary>
internal readonly record struct PerfCardAppearance(bool Dark, bool HighContrast, bool ReduceMotion, bool Transparency = true)
{
    public const string PrimaryKey = "TextFillColorPrimaryBrush";
    public const string SecondaryKey = "TextFillColorSecondaryBrush";
    public const string TertiaryKey = "TextFillColorTertiaryBrush";

    /// <summary>The card's fill is solid: transparency effects off, or high contrast.</summary>
    public bool Solid => !Transparency || HighContrast;

    /// <summary>The mini meters' fill.</summary>
    public Brush Ink => HighContrast ? SystemColors.HighlightBrush : PerfColors.ScoreInk(Dark).ToBrush();

    public Brush Track => HighContrast ? SystemColors.GrayTextBrush : PerfColors.Track(Dark).ToBrush();

    /// <summary>A high contrast track is a hairline outline, not a fill, so a fill stays distinct from it.</summary>
    public bool OutlinedTracks => HighContrast;

    /// <summary>The dots of an absent component's track: the label color at 18 %; high contrast gray text.</summary>
    public Brush AbsentDots => HighContrast ? SystemColors.GrayTextBrush : Label(PerfColors.TickOpacity);

    /// <summary>The meanings, hints and the footnote: a little darker than tertiary.</summary>
    public Brush Caption => HighContrast ? SystemColors.WindowTextBrush : PerfColors.CaptionInk(Dark).ToBrush();

    /// <summary>The label color (black or white) at <paramref name="opacity"/>; high contrast the text color.</summary>
    public Brush Label(double opacity) =>
        HighContrast ? SystemColors.WindowTextBrush : MediaColors.Frozen((Dark ? Colors.White : Colors.Black) with { A = (byte)Math.Round(255 * opacity) });

    /// <summary>The scale's ticks at <paramref name="opacity"/> of the label color; high contrast gray text.</summary>
    public Brush Tick(double opacity) => HighContrast ? SystemColors.GrayTextBrush : Label(opacity);

    /// <summary>The scale's fill: the pearl gradient across the whole <paramref name="width"/>; high contrast the highlight color.</summary>
    public Brush Pearl(double width)
    {
        if (HighContrast)
        {
            return SystemColors.HighlightBrush;
        }

        var (c, a, b) = PerfColors.Pearl(Dark);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(Math.Max(1, width), 0),
            GradientStops = { new GradientStop(c.ToColor(), 0), new GradientStop(a.ToColor(), 0.5), new GradientStop(b.ToColor(), 1) },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>The card's fill with transparency effects off (high contrast: the window color).</summary>
    public Brush SolidFill => HighContrast ? SystemColors.WindowBrush : PerfColors.SolidCard(Dark).ToBrush();

    /// <summary>A chip's text: mint above usual, else the secondary color (null); high contrast the text color.</summary>
    public Brush? ChipText(PerfChipTone tone) =>
        HighContrast ? SystemColors.WindowTextBrush : PerfColors.ChipText(tone, Dark) is { } color ? color.ToBrush() : null;

    public Brush ChipFill(PerfChipTone tone) => HighContrast ? Brushes.Transparent : PerfColors.ChipFill(tone, Dark).ToBrush();

    /// <summary>The outlined chips' dotted outline; high contrast draws every chip with a 1 px solid stroke in its text color.</summary>
    public Brush ChipOutline => HighContrast ? SystemColors.WindowTextBrush : Label(PerfColors.OutlineOpacity);

    /// <summary>The amber loop glyph of the dashboard's looping mark.</summary>
    public Brush LoopGlyph => new Modes.CapsuleBrushes(Dark, HighContrast).Bolt;
}
