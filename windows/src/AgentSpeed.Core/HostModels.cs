using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core;

/// <summary>
/// One host of the snapshot (DESIGN 13 <i>Dashboard → Hosts</i>, the settings status line): this computer or a remote host,
/// with its live value and today's totals over its cells.
/// </summary>
/// <param name="Id"><c>local</c> or the remote host's id.</param>
/// <param name="Name">The display name (REMOTE 2.1); <c>local</c> for this computer, which the UI names <c>remote.local</c>.</param>
/// <param name="Remote">The connection status; null for this computer and for a remote host before its first status.</param>
/// <param name="LiveEstimated">The estimated part of <paramref name="Live"/> (METRICS 6.3).</param>
/// <param name="Sessions">Today's main sessions (DESIGN 5): the relay's <c>status</c> for a remote host, as last reported.</param>
/// <param name="IsConnected">This computer, or a remote host whose relay is connected (after <c>ready</c>).</param>
public sealed record HostStatus(
    string Id,
    string Name,
    RemoteHostStatus? Remote,
    Rate Live,
    Rate LiveEstimated,
    LiveCost LiveCost,
    TokenTotals Today,
    CostTally TodayCost,
    int Sessions,
    bool IsConnected)
{
    public bool IsLocal => Id == RemoteHost.LocalId;

    /// <summary>The clock offset <c>δ = remote − local</c> in milliseconds (REMOTE 6); null for this computer and before <c>hello</c>.</summary>
    public long? ClockOffsetMs => Remote?.ClockOffsetMs;

    /// <summary><c>remote.clock.skewed</c>: <c>|δ| ≥ 5 s</c>.</summary>
    public bool IsClockSkewed => Remote?.IsClockSkewed ?? false;

    /// <summary>Listed on the dashboard (DESIGN 13): connected, or it produced output today.</summary>
    public bool IsListed => IsConnected || Today.Output > 0 || Live.Output > 0;
}

/// <summary>One (host, source) cell of the snapshot: the per-source values of <see cref="SourceStatus"/> restricted to one host.</summary>
/// <param name="Available">Detected on its host: this computer's root, or the relay's <c>status</c> while the host is connected.</param>
/// <param name="Unsupported">Its host reports the source's data in a format it cannot read.</param>
/// <param name="UnpricedModels">Models of today's responses without a known price, most unpriced requests first.</param>
/// <param name="ConfiguredModes">The configured values the host reports for the source.</param>
public sealed record HostSourceStatus(
    HostSource Cell,
    bool Available,
    bool Unsupported,
    int SessionCount,
    Rate Live,
    Rate LiveEstimated,
    LiveCost LiveCost,
    IntelligenceSums Intelligence,
    TokenTotals Today,
    CostTally TodayCost,
    IReadOnlyList<string> UnpricedModels,
    ExecutionModes ConfiguredModes)
{
    public string Host => Cell.Host;

    public SourceKind Source => Cell.Source;
}
