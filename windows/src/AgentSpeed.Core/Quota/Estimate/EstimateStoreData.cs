namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>A closed instance's interval as stored (QUOTAS 10.4); <paramref name="Hi"/> is null when unbounded.</summary>
public sealed record StoredInstance(CapClass Class, string Account, long R, double Lo, double? Hi, double PMax, long TLast);

/// <summary>A stored key change; a Claude CLI entry adds its organization's Desktop key.</summary>
public sealed record StoredKey(SourceKind Source, long At, string Key, string? Plan, string? Tier, string? OrgKey = null);

/// <summary>The content of <c>&lt;data&gt;/quota/estimate.json</c> (QUOTAS 10.4).</summary>
public sealed record EstimateStoreData(int Version, IReadOnlyList<QuotaObservation> Observations, IReadOnlyList<StoredInstance> Instances, IReadOnlyList<StoredKey> Keys)
{
    public static EstimateStoreData Empty { get; } = new(EstimateLimits.StoreVersion, [], [], []);
}
