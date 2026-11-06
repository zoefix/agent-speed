using System.Windows;
using System.Windows.Automation;
using AgentSpeed.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.Controls;

/// <summary>
/// Small Fluent notice with one OK button: why something did not happen, and the detail it concerns (a refused folder pick,
/// DESIGN 5). Enter and Esc both close it.
/// </summary>
internal sealed partial class NoticeDialog : Window
{
    private NoticeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => OkButton.Focus();
    }

    /// <summary>Shows the notice modally over <paramref name="owner"/>.</summary>
    public static void Show(Window? owner, string windowTitle, string heading, string detail, string ok)
    {
        var dialog = new NoticeDialog
        {
            Title = windowTitle,
            Language = owner?.Language ?? AppFonts.XmlLanguageFor(LocalizationSource.Instance.Localizer.LanguageCode),
        };
        dialog.Heading.Text = heading;
        dialog.Detail.Text = detail;
        dialog.Detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        dialog.OkButton.Content = ok;
        AutomationProperties.SetName(dialog, heading);
        AutomationProperties.SetHelpText(dialog, detail);
        AutomationProperties.SetName(dialog.Detail, detail);
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

        dialog.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
