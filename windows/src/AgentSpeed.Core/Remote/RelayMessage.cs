using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Remote;

/// <summary>The protocol version this app speaks (REMOTE 5.1).</summary>
public static class RemoteProtocol
{
    public const long Version = 1;
}

/// <summary>The per-host limits of REMOTE 5.4 and 5.6.</summary>
public static class RemoteLimits
{
    public const int Agents = 4096;

    /// <summary>Responses the app holds for today and the retention window; the ingestion prunes a host's oldest first.</summary>
    public const int Responses = 200_000;
    public const int ActionsPerAgent = 64;
    public const int FactsPerModeList = 20;
    public const int InvalidLines = 100;
    public const int AgentIdBytes = 4096;
    public const int KeyBytes = 512;
    public const int ProjectBytes = 256;
    public const int CwdBytes = 4096;
    public const int ModelBytes = 256;
    public const int ProviderBytes = 64;
    public const int ToolBytes = 128;
    public const int ErrorDetailCharacters = 200;

    /// <summary>Entries of an aggregate list, of <c>history.digests</c> and of <c>parents.links</c>.</summary>
    public const int HistoryList = 4096;
}

/// <summary>
/// A decoded relay line (REMOTE 5.2). Agent ids are already prefixed with <c>ssh://&lt;host id&gt;/</c> (REMOTE 2.2); times
/// are the host's until the connection converts them (REMOTE 6).
/// </summary>
internal abstract record RelayMessage
{
    private RelayMessage()
    {
    }

    public sealed record Hello(RelayHello Value) : RelayMessage;

    /// <summary>Seeding progress, 0–1.</summary>
    public sealed record Progress(double Value) : RelayMessage;

    public sealed record Agent(RemoteAgent Value) : RelayMessage;

    /// <summary>The prefixed id of an agent that left.</summary>
    public sealed record AgentRemoved(string Id) : RelayMessage;

    /// <summary>The complete parent map, child → parent, both prefixed.</summary>
    public sealed record Parents(IReadOnlyDictionary<string, string> Links) : RelayMessage
    {
        public bool Equals(Parents? other) => other is not null && Links.Count == other.Links.Count
            && Links.All(l => other.Links.TryGetValue(l.Key, out var parent) && parent == l.Value);

        public override int GetHashCode() => Links.Count;
    }

    public sealed record Response(RemoteResponse Value) : RelayMessage;

    public sealed record Tool(RemoteToolCompletion Value) : RelayMessage;

    public sealed record Ready : RelayMessage;

    public sealed record Status(RemoteSourceReport Value) : RelayMessage;

    public sealed record Clock(long Id, long NowMs) : RelayMessage;

    public sealed record HistoryDay(RemoteHistoryDay Value) : RelayMessage;

    public sealed record HistoryPerf(RemoteHistoryPerf Value) : RelayMessage;

    public sealed record HistoryDone(int Days, int Analyzed) : RelayMessage;

    /// <summary>History analysis progress, 0–1.</summary>
    public sealed record HistoryProgress(double Value) : RelayMessage;

    public sealed record Error(RelayError Value) : RelayMessage;

    /// <summary>A source's quota on the host (REMOTE 11.1); times are the host's until converted.</summary>
    public sealed record Quota(RemoteQuotaReport Value) : RelayMessage;

    /// <summary>A source's quota snapshot on the host went.</summary>
    public sealed record QuotaRemoved(string HostId, SourceKind Source) : RelayMessage;

    /// <summary>The state of the host's Claude Code status line bridge (REMOTE 11.3).</summary>
    public sealed record QuotaBridge(RemoteQuotaBridge Value) : RelayMessage;

    /// <summary>A <c>quota</c>, <c>quotaRemoved</c> or <c>quotaBridge</c> line: display only, never live data or history.</summary>
    public bool IsQuota => this is Quota or QuotaRemoved or QuotaBridge;

    /// <summary>A <c>historyFailed</c> error: it ends a history run, not the relay.</summary>
    public bool IsHistoryFailure => this is Error { Value.Code: RelayErrorCode.HistoryFailed };
}

/// <param name="Version">The relay's app version string; empty when a relay of another protocol sent none.</param>
/// <param name="NowMs">The host's clock at the time of writing.</param>
public sealed record RelayHello(long ProtocolVersion, string Version, string Arch, long NowMs);

/// <summary>An agent's complete state (REMOTE 5.4): everything an engine agent needs, with the host alongside.</summary>
/// <param name="Id"><c>ssh://&lt;host id&gt;/&lt;relay id&gt;</c>.</param>
/// <param name="RelayId">The id as the relay sent it (a path on the host); only used for display fallbacks.</param>
/// <param name="Actions">At most 64, oldest first.</param>
internal sealed record RemoteAgent(
    string HostId,
    string Id,
    string RelayId,
    SourceKind Source,
    string? Project,
    string? CwdPath,
    string? Model,
    bool IsSubagent,
    AgentPhase Phase,
    long? InFlightStartMs,
    long? LastLineMs,
    ExecutionModes Modes,
    ExecutionModes SessionModes,
    ExecutionModes ConfiguredModes,
    IReadOnlyList<WorkAction> Actions)
{
    public bool Equals(RemoteAgent? other) => other is not null && HostId == other.HostId && Id == other.Id && RelayId == other.RelayId
        && Source == other.Source && Project == other.Project && CwdPath == other.CwdPath && Model == other.Model
        && IsSubagent == other.IsSubagent && Phase == other.Phase && InFlightStartMs == other.InFlightStartMs
        && LastLineMs == other.LastLineMs && Modes.Equals(other.Modes) && SessionModes.Equals(other.SessionModes)
        && ConfiguredModes.Equals(other.ConfiguredModes) && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode() => HashCode.Combine(Id, Phase, LastLineMs, Actions.Count);
}

/// <summary>A <c>response</c> line: the record with the prefixed agent id, its host and the relay's project name.</summary>
/// <param name="IsCompletion">True for the first emission of the key (METRICS 4.4).</param>
internal sealed record RemoteResponse(string HostId, ParsedResponse Record, bool IsCompletion, string Project);

/// <summary>A <c>tool</c> line (METRICS 11.10 <i>Counting</i>).</summary>
/// <param name="AgentId">Prefixed.</param>
/// <param name="Call">The call with its first result (<see cref="WorkAction.ResultMs"/> set).</param>
internal sealed record RemoteToolCompletion(string HostId, string AgentId, SourceKind Source, WorkAction Call);

/// <param name="Sessions"><c>sessionCounts</c> for the app's <c>todayStartMs</c>.</param>
public sealed record RemoteSourceState(SourceKind Source, bool Available, int Sessions, bool Unsupported, ExecutionModes ConfiguredModes);

/// <summary>A <c>status</c> line: the host's source availability and warnings.</summary>
/// <param name="Sources">Sources this app knows, in the relay's order; entries for unknown sources are skipped.</param>
public sealed record RemoteSourceReport(IReadOnlyList<RemoteSourceState> Sources, int Malformed, int Unreadable, bool PartialHistory)
{
    public bool Equals(RemoteSourceReport? other) => other is not null && Sources.SequenceEqual(other.Sources)
        && Malformed == other.Malformed && Unreadable == other.Unreadable && PartialHistory == other.PartialHistory;

    public override int GetHashCode() => HashCode.Combine(Sources.Count, Malformed, Unreadable, PartialHistory);
}

public enum RelayErrorCode
{
    BadStart,
    HistoryFailed,
    Internal,
    Other,
}

/// <param name="RawCode">The code as sent (displayed only).</param>
/// <param name="Detail">At most 200 characters of plain text; displayed only, never logged.</param>
public sealed record RelayError(RelayErrorCode Code, string RawCode, string Detail)
{
    /// <summary>Every code but <c>historyFailed</c> ends the relay.</summary>
    public bool IsFatal => Code != RelayErrorCode.HistoryFailed;
}
