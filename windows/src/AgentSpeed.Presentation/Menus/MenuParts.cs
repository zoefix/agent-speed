using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Remote;
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
    public static MenuIcon? DotsOf(OrbDefinition orb, SourceAvailability sources) => DotsOf(orb.Sources, sources);

    /// <summary>The dots of an orb's or a board's sources in source order; null for all sources.</summary>
    public static MenuIcon? DotsOf(OrbSources tracked, SourceAvailability sources) =>
        tracked.Set is { } set ? new MenuIcon.Dots([.. set.Select(k => new SourceDot(k, sources.Available.Contains(k)))]) : null;

    /// <summary>
    /// <c>menu.size</c> ▸ small / medium / large, a separator and a slider over the whole range in 4 DIP steps (DESIGN 2b,
    /// Context menu) for one orb; the presets' check follows the slider.
    /// </summary>
    public static MenuRow SizeMenu(OrbDefinition orb, Localizer localizer, bool enabled = true)
    {
        var presets = AppSettings.OrbSizePresets;
        var current = OrbMenuModel.PresetIndex(orb.Size);
        var rows = new List<MenuEntry>(presets.Count + 2);
        for (var i = 0; i < presets.Count; i++)
        {
            rows.Add(Toggle(localizer[SizeKeys[i]], i == current, new MenuAction.SetOrbSize(orb.Id, presets[i])));
        }

        var id = orb.Id;
        rows.Add(MenuSeparator.Instance);
        rows.Add(new MenuSlider(localizer["settings.orb.size"], orb.Size, OrbStyles.MinSize, OrbStyles.MaxSize, OrbStyles.SizeStep)
        {
            Action = size => new MenuAction.SetOrbSize(id, size),
            ValueText = SizeText,
            CheckedRow = OrbMenuModel.PresetIndex,
        });
        return Submenu(localizer["menu.size"], rows, enabled);
    }

    /// <summary>An orb size as the slider rows show it (<c>148 pt</c>).</summary>
    public static string SizeText(double size) => string.Create(CultureInfo.InvariantCulture, $"{size:0} pt");

    /// <summary>
    /// <c>orbs.showName</c>, checked when the orb's name plate shows; choosing it stores the opposite for this orb.
    /// Disabled below 80 pt, where no plate is drawn.
    /// </summary>
    public static MenuRow ShowNameToggle(OrbDefinition orb, AppSettings settings, Localizer localizer)
    {
        var on = NamePlateRules.IsOn(orb, settings);
        return Toggle(localizer["orbs.showName"], on, new MenuAction.SetOrbShowName(orb.Id, !on), orb.Size >= NamePlateRules.MinSize);
    }

    /// <summary>
    /// <c>orbs.sources</c> ▸ <c>orbs.sources.all</c>, a separator, then every source in source order with its dot and a
    /// check when tracked (an orb's or a board's). Unavailable sources say why and stay selectable; the last tracked
    /// source cannot be unchecked.
    /// </summary>
    public static MenuRow SourcesMenu(OrbSources tracked, MenuAction all, Func<SourceKind, MenuAction> toggle, SourceAvailability sources,
        Localizer localizer)
    {
        var rows = new List<MenuEntry>
        {
            Toggle(localizer["orbs.sources.all"], tracked.TracksAll, all),
            MenuSeparator.Instance,
        };
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var on = !tracked.TracksAll && tracked.Known.Contains(kind);
            var locked = on && tracked.Unchecking(kind) is null;
            var state = sources.StateOf(kind);
            rows.Add(Toggle(OrbNaming.SourceName(kind, localizer), on, toggle(kind), !locked) with
            {
                Icon = new MenuIcon.Dots([new SourceDot(kind, state == SourceState.Available)]),
                Detail = state switch
                {
                    SourceState.Disabled => localizer["source.disabled"],
                    SourceState.NotFound => localizer["source.notFound"],
                    _ => null,
                },
                ToolTip = locked ? localizer["orbs.sources.lastOne"] : null,
            });
        }

        return Submenu(localizer["orbs.sources"], rows);
    }

    /// <summary>
    /// <c>remote.hosts</c> ▸ <c>remote.hosts.all</c>, a separator, then this computer and every remote host in settings order
    /// with its status dot and, while it is not connected, its word (DESIGN 13); the last tracked host cannot be unchecked.
    /// Null while no remote host exists.
    /// </summary>
    public static MenuRow? HostsMenu(OrbHosts tracked, MenuAction all, Func<string, MenuAction> toggle, AppSettings settings,
        IReadOnlyDictionary<string, MenuHostState>? states, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var choices = RemoteHostStates.Choices(settings.RemoteHosts, localizer);
        if (!OrbHostChoice.PickerShown(choices))
        {
            return null;
        }

        var rows = new List<MenuEntry>
        {
            Toggle(localizer["remote.hosts.all"], tracked.IsAll, all),
            MenuSeparator.Instance,
        };
        foreach (var entry in tracked.Entries(choices))
        {
            var state = states?.GetValueOrDefault(entry.Id) ?? MenuHostState.Default(entry.Id);
            rows.Add(Toggle(entry.Name, entry.Checked, toggle(entry.Id), entry.Enabled) with
            {
                Icon = new MenuIcon.StatusDot(RemoteDots.Tone(state.Dot)),
                Detail = state.Word,
                ToolTip = entry.Enabled ? null : localizer["remote.hosts.lastOne"],
            });
        }

        return Submenu(localizer["remote.hosts"], rows);
    }

    /// <summary><c>menu.effects</c> ▸ Off / Soft / Standard / Max (global).</summary>
    public static MenuRow EffectsMenu(EffectsIntensity current, Localizer localizer) =>
        Submenu(localizer["menu.effects"], EffectLevels.Select(e =>
            (MenuEntry)Toggle(localizer[e.Key], current == e.Level, new MenuAction.SetEffects(e.Level))));
}

/// <summary>An installed character for the menus: its pack id and localized name.</summary>
public sealed record MenuCharacter(string Id, string Name);
