namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// What the surfaces show of one window's estimate (QUOTAS 10.7, 10.8; DESIGN 4, 2a.6), computed at <see cref="AtMs"/>.
/// <see cref="LivePercent"/> replaces the vendor's percent (and its bar); <see cref="Estimated"/> marks it with <c>≈</c>.
/// <see cref="ResetsAtMs"/> is the reset it counts to (the next instance's after the vendor's passed; null while unknown).
/// The cap is <see cref="CapMicroUsd"/>, or only <see cref="LowMicroUsd"/> as "at least" when it is null; nothing at
/// <see cref="EstimateConfidence.None"/>.
/// </summary>
public sealed record QuotaWindowEstimate(
    long AtMs, double LivePercent, bool Estimated, long? ResetsAtMs, bool VendorPassed, long UsedMicroUsd, long UnpricedRequests,
    long? CapMicroUsd, long? LowMicroUsd, long? HighMicroUsd, EstimateConfidence Confidence, double? Coverage, long? AccountUsedMicroUsd,
    int Windows, string? Plan, string? Tier)
{
    public bool HasCap => CapMicroUsd is not null;

    /// <summary>Only a lower bound: shown as <c>≥</c>.</summary>
    public bool HasAtLeast => CapMicroUsd is null && LowMicroUsd is not null && Confidence != EstimateConfidence.None;

    public static QuotaWindowEstimate From(QuotaReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var e = entry.Estimate;
        return new QuotaWindowEstimate(entry.AtMs, entry.LivePercent, entry.LiveEstimated, entry.ResetsAtMs, entry.UsedPercent is null,
            entry.UsedMicroUsd, entry.UnpricedRequests, e.CapMicroUsd, e.LowMicroUsd, e.HighMicroUsd, e.Confidence, e.Coverage,
            e.AccountUsedMicroUsd, e.Windows, entry.Plan, entry.Tier);
    }
}
