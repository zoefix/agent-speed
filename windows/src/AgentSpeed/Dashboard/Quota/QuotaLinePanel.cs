using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// The widths every quota line of the Sources section shares (DESIGN 4): the widest label, the <c>100%</c> cell and the
/// widest reset text, so bars and percents line up across rows. Lines report what they need and are measured again
/// when a shared width changes.
/// </summary>
internal sealed class QuotaColumns
{
    private readonly Dictionary<QuotaLinePanel, (double Label, double Percent, double Reset)> _wants = [];

    public double Label { get; private set; }

    public double Percent { get; private set; }

    public double Reset { get; private set; }

    public void Report(QuotaLinePanel line, double label, double percent, double reset)
    {
        _wants[line] = (label, percent, reset);
        Recompute(except: line);
    }

    public void Forget(QuotaLinePanel line)
    {
        if (_wants.Remove(line))
        {
            Recompute(except: null);
        }
    }

    private void Recompute(QuotaLinePanel? except)
    {
        var label = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.Label);
        var percent = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.Percent);
        var reset = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.Reset);
        if (Math.Abs(label - Label) < 0.01 && Math.Abs(percent - Percent) < 0.01 && Math.Abs(reset - Reset) < 0.01)
        {
            return;
        }

        (Label, Percent, Reset) = (label, percent, reset);
        foreach (var line in _wants.Keys)
        {
            if (!ReferenceEquals(line, except))
            {
                line.InvalidateMeasure();
            }
        }
    }
}

/// <summary>
/// One quota line (DESIGN 4): the label in the section's label column, 8, the bar (flexible, at least
/// <see cref="MinBar"/>), 8, the percent right-aligned in a cell as wide as <c>100%</c>, 8, then the reset in the rest
/// of the row, its tail truncated. Children in that order: label, bar, percent cell, reset.
/// </summary>
internal sealed class QuotaLinePanel : Panel
{
    public const double Gap = 8;
    public const double MinBar = 88;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(QuotaColumns), typeof(QuotaLinePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) =>
        {
            if (e.OldValue is QuotaColumns old)
            {
                old.Forget((QuotaLinePanel)d);
            }
        }));

    /// <summary>The children's natural widths (label, percent cell, reset) from the last measure.</summary>
    private (double Label, double Percent, double Reset) _natural;

    public QuotaLinePanel()
    {
        Unloaded += (_, _) => Columns?.Forget(this);
    }

    public QuotaColumns? Columns
    {
        get => (QuotaColumns?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private (double Label, double Bar, double Percent, double Reset) Widths(double available)
    {
        var columns = Columns;
        var label = Math.Max(columns?.Label ?? 0, _natural.Label);
        var percent = Math.Max(columns?.Percent ?? 0, _natural.Percent);
        var reset = Math.Max(columns?.Reset ?? 0, _natural.Reset);
        var fixedPart = label + percent + 3 * Gap;
        var bar = Math.Max(MinBar, available - fixedPart - reset);
        var resetRoom = Math.Max(0, available - fixedPart - bar);
        return (label, bar, percent, resetRoom);
    }

    private double Desired(int index) => InternalChildren.Count > index ? InternalChildren[index].DesiredSize.Width : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var height = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(infinite);
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }

        _natural = (Desired(0), Desired(2), Desired(3));
        Columns?.Report(this, _natural.Label, _natural.Percent, _natural.Reset);
        var available = double.IsFinite(availableSize.Width) ? availableSize.Width : MinBar * 3;
        var widths = Widths(available);
        if (InternalChildren.Count > 3)
        {
            InternalChildren[3].Measure(new Size(widths.Reset, double.PositiveInfinity));
        }

        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : widths.Label + widths.Bar + widths.Percent + widths.Reset + 3 * Gap,
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (label, bar, percent, reset) = Widths(finalSize.Width);
        var h = finalSize.Height;
        var x = 0.0;
        Place(0, x, label, h, alignRight: false);
        x += label + Gap;
        Place(1, x, bar, h, alignRight: false);
        x += bar + Gap;
        Place(2, x, percent, h, alignRight: true);
        x += percent + Gap;
        Place(3, x, reset, h, alignRight: false);
        return finalSize;
    }

    private void Place(int index, double x, double width, double height, bool alignRight)
    {
        if (InternalChildren.Count <= index)
        {
            return;
        }

        var child = InternalChildren[index];
        var w = index == 1 ? width : Math.Min(width, child.DesiredSize.Width);
        var left = alignRight ? x + width - w : x;
        var top = (height - child.DesiredSize.Height) / 2;
        child.Arrange(new Rect(left, index == 1 ? 0 : top, Math.Max(0, w), index == 1 ? height : child.DesiredSize.Height));
    }
}
