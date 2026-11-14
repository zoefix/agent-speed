namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// QUOTAS 5.6 <i>Estimate</i>: a joined Claude Code snapshot of one machine reaches the estimate window by window. A window is
/// given only when its time is later than the last one given for the same kind, name and minutes in this run; the windows
/// given are grouped by their time, oldest first, each group a snapshot at that time. Not thread-safe.
/// </summary>
internal sealed class QuotaWindowFeed
{
    private readonly Dictionary<(QuotaWindowKind Kind, string Name, int? Minutes), long> _last = [];

    public IReadOnlyList<QuotaSnapshot> Next(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var groups = new SortedDictionary<long, List<QuotaWindow>>();
        foreach (var w in snapshot.Windows)
        {
            var t = snapshot.TimeOf(w);
            var key = (w.Kind, w.Name ?? string.Empty, w.Minutes);
            if (_last.TryGetValue(key, out var last) && t <= last)
            {
                continue;
            }

            _last[key] = t;
            if (!groups.TryGetValue(t, out var group))
            {
                group = [];
                groups.Add(t, group);
            }

            group.Add(w with { ObservedMs = null });
        }

        return [.. groups.Select(g => new QuotaSnapshot(snapshot.Source, g.Key, g.Value))];
    }
}
