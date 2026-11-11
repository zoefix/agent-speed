namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// One counted response for the ledger (QUOTAS 10.3): <paramref name="Model"/> is the price-table id, a null cost is
/// unpriced, <paramref name="Tokens"/> the input + output tokens. <paramref name="Provider"/> is Codex's
/// <c>model_provider</c>; <paramref name="CloudRoute"/> marks a Claude model id in a Bedrock or Vertex form.
/// <paramref name="Host"/> is the remote host the response came from, null on this computer. <paramref name="Requests"/>
/// counts the responses a row of the history's per-minute sums stands for.
/// </summary>
public sealed record EstimateResponse(
    SourceKind Source, string? File, long? StartMs, long EndMs, string Model, long? CostMicroUsd, long Tokens,
    string? Provider = null, bool CloudRoute = false, string? Host = null, long Requests = 1);

/// <summary>A Codex token count line with rate limits (QUOTAS 3): its file, time, windows and raw <c>plan_type</c>.</summary>
public sealed record CodexTokenLine(string File, long Ts, IReadOnlyList<QuotaWindow> Windows, string? PlanType);

/// <summary>An entry of a source's key timeline (QUOTAS 10.1).</summary>
public readonly record struct KeyEntry(long At, string Key, string? Plan, string? Tier);

/// <summary>A Claude CLI profile seen at <paramref name="At"/>: its account key, its organization's Desktop key, plan and tier (cleaned).</summary>
public sealed record ClaudeProfile(long At, string Key, string OrgKey, string? Plan, string? Tier);

/// <summary>A quota snapshot of this computer; a Claude Desktop one carries its organization's Desktop key.</summary>
public sealed record EstimateSnapshot(SourceKind Source, long ObservedMs, IReadOnlyList<QuotaWindow> Windows, string? DesktopOrgKey = null);

/// <summary>A host's quota line (REMOTE 11.1) with the account key, plan and tier it sent (null from older relays).</summary>
public sealed record HostSnapshot(string Host, SourceKind Source, long ObservedMs, IReadOnlyList<QuotaWindow> Windows, string? Account, string? Plan, string? Tier);

/// <summary>Everything the estimate is built from (QUOTAS 10.1–10.4).</summary>
public sealed class EstimateInput
{
    public List<EstimateResponse> Responses { get; } = [];

    public List<CodexTokenLine> CodexLines { get; } = [];

    /// <summary>Codex sign-in keys as they changed (<c>auth.json</c> <c>tokens.account_id</c>).</summary>
    public List<(long At, string Key)> CodexKeys { get; } = [];

    public List<ClaudeProfile> ClaudeProfiles { get; } = [];

    public List<EstimateSnapshot> Snapshots { get; } = [];

    public List<HostSnapshot> HostSnapshots { get; } = [];

    public EstimateStoreData? Stored { get; set; }
}
