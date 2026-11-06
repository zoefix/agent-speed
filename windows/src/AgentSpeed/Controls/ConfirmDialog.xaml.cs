using System.Windows;
using System.Windows.Automation;
using AgentSpeed.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.Controls;

/// <summary>
/// Small Fluent confirmation dialog for a destructive action. Cancel is the default and has focus, so Enter or Esc
/// never confirms by accident.
/// </summary>
internal sealed partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Shows the dialog modally; true when the user chose <paramref name="confirm"/>.</summary>
    public static bool Show(Window? owner, string windowTitle, string heading, string body, string confirm, string cancel) =>
        ShowWithOption(owner, windowTitle, heading, body, confirm, cancel, null).Confirmed;

    /// <summary>
    /// Shows the dialog modally with a checkbox <paramref name="option"/> (off) when it is not null; whether the user chose
    /// <paramref name="confirm"/>, and the checkbox's state then.
    /// </summary>
    public static (bool Confirmed, bool Option) ShowWithOption(Window? owner, string windowTitle, string heading, string body, string confirm,
        string cancel, string? option)
    {
        var dialog = new ConfirmDialog
        {
            Title = windowTitle,
            Language = owner?.Language ?? AppFonts.XmlLanguageFor(LocalizationSource.Instance.Localizer.LanguageCode),
        };
        dialog.Heading.Text = heading;
        dialog.Body.Text = body;
        dialog.ConfirmButton.Content = confirm;
        dialog.CancelButton.Content = cancel;
        if (option is not null)
        {
            dialog.OptionLabel.Text = option;
            dialog.Option.Visibility = Visibility.Visible;
            AutomationProperties.SetName(dialog.Option, option);
        }

        AutomationProperties.SetName(dialog, heading);
        AutomationProperties.SetHelpText(dialog, body);
        if (AppIconSource.Frame(32) is { } icon)
        {
            dialog.Icon = icon;
        }

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var confirmed = dialog.ShowDialog() == true;
        return (confirmed, confirmed && dialog.Option.IsChecked == true);
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
