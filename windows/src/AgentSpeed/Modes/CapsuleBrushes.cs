using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Theme;

namespace AgentSpeed.Modes;

/// <summary>Brushes of the effort capsule, its meter and bolts (DESIGN 4, Agents), per appearance and high contrast.</summary>
internal readonly record struct CapsuleBrushes(bool Dark, bool HighContrast)
{
    public const string SecondaryKey = "TextFillColorSecondaryBrush";

    /// <summary>Everything a configured value draws: quiet, like a note beside a measurement.</summary>
    public const string TertiaryKey = "TextFillColorTertiaryBrush";

    /// <summary>The label color (black or white) at <paramref name="opacity"/>.</summary>
    public Brush Label(double opacity) => MediaColors.Frozen((Dark ? Colors.White : Colors.Black) with { A = (byte)Math.Round(255 * opacity) });

    public Brush? Fill => HighContrast ? null : Label(EffortCapsulePalette.FillOpacity(Dark));

    public Brush Stroke => HighContrast ? SystemColors.WindowTextBrush : Label(EffortCapsulePalette.StrokeOpacity);

    public double StrokeThickness => HighContrast ? 1 : 0.5;

    /// <summary>The configured form's dashed stroke; the same dashes at 1 DIP in high contrast.</summary>
    public Brush DashedStroke => HighContrast ? SystemColors.WindowTextBrush : Label(EffortCapsulePalette.ConfiguredStrokeOpacity(Dark));

    public double DashedThickness => HighContrast ? 1 : EffortCapsule.DashedStroke;

    public Brush Unlit => HighContrast ? SystemColors.GrayTextBrush : Label(EffortCapsulePalette.UnlitOpacity(Dark));

    public Brush ConfiguredUnlit => HighContrast ? SystemColors.GrayTextBrush : Label(EffortCapsulePalette.ConfiguredUnlitOpacity(Dark));

    public Brush Bolt => HighContrast ? SystemColors.HighlightBrush : EffortCapsulePalette.Bolt(Dark).ToBrush();

    public Brush Heat(double heat) => EffortCapsulePalette.Heat(heat, Dark).ToBrush();

    /// <summary>The configured bolts in high contrast; null otherwise, for the tertiary text resource: never amber.</summary>
    public Brush? ConfiguredBolt => HighContrast ? SystemColors.WindowTextBrush : null;

    /// <summary>The text resource a tone's parts take where it has no brush of its own.</summary>
    public static string TextKey(EffortTone tone) => tone == EffortTone.Configured ? TertiaryKey : SecondaryKey;

    /// <summary>A lit bar; null for the neutral and configured tones, which take a text resource (<see cref="TextKey"/>).</summary>
    public Brush? LitBar(EffortTone tone, double barHeight)
    {
        if (HighContrast)
        {
            return SystemColors.WindowTextBrush;
        }

        switch (tone)
        {
            case EffortTone.Max:
                return Heat(EffortCapsulePalette.MaxHeat);
            case EffortTone.TopRung:
                var (top, bottom) = EffortCapsulePalette.TopRungBar(barHeight, Dark);
                return Frozen(new LinearGradientBrush(top.ToColor(), bottom.ToColor(), new Point(0, 0), new Point(0, 1)));
            default:
                return null;
        }
    }

    /// <summary>The word; null for the neutral and configured tones (<see cref="TextKey"/>).</summary>
    public Brush? Word(EffortTone tone)
    {
        if (HighContrast)
        {
            return SystemColors.WindowTextBrush;
        }

        return tone switch
        {
            EffortTone.Max => Heat(EffortCapsulePalette.MaxHeat),
            EffortTone.TopRung => Frozen(new LinearGradientBrush(
                EffortCapsulePalette.Heat(EffortCapsulePalette.TopRungLow, Dark).ToColor(),
                EffortCapsulePalette.Heat(EffortCapsulePalette.TopRungHigh, Dark).ToColor(), new Point(0, 0), new Point(1, 0))),
            _ => null,
        };
    }

    /// <summary>The top rung's glow color: the middle of its gradient.</summary>
    public Color Glow => EffortCapsulePalette.Heat((EffortCapsulePalette.TopRungLow + EffortCapsulePalette.TopRungHigh) / 2, Dark).ToColor();

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
