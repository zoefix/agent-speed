using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Quota;

/// <summary>
/// Where a section's quota lines find their windows' estimates (QUOTAS 10.7, 10.8): a state's windows on this computer
/// (<see cref="HostId"/> null) or on a remote host, with the money formatter of their amounts (none: no amounts).
/// </summary>
public sealed record QuotaEstimateSource(QuotaState State, CostDisplay? Cost, string? HostId = null)
{
    /// <summary>The window's estimate as it holds at <paramref name="nowMs"/> (<see cref="QuotaEstimateText.Current"/>).</summary>
    public QuotaWindowEstimate? Of(SourceKind source, QuotaShownWindow window, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(window);
        return QuotaEstimateText.Current(State.EstimateOf(source, window.Window, HostId), window, nowMs);
    }
}

/// <summary>The texts of a window's estimate (DESIGN 4 <i>Percent</i>, <i>Amount</i>, <i>Tooltip</i>; 2a.6 額度, its <c>≈</c> only).</summary>
public static class QuotaEstimateText
{
    /// <summary>The leading mark of an extrapolated percent and of an estimated cap.</summary>
    public const string Approx = "≈";

    /// <summary>
    /// The estimate while it still describes the window: none once the reset it counts to has passed, or while it and the
    /// vendor disagree on whether the vendor's reset has passed (both stale until the next estimate). A vendor value newer
    /// than the estimate's build raises its live percent to at least that value, and it is extrapolated only 0.5 points or
    /// more above it (QUOTAS 10.7).
    /// </summary>
    public static QuotaWindowEstimate? Current(QuotaWindowEstimate? estimate, QuotaShownWindow window, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (estimate is null || estimate.ResetsAtMs is { } reset && reset <= nowMs || estimate.VendorPassed != window.Passed)
        {
            return null;
        }

        if (window.Passed)
        {
            return estimate;
        }

        var vendor = window.Window.UsedPercent;
        var live = double.IsFinite(estimate.LivePercent) ? Math.Max(estimate.LivePercent, vendor) : vendor;
        var estimated = estimate.Estimated && live - vendor >= 0.5;
        return live == estimate.LivePercent && estimated == estimate.Estimated ? estimate : estimate with { LivePercent = live, Estimated = estimated };
    }

    /// <summary>The window as the live percent draws it: its rounded percent, bar and the reset it counts to.</summary>
    public static QuotaShownWindow Live(QuotaShownWindow window, QuotaWindowEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(estimate);
        var live = double.IsFinite(estimate.LivePercent) ? estimate.LivePercent : 0;
        return window with { Percent = QuotaWindows.Percent(live), Bar = Math.Clamp(live, 0, 100), ResetsAtMs = estimate.ResetsAtMs };
    }

    /// <summary>
    /// The dashboard's amount cell in the rate's digits: the used amount with its symbol (<c>US$4,938</c>), then
    /// <c>quota.amount.cap</c> (<c> / ≈5,742</c>) or <c>quota.amount.atLeast</c> (<c> / ≥598</c>), the cap in the same digits
    /// without the symbol (owner's choice 2026-11-11: the row then holds the short reset), split off as the cap part; only
    /// the used amount at confidence <c>none</c>; nothing without spend and cap.
    /// </summary>
    public static (string Used, string Cap) Amount(QuotaWindowEstimate? estimate, CostDisplay? cost, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (estimate is null || cost is null)
        {
            return (string.Empty, string.Empty);
        }

        var used = cost.CompactAmountText(estimate.UsedMicroUsd);
        var full = CapOf(estimate) switch
        {
            (CapKind.About, var cap) => loc.Format("quota.amount.cap", used, cost.CompactAmountNumber(cap)),
            (CapKind.AtLeast, var low) => loc.Format("quota.amount.atLeast", used, cost.CompactAmountNumber(low)),
            _ => null,
        };
        if (full is null)
        {
            return estimate.UsedMicroUsd > 0 ? (used, string.Empty) : (string.Empty, string.Empty);
        }

        return full.StartsWith(used, StringComparison.Ordinal) ? (used, full[used.Length..]) : (full, string.Empty);
    }

    /// <summary>
    /// What follows a window when it is spoken: on the dashboard (<paramref name="withAmount"/>) <c>a11y.quota.amount</c> (used,
    /// cap) while a cap shows, else <c>a11y.quota.amountNoCap</c> while an amount shows; then, on both, <c>a11y.quota.estimated</c>
    /// while the percent is extrapolated (the 額度 card shows no amount, owner's choice 2026-11-11). Each part leads with
    /// <c>list.separator</c>; empty without any.
    /// </summary>
    public static string SpokenTail(QuotaWindowEstimate? estimate, CostDisplay? cost, Localizer loc, bool withAmount)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (estimate is null)
        {
            return string.Empty;
        }

        var separator = loc["list.separator"];
        var tail = string.Empty;
        if (withAmount && cost is not null)
        {
            var used = Spoken(cost, estimate.UsedMicroUsd);
            if (CapOf(estimate) is (CapKind.About, var cap))
            {
                tail += separator + loc.Format("a11y.quota.amount", used, Spoken(cost, cap));
            }
            else if (Amount(estimate, cost, loc).Used.Length > 0)
            {
                tail += separator + loc.Format("a11y.quota.amountNoCap", used);
            }
        }

        return estimate.Estimated ? tail + separator + loc["a11y.quota.estimated"] : tail;
    }

    /// <summary>
    /// The tooltip's estimate lines in DESIGN 4's order: the used amount, the cap with its published range (or at least),
    /// the whole account's use while a cap exists, the coverage while known, the confidence with the windows it rests on, the
    /// plan (and tier) when known, marked when inferred (QUOTAS 10.2), and the live note while extrapolated. A non-USD currency
    /// adds the USD amount (DESIGN 9).
    /// </summary>
    public static IReadOnlyList<string> Details(QuotaWindowEstimate estimate, CostDisplay? cost, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new List<string>(7);
        var (kind, bound) = CapOf(estimate);
        if (cost is not null)
        {
            lines.Add(loc.Format("quota.estimate.used", cost.AmountWithUsd(estimate.UsedMicroUsd)));
            if (kind == CapKind.About)
            {
                var low = Math.Min(estimate.LowMicroUsd ?? bound, bound);
                var high = Math.Max(estimate.HighMicroUsd ?? bound, bound);
                lines.Add(loc.Format("quota.estimate.cap", cost.AmountWithUsd(bound), cost.AmountText(low), cost.AmountText(high)));
                if (estimate.AccountUsedMicroUsd is { } account)
                {
                    lines.Add(loc.Format("quota.estimate.account", cost.AmountWithUsd(account)));
                }
            }
            else if (kind == CapKind.AtLeast)
            {
                lines.Add(loc.Format("quota.estimate.atLeast", cost.AmountWithUsd(bound)));
            }
        }

        if (estimate.Coverage is { } coverage && double.IsFinite(coverage))
        {
            lines.Add(loc.Format("quota.estimate.coverage", (int)Math.Round(Math.Clamp(coverage, 0, 1) * 100, MidpointRounding.AwayFromZero)));
        }

        if (ConfidenceKey(estimate.Confidence) is { } confidence)
        {
            lines.Add(estimate.Windows > 0
                ? loc[confidence] + Dot + loc.Format("quota.estimate.windows", estimate.Windows)
                : loc[confidence]);
        }

        if (estimate.Plan is { Length: > 0 } plan)
        {
            lines.Add(loc.Format(estimate.PlanInferred ? "quota.estimate.planInferred" : "quota.estimate.plan",
                estimate.Tier is { Length: > 0 } tier ? plan + Dot + tier : plan));
        }

        if (estimate.Estimated)
        {
            lines.Add(loc["quota.estimate.live"]);
        }

        return lines;
    }

    private const string Dot = " · ";

    private enum CapKind
    {
        None,
        About,
        AtLeast,
    }

    /// <summary>The cap a window shows (QUOTAS 10.8): about the estimate, at least the lower bound, or none at confidence <c>none</c>.</summary>
    private static (CapKind Kind, long Value) CapOf(QuotaWindowEstimate estimate)
    {
        if (estimate.Confidence == EstimateConfidence.None)
        {
            return (CapKind.None, 0);
        }

        if (estimate.CapMicroUsd is { } cap)
        {
            return (CapKind.About, cap);
        }

        return estimate.HasAtLeast ? (CapKind.AtLeast, estimate.LowMicroUsd!.Value) : (CapKind.None, 0);
    }

    private static string? ConfidenceKey(EstimateConfidence confidence) => confidence switch
    {
        EstimateConfidence.High => "quota.estimate.confidence.high",
        EstimateConfidence.Medium => "quota.estimate.confidence.medium",
        EstimateConfidence.Low => "quota.estimate.confidence.low",
        _ => null,
    };

    /// <summary><c>161.00 美元</c>: how accessibility reads an amount (DESIGN 9).</summary>
    private static string Spoken(CostDisplay cost, long microUsd) => cost.Spoken(cost.Amount(microUsd).Number);
}
