using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Quota;

/// <summary>
/// The quotas as the surfaces show them: each switched-on source's snapshot, the sources whose app runs and is signed
/// in (the 額度 card, QUOTAS 6), and the Claude CLI bridge's status. Immutable.
/// </summary>
public sealed record QuotaState
{
    public static QuotaState Empty { get; } = new();

    /// <summary>The latest snapshot of each source whose quota switch is on (windows may be empty).</summary>
    public ImmutableDictionary<SourceKind, QuotaSnapshot> Snapshots { get; init; } = ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty;

    /// <summary>The sources whose app runs and is signed in.</summary>
    public ImmutableHashSet<SourceKind> Active { get; init; } = [];

    public ClaudeCliBridgeStatus CliStatus { get; init; } = ClaudeCliBridgeStatus.Off;

    /// <summary>When the quota file was written last (Settings' <c>settings.quota.cli.ok</c>).</summary>
    public DateTimeOffset? CliFileTime { get; init; }

    /// <summary>Each phase 2 route's status line (QUOTAS 9; DESIGN 5, <i>額度, phase 2</i>), while its switch is on.</summary>
    public ImmutableDictionary<QuotaRoute, QuotaRouteStatus> RouteStatuses { get; init; } = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty;

    /// <summary>A ZCode key is in the secret store (never the key itself).</summary>
    public bool ZcodeKeySaved { get; init; }

    /// <summary>Every remote host's quota lines and bridge state (REMOTE 11.1), by host id.</summary>
    public ImmutableDictionary<string, HostQuotaState> Hosts { get; init; } = ImmutableDictionary<string, HostQuotaState>.Empty;

    public QuotaRouteStatus? StatusOf(QuotaRoute route) => RouteStatuses.TryGetValue(route, out var status) ? status : null;

    public HostQuotaState HostOf(string hostId) => Hosts.GetValueOrDefault(hostId) ?? HostQuotaState.Empty;

    public QuotaSnapshot? Of(SourceKind source) => Snapshots.GetValueOrDefault(source);

    /// <summary>The source's quota has a window to show.</summary>
    public bool HasWindows(SourceKind source) => Of(source) is { HasWindows: true };

    /// <summary>A quota observed within the last 24 hours lists its source on the dashboard (DESIGN 4).</summary>
    public bool ListsSource(SourceKind source, long nowMs) => Of(source) is { HasWindows: true } s && nowMs - s.ObservedMs <= 24 * 3_600_000L;

    /// <summary>
    /// The 額度 card's sources among <paramref name="sources"/>, in source order: active with at least one window. Qoder's
    /// one account shows once, under the first Qoder source that runs.
    /// </summary>
    public IReadOnlyList<QuotaSnapshot> CardSources(IEnumerable<SourceKind> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var shown = new List<QuotaSnapshot>();
        foreach (var source in sources.Distinct().Order().Where(s => Active.Contains(s) && HasWindows(s)))
        {
            if (source != SourceKind.QoderCli || shown.All(s => s.Source != SourceKind.Qoder))
            {
                shown.Add(Snapshots[source]);
            }
        }

        return shown;
    }

    /// <summary>
    /// The 額度 card's items for an orb (DESIGN 13 <i>Host quotas</i>): this computer's first while the orb tracks it, then
    /// each tracked, connected host's sources whose agent runs and is signed in there, in settings order.
    /// </summary>
    /// <param name="hosts">The hosts in settings order, with their display names.</param>
    public IReadOnlyList<QuotaCardItem> CardItems(Func<SourceKind, bool> tracked, Func<string, bool> trackedHost,
        IReadOnlyList<(string Id, string Name)> hosts, IReadOnlySet<string> connected)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(trackedHost);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(connected);
        var items = new List<QuotaCardItem>();
        if (trackedHost(RemoteHost.LocalId))
        {
            items.AddRange(CardSources(Active.Where(tracked)).Select(s => new QuotaCardItem(s)));
        }

        foreach (var (id, name) in hosts)
        {
            if (!trackedHost(id) || !connected.Contains(id) || !Hosts.TryGetValue(id, out var host))
            {
                continue;
            }

            foreach (var source in Settings.QuotaSettings.Sources)
            {
                if (tracked(source) && host.Reports.TryGetValue(source, out var report) && report.Running && report.SignedIn && report.Snapshot.HasWindows)
                {
                    items.Add(new QuotaCardItem(report.Snapshot, id, name));
                }
            }
        }

        return items;
    }

    /// <summary>A host's quota lines in Dashboard → Hosts: every source with windows observed within a day, in source order.</summary>
    public IReadOnlyList<QuotaSnapshot> HostSnapshots(string hostId, long nowMs) =>
        Hosts.TryGetValue(hostId, out var host)
            ? [.. Settings.QuotaSettings.Sources.Select(s => host.Reports.GetValueOrDefault(s)?.Snapshot)
                .OfType<QuotaSnapshot>().Where(s => s.HasWindows && nowMs - s.ObservedMs <= 24 * 3_600_000L)]
            : [];

    public bool Equals(QuotaState? other) =>
        other is not null && CliStatus == other.CliStatus && CliFileTime == other.CliFileTime && Active.SetEquals(other.Active)
        && ZcodeKeySaved == other.ZcodeKeySaved
        && Snapshots.Count == other.Snapshots.Count && Snapshots.All(p => other.Snapshots.TryGetValue(p.Key, out var o) && p.Value.Equals(o))
        && RouteStatuses.Count == other.RouteStatuses.Count && RouteStatuses.All(p => other.RouteStatuses.TryGetValue(p.Key, out var o) && p.Value == o)
        && Hosts.Count == other.Hosts.Count && Hosts.All(p => other.Hosts.TryGetValue(p.Key, out var o) && p.Value.Equals(o));

    public override int GetHashCode() => HashCode.Combine(CliStatus, Snapshots.Count, Active.Count, Hosts.Count);
}

/// <summary>One remote host's quotas (REMOTE 11.1): each source's last <c>quota</c> line and the Claude Code bridge's state.</summary>
public sealed record HostQuotaState
{
    public static HostQuotaState Empty { get; } = new();

    public ImmutableDictionary<SourceKind, RemoteQuotaReport> Reports { get; init; } = ImmutableDictionary<SourceKind, RemoteQuotaReport>.Empty;

    public RemoteQuotaBridge? Bridge { get; init; }

    public bool Equals(HostQuotaState? other) =>
        other is not null && Equals(Bridge, other.Bridge) && Reports.Count == other.Reports.Count
        && Reports.All(p => other.Reports.TryGetValue(p.Key, out var o) && p.Value.Equals(o));

    public override int GetHashCode() => HashCode.Combine(Reports.Count, Bridge);
}

/// <summary>A source of the 額度 card: this computer's (no host) or a remote host's, whose header reads <c>&lt;source&gt; · &lt;host&gt;</c>.</summary>
public sealed record QuotaCardItem(QuotaSnapshot Snapshot, string? HostId = null, string? HostName = null)
{
    public SourceKind Source => Snapshot.Source;

    /// <summary>Distinct for the same source on several hosts.</summary>
    public string Key => HostId is null ? Source.ToString() : Source + "@" + HostId;
}

/// <summary>How a bar's fill reads (DESIGN 4, 2a.6): the source color, amber from 80 %, red from 95 % (the percent too).</summary>
public enum QuotaTone
{
    Normal,
    Warning,
    Critical,
}

/// <summary>One window line of the dashboard's Sources row or the orb's 額度 card, every text ready.</summary>
/// <param name="Bar">The bar's fill, 0…1.</param>
/// <param name="Pace">The pace mark, 0…1; null while the reset is unknown or passed.</param>
/// <param name="ResetLong">The dashboard's reset text; empty while unknown or passed.</param>
/// <param name="ResetShort">The orb card's reset text; empty while unknown or passed.</param>
/// <param name="Age">` · ` and <c>quota.age</c> while the snapshot is over an hour old; empty otherwise.</param>
public sealed record QuotaLine(string Label, int Percent, double Bar, QuotaTone Tone, double? Pace, string ResetLong, string ResetShort, string Age,
    string Tooltip, string Spoken, bool Passed)
{
    public string PercentText => QuotaText.Percent(Percent);

    public static QuotaTone ToneOf(int percent) => percent >= 95 ? QuotaTone.Critical : percent >= 80 ? QuotaTone.Warning : QuotaTone.Normal;
}

/// <summary>Builds the window lines of a snapshot at a moment (QUOTAS 2, 7; DESIGN 4).</summary>
public static class QuotaLines
{
    public static IReadOnlyList<QuotaLine> Of(QuotaSnapshot snapshot, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loc);
        var age = QuotaText.Age(snapshot.ObservedMs, nowMs, loc) is { } text ? " · " + text : string.Empty;
        var updated = QuotaText.Updated(snapshot.ObservedMs, nowMs, loc, zone);
        var shown = QuotaWindows.Shown(snapshot, nowMs, QuotaText.Labels(loc));
        var lines = new QuotaLine[shown.Count];
        for (var i = 0; i < lines.Length; i++)
        {
            var w = shown[i];
            var pace = w.Passed ? null : QuotaWindows.Pace(w.Window, nowMs);
            var tooltip = pace is { } p ? updated + "\n" + QuotaText.Pace(p, loc) : updated;
            lines[i] = new QuotaLine(w.Label, w.Percent, w.Bar / 100, QuotaLine.ToneOf(w.Percent), pace,
                w.ResetsAtMs is { } reset ? QuotaText.ResetLong(reset, nowMs, loc, zone) : string.Empty,
                w.ResetsAtMs is { } r ? QuotaText.ResetShort(r, nowMs, loc, zone) : string.Empty,
                age, tooltip, QuotaText.Spoken(w, nowMs, loc, zone), w.Passed);
        }

        return lines;
    }

    /// <summary>The windows spoken after a row's cost, joined by <c>list.separator</c>; empty without windows.</summary>
    public static string Spoken(IReadOnlyList<QuotaLine> lines, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(loc);
        return string.Join(loc["list.separator"], lines.Select(l => l.Spoken));
    }
}
