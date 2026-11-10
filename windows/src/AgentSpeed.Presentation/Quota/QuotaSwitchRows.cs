using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Quota;

/// <summary>
/// The phase 2 quota switches of Settings → Data Sources (DESIGN 5, <i>額度, phase 2</i>): which group shows which switch
/// after its source toggles, their captions, and when they can be switched.
/// </summary>
public static class QuotaSwitchRows
{
    /// <summary>The routes whose switches follow a group's source toggles, in order; Qoder's one switch sits in the IDE's group.</summary>
    public static IReadOnlyList<QuotaRoute> Of(LocationFolder folder) => folder switch
    {
        LocationFolder.GrokBuild => [QuotaRoute.GrokBuild, QuotaRoute.GrokBuildLive],
        LocationFolder.Devin => [QuotaRoute.Devin],
        LocationFolder.Antigravity => [QuotaRoute.Antigravity],
        LocationFolder.Kimi => [QuotaRoute.Kimi],
        LocationFolder.Gemini => [QuotaRoute.Gemini],
        LocationFolder.Droid => [QuotaRoute.Droid],
        LocationFolder.ZCode => [QuotaRoute.Zcode],
        LocationFolder.Qoder => [QuotaRoute.Qoder],
        LocationFolder.WorkBuddy => [QuotaRoute.WorkBuddy],
        _ => [],
    };

    public static string TitleKey(QuotaRoute route) => route switch
    {
        QuotaRoute.GrokBuild => "settings.quota.grokBuild",
        QuotaRoute.GrokBuildLive => "settings.quota.grokBuildLive",
        QuotaRoute.Devin => "settings.quota.devin",
        QuotaRoute.Antigravity => "settings.quota.antigravity",
        QuotaRoute.Kimi => "settings.quota.kimi",
        QuotaRoute.Gemini => "settings.quota.gemini",
        QuotaRoute.Droid => "settings.quota.droid",
        QuotaRoute.Zcode => "settings.quota.zcode",
        QuotaRoute.Qoder => "settings.quota.qoder",
        _ => "settings.quota.workbuddy",
    };

    /// <summary>
    /// The first caption: the route's hint; the browser routes name their site and host; ZCode says the key stays in
    /// Credential Manager. Empty for Grok's live request, which reads as part of the switch above it.
    /// </summary>
    public static string Hint(QuotaRoute route, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return route switch
        {
            QuotaRoute.GrokBuildLive => string.Empty,
            QuotaRoute.Zcode => loc["win.settings.quota.zcode.hint"],
            _ when Site(route) is { } site => loc.Format("settings.quota.cookies.hint", site.Name, site.Host),
            _ => loc[TitleKey(route) + ".hint"],
        };
    }

    /// <summary>The second caption, <c>settings.quota.unverified</c>, on every switch but the dependent one.</summary>
    public static bool HasNote(QuotaRoute route) => route != QuotaRoute.GrokBuildLive;

    /// <summary>Grok's live request is indented under the log's switch it depends on.</summary>
    public static bool IsIndented(QuotaRoute route) => route == QuotaRoute.GrokBuildLive;

    /// <summary>The switch's stored value, whatever its source toggle (Grok's live request keeps its own while the log's is off).</summary>
    public static bool StoredOn(QuotaRoute route, QuotaSettings quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        return route == QuotaRoute.GrokBuildLive ? quota.GrokBuildLive : quota.IsOn(route);
    }

    /// <summary>The switch's source is enabled (QUOTAS 9.1); Qoder's shared account counts while either Qoder source is.</summary>
    public static bool SourceIsOn(QuotaRoute route, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return route == QuotaRoute.Qoder
            ? settings.IsSourceEnabled(SourceKind.Qoder) || settings.IsSourceEnabled(SourceKind.QoderCli)
            : settings.IsSourceEnabled(route.Source());
    }

    /// <summary>The switch can be flipped: its source is enabled, and for Grok's live request the log's switch is on.</summary>
    public static bool CanSwitch(QuotaRoute route, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SourceIsOn(route, settings) && (route != QuotaRoute.GrokBuildLive || settings.Quota.GrokBuild);
    }

    /// <summary>The site a browser route reads, as its caption, dialog and status name it.</summary>
    public static (string Name, string Host)? Site(QuotaRoute route) => route switch
    {
        QuotaRoute.Qoder => ("Qoder", QuotaHosts.Qoder),
        QuotaRoute.WorkBuddy => ("WorkBuddy", QuotaHosts.WorkBuddy),
        _ => null,
    };
}
