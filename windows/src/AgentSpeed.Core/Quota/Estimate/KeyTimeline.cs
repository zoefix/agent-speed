namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>Key timelines (QUOTAS 10.1): sorted, without duplicates; the entry in force is the last at or before a time, else the first.</summary>
public static class KeyTimeline
{
    public static KeyEntry? InForce(IReadOnlyList<KeyEntry> timeline, long at)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Count == 0)
        {
            return null;
        }

        int lo = 0, hi = timeline.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (at < timeline[mid].At)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo > 0 ? timeline[lo - 1] : timeline[0];
    }

    public static List<KeyEntry> Normalized(IEnumerable<KeyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.Distinct().ToList();
        list.Sort(Compare);
        return list;
    }

    private static int Compare(KeyEntry a, KeyEntry b)
    {
        var c = a.At.CompareTo(b.At);
        if (c == 0)
        {
            c = string.CompareOrdinal(a.Key, b.Key);
        }

        if (c == 0)
        {
            c = string.CompareOrdinal(a.Plan ?? "None", b.Plan ?? "None");
        }

        return c != 0 ? c : string.CompareOrdinal(a.Tier ?? "None", b.Tier ?? "None");
    }
}
