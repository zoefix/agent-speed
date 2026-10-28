namespace AgentSpeed.Presentation.Layout;

/// <param name="Columns">Items per row.</param>
/// <param name="Widths">Width of each column: its widest item.</param>
public sealed record AlignedGridLayout(int Columns, IReadOnlyList<double> Widths, double Width);

/// <summary>
/// A grid filled row by row whose columns are as wide as their widest item. It takes the first column count that fits
/// (the speed-title list offers 4, 3 and 2, which all divide its twelve titles into whole rows).
/// </summary>
public static class AlignedGrid
{
    public static AlignedGridLayout Choose(IReadOnlyList<double> itemWidths, double available, IReadOnlyList<int> columnChoices, double spacing)
    {
        ArgumentNullException.ThrowIfNull(itemWidths);
        ArgumentNullException.ThrowIfNull(columnChoices);
        if (columnChoices.Count == 0)
        {
            throw new ArgumentException("At least one column count is needed.", nameof(columnChoices));
        }

        AlignedGridLayout? layout = null;
        foreach (var choice in columnChoices)
        {
            layout = For(itemWidths, Math.Max(1, choice), spacing);
            if (layout.Width <= available + 0.5)
            {
                return layout;
            }
        }

        return layout!;
    }

    public static AlignedGridLayout For(IReadOnlyList<double> itemWidths, int columns, double spacing)
    {
        ArgumentNullException.ThrowIfNull(itemWidths);
        columns = Math.Max(1, Math.Min(columns, Math.Max(1, itemWidths.Count)));
        var widths = new double[columns];
        for (var i = 0; i < itemWidths.Count; i++)
        {
            var width = double.IsFinite(itemWidths[i]) ? Math.Max(0, itemWidths[i]) : 0;
            widths[i % columns] = Math.Max(widths[i % columns], width);
        }

        return new AlignedGridLayout(columns, widths, widths.Sum() + spacing * (columns - 1));
    }
}
