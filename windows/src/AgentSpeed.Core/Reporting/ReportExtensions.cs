namespace AgentSpeed.Core.Reporting;

/// <param name="TaskScore">The retired task score extension, which now receives the thinking speed only (METRICS 11.10, Legacy).</param>
/// <param name="ExecutionModesVersion">The negotiated execution-modes version while <paramref name="ExecutionModes"/> is on: 1, or 2,
/// which adds the reasoning effort (EXECUTION-MODES, Report extension).</param>
public sealed record ReportExtensions(bool ModelHarnessRequests = false, bool ExecutionModes = false, bool ProfileAvatar = false, bool SharedIdentity = false,
    bool ReportWebSocket = false, bool TaskScore = false, bool PerfScore = false, int ExecutionModesVersion = 1)
{
    /// <summary>The execution-modes versions this client knows; any other number is not advertised.</summary>
    public const int NewestModesVersion = 2;

    /// <summary>The version a report's mode rows follow, 0 while execution modes are off.</summary>
    public int ModesVersion => ExecutionModes ? ExecutionModesVersion : 0;

    /// <summary>The name of the retired task score extension (METRICS 11.10, Legacy).</summary>
    public const string TaskScoreName = "taskScore";

    /// <summary>The name of the performance score extension (METRICS 11.10).</summary>
    public const string PerfScoreName = "perfScore";

    public bool HasProfile => ProfileAvatar || SharedIdentity;
    public ReportJson Json => ReportJson.Object(new[]
    {
        (Name: "modelHarnessRequests", On: ModelHarnessRequests, Version: 1), (Name: "executionModes", On: ExecutionModes, Version: ExecutionModesVersion),
        (Name: "profileAvatar", On: ProfileAvatar, Version: 1), (Name: "sharedIdentity", On: SharedIdentity, Version: 1),
        (Name: ReportWebSocketProtocol.Extension, On: ReportWebSocket, Version: 1), (Name: TaskScoreName, On: TaskScore, Version: 1),
        (Name: PerfScoreName, On: PerfScore, Version: 1),
    }.Where(e => e.On).Select(e => (e.Name, ReportJson.Of(e.Version))));
}

public readonly record struct ReportModeKey(string ModelId, SourceKind Source, ExecutionModeKind Kind, string Mode, string Evidence, string? Provider);
