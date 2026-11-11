using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// The widths every quota line of a section shares (DESIGN 4): the widest label, the <c>100%</c> cell and the widest reset
/// text (with its age where the age is shown), so bars and percents line up across rows. Lines report what they need and
/// are measured again when a shared width changes. Each section (Sources, Hosts) has its own.
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
/// <see cref="CompactBar"/>), 8, the percent right-aligned in a cell as wide as <c>100%</c>, 8, then the reset in the rest of
/// the row, its tail truncated, and the age after it. The age is shown only while the line still holds it with a
/// <see cref="MinBar"/> bar; otherwise the reset keeps its room and the age moves to the tooltip (<see cref="AgeShown"/>).
/// Children in that order: label, bar, percent cell, reset, age.
/// </summary>
internal sealed class QuotaLinePanel : Panel
{
    public const double Gap = 8;

    /// <summary>The bar's comfortable length: an age is only added after the reset while the bar keeps it.</summary>
    public const double MinBar = 88;

    /// <summary>The shortest bar, for sections with long group labels (<c>Claude/GPT 7d</c>): the reset keeps its room first.</summary>
    public const double CompactBar = 52;

    /// <summary>
    /// The section's shared columns, set once on the section and inherited by every line in it (an inherited value never waits
    /// for a binding to find its ancestor, so a line that appears later still joins the section).
    /// </summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(QuotaColumns), typeof(QuotaLinePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, e) =>
        {
            if (d is QuotaLinePanel panel)
            {
                if (e.OldValue is QuotaColumns old)
                {
                    old.Forget(panel);
                }

                panel.InvalidateMeasure();
            }
        }));

    private static readonly DependencyPropertyKey AgeShownKey = DependencyProperty.RegisterReadOnly(
        nameof(AgeShown), typeof(bool), typeof(QuotaLinePanel), new PropertyMetadata(true));

    /// <summary>False while the line has an age it cannot hold: the tooltip carries it instead.</summary>
    public static readonly DependencyProperty AgeShownProperty = AgeShownKey.DependencyProperty;

    /// <summary>The children's natural widths (label, percent cell, reset, age) from the last measure.</summary>
    private (double Label, double Percent, double Reset, double Age) _natural;

    private bool _ageFits = true;

    public QuotaLinePanel()
    {
        // A line that leaves the tree leaves the section's widths; coming back, it reports again.
        Unloaded += (_, _) => Columns?.Forget(this);
        Loaded += (_, _) => InvalidateMeasure();
    }

    public QuotaColumns? Columns
    {
        get => (QuotaColumns?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static QuotaColumns? GetColumns(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (QuotaColumns?)element.GetValue(ColumnsProperty);
    }

    public static void SetColumns(DependencyObject element, QuotaColumns? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnsProperty, value);
    }

    public bool AgeShown => (bool)GetValue(AgeShownProperty);

    private bool HasAge => _natural.Age > 0;

    private double ResetNeed => _natural.Reset + (HasAge && _ageFits ? _natural.Age : 0);

    private (double Label, double Bar, double Percent, double Reset) Widths(double available)
    {
        var columns = Columns;
        var label = Math.Max(columns?.Label ?? 0, _natural.Label);
        var percent = Math.Max(columns?.Percent ?? 0, _natural.Percent);
        var reset = Math.Max(columns?.Reset ?? 0, ResetNeed);
        var fixedPart = label + percent + 3 * Gap;
        var bar = Math.Max(CompactBar, available - fixedPart - reset);
        var resetRoom = Math.Max(0, available - fixedPart - bar);
        return (label, bar, percent, resetRoom);
    }

    private double Desired(int index) => InternalChildren.Count > index ? InternalChildren[index].DesiredSize.Width : 0;

    /// <summary>A text's room rounded up with a pixel to spare, so layout rounding never trims a reset that fits.</summary>
    private static double Whole(double width) => width > 0 ? Math.Ceiling(width) + 1 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var height = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(infinite);
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }

        _natural = (Desired(0), Desired(2), Whole(Desired(3)), Whole(Desired(4)));
        var available = double.IsFinite(availableSize.Width) ? availableSize.Width : MinBar * 3;
        var columns = Columns;
        columns?.Report(this, _natural.Label, _natural.Percent, ResetNeed);

        // The age stays only while the reset and the age fit beside a comfortable bar in the section's columns.
        var label = Math.Max(columns?.Label ?? 0, _natural.Label);
        var percent = Math.Max(columns?.Percent ?? 0, _natural.Percent);
        var fits = !HasAge || _natural.Reset + _natural.Age <= available - label - percent - MinBar - 3 * Gap + 0.5;
        if (fits != _ageFits)
        {
            _ageFits = fits;
            columns?.Report(this, _natural.Label, _natural.Percent, ResetNeed);
        }

        SetValue(AgeShownKey, !HasAge || _ageFits);
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
        if (InternalChildren.Count > 4)
        {
            // Shown, the age follows the whole reset (it only shows where both fit); hidden, it takes no room.
            var shown = HasAge && _ageFits;
            var left = x + Math.Min(reset, InternalChildren[3].DesiredSize.Width);
            Place(4, left, shown ? Math.Max(0, x + reset - left) : 0, h, alignRight: false);
            InternalChildren[4].Opacity = shown ? 1 : 0;
        }

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
