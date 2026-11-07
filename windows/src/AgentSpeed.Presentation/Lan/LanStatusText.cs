using AgentSpeed.Core.Lan;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.Presentation.Lan;

/// <summary>
/// The status lines and dots of Settings → API → 區域網路 (DESIGN 14): the server's line, and each target's line with the
/// time of its last send or the countdown to its next attempt. The dots reuse the host dot's five looks (DESIGN 13).
/// </summary>
public static class LanStatusText
{
    public static RemoteDotKind ServerDot(LanServerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Kind switch
        {
            LanServerStatusKind.Listening => status.Devices > 0 ? RemoteDotKind.Connected : RemoteDotKind.Progress,
            LanServerStatusKind.PortInUse => RemoteDotKind.Retrying,
            LanServerStatusKind.Failed => RemoteDotKind.Attention,
            _ => RemoteDotKind.Idle,
        };
    }

    public static string Server(LanServerStatus status, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            LanServerStatusKind.Listening when status.Devices == 1 => loc.Format("lan.server.status.connected.one", 1),
            LanServerStatusKind.Listening when status.Devices > 1 => loc.Format("lan.server.status.connected", status.Devices),
            LanServerStatusKind.Listening => loc["lan.server.status.listening"],
            LanServerStatusKind.PortInUse => loc.Format("lan.server.status.portInUse", status.Port),
            LanServerStatusKind.Failed => loc["lan.server.status.failed"],
            LanServerStatusKind.NetworkOff => loc["lan.server.status.networkOff"],
            _ => string.Empty,
        };
    }

    public static RemoteDotKind TargetDot(LanTargetStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Kind switch
        {
            LanTargetStatusKind.Ok => RemoteDotKind.Connected,
            LanTargetStatusKind.Waiting => RemoteDotKind.Progress,
            LanTargetStatusKind.Unreachable or LanTargetStatusKind.HttpStatus or LanTargetStatusKind.Failed => RemoteDotKind.Retrying,
            LanTargetStatusKind.NotLocal or LanTargetStatusKind.NotFound => RemoteDotKind.Attention,
            _ => RemoteDotKind.Idle,
        };
    }

    /// <param name="nowMs">The monotonic clock of <see cref="LanTargetStatus.RetryAtMs"/>.</param>
    /// <param name="now">The wall clock of <see cref="LanTargetStatus.LastSuccess"/>.</param>
    public static string Target(LanTargetStatus status, long nowMs, DateTimeOffset now, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            LanTargetStatusKind.Ok => loc.Format("lan.target.status.ok", DisplayText.TimeAgo(now - (status.LastSuccess ?? now), loc)),
            LanTargetStatusKind.Waiting => loc["lan.target.status.waiting"],
            LanTargetStatusKind.NotFound => loc["lan.target.status.notFound"],
            LanTargetStatusKind.NotLocal => loc["lan.target.status.notLocal"],
            LanTargetStatusKind.Unreachable => loc.Format("lan.target.status.unreachable", SecondsLeft(status, nowMs)),
            LanTargetStatusKind.HttpStatus => loc.Format("lan.target.status.httpStatus", status.HttpCode),
            LanTargetStatusKind.Failed => loc["lan.target.status.failed"],
            LanTargetStatusKind.NetworkOff => loc["lan.target.status.networkOff"],
            _ => loc["lan.target.status.off"],
        };
    }

    /// <summary>
    /// The sheet's <i>Send Test</i> result: <c>lan.target.test.ok</c>, or the status sentence; unreachable is the plain
    /// <c>remote.status.unreachable</c>, since a test does not retry (DESIGN 14).
    /// </summary>
    public static string Test(LanTargetStatus status, long nowMs, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            LanTargetStatusKind.Ok => loc["lan.target.test.ok"],
            LanTargetStatusKind.Unreachable => loc["remote.status.unreachable"],
            _ => Target(status, nowMs, DateTimeOffset.Now, loc),
        };
    }

    /// <summary>The line counts down (unreachable with a next attempt).</summary>
    public static bool CountsDown(LanTargetStatus status) => status is { Kind: LanTargetStatusKind.Unreachable, RetryAtMs: not null };

    private static int SecondsLeft(LanTargetStatus status, long nowMs) =>
        status.RetryAtMs is { } at ? (int)Math.Max(1, (at - nowMs + 999) / 1000) : 1;
}
