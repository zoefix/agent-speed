namespace AgentSpeed.Core.Quota.Estimate;

public sealed partial class EstimateEvaluation
{
    private static readonly IReadOnlyDictionary<string, InferredPlan> NoPlans = new Dictionary<string, InferredPlan>();

    /// <summary>
    /// QUOTAS 10.2 <i>Inferred plan</i>: the plan of each account Claude Desktop spends on as its own, unless the CLI names
    /// that organization a non-personal one; by account. It fills only a missing plan of the entries and never forms a cap
    /// class.
    /// </summary>
    public IReadOnlyDictionary<string, InferredPlan> InferredPlans { get; private set; } = NoPlans;

    private void InferPlans()
    {
        var own = Model.DesktopOrganizations.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (entry, org) in Model.OrgKeys)
        {
            if (entry.Plan is not null && !AccountKeys.IsPersonalClaudePlan(entry.Plan))
            {
                own.Remove(org);
            }
        }

        if (own.Count == 0)
        {
            return;
        }

        var anchors = PlanInference.Anchors(Estimates);
        var newest = Model.NewestDesktopSnapshots();
        var plans = new Dictionary<string, InferredPlan>(StringComparer.Ordinal);
        foreach (var key in own)
        {
            IReadOnlyCollection<QuotaWindowKind> kinds = newest.TryGetValue(key, out var s) ? [.. s.Windows.Select(w => w.Kind)] : [];
            var estimate = Estimates.GetValueOrDefault(CapClass.Of(key, null, null, PlanInference.FiveHour));
            if (PlanInference.Infer(kinds, estimate, anchors) is { } plan)
            {
                plans[key] = plan;
            }
        }

        InferredPlans = plans;
    }
}
