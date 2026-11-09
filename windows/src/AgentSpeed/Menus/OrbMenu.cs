using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Menus;

/// <summary>The right-click menu of a floating orb (DESIGN 2b, Context menu; <see cref="OrbMenuModel"/>).</summary>
internal static class OrbMenu
{
    /// <summary>The menu of <paramref name="orbId"/>; null when no such orb exists.</summary>
    /// <param name="sources">Source availability, for example <see cref="SourceAvailability.From"/> of the live snapshot.</param>
    /// <param name="sourceTraffic">Per-process traffic can be measured now (DESIGN 10).</param>
    /// <param name="hosts">Each host's dot and word for the Hosts submenu (DESIGN 13).</param>
    public static ContextMenu? Build(IAppCommands commands, string orbId, AppSettings settings, SourceAvailability sources, Localizer loc,
        bool sourceTraffic = true, IReadOnlyDictionary<string, MenuHostState>? hosts = null) =>
        OrbMenuModel.Build(orbId, settings, sources, loc, sourceTraffic, hosts, commands.InstalledCharacters) is { } level
            ? MenuBuilder.Build(level, commands, loc)
            : null;

    /// <summary>The question the menu's Remove asks first (the menu has no undo banner); true to remove.</summary>
    public static bool ConfirmRemove(Window? owner, string orbName, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return ConfirmDialog.Show(owner, loc["app.name"], loc.Format("orbs.remove.confirm", orbName), loc["orbs.remove.detail"],
            loc["orbs.remove.button"], loc["history.cancel"]);
    }
}
