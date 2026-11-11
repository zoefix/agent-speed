namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateModel
{
    /// <summary>A token count line with its windows that have a reset, its instance tags, account, plan and <c>tLo</c>.</summary>
    private sealed class CodexLineState(CodexTokenLine raw, List<QuotaWindow> windows)
    {
        public CodexTokenLine Raw { get; } = raw;

        public List<QuotaWindow> Windows { get; } = windows;

        public string[] Tags { get; set; } = [];

        public long TLo { get; set; } = raw.Ts;

        public string Account { get; set; } = "";

        public string? Plan { get; set; }
    }

    /// <summary>
    /// QUOTAS 10.1 Codex history: each line takes the response it follows (same file, at most 5 s after its end), lines that
    /// name a common window instance are one account, a chain seen while <c>auth.json</c> gave a key takes that key, others
    /// are keyed by their smallest instance tag; a null plan takes the account's nearest earlier plan, else its first later.
    /// </summary>
    private List<CodexLineState> ResolveCodex(out int[] lineOf)
    {
        var responses = _input.Responses;
        lineOf = new int[responses.Count];
        Array.Fill(lineOf, -1);
        var lines = new List<CodexLineState>();
        foreach (var raw in _input.CodexLines.OrderBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Ts))
        {
            var windows = raw.Windows.Where(w => w.ResetsAtMs is { } r && r != 0 && w.Minutes is not null).ToList();
            if (windows.Count > 0)
            {
                lines.Add(new CodexLineState(raw, windows));
            }
        }

        var clusters = new Dictionary<string, SortedSet<long>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            foreach (var w in line.Windows)
            {
                var id = EstimateNames.WindowId(w);
                if (!clusters.TryGetValue(id, out var set))
                {
                    set = [];
                    clusters.Add(id, set);
                }

                set.Add(w.ResetsAtMs!.Value);
            }
        }

        var tagOf = new Dictionary<(string Window, long R), string>();
        foreach (var (id, resets) in clusters)
        {
            long? first = null;
            foreach (var r in resets)
            {
                if (first is null || r - first > EstimateLimits.InstanceJoinMs)
                {
                    first = r;
                }

                tagOf[(id, r)] = id + "@" + first.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var chains = new CodexChains();
        foreach (var line in lines)
        {
            line.Tags = [.. line.Windows.Select(w => tagOf[(EstimateNames.WindowId(w), w.ResetsAtMs!.Value)])];
            chains.Link(line.Tags);
        }

        MatchResponses(lines, lineOf);
        AssignAccounts(lines, chains, tagOf.Values);
        AssignPlans(lines);
        CodexAccount = lines.Count > 0 ? lines.OrderBy(l => l.Raw.Ts).Last().Account : null;
        return lines;
    }

    private void MatchResponses(List<CodexLineState> lines, int[] lineOf)
    {
        var responses = _input.Responses;
        var byFile = new Dictionary<string, (int[] Indices, long[] Ends)>(StringComparer.Ordinal);
        foreach (var group in Enumerable.Range(0, responses.Count)
                     .Where(i => responses[i].Source == SourceKind.Codex && responses[i].Host is null && responses[i].File is not null)
                     .GroupBy(i => responses[i].File!, StringComparer.Ordinal))
        {
            var indices = group.OrderBy(i => responses[i].EndMs).ToArray();
            byFile.Add(group.Key, (indices, indices.Select(i => responses[i].EndMs).ToArray()));
        }

        var used = new HashSet<int>();
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            if (!byFile.TryGetValue(line.Raw.File, out var file))
            {
                continue;
            }

            var ts = line.Raw.Ts;
            for (var j = EstimateMath.BisectLeft(file.Ends, ts - EstimateLimits.LineMatchMs); j < file.Ends.Length && file.Ends[j] <= ts; j++)
            {
                var i = file.Indices[j];
                if (used.Add(i))
                {
                    lineOf[i] = l;
                    line.TLo = Math.Min(ts, responses[i].StartMs is { } start and not 0 ? start : ts);
                    break;
                }
            }
        }
    }

    private void AssignAccounts(List<CodexLineState> lines, CodexChains chains, IEnumerable<string> tags)
    {
        var codexKeys = Timeline(SourceKind.Codex);
        var keyed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.OrderBy(l => l.Raw.Ts))
        {
            if (codexKeys.Count > 0 && line.Raw.Ts >= codexKeys[0].At && KeyTimeline.InForce(codexKeys, line.Raw.Ts) is { } e)
            {
                keyed[chains.Find(line.Tags[0])] = e.Key;
            }
        }

        var smallest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags.Distinct(StringComparer.Ordinal))
        {
            var root = chains.Find(tag);
            if (!smallest.TryGetValue(root, out var min) || string.CompareOrdinal(tag, min) < 0)
            {
                smallest[root] = tag;
            }
        }

        foreach (var line in lines)
        {
            var root = chains.Find(line.Tags[0]);
            line.Account = keyed.TryGetValue(root, out var key) ? key : AccountKeys.Key(_salt, AccountKeys.CodexChainVendor, smallest[root]);
        }
    }

    private static void AssignPlans(List<CodexLineState> lines)
    {
        var plans = new Dictionary<string, List<(long Ts, string Plan)>>(StringComparer.Ordinal);
        foreach (var line in lines.OrderBy(l => l.Raw.Ts))
        {
            line.Plan = AccountKeys.CleanPlan(line.Raw.PlanType);
            if (line.Plan is { } plan)
            {
                if (!plans.TryGetValue(line.Account, out var list))
                {
                    list = [];
                    plans.Add(line.Account, list);
                }

                list.Add((line.Raw.Ts, plan));
            }
        }

        foreach (var line in lines)
        {
            if (line.Plan is not null || !plans.TryGetValue(line.Account, out var known))
            {
                continue;
            }

            string? before = null;
            foreach (var (ts, plan) in known)
            {
                if (ts <= line.Raw.Ts)
                {
                    before = plan;
                }
            }

            line.Plan = before ?? known[0].Plan;
        }
    }
}
