namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// What a report entry is computed from: the newest vendor value of an account's window instance and its class estimate;
/// <paramref name="PlanInferred"/> when <paramref name="Plan"/> and <paramref name="Tier"/> were inferred (QUOTAS 10.2).
/// </summary>
public sealed record EntryBasis(
    SourceKind Source, IReadOnlyList<SourceKind> Sources, string? Plan, string? Tier, QuotaWindowKind Kind, string? Name, int Minutes,
    double P, long T, long R, long S, string Account, CapEstimate Estimate, long? FirstAfterReset, bool PlanInferred = false);

/// <summary>A report entry at a moment (REPORTING-EXTENSIONS 11.2, QUOTAS 10.7, 10.8).</summary>
public static class QuotaEntryBuilder
{
    /// <param name="account">The tag the entry carries for the account (<see cref="AccountKeys.ReportTag"/>); empty where nothing is sent.</param>
    public static QuotaReportEntry Build(EntryBasis b, string account, long nowMs, SpendLedger ledger, int priceVersion)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(ledger);
        var estimate = b.Estimate;
        var name = b.Kind == QuotaWindowKind.ModelWeek ? b.Name : null;
        var cap = estimate.Cap;
        var live = LivePercent.Of(b.P, b.T, b.R, b.S, b.Minutes, b.Kind, cap, ledger, b.Account, name, nowMs, b.FirstAfterReset);
        var passed = nowMs >= b.R;
        var start = !passed ? b.S : live.ResetsAtMs is { } resets and not 0 ? resets - b.Minutes * EstimateLimits.Minute : nowMs;
        var (used, unpriced) = ledger.Outer(b.Account, name, start, nowMs);
        var observed = ledger.Outer(b.Account, name, b.S, b.T).Cost;
        double? coverage = null;
        if (cap is { } k && k != 0 && b.P >= 5)
        {
            coverage = EstimateMath.RoundHalfAway(Math.Min(1.0, Math.Max(0.0, observed / (k * b.P / 100))), 3);
        }

        var entryEstimate = new QuotaEntryEstimate(
            EstimateMath.Micro(cap), EstimateMath.Micro(estimate.Low), EstimateMath.Micro(estimate.High), estimate.Confidence, coverage,
            cap is { } c && c != 0 ? EstimateMath.Micro(c * live.Percent / 100) : null, estimate.Windows);
        return new QuotaReportEntry(
            account, b.Source, b.Sources, b.Plan, b.Tier, b.Kind, b.Name, b.Minutes,
            passed ? null : EstimateMath.RoundHalfAway(b.P, 2),
            passed ? null : Math.Max(0, nowMs - b.T),
            live.ResetsAtMs is { } r ? Math.Max(0, r - nowMs) : null,
            EstimateMath.RoundHalfAway(live.Percent, 1), live.Estimated, used, passed ? null : observed, unpriced, entryEstimate,
            ledger.Mix(b.Account, name, start, nowMs), priceVersion)
        {
            AtMs = nowMs,
            PlanInferred = b.PlanInferred,
        };
    }
}
