using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Perf;

/// <summary>
/// Lays out the card's score row (DESIGN 12, <i>Score card</i>, 2): the number at the left; the usual chip trailing on the
/// same row, its centre level with the digits' cap-height centre; the scale under it, its track 10 DIP under the number's
/// baseline and as wide as the content.
/// </summary>
internal sealed class PerfScoreBlock : Panel
{
    public const double ScaleGap = 10;

    public PerfScoreBlock(PerfNumberView number, PerfChipView chip, PerfScaleView scale)
    {
        Number = number;
        Chip = chip;
        Scale = scale;
        Children.Add(number);
        Children.Add(chip);
        Children.Add(scale);
    }

    public PerfNumberView Number { get; }

    public PerfChipView Chip { get; }

    public PerfScaleView Scale { get; }

    /// <summary>Where the scale's track starts inside it: the reference tick reaches above it.</summary>
    private static double TrackTop => (PerfScaleView.TickExtra + PerfScaleView.ReferenceExtra) / 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        Number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Scale.Measure(new Size(width, double.PositiveInfinity));
        var scaleTop = Number.Baseline + ScaleGap - TrackTop;
        var height = Math.Max(Number.DesiredSize.Height, scaleTop + Scale.DesiredSize.Height);
        return new Size(width, Math.Ceiling(height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Number.Arrange(new Rect(0, 0, Number.DesiredSize.Width, Number.DesiredSize.Height));
        var capCentre = Number.Baseline - Number.CapHeight / 2;
        var chip = Chip.DesiredSize;
        Chip.Arrange(new Rect(Math.Max(0, finalSize.Width - chip.Width), Math.Round(capCentre - chip.Height / 2), chip.Width, chip.Height));
        var scaleTop = Number.Baseline + ScaleGap - TrackTop;
        Scale.Arrange(new Rect(0, scaleTop, finalSize.Width, Scale.DesiredSize.Height));
        return finalSize;
    }
}
