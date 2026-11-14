namespace AgentSpeed.Core.Quota;

/// <summary>
/// QUOTAS 5.6 <i>Joining the routes</i>: the <c>claudeCode</c> snapshot from the bridge's part and the usage cache's, window
/// by window (kind and name) with the helper's rule, the bridge's value being the stored one. Each joined window carries its
/// own time and the snapshot's is the newest of them.
/// </summary>
public static class ClaudeCodeRoutes
{
    public static QuotaSnapshot? Join(QuotaSnapshot? bridge, QuotaSnapshot? cache)
    {
        if (bridge is null || cache is null)
        {
            return bridge ?? cache;
        }

        long tb = bridge.ObservedMs, tc = cache.ObservedMs;
        var joined = new List<QuotaWindow>(bridge.Windows.Count + cache.Windows.Count);
        var taken = new HashSet<(QuotaWindowKind, string?)>();
        foreach (var w in bridge.Windows)
        {
            if (cache.Windows.LastOrDefault(x => SameId(x, w)) is not { } c)
            {
                joined.Add(w with { ObservedMs = tb });
                continue;
            }

            taken.Add((c.Kind, c.Name));
            if (SameWindow(w, c))
            {
                joined.Add((c.UsedPercent > w.UsedPercent ? c : w) with { ObservedMs = Math.Max(tb, tc) });
            }
            else if (NewerPeriod(w, c))
            {
                joined.Add(c with { ObservedMs = tc });
            }
            else
            {
                joined.Add(w with { ObservedMs = tb });
            }
        }

        joined.AddRange(cache.Windows.Where(x => !taken.Contains((x.Kind, x.Name))).Select(x => x with { ObservedMs = tc }));
        var observed = joined.Count > 0 ? joined.Max(w => w.ObservedMs!.Value) : Math.Max(tb, tc);
        return new QuotaSnapshot(SourceKind.ClaudeCode, observed, QuotaWindows.Ordered(joined));
    }

    private static bool SameId(QuotaWindow a, QuotaWindow b) => a.Kind == b.Kind && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>Resets within 60 s of each other, or both unknown.</summary>
    private static bool SameWindow(QuotaWindow a, QuotaWindow b) =>
        (a.ResetsAtMs, b.ResetsAtMs) switch
        {
            (null, null) => true,
            ({ } ra, { } rb) => Math.Abs(rb - ra) <= ClaudeCliQuota.SameWindowMs,
            _ => false,
        };

    /// <summary>The new value's reset is known and more than 60 s after the old one's (an unknown one counts as earlier than any).</summary>
    private static bool NewerPeriod(QuotaWindow old, QuotaWindow next) =>
        next.ResetsAtMs is { } rn && (old.ResetsAtMs is not { } ro || rn - ro > ClaudeCliQuota.SameWindowMs);
}
