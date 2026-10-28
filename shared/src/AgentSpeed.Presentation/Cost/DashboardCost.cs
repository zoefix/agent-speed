using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;

namespace AgentSpeed.Presentation.Cost;

/// <summary>One source's share of today's billable cost in the dashboard's ⓘ popup.</summary>
/// <param name="Share">The part's cost relative to the largest part (0..1), for the thin share bar.</param>
public sealed record TodayCostPart(SourceKind Source, CostTally Cost, long Requests, string AmountText, double Share);

/// <summary>
/// 今日費用 under the dashboard grid (DESIGN 9 dashboard Today, METRICS 9.4, 9.6): today's billable cost, its parts
/// sorted by cost, the unpriced and excluded notices and the rate line.
/// </summary>
public sealed record TodayCostSummary(
    CostTally Total,
    long Requests,
    IReadOnlyList<TodayCostPart> Parts,
    IReadOnlyList<SourceKind> Excluded,
    string AmountText,
    string Spoken,
    string? UnpricedText,
    string? ExcludedText,
    string? RateLine)
{
    public static TodayCostSummary From(SpeedSnapshot snapshot, CostDisplay cost)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(cost);
        var loc = cost.Localizer;
        var billable = cost.Billable;
        var total = CostTally.Zero;
        long requests = 0;
        var parts = new List<(SourceKind Source, CostTally Cost, long Requests)>();
        var excluded = new List<SourceKind>();
        foreach (var status in snapshot.Sources)
        {
            if (status.Today.Requests <= 0)
            {
                continue;
            }

            if (!billable.Contains(status.Kind))
            {
                excluded.Add(status.Kind);
                continue;
            }

            total += status.TodayCost;
            requests += status.Today.Requests;
            parts.Add((status.Kind, status.TodayCost, status.Today.Requests));
        }

        parts.Sort(static (a, b) => a.Cost.CostMicroUsd != b.Cost.CostMicroUsd
            ? b.Cost.CostMicroUsd.CompareTo(a.Cost.CostMicroUsd)
            : a.Source.CompareTo(b.Source));
        var largest = parts.Count > 0 ? parts.Max(p => p.Cost.CostMicroUsd) : 0;
        var rows = parts
            .Select(p => new TodayCostPart(p.Source, p.Cost, p.Requests, cost.TallyText(p.Cost, p.Requests),
                largest > 0 ? (double)p.Cost.CostMicroUsd / largest : 0))
            .ToList();
        var amount = requests == 0 ? cost.AmountText(0) : cost.TallyText(total, requests);
        var spoken = requests == 0 ? cost.Spoken(cost.Amount(0).Number) : cost.SpokenTally(total, requests);
        return new TodayCostSummary(
            total,
            requests,
            rows,
            excluded,
            amount,
            spoken,
            total.UnpricedRequests > 0 ? loc.Format("cost.unpriced", total.UnpricedRequests) : null,
            excluded.Count > 0
                ? loc.Format("cost.subscriptionExcluded", string.Join(loc["list.separator"], excluded.Select(k => loc[DisplayText.SourceKey(k)])))
                : null,
            cost.IsUsd ? null : cost.RateStatus);
    }
}

/// <summary>A dashboard source row in money (DESIGN 9 Sources).</summary>
public static class SourceCost
{
    /// <summary>
    /// Today's cost of the source: <c>–</c> when nothing is priced, <c>≥</c> when part is; a subscription source shows
    /// <c>cost.subscription.tag</c> unless subscriptions are included.
    /// </summary>
    public static (string Text, string Spoken) Today(SourceStatus status, CostDisplay cost)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(cost);
        if (cost.Settings.IsSubscription(status.Kind) && !cost.Settings.IncludeSubscriptions)
        {
            var tag = cost.Localizer["cost.subscription.tag"];
            return (tag, tag);
        }

        return (cost.TallyText(status.TodayCost, status.Today.Requests), cost.SpokenTally(status.TodayCost, status.Today.Requests));
    }

    /// <summary>
    /// The row's live money value: its own source's API-equivalent rate (marks only change what totals count), null
    /// below the visible minimum or when nothing is priced (the row shows its dash).
    /// </summary>
    public static LiveMoney? Live(SourceStatus status, CostDisplay cost)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(cost);
        var only = SourceSet.Of(status.Kind);
        var snapshot = SpeedSnapshot.Empty with { Sources = [status] };
        var reading = OrbSubset.Cost(snapshot, only, only);
        var money = cost.Live(reading, 0);
        var idle = DisplayText.IsNegligibleSpeed(status.Live.Output) && reading.MicroUsdPerSecond <= 0;
        return idle || money.Unknown ? null : money;
    }
}
