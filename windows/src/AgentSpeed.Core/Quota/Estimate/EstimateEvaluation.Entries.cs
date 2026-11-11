namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateEvaluation
{
    /// <summary>
    /// The report entries at <paramref name="nowMs"/> for the service of api origin <paramref name="origin"/>
    /// (REPORTING-EXTENSIONS 11.2): one per account and window, its newest instance, each carrying its account's tag for that
    /// service; in source order, then window order, then the source of the newest value, then the tag (two accounts of one
    /// source and window); at most 16. <paramref name="include"/> keeps the percent's sources.
    /// </summary>
    public IReadOnlyList<QuotaReportEntry> Entries(long nowMs, string origin, Func<SourceKind, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var rows = new List<(Current Current, string Tag)>();
        foreach (var current in _current.Values)
        {
            if (include is null || include(current.Last.Source))
            {
                rows.Add((current, AccountKeys.ReportTag(origin, current.Item.Account)));
            }
        }

        rows.Sort(static (a, b) => CompareRows(a.Current, a.Tag, b.Current, b.Tag));
        return [.. rows.Take(EstimateLimits.MaxEntries).Select(r => EntryOf(r.Current, r.Tag, nowMs))];
    }

    /// <summary>
    /// The entry of one account's window at <paramref name="nowMs"/> as shown (never sent, so without a tag), null when the
    /// account has no instance of it.
    /// </summary>
    public QuotaReportEntry? EntryFor(string account, string windowId, long nowMs) =>
        _current.TryGetValue((account, windowId), out var current) ? EntryOf(current, string.Empty, nowMs) : null;

    private static int CompareRows(Current a, string aTag, Current b, string bTag)
    {
        var c = a.Sources[0].CompareTo(b.Sources[0]);
        for (var i = 0; c == 0 && i < a.NameRank.Length; i++)
        {
            c = a.NameRank[i].CompareTo(b.NameRank[i]);
        }

        if (c == 0)
        {
            c = string.CompareOrdinal(EstimateNames.Source(a.Last.Source), EstimateNames.Source(b.Last.Source));
        }

        return c != 0 ? c : string.CompareOrdinal(aTag, bTag);
    }

    private QuotaReportEntry EntryOf(Current current, string tag, long nowMs)
    {
        var x = current.Item;
        var last = current.Last;
        var instance = x.Instance;
        long? firstAfter = last.Kind == QuotaWindowKind.FiveHour ? Model.FirstStartAfter(x.Account, instance.R, nowMs) : null;
        // An inferred plan fills only a missing one and never moves the class (QUOTAS 10.2).
        var inferred = x.Plan is null ? InferredPlans.GetValueOrDefault(x.Account) : null;
        var basis = new EntryBasis(last.Source, current.Sources, inferred?.Plan ?? x.Plan, inferred is null ? x.Tier : inferred.Tier, last.Kind,
            last.Name, last.Minutes, last.P, last.T, instance.R, instance.S, x.Account, Estimates.GetValueOrDefault(x.Class) ?? CapEstimate.None,
            firstAfter, inferred is not null);
        return QuotaEntryBuilder.Build(basis, tag, nowMs, Model.Ledger, Model.PriceVersion);
    }
}
