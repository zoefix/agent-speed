using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Menus;

/// <summary>Rows shared by the orb menu and the quick menu.</summary>
internal static class MenuParts
{
    private static readonly string[] SizeKeys = ["menu.size.small", "menu.size.medium", "menu.size.large"];

    private static readonly (EffectsIntensity Level, string Key)[] EffectLevels =
    [
        (EffectsIntensity.Off, "effects.off"),
        (EffectsIntensity.Soft, "effects.soft"),
        (EffectsIntensity.Standard, "effects.standard"),
        (EffectsIntensity.Max, "effects.max"),
    ];

    public static MenuRow Toggle(string label, bool isChecked, MenuAction action, bool enabled = true) =>
        new(label) { Action = action, IsCheckable = true, IsChecked = isChecked, IsEnabled = enabled };

    public static MenuRow Command(string label, MenuAction action, MenuGlyph? glyph = null) =>
        new(label) { Action = action, Icon = glyph is { } g ? new MenuIcon.Glyph(g) : null };

    public static MenuRow Submenu(string label, IEnumerable<MenuEntry> entries, bool enabled = true) =>
        new(label) { Submenu = new MenuLevel([.. entries]), IsEnabled = enabled };

    /// <summary>The dots of an orb's sources in source order; null for an all-sources orb.</summary>
    public static MenuIcon? DotsOf(OrbDefinition orb, SourceAvailability sources) =>
        orb.Sources.Set is { } set ? new MenuIcon.Dots([.. set.Select(k => new SourceDot(k, sources.Available.Contains(k)))]) : null;

    /// <summary><c>menu.size</c> ▸ small / medium / large for one orb.</summary>
    public static MenuRow SizeMenu(OrbDefinition orb, Localizer localizer, bool enabled = true)
    {
        var presets = AppSettings.OrbSizePresets;
        var current = OrbMenuModel.PresetIndex(orb.Size);
        var rows = new List<MenuEntry>(presets.Count);
        for (var i = 0; i < presets.Count; i++)
        {
            rows.Add(Toggle(localizer[SizeKeys[i]], i == current, new MenuAction.SetOrbSize(orb.Id, presets[i])));
        }

        return Submenu(localizer["menu.size"], rows, enabled);
    }

    /// <summary>
    /// <c>orbs.showName</c>, checked when the orb's name plate shows; choosing it stores the opposite for this orb.
    /// Disabled below 80 pt, where no plate is drawn.
    /// </summary>
    public static MenuRow ShowNameToggle(OrbDefinition orb, AppSettings settings, Localizer localizer)
    {
        var on = NamePlateRules.IsOn(orb, settings);
        return Toggle(localizer["orbs.showName"], on, new MenuAction.SetOrbShowName(orb.Id, !on), orb.Size >= NamePlateRules.MinSize);
    }

    /// <summary><c>menu.effects</c> ▸ Off / Soft / Standard / Max (global).</summary>
    public static MenuRow EffectsMenu(EffectsIntensity current, Localizer localizer) =>
        Submenu(localizer["menu.effects"], EffectLevels.Select(e =>
            (MenuEntry)Toggle(localizer[e.Key], current == e.Level, new MenuAction.SetEffects(e.Level))));
}
