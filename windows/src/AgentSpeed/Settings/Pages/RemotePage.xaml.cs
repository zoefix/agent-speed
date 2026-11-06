using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Localization;
using AgentSpeed.Settings.Remote;

namespace AgentSpeed.Settings.Pages;

/// <summary>Settings → 遠端主機 (DESIGN 13, <see cref="RemoteHostsPaneViewModel"/>).</summary>
internal sealed partial class RemotePage : UserControl
{
    public RemotePage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (Model is { } model)
            {
                model.IsVisible = IsVisible;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (Model is { } model)
            {
                model.IsVisible = IsVisible;
            }
        };
    }

    private RemoteHostsPaneViewModel? Model => DataContext as RemoteHostsPaneViewModel;

    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemoteHostRowViewModel row, ContextMenu: { } menu } && Model is { } model)
        {
            RemoteHostActionsMenu.Fill(menu, model, row, LocalizationSource.Instance.Localizer);
        }
        else
        {
            e.Handled = true;
        }
    }

    private void OnActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RemoteHostRowViewModel row } button || Model is not { } model)
        {
            return;
        }

        var menu = new ContextMenu();
        RemoteHostActionsMenu.Fill(menu, model, row, LocalizationSource.Instance.Localizer);
        RemoteHostActionsMenu.OpenUnder(menu, button);
    }
}
