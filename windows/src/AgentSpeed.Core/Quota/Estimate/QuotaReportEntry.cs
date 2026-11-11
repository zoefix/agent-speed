namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>The <c>estimate</c> object of a report entry (REPORTING-EXTENSIONS 11.2).</summary>
public sealed record QuotaEntryEstimate(
    long? CapMicroUsd, long? LowMicroUsd, long? HighMicroUsd, EstimateConfidence Confidence, double? Coverage,
    long? AccountUsedMicroUsd, int Windows);

/// <summary>
/// One account × window as reported (REPORTING-EXTENSIONS 11.2) and shown (QUOTAS 10.7, 10.8): the account's tag for the
/// service, the vendor's percent (null once its reset passed), the live percent, the measured spend and the estimate, at
/// <see cref="AtMs"/>.
/// </summary>
/// <param name="Account">The account's tag for the reporting service (<see cref="AccountKeys.ReportTag"/>, 16 hex digits),
/// never its id or key; empty on an entry that is only shown.</param>
public sealed record QuotaReportEntry(
    string Account, SourceKind Source, IReadOnlyList<SourceKind> Sources, string? Plan, string? Tier, QuotaWindowKind Kind, string? Name,
    int Minutes, double? UsedPercent, long? PercentAgeMs, long? ResetsInMs, double LivePercent, bool LiveEstimated,
    long UsedMicroUsd, long? ObservedUsedMicroUsd, long UnpricedRequests, QuotaEntryEstimate Estimate,
    IReadOnlyList<QuotaMixShare> Mix, int PriceVersion)
{
    /// <summary>When the entry was computed.</summary>
    public long AtMs { get; init; }

    /// <summary><see cref="Plan"/> and <see cref="Tier"/> were inferred from the quota (QUOTAS 10.2); reports send them alike.</summary>
    public bool PlanInferred { get; init; }

    /// <summary>The reset the live percent counts to: the vendor's, or the next instance's after it passed.</summary>
    public long? ResetsAtMs => ResetsInMs is { } inMs ? AtMs + inMs : null;

    public string WindowId => EstimateNames.WindowId(Kind, Name, Minutes);
}
