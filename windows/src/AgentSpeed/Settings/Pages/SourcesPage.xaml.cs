using System.Media;
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
    private bool _savingKey;

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

    /// <summary>Enter saves the pasted ZCode key; keyboard focus then moves to Remove.</summary>
    private void OnZcodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is PasswordBox box)
        {
            SaveZcodeKey(box, focusRemove: true);
            e.Handled = true;
        }
    }

    /// <summary>A pasted key is also saved when the field loses focus (DESIGN 5).</summary>
    private void OnZcodeKeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is PasswordBox { Password.Length: > 0 } box)
        {
            SaveZcodeKey(box, focusRemove: false);
        }
    }

    private void OnZcodeKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { Parent: Grid entry } box && entry.Children.OfType<TextBlock>().FirstOrDefault() is { } placeholder)
        {
            placeholder.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>After Remove the empty field takes keyboard focus, ready for the next key.</summary>
    private void OnZcodeRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { IsKeyboardFocused: true } button)
        {
            FocusInFields(button, "ZcodeKeyBox");
        }
    }

    /// <summary>
    /// The field is cleared right after the key is handed over, saved or not, so the key never stays in it; a key that is
    /// not one (or that Credential Manager refused) beeps.
    /// </summary>
    private void SaveZcodeKey(PasswordBox box, bool focusRemove)
    {
        if (_savingKey || box.DataContext is not ZcodeFieldsViewModel zcode)
        {
            return;
        }

        _savingKey = true;
        try
        {
            var saved = zcode.Save(box.Password);
            box.Clear();
            if (!saved)
            {
                SystemSounds.Beep.Play();
            }
            else if (focusRemove)
            {
                FocusInFields(box, "ZcodeRemove");
            }
        }
        finally
        {
            _savingKey = false;
        }
    }

    /// <summary>Focuses a named control of the same ZCode fields once the saved and empty states have swapped.</summary>
    private void FocusInFields(DependencyObject from, string name)
    {
        var fields = from;
        while (fields is not null && fields is not FrameworkElement { Name: "ZcodeFields" })
        {
            fields = VisualTreeHelper.GetParent(fields);
        }

        if (fields is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Find<Control>(fields, name) is { IsVisible: true, IsEnabled: true } control)
            {
                control.Focus();
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
