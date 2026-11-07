using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentSpeed.Menus;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.Settings.Lan;

/// <summary>
/// The pull-downs of Settings → API → 區域網路 (DESIGN 14) in the app's aligned Fluent menu: a target row's <i>Edit…</i> and
/// <i>Remove…</i>, and an address's three copy forms.
/// </summary>
internal static class LanMenus
{
    public static void FillTarget(ContextMenu menu, LanPaneViewModel pane, LanTargetRowViewModel row, Localizer loc)
    {
        Prepare(menu, row.ActionsName, loc);
        menu.Items.Add(Item(loc["lan.target.edit"], "", () => pane.EditCommand.Execute(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(loc["lan.target.remove"], "", () => pane.RemoveCommand.Execute(row)));
    }

    public static void FillAddress(ContextMenu menu, LanPaneViewModel pane, LanAddressRow row, Localizer loc)
    {
        Prepare(menu, loc["lan.copy"], loc);
        menu.Items.Add(Item(loc["lan.copy.http"], "", () => pane.CopyCommand.Execute(row.HttpUrl)));
        menu.Items.Add(Item(loc["lan.copy.ws"], "", () => pane.CopyCommand.Execute(row.WebSocketUrl)));
        menu.Items.Add(Item(loc["lan.copy.tcp"], "", () => pane.CopyCommand.Execute(row.TcpAddress)));
    }

    /// <summary>Opens the menu under <paramref name="target"/>, keyboard focus on its first item.</summary>
    public static void OpenUnder(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static void Prepare(ContextMenu menu, string name, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Items.Clear();
        menu.UseLayoutRounding = true;
        menu.SetResourceReference(Control.FontFamilyProperty, AppFonts.UiFontKey);
        menu.Language = AppFonts.XmlLanguageFor(loc.LanguageCode);
        AutomationProperties.SetName(menu, name);
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
        item.Click += (_, _) => item.Dispatcher.BeginInvoke(run);
        return item;
    }
}
