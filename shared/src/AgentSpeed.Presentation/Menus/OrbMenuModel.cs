using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Orb;
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
    public static MenuLevel? Build(string orbId, AppSettings settings, SourceAvailability sources, Localizer localizer, bool sourceTraffic = true,
        IReadOnlyList<AgentStatus>? agents = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        var document = settings.Orbs ?? OrbsDocument.Fresh();
        if (document.Find(orbId) is not { } orb)
        {
            return null;
        }

        var id = orb.Id;
        var modeAgents = agents?.Where(a => orb.Sources.Tracks(a.Source)).ToArray();
        var limit = localizer.Format("orbs.limit", OrbsDocument.MaxOrbs);
        var canAdd = document.CanAdd;
        var canSplit = OrbEditing.CanSplit(document, id, sources.Available);
        var splitTip = canSplit ? null : !canAdd ? limit : orb.Sources.TracksAll ? localizer["orbs.split.unavailable"] : null;
        var lastVisible = document.Orbs.Count(o => o.Visible) <= 1;
        var hide = lastVisible
            ? new MenuRow(localizer["menu.hideOrb"]) { Action = new MenuAction.SetShowOrbs(false) }
            : new MenuRow(localizer["menu.orb.hide"]) { Action = new MenuAction.SetOrbVisible(id, false) };

        return new MenuLevel(
        [
            new MenuRow(OrbNaming.DisplayName(orb, localizer)) { Icon = DotsOf(orb, sources), IsHeader = true, IsEnabled = false },
            .. (modeAgents is { Length: > 0 } ? new MenuEntry[]
            {
                new MenuRow(localizer["mode.label"] + ": " + ExecutionModeText.Aggregate(modeAgents.Select(a => a.Modes), localizer))
                {
                    IsEnabled = false,
                    ToolTip = string.Join(Environment.NewLine, modeAgents.Select(a => (a.Model ?? localizer[DisplayText.SourceKey(a.Source)])
                        + ": " + ExecutionModeText.Detail(a.Modes, localizer))),
                },
            } : []),
            MenuSeparator.Instance,
            StyleMenu(orb, localizer),
            SourcesMenu(orb, sources, localizer),
            UnitMenu(orb, settings, localizer),
            TrafficMenu(orb, settings, localizer, sourceTraffic),
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
    /// <c>orbs.traffic</c> ▸ <c>orbs.traffic.sources</c> / <c>orbs.traffic.machine</c> / <c>orbs.traffic.off</c>, a check on
    /// the current one (DESIGN 10); disabled while measuring is off. Without per-process data the sources choice adds
    /// <c>traffic.unavailable.short</c>. The quick menu's per-orb submenu has the same item.
    /// </summary>
    internal static MenuRow TrafficMenu(OrbDefinition orb, AppSettings settings, Localizer localizer, bool sourceTraffic)
    {
        var enabled = settings.Traffic.Enabled;
        return Submenu(localizer["orbs.traffic"],
        [
            Toggle(localizer["orbs.traffic.sources"], orb.Traffic == OrbTraffic.Sources, new MenuAction.SetOrbTraffic(orb.Id, OrbTraffic.Sources)) with
            {
                Detail = enabled && !sourceTraffic ? localizer["traffic.unavailable.short"] : null,
            },
            Toggle(localizer["orbs.traffic.machine"], orb.Traffic == OrbTraffic.Machine, new MenuAction.SetOrbTraffic(orb.Id, OrbTraffic.Machine)),
            Toggle(localizer["orbs.traffic.off"], orb.Traffic == OrbTraffic.Off, new MenuAction.SetOrbTraffic(orb.Id, OrbTraffic.Off)),
        ]) with { IsEnabled = enabled };
    }

    private static MenuRow StyleMenu(OrbDefinition orb, Localizer localizer) =>
        Submenu(localizer["orbs.style"], OrbStyles.All.Select(style =>
            (MenuEntry)Toggle(localizer["style." + OrbStyles.Key(style)], orb.Style == style, new MenuAction.SetOrbStyle(orb.Id, style))));

    /// <summary>
    /// <c>orbs.sources.all</c>, a separator, then every source in source order with its dot and a check when tracked.
    /// Unavailable sources say why and stay selectable; the last tracked source cannot be unchecked.
    /// </summary>
    private static MenuRow SourcesMenu(OrbDefinition orb, SourceAvailability sources, Localizer localizer)
    {
        var rows = new List<MenuEntry>
        {
            Toggle(localizer["orbs.sources.all"], orb.Sources.TracksAll, new MenuAction.SetOrbAllSources(orb.Id)),
            MenuSeparator.Instance,
        };
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var tracked = !orb.Sources.TracksAll && orb.Sources.Known.Contains(kind);
            var locked = tracked && !OrbEditing.CanUncheck(orb, kind);
            var state = sources.StateOf(kind);
            rows.Add(Toggle(OrbNaming.SourceName(kind, localizer), tracked, new MenuAction.ToggleOrbSource(orb.Id, kind), !locked) with
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
}
