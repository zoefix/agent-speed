using System.Windows;
using System.Windows.Controls;

namespace AgentSpeed.Settings.Remote;

/// <summary>An editor's Hosts section (<see cref="HostPickerViewModel"/>).</summary>
internal sealed partial class HostPickerSection : UserControl
{
    public HostPickerSection()
    {
        InitializeComponent();
    }

    private void OnHostClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HostPickerRowViewModel row } && DataContext is HostPickerViewModel picker)
        {
            picker.Toggle(row);
        }
    }
}
