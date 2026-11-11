using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// The widths every quota line of a section shares (DESIGN 4): the widest label, the <c>≈100%</c> cell, the widest amount
/// and the widest reset text (with its age where the age is shown, and without it in <see cref="ResetText"/>), so bars,
/// percents and resets line up across rows. Lines report what they need and are measured again when a shared width changes.
/// Each section (Sources, Hosts) has its own.
/// </summary>
internal sealed class QuotaColumns
{
    private readonly Dictionary<QuotaLinePanel, (double Label, double Percent, double Amount, double Reset, double ResetText)> _wants = [];

    public double Label { get; private set; }

    public double Percent { get; private set; }

    /// <summary>The widest amount (DESIGN 4 <i>Amount</i>); 0 while no line of the section has one.</summary>
    public double Amount { get; private set; }

    public double Reset { get; private set; }

    /// <summary>The widest reset text alone: the section's short resets show only while all of them fit.</summary>
    public double ResetText { get; private set; }

    public void Report(QuotaLinePanel line, double label, double percent, double amount, double reset, double resetText)
    {
        _wants[line] = (label, percent, amount, reset, resetText);
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
        var amount = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.Amount);
        var reset = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.Reset);
        var resetText = _wants.Count == 0 ? 0 : _wants.Values.Max(w => w.ResetText);
        if (Math.Abs(label - Label) < 0.01 && Math.Abs(percent - Percent) < 0.01 && Math.Abs(amount - Amount) < 0.01 && Math.Abs(reset - Reset) < 0.01
            && Math.Abs(resetText - ResetText) < 0.01)
        {
            return;
        }

        (Label, Percent, Amount, Reset, ResetText) = (label, percent, amount, reset, resetText);
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
/// <see cref="CompactBar"/>), 8, the percent right-aligned in a cell as wide as <c>≈100%</c>, 8, the amount left-aligned in
/// the section's amount column and 8 after it (neither while no line of the section has an amount), then the reset in the
/// rest of the row and the age after it. The reset is the long one, its tail truncated, while no line of the section has an
/// amount; beside amounts it is the short one (<see cref="ShortResets"/>, owner's choice 2026-11-11), shown only while every
/// short reset of the section fits beside a <see cref="CompactBar"/> bar, and the tooltip then opens with the long one. The
/// age is shown only while the line still holds it with a <see cref="MinBar"/> bar; otherwise the reset keeps its room and
/// the age moves to the tooltip (<see cref="AgeShown"/>). Children in that order: label, bar, percent cell, amount, reset
/// (a <see cref="QuotaResetCell"/>), age.
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

    private static readonly DependencyPropertyKey ShortResetsKey = DependencyProperty.RegisterReadOnly(
        nameof(ShortResets), typeof(bool), typeof(QuotaLinePanel), new PropertyMetadata(false));

    /// <summary>The section has amounts: the line shows the short reset (or none), and its tooltip opens with the long one.</summary>
    public static readonly DependencyProperty ShortResetsProperty = ShortResetsKey.DependencyProperty;

    private const int LabelChild = 0;
    private const int BarChild = 1;
    private const int PercentChild = 2;
    private const int AmountChild = 3;
    private const int ResetChild = 4;
    private const int AgeChild = 5;

    /// <summary>The children's natural widths (label, percent cell, amount, reset, age) from the last measure.</summary>
    private (double Label, double Percent, double Amount, double Reset, double Age) _natural;

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

    public bool ShortResets => (bool)GetValue(ShortResetsProperty);

    /// <summary>The section's short resets all fit the room the line gives its reset.</summary>
    private bool ResetFits(double room) => !ShortResets || room + 0.5 >= Math.Max(Columns?.ResetText ?? 0, _natural.Reset);

    private bool HasAge => _natural.Age > 0;

    private double ResetNeed => _natural.Reset + (HasAge && _ageFits ? _natural.Age : 0);

    /// <summary>The amount column with the gap after it; nothing while the section has no amount.</summary>
    private static double AmountPart(double amount) => amount > 0 ? amount + Gap : 0;

    private (double Label, double Bar, double Percent, double Amount, double Reset) Widths(double available)
    {
        var columns = Columns;
        var label = Math.Max(columns?.Label ?? 0, _natural.Label);
        var percent = Math.Max(columns?.Percent ?? 0, _natural.Percent);
        var amount = Math.Max(columns?.Amount ?? 0, _natural.Amount);
        var reset = Math.Max(columns?.Reset ?? 0, ResetNeed);
        var fixedPart = label + percent + AmountPart(amount) + 3 * Gap;
        var bar = Math.Max(CompactBar, available - fixedPart - reset);
        var resetRoom = Math.Max(0, available - fixedPart - bar);
        return (label, bar, percent, amount, resetRoom);
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

        var columns = Columns;
        var shortResets = Math.Max(columns?.Amount ?? 0, Whole(Desired(AmountChild))) > 0;
        SetValue(ShortResetsKey, shortResets);
        if (InternalChildren.Count > ResetChild && InternalChildren[ResetChild] is QuotaResetCell cell && cell.UseShort != shortResets)
        {
            cell.UseShort = shortResets;
            cell.Measure(infinite);
        }

        _natural = (Desired(LabelChild), Desired(PercentChild), Whole(Desired(AmountChild)), Whole(Desired(ResetChild)), Whole(Desired(AgeChild)));
        var available = double.IsFinite(availableSize.Width) ? availableSize.Width : MinBar * 3;
        columns?.Report(this, _natural.Label, _natural.Percent, _natural.Amount, ResetNeed, _natural.Reset);

        // The age stays only while the reset and the age fit beside a comfortable bar in the section's columns.
        var label = Math.Max(columns?.Label ?? 0, _natural.Label);
        var percent = Math.Max(columns?.Percent ?? 0, _natural.Percent);
        var amount = AmountPart(Math.Max(columns?.Amount ?? 0, _natural.Amount));
        var fits = !HasAge || _natural.Reset + _natural.Age <= available - label - percent - amount - MinBar - 3 * Gap + 0.5;
        if (fits != _ageFits)
        {
            _ageFits = fits;
            columns?.Report(this, _natural.Label, _natural.Percent, _natural.Amount, ResetNeed, _natural.Reset);
        }

        SetValue(AgeShownKey, !HasAge || _ageFits);
        var widths = Widths(available);
        if (InternalChildren.Count > ResetChild)
        {
            InternalChildren[ResetChild].Measure(new Size(widths.Reset, double.PositiveInfinity));
        }

        return new Size(double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : widths.Label + widths.Bar + widths.Percent + AmountPart(widths.Amount) + widths.Reset + 3 * Gap, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (label, bar, percent, amount, reset) = Widths(finalSize.Width);
        var h = finalSize.Height;
        var x = 0.0;
        Place(LabelChild, x, label, h, alignRight: false);
        x += label + Gap;
        Place(BarChild, x, bar, h, alignRight: false);
        x += bar + Gap;
        Place(PercentChild, x, percent, h, alignRight: true);
        x += percent + Gap;
        Place(AmountChild, x, amount, h, alignRight: false);
        x += AmountPart(amount);

        // Beside amounts the short resets show whole, or not at all.
        var resetShown = ResetFits(reset);
        Place(ResetChild, x, resetShown ? reset : 0, h, alignRight: false);
        if (InternalChildren.Count > ResetChild)
        {
            InternalChildren[ResetChild].Opacity = resetShown ? 1 : 0;
        }

        if (InternalChildren.Count > AgeChild)
        {
            // Shown, the age follows the whole reset (it only shows where both fit); hidden, it takes no room.
            var shown = HasAge && _ageFits && resetShown;
            var left = x + (resetShown ? Math.Min(reset, InternalChildren[ResetChild].DesiredSize.Width) : 0);
            Place(AgeChild, left, shown ? Math.Max(0, x + reset - left) : 0, h, alignRight: false);
            InternalChildren[AgeChild].Opacity = shown ? 1 : 0;
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
        var w = index == BarChild ? width : Math.Min(width, child.DesiredSize.Width);
        var left = alignRight ? x + width - w : x;
        var top = (height - child.DesiredSize.Height) / 2;
        child.Arrange(new Rect(left, index == BarChild ? 0 : top, Math.Max(0, w), index == BarChild ? height : child.DesiredSize.Height));
    }
}
