using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using AgentSpeed.Localization;
using AgentSpeed.Settings.Lan;

namespace AgentSpeed.Settings.Pages;

/// <summary>
/// Settings → API → 區域網路 (DESIGN 14, <see cref="LanPaneViewModel"/>). The server's rows expand in 0.2 s when it is turned
/// on (a fade with Reduce Motion); the port commits on Return or when it loses focus, Esc restores it.
/// </summary>
internal sealed partial class LanPage : UserControl
{
    private static readonly TimeSpan Expand = TimeSpan.FromSeconds(0.2);
    private LanPaneViewModel? _model;
    private string? _announced;

    public LanPage()
    {
        InitializeComponent();
        ServerRows.GrowDuration = Expand;
        IsVisibleChanged += (_, _) => Follow();
        DataContextChanged += (_, _) => Attach(DataContext as LanPaneViewModel);
    }

    /// <summary>The Add Device… button for the developer capture.</summary>
    internal Button AddDeviceButton => AddButton.IsVisible ? AddButton : EmptyAddButton;

    private void Attach(LanPaneViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
        }

        ServerRows.Reveal(model?.ShowsServerRows == true, animate: false, reduceMotion: false);
        Follow();
    }

    private void Follow()
    {
        if (_model is { } model)
        {
            model.IsVisible = IsVisible;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        if (e.PropertyName is nameof(LanPaneViewModel.ShowsServerRows) or "" or null)
        {
            ServerRows.Reveal(model.ShowsServerRows, animate: IsVisible, model.ReduceMotion);
        }

        if (e.PropertyName == nameof(LanPaneViewModel.ServerStatusText) && model.ServerStatusText != _announced)
        {
            _announced = model.ServerStatusText;
            if (UIElementAutomationPeer.FromElement(ServerStatusLine) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private void OnPortTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnPortKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _model.CommitPort();
            PortBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _model.RestorePort();
            PortBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnPortLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _model?.CommitPort();

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LanAddressRow row } button && _model is { } model)
        {
            var menu = new ContextMenu();
            LanMenus.FillAddress(menu, model, row, LocalizationSource.Instance.Localizer);
            LanMenus.OpenUnder(menu, button);
        }
    }

    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LanTargetRowViewModel row, ContextMenu: { } menu } && _model is { } model)
        {
            LanMenus.FillTarget(menu, model, row, LocalizationSource.Instance.Localizer);
        }
        else
        {
            e.Handled = true;
        }
    }

    private void OnActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LanTargetRowViewModel row } button && _model is { } model)
        {
            var menu = new ContextMenu();
            LanMenus.FillTarget(menu, model, row, LocalizationSource.Instance.Localizer);
            LanMenus.OpenUnder(menu, button);
        }
    }
}
