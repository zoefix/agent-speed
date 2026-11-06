using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Controls;

/// <summary>
/// One line of two children side by side, aligned on their baselines' row: the first keeps its natural width (up to the
/// whole line) and the second gets what is left, so it gives way first. A host's name and its destination (DESIGN 13).
/// </summary>
internal sealed class KeepFirstPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            return default;
        }

        var first = children[0];
        first.Measure(availableSize);
        var width = Math.Min(first.DesiredSize.Width, availableSize.Width);
        var height = first.DesiredSize.Height;
        for (var i = 1; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(Math.Max(0, availableSize.Width - width), availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var width = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }
}
