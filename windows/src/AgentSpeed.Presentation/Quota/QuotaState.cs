using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
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

    public QuotaSnapshot? Of(SourceKind source) => Snapshots.GetValueOrDefault(source);

    /// <summary>The source's quota has a window to show.</summary>
    public bool HasWindows(SourceKind source) => Of(source) is { HasWindows: true };

    /// <summary>A quota observed within the last 24 hours lists its source on the dashboard (DESIGN 4).</summary>
    public bool ListsSource(SourceKind source, long nowMs) => Of(source) is { HasWindows: true } s && nowMs - s.ObservedMs <= 24 * 3_600_000L;

    /// <summary>The 額度 card's sources among <paramref name="sources"/>, in source order: active with at least one window.</summary>
    public IReadOnlyList<QuotaSnapshot> CardSources(IEnumerable<SourceKind> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return [.. sources.Distinct().Order().Where(s => Active.Contains(s) && HasWindows(s)).Select(s => Snapshots[s])];
    }

    public bool Equals(QuotaState? other) =>
        other is not null && CliStatus == other.CliStatus && CliFileTime == other.CliFileTime && Active.SetEquals(other.Active)
        && Snapshots.Count == other.Snapshots.Count && Snapshots.All(p => other.Snapshots.TryGetValue(p.Key, out var o) && p.Value.Equals(o));

    public override int GetHashCode() => HashCode.Combine(CliStatus, Snapshots.Count, Active.Count);
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
