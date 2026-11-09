using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Theme;

namespace AgentSpeed.Menus;

/// <summary>
/// Turns a menu model into a Fluent context menu whose rows share one text column (MenuStyles.xaml): every item of a
/// level gets that level's icon and trailing column flags, so labels line up with or without an icon or a check.
/// </summary>
internal static class MenuBuilder
{
    public const string ItemStyleKey = "AppMenuItemStyle";

    public static ContextMenu Build(MenuLevel level, IAppCommands commands, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(localizer);
        var menu = new ContextMenu { UseLayoutRounding = true };
        menu.SetResourceReference(Control.FontFamilyProperty, AppFonts.UiFontKey);
        menu.Language = AppFonts.XmlLanguageFor(localizer.LanguageCode);
        Fill(menu.Items, level, commands);
        return menu;
    }

    /// <summary>Runs a row's action.</summary>
    public static void Run(MenuAction action, IAppCommands commands)
    {
        switch (action)
        {
            case MenuAction.OpenUpdate:
                commands.OpenUpdate();
                break;
            case MenuAction.OpenDashboard:
                commands.ToggleDashboardFromMenu();
                break;
            case MenuAction.OpenHistory:
                commands.OpenHistory();
                break;
            case MenuAction.OpenSettings a:
                commands.OpenSettings(a.Page);
                break;
            case MenuAction.CheckForUpdates:
                commands.CheckForUpdates();
                break;
            case MenuAction.Quit:
                commands.Quit();
                break;
            case MenuAction.SetEffects a:
                commands.UpdateSettings(s => s with { Effects = a.Level });
                break;
            case MenuAction.SetShowOnTaskbar a:
                commands.UpdateSettings(s => s with { ShowOnTaskbar = a.Show });
                break;
            case MenuAction.SetShowOrbs a:
                commands.SetShowOrbs(a.Show);
                break;
            case MenuAction.SetOrbStyle a:
                commands.SetOrbStyle(a.OrbId, a.Style);
                break;
            case MenuAction.SetOrbUnit a:
                commands.SetOrbUnit(a.OrbId, a.Unit);
                break;
            case MenuAction.SetOrbCharacter a:
                commands.SetOrbCharacter(a.OrbId, a.Character);
                break;
            case MenuAction.SetOrbCharacterReadout a:
                commands.SetOrbCharacterReadout(a.OrbId, a.Readout, a.On);
                break;
            case MenuAction.SetOrbTraffic a:
                commands.SetOrbTraffic(a.OrbId, a.Traffic);
                break;
            case MenuAction.SetOrbAllSources a:
                commands.SetOrbAllSources(a.OrbId);
                break;
            case MenuAction.ToggleOrbSource a:
                commands.ToggleOrbSource(a.OrbId, a.Source);
                break;
            case MenuAction.SetOrbAllHosts a:
                commands.SetOrbAllHosts(a.OrbId);
                break;
            case MenuAction.ToggleOrbHost a:
                commands.ToggleOrbHost(a.OrbId, a.HostId);
                break;
            case MenuAction.SetOrbSize a:
                commands.SetOrbSize(a.OrbId, a.Size);
                break;
            case MenuAction.SetOrbAlwaysOnTop a:
                commands.SetOrbAlwaysOnTop(a.OrbId, a.On);
                break;
            case MenuAction.SetOrbClickThrough a:
                commands.SetOrbClickThrough(a.OrbId, a.On);
                break;
            case MenuAction.SetOrbVisible a:
                commands.SetOrbVisible(a.OrbId, a.Visible);
                break;
            case MenuAction.SetOrbShowName a:
                commands.SetOrbShowName(a.OrbId, a.Show);
                break;
            case MenuAction.AddOrb a:
                commands.AddOrb(a.FromOrbId);
                break;
            case MenuAction.DuplicateOrb a:
                commands.DuplicateOrb(a.OrbId);
                break;
            case MenuAction.SplitOrb a:
                commands.SplitOrb(a.OrbId);
                break;
            case MenuAction.MergeOrbs a:
                commands.MergeOrbs(a.KeepOrbId);
                break;
            case MenuAction.RemoveOrb a:
                commands.RemoveOrb(a.OrbId);
                break;
            case MenuAction.OpenOrbSettings a:
                commands.OpenOrbSettings(a.OrbId);
                break;
            case MenuAction.SetShowBoards a:
                commands.SetShowBoards(a.Show);
                break;
            case MenuAction.SetBoardStyle a:
                commands.SetBoardStyle(a.BoardId, a.Style);
                break;
            case MenuAction.SetBoardAllSources a:
                commands.SetBoardAllSources(a.BoardId);
                break;
            case MenuAction.ToggleBoardSource a:
                commands.ToggleBoardSource(a.BoardId, a.Source);
                break;
            case MenuAction.SetBoardAllHosts a:
                commands.SetBoardAllHosts(a.BoardId);
                break;
            case MenuAction.ToggleBoardHost a:
                commands.ToggleBoardHost(a.BoardId, a.HostId);
                break;
            case MenuAction.SetBoardSize a:
                commands.SetBoardSize(a.BoardId, a.Size);
                break;
            case MenuAction.SetBoardReadout a:
                commands.SetBoardReadout(a.BoardId, a.Readout, a.On);
                break;
            case MenuAction.SetBoardAlwaysOnTop a:
                commands.SetBoardAlwaysOnTop(a.BoardId, a.On);
                break;
            case MenuAction.SetBoardClickThrough a:
                commands.SetBoardClickThrough(a.BoardId, a.On);
                break;
            case MenuAction.AddBoard a:
                commands.AddBoard(a.FromId);
                break;
            case MenuAction.RemoveBoard a:
                commands.RemoveBoard(a.BoardId);
                break;
            case MenuAction.OpenBoardSettings a:
                commands.OpenBoardSettings(a.BoardId);
                break;
            case MenuAction.PauseReporting a:
                commands.PauseReporting(a.Indefinitely);
                break;
            case MenuAction.ResumeReporting:
                commands.ResumeReporting();
                break;
            case MenuAction.OpenLeaderboard:
                commands.OpenReportLeaderboard();
                break;
        }
    }

    private static void Fill(ItemCollection items, MenuLevel level, IAppCommands commands)
    {
        var iconColumn = level.ReservesIconColumn;
        var trailingColumn = level.ReservesTrailingColumn;
        foreach (var entry in level.Entries)
        {
            items.Add(entry switch
            {
                MenuRow row => Item(row, iconColumn, trailingColumn, commands),
                MenuSlider slider => SliderItem(slider, items, iconColumn, trailingColumn, commands),
                _ => new Separator(),
            });
        }
    }

    /// <summary>A slider row; every new value runs its action and moves the check of the level's rows (the presets).</summary>
    private static MenuSliderItem SliderItem(MenuSlider slider, ItemCollection level, bool iconColumn, bool trailingColumn,
        IAppCommands commands)
    {
        var item = new MenuSliderItem(slider, value =>
        {
            Run(slider.Action(value), commands);
            if (slider.CheckedRow is not { } checkedRow)
            {
                return;
            }

            var index = checkedRow(value);
            var rows = level.OfType<MenuItem>().Where(i => i.IsCheckable).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].IsChecked = i == index;
            }
        });
        item.SetResourceReference(FrameworkElement.StyleProperty, ItemStyleKey);
        MenuChrome.SetIconColumn(item, iconColumn);
        MenuChrome.SetTrailingColumn(item, trailingColumn);
        return item;
    }

    private static MenuItem Item(MenuRow row, bool iconColumn, bool trailingColumn, IAppCommands commands)
    {
        var item = new MenuItem
        {
            // Labels never carry access keys; a custom orb name may contain '_'.
            Header = row.Label.Replace("_", "__", StringComparison.Ordinal),
            Icon = Icon(row.Icon),
            IsCheckable = row.IsCheckable,
            IsChecked = row.IsChecked,
            IsEnabled = row.IsEnabled,
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, ItemStyleKey);
        MenuChrome.SetIconColumn(item, iconColumn);
        MenuChrome.SetTrailingColumn(item, trailingColumn);
        MenuChrome.SetIsHeader(item, row.IsHeader);
        if (row.IsDefault)
        {
            item.FontWeight = FontWeights.SemiBold;
        }

        if (row.Detail is { Length: > 0 } detail)
        {
            MenuChrome.SetDetail(item, detail);
            AutomationProperties.SetItemStatus(item, detail);
        }

        if (row.ToolTip is { Length: > 0 } tip)
        {
            item.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(item, true);
        }

        if ((row.ToolTip ?? row.Detail) is { Length: > 0 } help)
        {
            AutomationProperties.SetHelpText(item, help);
        }

        if (row.Submenu is { } submenu)
        {
            Fill(item.Items, submenu, commands);
        }
        else if (row.Action is { } action)
        {
            item.Click += (_, _) => Run(action, commands);
        }

        return item;
    }

    private static UIElement? Icon(MenuIcon? icon) => icon switch
    {
        MenuIcon.Glyph glyph => GlyphIcon(glyph.Kind),
        MenuIcon.Dots dots => new MenuDots(dots.Sources),
        MenuIcon.AccentDot => AccentDot(),
        MenuIcon.StatusDot status => StatusDot(status.Tone),
        _ => null,
    };

    /// <summary>A 7 DIP dot in the status color (a ring for something switched off); the row's text always says what it means.</summary>
    private static Ellipse StatusDot(MenuStatusTone tone)
    {
        var dot = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (tone == MenuStatusTone.Hollow)
        {
            dot.StrokeThickness = 1.1;
            dot.SetResourceReference(Shape.StrokeProperty, "TextFillColorTertiaryBrush");
            return dot;
        }

        dot.SetResourceReference(Shape.FillProperty, tone switch
        {
            MenuStatusTone.Green => "SystemFillColorSuccessBrush",
            MenuStatusTone.Amber => "SystemFillColorCautionBrush",
            MenuStatusTone.Red => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        });
        return dot;
    }

    private static TextBlock GlyphIcon(MenuGlyph glyph)
    {
        var icon = new TextBlock
        {
            Text = glyph switch
            {
                MenuGlyph.Dashboard => "",
                MenuGlyph.History => "",
                MenuGlyph.Settings => "",
                MenuGlyph.About => "",
                MenuGlyph.CheckForUpdates => "",
                _ => "",
            },
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.SymbolFontKey);
        return icon;
    }

    private static Ellipse AccentDot()
    {
        var dot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        return dot;
    }
}
