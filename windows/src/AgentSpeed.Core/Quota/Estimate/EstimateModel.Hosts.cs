namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateModel
{
    private readonly Dictionary<(string Host, SourceKind Source), List<KeyEntry>> _hostTimelines = [];
    private readonly Dictionary<(string Host, SourceKind Source, string Key), string> _hostAccounts = [];

    /// <summary>The account a host's source spends on at <paramref name="atMs"/>: its key unless the fingerprint contradicts it, else the (host, source) pair.</summary>
    public string HostAccountOf(string host, SourceKind source, long atMs)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _hostTimelines.TryGetValue((host, source), out var timeline) && KeyTimeline.InForce(timeline, atMs) is { } e
            ? _hostAccounts[(host, source, e.Key)]
            : HostPair(host, source);
    }

    /// <summary>
    /// REMOTE 11.1: a host's sources and this computer's sources with equal keys are one account unless the latest
    /// comparable pair of their windows contradicts it; a host without keys (an older relay) keeps (host, source) accounts.
    /// </summary>
    private void BuildHosts(IReadOnlyList<CodexLineState> lines)
    {
        var raw = new Dictionary<(string, SourceKind), List<KeyEntry>>();
        foreach (var s in _input.HostSnapshots)
        {
            if (s.Account is { } key && AccountKeys.IsKey(key))
            {
                if (!raw.TryGetValue((s.Host, s.Source), out var list))
                {
                    list = [];
                    raw.Add((s.Host, s.Source), list);
                }

                list.Add(new KeyEntry(s.ObservedMs, key, AccountKeys.CleanPlan(s.Plan), AccountKeys.CleanPlan(s.Tier)));
            }
        }

        if (raw.Count == 0)
        {
            return;
        }

        var local = LocalPointsByKey();
        AddCodexLinePoints(local, lines);
        foreach (var ((host, source), entries) in raw)
        {
            _hostTimelines[(host, source)] = KeyTimeline.Normalized(entries);
            foreach (var key in entries.Select(e => e.Key).Distinct(StringComparer.Ordinal))
            {
                var mine = _input.HostSnapshots.Where(s => s.Host == host && s.Source == source && s.Account == key).ToList();
                var points = new WindowPointSet(mine.SelectMany(s => Points(s.ObservedMs, s.Windows)));
                var at = mine.Max(s => s.ObservedMs);
                var fingerprint = local.TryGetValue(key, out var theirs) ? WindowPointSet.Fingerprint(points, new WindowPointSet(theirs), at) : null;
                _hostAccounts[(host, source, key)] = AccountMerge.KeysShare(fingerprint) ? key : HostPair(host, source) + "#" + key;
            }
        }
    }

    private Dictionary<string, List<WindowPoint>> LocalPointsByKey()
    {
        var byKey = new Dictionary<string, List<WindowPoint>>(StringComparer.Ordinal);
        void Add(string key, WindowPoint point)
        {
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey.Add(key, list);
            }

            list.Add(point);
        }

        foreach (var s in _input.Snapshots)
        {
            if (s.Source is SourceKind.ClaudeCode or SourceKind.ClaudeDesktop && KeyTimeline.InForce(Timeline(s.Source), s.ObservedMs) is { } e)
            {
                foreach (var p in Points(s.ObservedMs, s.Windows))
                {
                    Add(e.Key, p);
                }
            }
        }

        foreach (var o in _stored.Observations)
        {
            Add(o.Account, new WindowPoint(o.T, o.WindowId, o.R, o.Minutes));
        }

        return byKey;
    }

    private void AddCodexLinePoints(Dictionary<string, List<WindowPoint>> byKey, IReadOnlyList<CodexLineState> lines)
    {
        foreach (var line in lines)
        {
            if (!byKey.TryGetValue(line.Account, out var list))
            {
                list = [];
                byKey.Add(line.Account, list);
            }

            list.AddRange(Points(line.Raw.Ts, line.Windows));
        }
    }

    private static IEnumerable<WindowPoint> Points(long t, IReadOnlyList<QuotaWindow> windows)
    {
        foreach (var w in windows)
        {
            if (w.ResetsAtMs is { } r && w.Minutes is { } minutes)
            {
                yield return new WindowPoint(t, EstimateNames.WindowId(w), r, minutes);
            }
        }
    }

    private string? HostResponseAccount(string host, EstimateResponse r)
    {
        if ((r.Source == SourceKind.Codex && !IsOpenAi(r.Provider)) || (r.Source is SourceKind.ClaudeCode or SourceKind.ClaudeDesktop && r.CloudRoute))
        {
            return null;
        }

        return HostAccountOf(host, r.Source, r.EndMs);
    }

    private void CollectHostObservations()
    {
        foreach (var s in _input.HostSnapshots)
        {
            var account = HostAccountOf(s.Host, s.Source, s.ObservedMs);
            var tLo = s.Source == SourceKind.ClaudeCode ? EarliestLiveStart(account, s.ObservedMs) : s.ObservedMs;
            AddWindows(account, s.Source, s.ObservedMs, tLo, s.Windows, AccountKeys.CleanPlan(s.Plan), AccountKeys.CleanPlan(s.Tier));
        }
    }
}
