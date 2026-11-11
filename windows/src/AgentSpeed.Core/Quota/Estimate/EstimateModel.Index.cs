namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateModel
{
    /// <summary>
    /// Per account: its responses by end time, and for its Claude CLI responses the ends with the smallest start of every
    /// suffix, so that <c>tLo</c>, the sources of a window and the first response after a reset are cheap.
    /// </summary>
    private sealed class AccountResponses(int[] byEnd, long[] ends, long[] cliEnds, long[] cliMinStart)
    {
        public int[] ByEnd { get; } = byEnd;

        public long[] Ends { get; } = ends;

        public long[] CliEnds { get; } = cliEnds;

        public long[] CliMinStart { get; } = cliMinStart;
    }

    private Dictionary<string, AccountResponses>? _index;

    private Dictionary<string, AccountResponses> AccountIndex()
    {
        if (_index is { } built)
        {
            return built;
        }

        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < _accounts.Length; i++)
        {
            if (_accounts[i] is { } account)
            {
                if (!groups.TryGetValue(account, out var list))
                {
                    list = [];
                    groups.Add(account, list);
                }

                list.Add(i);
            }
        }

        var responses = _input.Responses;
        var index = new Dictionary<string, AccountResponses>(StringComparer.Ordinal);
        foreach (var (account, list) in groups)
        {
            var byEnd = list.OrderBy(i => responses[i].EndMs).ToArray();
            var ends = byEnd.Select(i => responses[i].EndMs).ToArray();
            var cli = byEnd.Where(i => responses[i].Source == SourceKind.ClaudeCode && responses[i].StartMs is not null).ToArray();
            var cliEnds = cli.Select(i => responses[i].EndMs).ToArray();
            var cliMinStart = new long[cli.Length];
            var min = long.MaxValue;
            for (var j = cli.Length - 1; j >= 0; j--)
            {
                min = Math.Min(min, responses[cli[j]].StartMs!.Value);
                cliMinStart[j] = min;
            }

            index.Add(account, new AccountResponses(byEnd, ends, cliEnds, cliMinStart));
        }

        _index = index;
        return index;
    }

    /// <summary>The sources whose responses of the account ended at or after <paramref name="fromMs"/>.</summary>
    internal IEnumerable<SourceKind> SourcesSince(string account, long fromMs)
    {
        if (!AccountIndex().TryGetValue(account, out var index))
        {
            yield break;
        }

        for (var i = EstimateMath.BisectLeft(index.Ends, fromMs); i < index.ByEnd.Length; i++)
        {
            yield return _input.Responses[index.ByEnd[i]].Source;
        }
    }

    /// <summary>The start of the first response of the account that started at or after <paramref name="r"/> and ended by <paramref name="nowMs"/>.</summary>
    internal long? FirstStartAfter(string account, long r, long nowMs)
    {
        if (!AccountIndex().TryGetValue(account, out var index))
        {
            return null;
        }

        long? first = null;
        for (var i = EstimateMath.BisectLeft(index.Ends, r); i < index.ByEnd.Length; i++)
        {
            var response = _input.Responses[index.ByEnd[i]];
            if (response.EndMs > nowMs)
            {
                break;
            }

            if (response.StartMs is { } start && start >= r && (first is null || start < first))
            {
                first = start;
            }
        }

        return first;
    }
}
