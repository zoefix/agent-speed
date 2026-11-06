using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentSpeed.Menus;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.Settings.Remote;

/// <summary>
/// A host row's actions (DESIGN 13), for its context menu and its ellipsis button: <i>Edit…</i>, <i>Retry now</i> (only while
/// it is failing), <i>Copy SSH command</i> and <i>Remove…</i>, in the app's aligned Fluent menu.
/// </summary>
internal static class RemoteHostActionsMenu
{
    public static void Fill(ContextMenu menu, RemoteHostsPaneViewModel pane, RemoteHostRowViewModel row, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Items.Clear();
        menu.UseLayoutRounding = true;
        menu.SetResourceReference(Control.FontFamilyProperty, AppFonts.UiFontKey);
        menu.Language = AppFonts.XmlLanguageFor(loc.LanguageCode);
        AutomationProperties.SetName(menu, row.ActionsName);
        menu.Items.Add(Item(loc["remote.edit"], "", () => pane.EditCommand.Execute(row)));
        if (row.CanRetry)
        {
            menu.Items.Add(Item(loc["remote.retry"], "", () => pane.RetryCommand.Execute(row)));
        }

        menu.Items.Add(Item(loc["remote.copyCommand"], "", () => pane.CopyCommand.Execute(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(loc["remote.remove"], "", () => pane.RemoveCommand.Execute(row)));
    }

    /// <summary>Opens the menu under <paramref name="target"/> (the ellipsis button), keyboard focus on its first item.</summary>
    public static void OpenUnder(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string label, string glyph, Action run)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.SymbolFontKey);
        var item = new MenuItem { Header = label.Replace("_", "__", StringComparison.Ordinal), Icon = icon };
        item.SetResourceReference(FrameworkElement.StyleProperty, MenuBuilder.ItemStyleKey);
        MenuChrome.SetIconColumn(item, true);
        MenuChrome.SetTrailingColumn(item, false);
        // After the menu has closed, so a dialog the action opens takes the focus from the window, not the menu.
        item.Click += (_, _) => item.Dispatcher.BeginInvoke(run);
        return item;
    }
}
