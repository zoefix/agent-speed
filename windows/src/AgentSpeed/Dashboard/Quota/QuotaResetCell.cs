using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// A quota line's reset cell (DESIGN 4, Quota lines): the long reset and the short one, one of them shown as its section
/// decides (<see cref="UseShort"/>, set by <see cref="QuotaLinePanel"/>); switching never crossfades, each text still does
/// when it changes. Children: the long text, then the short one.
/// </summary>
internal sealed class QuotaResetCell : Grid
{
    private bool _useShort;

    public bool UseShort
    {
        get => _useShort;
        set
        {
            if (_useShort == value)
            {
                return;
            }

            _useShort = value;
            Show();
        }
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        Show();
    }

    private void Show()
    {
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Visibility = (i == 1) == _useShort ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
