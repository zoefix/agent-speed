using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Quota;

/// <summary>What leads a status line: a check, a pause, the network switch, or the caution triangle of a problem.</summary>
public enum QuotaStatusGlyph
{
    Ok,
    Paused,
    NetworkOff,
    Problem,
}

/// <summary>The status line under a phase 2 quota switch (DESIGN 5, <i>額度, phase 2</i>): whether it shows, its glyph and its sentence.</summary>
public static class QuotaStatusText
{
    /// <summary>
    /// The status line to show, null for none: only while the route runs (source enabled, switch on); ZCode says nothing
    /// before a key is saved (its field says what to do); with the network switch off every route that asks over the
    /// network reads <see cref="QuotaStatusKind.NetworkOff"/>.
    /// </summary>
    public static QuotaRouteStatus? Shown(QuotaRoute route, QuotaRouteStatus? status, bool runs, bool networkEnabled, bool zcodeKeySaved)
    {
        if (!runs || (route == QuotaRoute.Zcode && !zcodeKeySaved))
        {
            return null;
        }

        return !networkEnabled && !route.IsLocal() ? QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff) : status;
    }

    public static QuotaStatusGlyph Glyph(QuotaRouteStatus status) => status.Kind switch
    {
        QuotaStatusKind.Ok => QuotaStatusGlyph.Ok,
        QuotaStatusKind.NotRunning => QuotaStatusGlyph.Paused,
        QuotaStatusKind.NetworkOff => QuotaStatusGlyph.NetworkOff,
        _ => QuotaStatusGlyph.Problem,
    };

    /// <summary>
    /// The sentence of <c>settings.quota.status.*</c>: the time of a success (short time today, else date and time), the
    /// site a browser route looks for, the agent that has to renew its sign-in or is not running, the host that refused.
    /// </summary>
    public static string Text(QuotaRouteStatus status, QuotaRoute route, long nowMs, Localizer loc, ZaiRegion region = ZaiRegion.Global,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            QuotaStatusKind.Ok => loc.Format("settings.quota.status.ok", QuotaText.When(status.AtMs, nowMs, loc, zone)),
            QuotaStatusKind.NoCredential => loc["settings.quota.status.noCredential"],
            QuotaStatusKind.NoBrowser => loc.Format("settings.quota.status.noBrowser", QuotaSwitchRows.Site(route)?.Name ?? AgentName(route, loc)),
            QuotaStatusKind.BrowserLocked => loc["win.settings.quota.status.browserLocked"],
            QuotaStatusKind.BrowserProtected => loc["win.settings.quota.status.browserProtected"],
            QuotaStatusKind.Expired => loc.Format("settings.quota.status.expired", AgentName(route, loc)),
            QuotaStatusKind.Denied => loc.Format("settings.quota.status.denied", status.Host is { Length: > 0 } host ? host : route.Host(region)),
            QuotaStatusKind.NetworkOff => loc["settings.quota.status.networkOff"],
            QuotaStatusKind.NotRunning => loc.Format("settings.quota.status.notRunning", AgentName(route, loc)),
            QuotaStatusKind.NoCertificates => loc["settings.quota.status.noCertificates"],
            _ => loc["settings.quota.status.failed"],
        };
    }

    /// <summary>The agent a status names: the source's name; Qoder's one account serves the IDE and the CLI.</summary>
    public static string AgentName(QuotaRoute route, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return route == QuotaRoute.Qoder ? "Qoder" : loc[DisplayText.SourceKey(route.Source())];
    }
}
