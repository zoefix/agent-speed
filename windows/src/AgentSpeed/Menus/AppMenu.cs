using System.Windows.Controls;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Menus;

/// <summary>The quick menu of the tray icon and the taskbar widget (DESIGN 2b, Master switch; <see cref="QuickMenuModel"/>).</summary>
internal static class AppMenu
{
    /// <param name="update">The available release; adds the "Update to …" item on top (UPDATES section 4).</param>
    /// <param name="sources">Source availability for the orbs' colour dots; null treats every source as available.</param>
    /// <param name="sourceTraffic">Per-process traffic can be measured now (DESIGN 10).</param>
    /// <param name="report">Live reporting's item while it is on and registered (DESIGN 11, Menu).</param>
    public static ContextMenu Build(IAppCommands commands, AppSettings settings, Localizer loc, ReleaseInfo? update = null,
        SourceAvailability? sources = null, bool sourceTraffic = true, ReportMenuState? report = null) =>
        MenuBuilder.Build(QuickMenuModel.Build(settings, sources ?? SourceAvailability.All, loc, update, sourceTraffic, report, commands.InstalledCharacters),
            commands, loc);
}
