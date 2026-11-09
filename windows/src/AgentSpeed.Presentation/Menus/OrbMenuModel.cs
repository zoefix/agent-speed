using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using static AgentSpeed.Presentation.Menus.MenuParts;

namespace AgentSpeed.Presentation.Menus;

/// <summary>
/// The context menu of one floating orb (DESIGN 2b, Context menu, items 1–9). Item 3 is followed by
/// <c>orbs.showName</c> (the orb's name plate).
/// </summary>
public static class OrbMenuModel
{
    /// <summary>A size within this distance of its nearest preset checks that preset.</summary>
    public const double PresetTolerance = 8;

    /// <summary>The menu of <paramref name="orbId"/>; null when no such orb exists.</summary>
    /// <param name="sources">Source availability (split, the sources' secondary text and faded dots).</param>
    /// <param name="sourceTraffic">Per-process traffic can be measured now (else the sources choice says it is unavailable).</param>
    /// <param name="hosts">Each host's dot and word for the Hosts submenu (DESIGN 13), by id.</param>
    /// <param name="characters">The installed characters (id, localized name) in catalog order, for a <c>character</c> orb.</param>
    public static MenuLevel? Build(string orbId, AppSettings settings, SourceAvailability sources, Localizer localizer, bool sourceTraffic = true,
        IReadOnlyDictionary<string, MenuHostState>? hosts = null, IReadOnlyList<MenuCharacter>? characters = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        var document = settings.Orbs ?? OrbsDocument.Fresh();
        if (document.Find(orbId) is not { } orb)
        {
            return null;
        }

        var id = orb.Id;
        var limit = localizer.Format("orbs.limit", OrbsDocument.MaxOrbs);
        var canAdd = document.CanAdd;
        var canSplit = OrbEditing.CanSplit(document, id, sources.Available);
        var splitTip = canSplit ? null : !canAdd ? limit : orb.Sources.TracksAll ? localizer["orbs.split.unavailable"] : null;
        var lastVisible = document.Orbs.Count(o => o.Visible) <= 1;
        var hide = lastVisible
            ? new MenuRow(localizer["menu.hideOrb"]) { Action = new MenuAction.SetShowOrbs(false) }
            : new MenuRow(localizer["menu.orb.hide"]) { Action = new MenuAction.SetOrbVisible(id, false) };

        var hostsMenu = HostsMenu(orb.Hosts, new MenuAction.SetOrbAllHosts(id), host => new MenuAction.ToggleOrbHost(id, host), settings, hosts,
            localizer);
        var name = OrbNaming.DisplayName(orb, localizer, RemoteHostStates.Choices(settings.RemoteHosts, localizer));
        return new MenuLevel(
        [
            new MenuRow(name) { Icon = DotsOf(orb, sources), IsHeader = true, IsEnabled = false },
            MenuSeparator.Instance,
            StyleMenu(orb, localizer),
            .. orb.Style == OrbStyle.Character ? [CharacterMenu(orb, characters ?? [], localizer)] : Array.Empty<MenuEntry>(),
            SourcesMenu(orb, sources, localizer),
            .. hostsMenu is null ? Array.Empty<MenuEntry>() : [hostsMenu],
            .. orb.Style == OrbStyle.Character
                ? [ReadoutsMenu(orb, settings, localizer)]
                : new MenuEntry[] { UnitMenu(orb, settings, localizer), TrafficMenu(orb, settings, localizer, sourceTraffic) },
            ShowNameToggle(orb, settings, localizer),
            SizeMenu(orb, localizer),
            EffectsMenu(settings.Effects, localizer),
            MenuSeparator.Instance,
            Toggle(localizer["menu.alwaysOnTop"], orb.AlwaysOnTop, new MenuAction.SetOrbAlwaysOnTop(id, !orb.AlwaysOnTop)),
            Toggle(localizer["menu.clickThrough"], orb.ClickThrough, new MenuAction.SetOrbClickThrough(id, !orb.ClickThrough)),
            MenuSeparator.Instance,
            new MenuRow(localizer["orbs.add"]) { Action = new MenuAction.AddOrb(id), IsEnabled = canAdd, ToolTip = canAdd ? null : limit },
            new MenuRow(localizer["menu.orb.duplicate"]) { Action = new MenuAction.DuplicateOrb(id), IsEnabled = canAdd, ToolTip = canAdd ? null : limit },
            new MenuRow(localizer["menu.orb.split"]) { Action = new MenuAction.SplitOrb(id), IsEnabled = canSplit, ToolTip = splitTip },
            new MenuRow(localizer["menu.orb.mergeAll"]) { Action = new MenuAction.MergeOrbs(id), IsEnabled = document.CanMerge },
            BoardMenuModel.AddRow(settings.Boards, id, localizer),
            MenuSeparator.Instance,
            hide,
            new MenuRow(localizer["menu.orb.remove"]) { Action = new MenuAction.RemoveOrb(id), IsEnabled = document.CanRemove },
            MenuSeparator.Instance,
            Command(localizer["menu.orb.settings"], new MenuAction.OpenOrbSettings(id), MenuGlyph.Settings),
            Command(localizer["menu.dashboard"], new MenuAction.OpenDashboard(), MenuGlyph.Dashboard),
            Command(localizer["menu.quit"], new MenuAction.Quit(), MenuGlyph.Quit),
        ]);
    }

    /// <summary>The size preset a size checks (0 small, 1 medium, 2 large): the nearest one within 8; else −1.</summary>
    public static int PresetIndex(double size)
    {
        var presets = AppSettings.OrbSizePresets;
        var best = -1;
        for (var i = 0; i < presets.Count; i++)
        {
            if (best < 0 || Math.Abs(presets[i] - size) < Math.Abs(presets[best] - size))
            {
                best = i;
            }
        }

        return best >= 0 && Math.Abs(presets[best] - size) < PresetTolerance ? best : -1;
    }

    /// <summary>
    /// <c>orbs.unit</c> ▸ <c>orbs.unit.tokens</c> / <c>orbs.unit.cost</c> (<c>US$/s · API 等值</c>), a check on the current
    /// one (DESIGN 9 Orb, Switching 2); the quick menu's per-orb submenu has the same item.
    /// </summary>
    internal static MenuRow UnitMenu(OrbDefinition orb, AppSettings settings, Localizer localizer)
    {
        var symbol = Core.Money.CurrencyTable.Bundled.CurrencyOrUsd(settings.Cost.Currency).SymbolFor(localizer.LanguageCode);
        return Submenu(localizer["orbs.unit"],
        [
            Toggle(localizer["orbs.unit.tokens"], orb.Unit == OrbUnit.Tokens, new MenuAction.SetOrbUnit(orb.Id, OrbUnit.Tokens)),
            Toggle(localizer.Format("orbs.unit.cost", symbol), orb.Unit == OrbUnit.Cost, new MenuAction.SetOrbUnit(orb.Id, OrbUnit.Cost)),
        ]);
    }

    /// <summary>
    /// <c>orbs.traffic</c> ▸ <c>orbs.traffic.sources</c> / <c>orbs.traffic.machine</c> / <c>orbs.traffic.intelligence</c> /
    /// <c>orbs.traffic.off</c>, a check on the current one (DESIGN 10). The network choices are disabled while measuring is
    /// off (intelligence needs no measuring); without per-process data the sources choice adds <c>traffic.unavailable.short</c>.
    /// The quick menu's per-orb submenu has the same item.
    /// </summary>
    internal static MenuRow TrafficMenu(OrbDefinition orb, AppSettings settings, Localizer localizer, bool sourceTraffic)
    {
        var measuring = settings.Traffic.Enabled;
        MenuRow Choice(string key, OrbTraffic traffic) =>
            Toggle(localizer[key], orb.Traffic == traffic, new MenuAction.SetOrbTraffic(orb.Id, traffic), measuring || !OrbTraffics.IsNetwork(traffic));
        return Submenu(localizer["orbs.traffic"],
        [
            Choice("orbs.traffic.sources", OrbTraffic.Sources) with
            {
                Detail = measuring && !sourceTraffic ? localizer["traffic.unavailable.short"] : null,
            },
            Choice("orbs.traffic.machine", OrbTraffic.Machine),
            Choice("orbs.traffic.intelligence", OrbTraffic.Intelligence),
            Choice("orbs.traffic.off", OrbTraffic.Off),
        ]);
    }

    /// <summary>
    /// <c>characters.title</c> ▸ <c>characters.auto</c>, every installed character (checked when locked to it),
    /// <c>characters.more</c> (the orb's Settings, where the character section sits under the style cards).
    /// </summary>
    internal static MenuRow CharacterMenu(OrbDefinition orb, IReadOnlyList<MenuCharacter> characters, Localizer localizer)
    {
        var locked = orb.Character is { } id && characters.Any(c => c.Id == id) ? id : null;
        var rows = new List<MenuEntry> { Toggle(localizer["characters.auto"], locked is null, new MenuAction.SetOrbCharacter(orb.Id, null)) };
        if (characters.Count > 0)
        {
            rows.Add(MenuSeparator.Instance);
            rows.AddRange(characters.Select(c => (MenuEntry)Toggle(c.Name, c.Id == locked, new MenuAction.SetOrbCharacter(orb.Id, c.Id))));
        }

        rows.Add(MenuSeparator.Instance);
        rows.Add(new MenuRow(localizer["characters.more"]) { Action = new MenuAction.OpenOrbSettings(orb.Id) });
        return Submenu(localizer["characters.title"], [.. rows]);
    }

    /// <summary>
    /// <c>character.readouts</c> ▸ a check per card (network disabled while traffic is not measured), the section header
    /// <c>character.readouts.networkScope</c> and the radio pair sources / machine (disabled while the network card is off).
    /// </summary>
    internal static MenuRow ReadoutsMenu(OrbDefinition orb, AppSettings settings, Localizer localizer)
    {
        var measuring = settings.Traffic.Enabled;
        var readouts = orb.CharacterReadouts;
        var rows = new List<MenuEntry>();
        foreach (var readout in CharacterReadouts.All)
        {
            var on = readouts[readout];
            rows.Add(Toggle(localizer[CharacterReadouts.TitleKey(readout)], on, new MenuAction.SetOrbCharacterReadout(orb.Id, readout, !on),
                readout != CharacterReadout.Network || measuring));
        }

        var scope = measuring && readouts.Network;
        var machine = orb.Traffic == OrbTraffic.Machine;
        rows.Add(MenuSeparator.Instance);
        rows.Add(new MenuRow(localizer["character.readouts.networkScope"]) { IsHeader = true, IsEnabled = false });
        rows.Add(Toggle(localizer["orbs.traffic.sources"], !machine, new MenuAction.SetOrbTraffic(orb.Id, OrbTraffic.Sources), scope));
        rows.Add(Toggle(localizer["orbs.traffic.machine"], machine, new MenuAction.SetOrbTraffic(orb.Id, OrbTraffic.Machine), scope));
        return Submenu(localizer["character.readouts"], [.. rows]);
    }

    private static MenuRow StyleMenu(OrbDefinition orb, Localizer localizer) =>
        Submenu(localizer["orbs.style"], OrbStyles.All.Select(style =>
            (MenuEntry)Toggle(localizer["style." + OrbStyles.Key(style)], orb.Style == style, new MenuAction.SetOrbStyle(orb.Id, style))));

    private static MenuRow SourcesMenu(OrbDefinition orb, SourceAvailability sources, Localizer localizer) =>
        MenuParts.SourcesMenu(orb.Sources, new MenuAction.SetOrbAllSources(orb.Id), kind => new MenuAction.ToggleOrbSource(orb.Id, kind), sources, localizer);
}
