namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateModel
{
    private void BuildTimelines()
    {
        var raw = new Dictionary<SourceKind, List<KeyEntry>>
        {
            [SourceKind.Codex] = [.. _input.CodexKeys.Select(k => new KeyEntry(k.At, k.Key, null, null))],
            [SourceKind.ClaudeCode] = [],
        };
        foreach (var p in _input.ClaudeProfiles.OrderBy(p => p.At))
        {
            var entry = new KeyEntry(p.At, p.Key, p.Plan, p.Tier);
            raw[SourceKind.ClaudeCode].Add(entry);
            _orgKeys[entry] = p.OrgKey;
        }

        foreach (var k in _stored.Keys)
        {
            var entry = new KeyEntry(k.At, k.Key, k.Plan, k.Tier);
            Append(raw, k.Source, entry);
            if (k.Source == SourceKind.ClaudeCode && !string.IsNullOrEmpty(k.OrgKey))
            {
                _orgKeys.TryAdd(entry, k.OrgKey);
            }
        }

        foreach (var s in _input.Snapshots)
        {
            if (s.Source == SourceKind.ClaudeDesktop && s.DesktopOrgKey is { } org)
            {
                Append(raw, SourceKind.ClaudeDesktop, new KeyEntry(s.ObservedMs, org, null, null));
            }
        }

        foreach (var (source, entries) in raw)
        {
            _timelines[source] = KeyTimeline.Normalized(entries);
        }

        _desktopOrgs = _timelines.Remove(SourceKind.ClaudeDesktop, out var orgs) ? orgs : [];
        _timelines[SourceKind.ClaudeDesktop] = DesktopTimeline(_timelines[SourceKind.ClaudeCode]);
    }

    private static void Append(Dictionary<SourceKind, List<KeyEntry>> timelines, SourceKind source, KeyEntry entry)
    {
        if (!timelines.TryGetValue(source, out var list))
        {
            list = [];
            timelines.Add(source, list);
        }

        list.Add(entry);
    }

    /// <summary>Every fresh and stored window observation of a Claude source: the point and, for a stored one, its account.</summary>
    private List<(WindowPoint Point, string? Account)> WindowPoints(SourceKind source)
    {
        var points = new List<(WindowPoint, string?)>();
        foreach (var s in _input.Snapshots)
        {
            if (s.Source != source)
            {
                continue;
            }

            foreach (var w in s.Windows)
            {
                if (w.ResetsAtMs is { } r && w.Minutes is { } minutes)
                {
                    points.Add((new WindowPoint(s.ObservedMs, EstimateNames.WindowId(w), r, minutes), null));
                }
            }
        }

        foreach (var o in _stored.Observations)
        {
            if (o.Source == source)
            {
                points.Add((new WindowPoint(o.T, o.WindowId, o.R, o.Minutes), o.Account));
            }
        }

        return points;
    }

    /// <summary>
    /// QUOTAS 10.1: an entry at each entry of Desktop's organization timeline and of the CLI's key timeline: the CLI's key
    /// while the merge rule holds, else Desktop's own organization key.
    /// </summary>
    private List<KeyEntry> DesktopTimeline(List<KeyEntry> cli)
    {
        var timeline = new List<KeyEntry>();
        if (_desktopOrgs.Count == 0)
        {
            return timeline;
        }

        var desk = new WindowPointSet(WindowPoints(SourceKind.ClaudeDesktop).Select(p => p.Point));
        var cliPoints = WindowPoints(SourceKind.ClaudeCode)
            .Select(p => (p.Point, Key: p.Account ?? KeyTimeline.InForce(cli, p.Point.T)?.Key))
            .ToList();
        var cliSets = new Dictionary<string, WindowPointSet>(StringComparer.Ordinal);
        var times = _desktopOrgs.Select(o => o.At).Concat(cli.Select(c => c.At)).Distinct().Order();
        foreach (var at in times)
        {
            var own = KeyTimeline.InForce(_desktopOrgs, at)!.Value.Key;
            if (KeyTimeline.InForce(cli, at) is { } c)
            {
                if (!cliSets.TryGetValue(c.Key, out var set))
                {
                    set = new WindowPointSet(cliPoints.Where(p => string.Equals(p.Key, c.Key, StringComparison.Ordinal)).Select(p => p.Point));
                    cliSets.Add(c.Key, set);
                }

                var fingerprint = WindowPointSet.Fingerprint(desk, set, at);
                if (AccountMerge.DesktopSharesCli(own, _orgKeys.GetValueOrDefault(c), c.Plan, fingerprint))
                {
                    timeline.Add(c with { At = at });
                    continue;
                }
            }

            timeline.Add(new KeyEntry(at, own, null, null));
        }

        return timeline;
    }
}
