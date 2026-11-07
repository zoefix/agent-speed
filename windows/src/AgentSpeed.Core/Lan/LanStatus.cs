namespace AgentSpeed.Core.Lan;

/// <summary>A target's status (LAN 5.5); <c>Waiting</c> until its first attempt.</summary>
public enum LanTargetStatusKind
{
    Off,
    NetworkOff,
    Waiting,
    Ok,
    NotFound,
    NotLocal,
    Unreachable,
    HttpStatus,
    Failed,
}

/// <param name="HttpCode">The status code of <see cref="LanTargetStatusKind.HttpStatus"/>.</param>
/// <param name="LastSuccess">The wall clock of the latest success (the status line's time); never persisted.</param>
/// <param name="RetryAtMs">The monotonic time of the next HTTP attempt while backing off.</param>
public sealed record LanTargetStatus(LanTargetStatusKind Kind, int HttpCode = 0, DateTimeOffset? LastSuccess = null, long? RetryAtMs = null)
{
    public static LanTargetStatus Off { get; } = new(LanTargetStatusKind.Off);

    public static LanTargetStatus NetworkOff { get; } = new(LanTargetStatusKind.NetworkOff);

    public static LanTargetStatus Waiting { get; } = new(LanTargetStatusKind.Waiting);
}

/// <summary>The server's state for the status line (DESIGN 14).</summary>
public enum LanServerStatusKind
{
    Off,
    NetworkOff,
    Listening,
    PortInUse,
    Failed,
}

/// <param name="Devices">Distinct peer addresses with an open stream or an authorized request in the last 10 s.</param>
public sealed record LanServerStatus(LanServerStatusKind Kind, int Port, int Devices = 0)
{
    public static LanServerStatus Off { get; } = new(LanServerStatusKind.Off, 0);
}
