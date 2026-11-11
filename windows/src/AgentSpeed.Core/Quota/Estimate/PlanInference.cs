namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>How a plan was inferred (QUOTAS 10.2 <i>Inferred plan</i>).</summary>
public enum PlanInferenceRule
{
    /// <summary>A weekly window and no 5-hour window.</summary>
    Shape,

    /// <summary>Its 5-hour cap against a Claude plan's on this computer.</summary>
    Ratio,
}

/// <summary>
/// The plan of an account Claude Desktop spends on as its own, inferred from its windows and caps (QUOTAS 10.2): Desktop
/// names no plan, and its credentials stay unread.
/// </summary>
public sealed record InferredPlan(string Plan, string? Tier, PlanInferenceRule Rule);

/// <summary>A 5-hour cap class of a Claude plan: what the ratio rule compares an account's 5-hour cap with.</summary>
public sealed record PlanAnchor(string Plan, string? Tier, CapEstimate Estimate);

/// <summary>The rules of QUOTAS 10.2 <i>Inferred plan</i>.</summary>
public static class PlanInference
{
    public const string Ultra = "claude_ultra";
    public const double Tolerance = 1.5;

    private const string Pro = "claude_pro";
    private const string VendorPrefix = "anthropic/";

    /// <summary>Claude's plans and their 5-hour usage relative to Pro.</summary>
    private static readonly (string Plan, string? Tier, double Multiplier)[] Plans =
        [(Pro, null, 1), ("claude_max", "default_claude_max_5x", 5), ("claude_max", "default_claude_max_20x", 20)];

    public static string FiveHour { get; } = EstimateNames.WindowId(QuotaWindowKind.FiveHour, null, 300);

    public static string Name(PlanInferenceRule rule) => rule == PlanInferenceRule.Shape ? "shape" : "ratio";

    /// <summary>A plan's 5-hour usage relative to Pro: Pro of any tier is 1, Max only with a known tier; else null.</summary>
    public static double? Multiplier(string plan, string? tier)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan == Pro)
        {
            return 1;
        }

        foreach (var p in Plans)
        {
            if (p.Plan == plan && p.Tier == tier)
            {
                return p.Multiplier;
            }
        }

        return null;
    }

    /// <summary>An estimate the ratio rule rests on: a cap, at medium or high confidence.</summary>
    public static bool Usable(CapEstimate? estimate) =>
        estimate is { Confidence: EstimateConfidence.High or EstimateConfidence.Medium, Cap: > 0 };

    /// <summary>
    /// The shape rule, then the ratio rule: <paramref name="kinds"/> are the windows of the account's newest Desktop snapshot
    /// (with or without a reset), <paramref name="own"/> its 5-hour estimate, <paramref name="anchors"/> sorted by plan, then
    /// tier with null first. Null when neither decides.
    /// </summary>
    public static InferredPlan? Infer(IReadOnlyCollection<QuotaWindowKind> kinds, CapEstimate? own, IReadOnlyList<PlanAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(anchors);
        if (kinds.Contains(QuotaWindowKind.SevenDay) && !kinds.Contains(QuotaWindowKind.FiveHour))
        {
            return new InferredPlan(Ultra, null, PlanInferenceRule.Shape);
        }

        if (!Usable(own))
        {
            return null;
        }

        var limit = Math.Log(Tolerance);
        (double Distance, string Plan, string? Tier)? best = null;
        foreach (var anchor in anchors)
        {
            if (Multiplier(anchor.Plan, anchor.Tier) is not { } m || !Usable(anchor.Estimate))
            {
                continue;
            }

            var scaled = m * own!.Cap!.Value / anchor.Estimate.Cap!.Value;
            foreach (var candidate in Plans)
            {
                var distance = Math.Abs(Math.Log(scaled / candidate.Multiplier));
                if (distance <= limit && (best is null || distance < best.Value.Distance))
                {
                    best = (distance, candidate.Plan, candidate.Tier);
                }
            }
        }

        return best is { } b ? new InferredPlan(b.Plan, b.Tier, PlanInferenceRule.Ratio) : null;
    }

    /// <summary>Every 5-hour cap class of a Claude plan, sorted by plan, then tier with null first (the reference's order).</summary>
    public static IReadOnlyList<PlanAnchor> Anchors(IReadOnlyDictionary<CapClass, CapEstimate> estimates)
    {
        ArgumentNullException.ThrowIfNull(estimates);
        var anchors = new List<PlanAnchor>();
        foreach (var (cls, estimate) in estimates)
        {
            if (cls.Plan is { } vendorPlan && cls.Window == FiveHour && vendorPlan.StartsWith(VendorPrefix, StringComparison.Ordinal))
            {
                anchors.Add(new PlanAnchor(vendorPlan[VendorPrefix.Length..], cls.Tier, estimate));
            }
        }

        anchors.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Plan, b.Plan);
            return c != 0 ? c : string.CompareOrdinal(a.Tier ?? string.Empty, b.Tier ?? string.Empty);
        });
        return anchors;
    }
}
