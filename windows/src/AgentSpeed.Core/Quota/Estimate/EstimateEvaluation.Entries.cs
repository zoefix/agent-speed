namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateEvaluation
{
    /// <summary>
    /// The report entries at <paramref name="nowMs"/> (REPORTING-EXTENSIONS 11.2): one per account and window, its newest
    /// instance, in source order then window order, at most 16; <paramref name="include"/> keeps the percent's sources.
    /// </summary>
    public IReadOnlyList<QuotaReportEntry> Entries(long nowMs, Func<SourceKind, bool>? include = null)
    {
        var rows = new List<(Current Current, QuotaReportEntry Entry)>();
        foreach (var current in _current.Values)
        {
            if (include is null || include(current.Last.Source))
            {
                rows.Add((current, EntryOf(current, nowMs)));
            }
        }

        rows.Sort((a, b) => CompareRows(a.Current, b.Current));
        return [.. rows.Take(EstimateLimits.MaxEntries).Select(r => r.Entry)];
    }

    /// <summary>The entry of one account's window at <paramref name="nowMs"/>, null when the account has no instance of it.</summary>
    public QuotaReportEntry? EntryFor(string account, string windowId, long nowMs) =>
        _current.TryGetValue((account, windowId), out var current) ? EntryOf(current, nowMs) : null;

    private static int CompareRows(Current a, Current b)
    {
        var c = a.Sources[0].CompareTo(b.Sources[0]);
        for (var i = 0; c == 0 && i < a.NameRank.Length; i++)
        {
            c = a.NameRank[i].CompareTo(b.NameRank[i]);
        }

        return c != 0 ? c : string.CompareOrdinal(EstimateNames.Source(a.Last.Source), EstimateNames.Source(b.Last.Source));
    }

    private QuotaReportEntry EntryOf(Current current, long nowMs)
    {
        var x = current.Item;
        var last = current.Last;
        var instance = x.Instance;
        long? firstAfter = last.Kind == QuotaWindowKind.FiveHour ? Model.FirstStartAfter(x.Account, instance.R, nowMs) : null;
        var basis = new EntryBasis(last.Source, current.Sources, x.Plan, x.Tier, last.Kind, last.Name, last.Minutes, last.P, last.T,
            instance.R, instance.S, x.Account, Estimates.GetValueOrDefault(x.Class) ?? CapEstimate.None, firstAfter);
        return QuotaEntryBuilder.Build(basis, nowMs, Model.Ledger, Model.PriceVersion);
    }
}
