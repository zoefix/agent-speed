namespace AgentSpeed.Core.Reporting;

/// <param name="TaskScore">The retired task score extension, which now receives the thinking speed only (METRICS 11.10, Legacy).</param>
public sealed record ReportExtensions(bool ModelHarnessRequests = false, bool ExecutionModes = false, bool ProfileAvatar = false, bool SharedIdentity = false,
    bool ReportWebSocket = false, bool TaskScore = false, bool PerfScore = false)
{
    /// <summary>The name of the retired task score extension (METRICS 11.10, Legacy).</summary>
    public const string TaskScoreName = "taskScore";

    /// <summary>The name of the performance score extension (METRICS 11.10).</summary>
    public const string PerfScoreName = "perfScore";

    public bool HasProfile => ProfileAvatar || SharedIdentity;
    public ReportJson Json => ReportJson.Object(new[]
    {
        (Name: "modelHarnessRequests", On: ModelHarnessRequests), (Name: "executionModes", On: ExecutionModes),
        (Name: "profileAvatar", On: ProfileAvatar), (Name: "sharedIdentity", On: SharedIdentity),
        (Name: ReportWebSocketProtocol.Extension, On: ReportWebSocket), (Name: TaskScoreName, On: TaskScore), (Name: PerfScoreName, On: PerfScore),
    }.Where(e => e.On).Select(e => (e.Name, ReportJson.Of(1))));
}

public readonly record struct ReportModeKey(string ModelId, SourceKind Source, ExecutionModeKind Kind, string Mode, string Evidence, string? Provider);
