using System.Collections.Immutable;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Quota.Routes;

/// <summary>
/// The phase 2 readers (QUOTAS 9.1), one per switch: Grok Build's log and its live request share the <c>grokBuild</c>
/// snapshot (the newer wins); Qoder's one account serves both Qoder sources.
/// </summary>
public enum QuotaRoute
{
    GrokBuild,
    GrokBuildLive,
    Devin,
    Antigravity,
    Kimi,
    Gemini,
    Droid,
    Zcode,
    Qoder,
    WorkBuddy,
}

public static class QuotaRoutes
{
    public static IReadOnlyList<QuotaRoute> All { get; } = Enum.GetValues<QuotaRoute>();

    public static SourceKind Source(this QuotaRoute route) => route switch
    {
        QuotaRoute.GrokBuild or QuotaRoute.GrokBuildLive => SourceKind.GrokBuild,
        QuotaRoute.Devin => SourceKind.Devin,
        QuotaRoute.Antigravity => SourceKind.Antigravity,
        QuotaRoute.Kimi => SourceKind.Kimi,
        QuotaRoute.Gemini => SourceKind.Gemini,
        QuotaRoute.Droid => SourceKind.Droid,
        QuotaRoute.Zcode => SourceKind.ZCode,
        QuotaRoute.Qoder => SourceKind.Qoder,
        _ => SourceKind.WorkBuddy,
    };

    /// <summary>The sources whose running agent makes the route ask (QUOTAS 6, 9.2–9.9).</summary>
    public static IReadOnlyList<SourceKind> Agents(this QuotaRoute route) =>
        route == QuotaRoute.Qoder ? [SourceKind.Qoder, SourceKind.QoderCli] : [route.Source()];

    /// <summary>Reads a file on this computer; no network.</summary>
    public static bool IsLocal(this QuotaRoute route) => route is QuotaRoute.GrokBuild or QuotaRoute.Devin;

    /// <summary>Reads the browser's session (QUOTAS 9.10): off by default, turned on only after the user agrees.</summary>
    public static bool ReadsBrowser(this QuotaRoute route) => route is QuotaRoute.Qoder or QuotaRoute.WorkBuddy;

    /// <summary>The host a refusal names (<c>settings.quota.status.denied</c>).</summary>
    public static string Host(this QuotaRoute route, ZaiRegion region = ZaiRegion.Global, string? site = null) => route switch
    {
        QuotaRoute.GrokBuild or QuotaRoute.GrokBuildLive => QuotaHosts.Grok,
        QuotaRoute.Devin or QuotaRoute.Antigravity => QuotaHosts.Loopback,
        QuotaRoute.Kimi => QuotaHosts.Kimi,
        QuotaRoute.Gemini => QuotaHosts.Gemini,
        QuotaRoute.Droid => QuotaHosts.Factory,
        QuotaRoute.Zcode => region.Host(),
        QuotaRoute.Qoder => site ?? QuotaHosts.Qoder,
        _ => QuotaHosts.WorkBuddy,
    };

    /// <summary>The sites whose cookies a browser route reads, in the order tried.</summary>
    public static IReadOnlyList<string> CookieSites(this QuotaRoute route) => route switch
    {
        QuotaRoute.Qoder => [QuotaHosts.Qoder, QuotaHosts.QoderCn],
        QuotaRoute.WorkBuddy => ["workbuddy.cn"],
        _ => [],
    };
}

/// <summary>
/// What one round may do (QUOTAS 9.1): the master switch, the routes that may run (source enabled and switch on), the
/// agents that run and whether the dashboard is open.
/// </summary>
public sealed record QuotaVendorRound(long NowMs, bool NetworkEnabled, IReadOnlySet<QuotaRoute> Routes, IReadOnlySet<SourceKind> Running,
    bool DashboardOpen, ZaiRegion ZaiRegion = ZaiRegion.Global, bool ReadsLocalNow = false);

/// <summary>A route's state as the UI shows it: its snapshot, its status line and whether it counts as signed in.</summary>
public sealed record QuotaRouteReading(QuotaSnapshot? Snapshot = null, QuotaRouteStatus? Status = null, bool SignedIn = false, string? Host = null);

/// <summary>Every route's reading after a round.</summary>
public sealed record QuotaVendorReadings(ImmutableDictionary<QuotaRoute, QuotaRouteReading> Routes, bool AntigravityRuns = false)
{
    public static QuotaVendorReadings Empty { get; } = new(ImmutableDictionary<QuotaRoute, QuotaRouteReading>.Empty);

    /// <summary>The source's snapshot: Grok Build's newer of log and live, Qoder's for both Qoder sources.</summary>
    public QuotaSnapshot? Snapshot(SourceKind source)
    {
        var key = source == SourceKind.QoderCli ? SourceKind.Qoder : source;
        var newest = QuotaRoutes.All.Where(r => r.Source() == key).Select(r => Routes.GetValueOrDefault(r)?.Snapshot).OfType<QuotaSnapshot>()
            .MaxBy(s => s.ObservedMs);
        return newest is null ? null : newest with { Source = source };
    }

    public bool SignedIn(SourceKind source)
    {
        var key = source == SourceKind.QoderCli ? SourceKind.Qoder : source;
        return QuotaRoutes.All.Any(r => r.Source() == key && Routes.GetValueOrDefault(r)?.SignedIn == true);
    }

    public QuotaRouteStatus? Status(QuotaRoute route) => Routes.GetValueOrDefault(route)?.Status;

    public bool Equals(QuotaVendorReadings? other) =>
        other is not null && AntigravityRuns == other.AntigravityRuns && Routes.Count == other.Routes.Count
        && Routes.All(p => other.Routes.TryGetValue(p.Key, out var o) && p.Value.Equals(o));

    public override int GetHashCode() => HashCode.Combine(Routes.Count, AntigravityRuns);
}
