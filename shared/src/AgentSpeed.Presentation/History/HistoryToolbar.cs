namespace AgentSpeed.Presentation.History;

/// <summary>How far the Usage History toolbar narrows to stay on one line, from roomiest to tightest.</summary>
public enum ToolbarDensity
{
    Full,

    /// <summary>The export button shows only its icon.</summary>
    IconExport,

    /// <summary>Range items lose part of their side padding.</summary>
    CompactRange,

    /// <summary>The source picker is narrower too (a long source name is trimmed).</summary>
    CompactPicker,

    /// <summary>The least padding the range items keep.</summary>
    Tight,
}

/// <summary>
/// Layout of the Usage History toolbar (DESIGN 4b, item 1): source picker (with the <c>history.includeSubscriptions</c>
/// checkbox while a source is marked as a subscription, DESIGN 9), range segments and export on one line, narrowing the
/// labels' room step by step instead of wrapping, down to the 720 DIP minimum window width.
/// </summary>
public static class HistoryToolbar
{
    /// <summary>The subscriptions icon toggle; a little narrower at the tightest density.</summary>
    public const double SubscriptionsIconWidth = 32;

    public const double TightSubscriptionsIconWidth = 28;

    /// <summary>Border and inner padding of the segmented container, both sides together.</summary>
    public const double RangeChrome = 6;

    /// <summary>Space between the icon and the label of the export button.</summary>
    public const double ExportLabelGap = 8;

    /// <summary>The source picker's usual minimum width.</summary>
    public const double PickerMinWidth = 184;

    /// <summary>The widest the source picker grows for a long source name.</summary>
    public const double PickerMaxWidth = 240;

    /// <summary>The source picker's fixed width once it narrows.</summary>
    public const double CompactPickerWidth = 160;

    /// <summary>The roomiest density whose total width fits; the tightest one when none does.</summary>
    /// <param name="labelWidths">Width of each range label as drawn when selected (semibold).</param>
    /// <param name="pickerWidth">The source picker at its normal minimum width (wider when its content needs it).</param>
    /// <param name="exportIconWidth">The export button with its icon only.</param>
    /// <param name="exportLabelWidth">The export label text.</param>
    /// <param name="subscriptionsLabelWidth">The subscriptions checkbox with its label; 0 when it is not offered.</param>
    /// <param name="subscriptionsIconWidth">The subscriptions toggle as an icon; 0 when it is not offered.</param>
    public static ToolbarDensity Fit(double available, IReadOnlyList<double> labelWidths, double pickerWidth, double exportIconWidth,
        double exportLabelWidth, double subscriptionsLabelWidth = 0, double subscriptionsIconWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(labelWidths);
        foreach (var density in Enum.GetValues<ToolbarDensity>())
        {
            if (Width(density, labelWidths, pickerWidth, exportIconWidth, exportLabelWidth, subscriptionsLabelWidth, subscriptionsIconWidth)
                <= available + 0.5)
            {
                return density;
            }
        }

        return ToolbarDensity.Tight;
    }

    /// <summary>Everything on the toolbar line at <paramref name="density"/>.</summary>
    public static double Width(ToolbarDensity density, IReadOnlyList<double> labelWidths, double pickerWidth, double exportIconWidth,
        double exportLabelWidth, double subscriptionsLabelWidth = 0, double subscriptionsIconWidth = 0) =>
        PickerWidth(density, pickerWidth) + SubscriptionsWidth(density, subscriptionsLabelWidth, subscriptionsIconWidth)
        + 2 * RangeMargin(density) + RangeWidth(density, labelWidths)
        + exportIconWidth + (ShowsExportLabel(density) ? ExportLabelGap + exportLabelWidth : 0);

    /// <summary>The subscriptions checkbox with its gap: labelled while the range keeps its full padding, then an icon toggle.</summary>
    public static double SubscriptionsWidth(ToolbarDensity density, double labelWidth, double iconWidth)
    {
        var width = ShowsSubscriptionsLabel(density) ? labelWidth : SubscriptionsIcon(density, iconWidth);
        return double.IsFinite(width) && width > 0 ? SubscriptionsGap(density) + width : 0;
    }

    /// <summary>Space between the source picker and the subscriptions checkbox (less at the tightest density).</summary>
    public static double SubscriptionsGap(ToolbarDensity density) => density == ToolbarDensity.Tight ? 4 : 8;

    /// <summary>The icon toggle's width at <paramref name="density"/>, given its usual <paramref name="iconWidth"/>.</summary>
    public static double SubscriptionsIcon(ToolbarDensity density, double iconWidth) =>
        density == ToolbarDensity.Tight ? Math.Min(iconWidth, TightSubscriptionsIconWidth) : iconWidth;

    /// <summary>The checkbox shows its label (<c>history.includeSubscriptions</c>); tighter, it becomes an icon toggle with the label as its tooltip.</summary>
    public static bool ShowsSubscriptionsLabel(ToolbarDensity density) => density <= ToolbarDensity.IconExport;

    /// <summary>The picker's width: as wide as it needs (at least <see cref="PickerMinWidth"/>), or fixed once compact.</summary>
    public static double PickerWidth(ToolbarDensity density, double pickerWidth) =>
        NarrowsPicker(density) ? CompactPickerWidth : Math.Max(PickerMinWidth, double.IsFinite(pickerWidth) ? pickerWidth : 0);

    public static bool NarrowsPicker(ToolbarDensity density) => density >= ToolbarDensity.CompactPicker;

    /// <summary>The segmented control: every item at its padding and minimum width, inside the container.</summary>
    public static double RangeWidth(ToolbarDensity density, IReadOnlyList<double> labelWidths)
    {
        ArgumentNullException.ThrowIfNull(labelWidths);
        var padding = ItemPadding(density);
        var minimum = ItemMinWidth(density);
        return RangeChrome + labelWidths.Sum(w => Math.Max(minimum, Math.Ceiling(w) + 2 * padding));
    }

    public static bool ShowsExportLabel(ToolbarDensity density) => density == ToolbarDensity.Full;

    /// <summary>Left and right padding of each range item.</summary>
    public static double ItemPadding(ToolbarDensity density) => density switch
    {
        ToolbarDensity.Full or ToolbarDensity.IconExport => 10,
        ToolbarDensity.CompactRange or ToolbarDensity.CompactPicker => 6,
        _ => 4,
    };

    public static double ItemMinWidth(ToolbarDensity density) => density switch
    {
        ToolbarDensity.Full or ToolbarDensity.IconExport => 40,
        ToolbarDensity.CompactRange or ToolbarDensity.CompactPicker => 32,
        _ => 28,
    };

    /// <summary>Space between the segmented control and its neighbours, each side.</summary>
    public static double RangeMargin(ToolbarDensity density) => density switch
    {
        ToolbarDensity.Full or ToolbarDensity.IconExport => 12,
        ToolbarDensity.CompactRange or ToolbarDensity.CompactPicker => 6,
        _ => 4,
    };
}
