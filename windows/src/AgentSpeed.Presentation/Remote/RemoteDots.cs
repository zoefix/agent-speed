using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Menus;

namespace AgentSpeed.Presentation.Remote;

/// <summary>
/// The five looks of a host's status dot (DESIGN 13): connected green, a breathing gray dot while connecting, installing or
/// seeding, orange while retrying, red when it needs the user, hollow while off.
/// </summary>
public enum RemoteDotKind
{
    Connected,
    Progress,
    Retrying,
    Attention,
    Idle,
}

public static class RemoteDots
{
    public static RemoteDotKind Of(RemoteStatus status) => status.Kind switch
    {
        RemoteStatusKind.Connected => RemoteDotKind.Connected,
        RemoteStatusKind.Connecting or RemoteStatusKind.Installing or RemoteStatusKind.Seeding => RemoteDotKind.Progress,
        RemoteStatusKind.NetworkOff or RemoteStatusKind.Disabled => RemoteDotKind.Idle,
        _ => status.NeedsUser ? RemoteDotKind.Attention : RemoteDotKind.Retrying,
    };

    /// <summary>The menus' dot for a host.</summary>
    public static MenuStatusTone Tone(RemoteDotKind kind) => kind switch
    {
        RemoteDotKind.Connected => MenuStatusTone.Green,
        RemoteDotKind.Retrying => MenuStatusTone.Amber,
        RemoteDotKind.Attention => MenuStatusTone.Red,
        RemoteDotKind.Idle => MenuStatusTone.Hollow,
        _ => MenuStatusTone.Gray,
    };

    /// <summary><i>Retry now</i> is offered while the host is failing (not for an invalid destination, which only an edit fixes).</summary>
    public static bool CanRetry(RemoteStatus status) =>
        Of(status) is RemoteDotKind.Retrying or RemoteDotKind.Attention && status.Kind != RemoteStatusKind.InvalidDestination;
}
