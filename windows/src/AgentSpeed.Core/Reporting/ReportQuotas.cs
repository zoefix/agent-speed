using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Core.Reporting;

/// <summary>The <c>quotas</c> member of a report (REPORTING-EXTENSIONS 11.2), members in the schema's order.</summary>
public static class ReportQuotas
{
    public static ReportJson Json(IReadOnlyList<QuotaReportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return ReportJson.Array(entries.Select(Entry));
    }

    public static ReportJson Entry(QuotaReportEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var estimate = e.Estimate;
        return ReportJson.Object(
            ("source", ReportJson.Of(ReportBody.SourceId(e.Source))),
            ("sources", ReportJson.Array(e.Sources.Select(s => ReportJson.Of(ReportBody.SourceId(s))))),
            ("plan", Text(e.Plan)),
            ("tier", Text(e.Tier)),
            ("window", ReportJson.Object(
                ("kind", ReportJson.Of(EstimateNames.Kind(e.Kind))),
                ("name", Text(e.Name)),
                ("minutes", ReportJson.Of(e.Minutes)))),
            ("usedPercent", e.UsedPercent is { } used ? ReportJson.Decimal(used, 2) : ReportJson.Null),
            ("percentAgeMs", ReportJson.OfOptional(e.PercentAgeMs)),
            ("resetsInMs", ReportJson.OfOptional(e.ResetsInMs)),
            ("livePercent", ReportJson.Decimal(e.LivePercent, 1)),
            ("liveEstimated", ReportJson.Of(e.LiveEstimated)),
            ("usedMicroUsd", ReportJson.Of(e.UsedMicroUsd)),
            ("observedUsedMicroUsd", ReportJson.OfOptional(e.ObservedUsedMicroUsd)),
            ("unpricedRequests", ReportJson.Of(e.UnpricedRequests)),
            ("estimate", ReportJson.Object(
                ("capMicroUsd", ReportJson.OfOptional(estimate.CapMicroUsd)),
                ("lowMicroUsd", ReportJson.OfOptional(estimate.LowMicroUsd)),
                ("highMicroUsd", ReportJson.OfOptional(estimate.HighMicroUsd)),
                ("confidence", ReportJson.Of(CapEstimate.Name(estimate.Confidence))),
                ("coverage", estimate.Coverage is { } coverage ? ReportJson.Decimal(coverage, 3) : ReportJson.Null),
                ("accountUsedMicroUsd", ReportJson.OfOptional(estimate.AccountUsedMicroUsd)),
                ("windows", ReportJson.Of(estimate.Windows)))),
            ("mix", ReportJson.Array(e.Mix.Select(m => ReportJson.Object(("model", ReportJson.Of(m.Model)), ("share", ReportJson.Decimal(m.Share, 3)))))),
            ("priceVersion", ReportJson.Of(e.PriceVersion)));
    }

    private static ReportJson Text(string? value) => value is null ? ReportJson.Null : ReportJson.Of(value);
}
