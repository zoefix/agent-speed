using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Settings.Sources;

namespace AgentSpeed.Settings.Pages;

internal sealed partial class SourcesPage : UserControl
{
    private SourcesPaneViewModel? _pane;

    public SourcesPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as SettingsViewModel)?.SourcesPane);
    }

    private void Attach(SourcesPaneViewModel? pane)
    {
        if (_pane is not null)
        {
            _pane.RowRemoved -= OnRowRemoved;
        }

        _pane = pane;
        if (_pane is not null)
        {
            _pane.RowRemoved += OnRowRemoved;
        }
    }

    /// <summary>A removed row took keyboard focus with it: focus stays in its group, on Add Folder… (else Reset).</summary>
    private void OnRowRemoved(SourceGroupViewModel group)
    {
        if (!IsKeyboardFocusWithin && Keyboard.FocusedElement is not null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Groups.ItemContainerGenerator.ContainerFromItem(group) is not DependencyObject container)
            {
                return;
            }

            foreach (var name in new[] { "AddButton", "ResetButton" })
            {
                if (Find<Button>(container, name) is { IsEnabled: true } button)
                {
                    button.Focus();
                    return;
                }
            }
        });
    }

    private static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            if (Find<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
