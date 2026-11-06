using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>A host's connection status code (REMOTE 4.5).</summary>
public enum RemoteStatusKind
{
    /// <summary><c>ssh</c> running, no <c>hello</c> yet (probe and upload included).</summary>
    Connecting,

    /// <summary>Uploading the relay.</summary>
    Installing,

    /// <summary><c>hello</c> received, no <c>ready</c> yet.</summary>
    Seeding,
    Connected,
    Unreachable,
    AuthFailed,
    HostKeyUnknown,
    UnsupportedSystem,
    CannotVerify,
    UploadFailed,
    NotExecutable,
    VersionMismatch,
    ProtocolError,
    Exited,
    SshMissing,
    InvalidDestination,
    NetworkOff,
    Disabled,
}

/// <summary>A host's connection status (REMOTE 4.5) with its facts.</summary>
/// <param name="Fraction"><see cref="RemoteStatusKind.Installing"/>: the share of the relay written; <see cref="RemoteStatusKind.Seeding"/>: the relay's progress.</param>
/// <param name="System"><see cref="RemoteStatusKind.UnsupportedSystem"/>: the <c>uname -s -m</c> the host reported.</param>
/// <param name="RelayProtocol"><see cref="RemoteStatusKind.VersionMismatch"/>: the relay's <c>hello.protocol</c>.</param>
/// <param name="ExitStatus"><see cref="RemoteStatusKind.Exited"/>: the exit status of <c>ssh</c>.</param>
public readonly record struct RemoteStatus(
    RemoteStatusKind Kind, double Fraction = 0, string? System = null, long RelayProtocol = 0, int ExitStatus = 0)
{
    public static RemoteStatus Connecting => new(RemoteStatusKind.Connecting);

    public static RemoteStatus Connected => new(RemoteStatusKind.Connected);

    public static RemoteStatus Unreachable => new(RemoteStatusKind.Unreachable);

    public static RemoteStatus AuthFailed => new(RemoteStatusKind.AuthFailed);

    public static RemoteStatus HostKeyUnknown => new(RemoteStatusKind.HostKeyUnknown);

    public static RemoteStatus CannotVerify => new(RemoteStatusKind.CannotVerify);

    public static RemoteStatus UploadFailed => new(RemoteStatusKind.UploadFailed);

    public static RemoteStatus NotExecutable => new(RemoteStatusKind.NotExecutable);

    public static RemoteStatus ProtocolError => new(RemoteStatusKind.ProtocolError);

    public static RemoteStatus SshMissing => new(RemoteStatusKind.SshMissing);

    public static RemoteStatus InvalidDestination => new(RemoteStatusKind.InvalidDestination);

    public static RemoteStatus NetworkOff => new(RemoteStatusKind.NetworkOff);

    public static RemoteStatus Disabled => new(RemoteStatusKind.Disabled);

    public static RemoteStatus Installing(double fraction) => new(RemoteStatusKind.Installing, fraction);

    public static RemoteStatus Seeding(double fraction) => new(RemoteStatusKind.Seeding, fraction);

    public static RemoteStatus UnsupportedSystem(string system) => new(RemoteStatusKind.UnsupportedSystem, System: system);

    public static RemoteStatus VersionMismatch(long relayProtocol) => new(RemoteStatusKind.VersionMismatch, RelayProtocol: relayProtocol);

    public static RemoteStatus Exited(int status) => new(RemoteStatusKind.Exited, ExitStatus: status);

    /// <summary>The status code the log keeps (never stderr, REMOTE 4.1).</summary>
    public string Code => Kind switch
    {
        RemoteStatusKind.Connecting => "connecting",
        RemoteStatusKind.Installing => "installing",
        RemoteStatusKind.Seeding => "seeding",
        RemoteStatusKind.Connected => "connected",
        RemoteStatusKind.Unreachable => "unreachable",
        RemoteStatusKind.AuthFailed => "authFailed",
        RemoteStatusKind.HostKeyUnknown => "hostKeyUnknown",
        RemoteStatusKind.UnsupportedSystem => "unsupportedSystem",
        RemoteStatusKind.CannotVerify => "cannotVerify",
        RemoteStatusKind.UploadFailed => "uploadFailed",
        RemoteStatusKind.NotExecutable => "notExecutable",
        RemoteStatusKind.VersionMismatch => "versionMismatch",
        RemoteStatusKind.ProtocolError => "protocolError",
        RemoteStatusKind.Exited => "exited",
        RemoteStatusKind.SshMissing => "sshMissing",
        RemoteStatusKind.InvalidDestination => "invalidDestination",
        RemoteStatusKind.NetworkOff => "networkOff",
        _ => "disabled",
    };

    /// <summary><c>connecting</c>, <c>installing</c> or <c>seeding</c>: the UI shows progress.</summary>
    public bool IsInProgress => Kind is RemoteStatusKind.Connecting or RemoteStatusKind.Installing or RemoteStatusKind.Seeding;

    public bool IsSeeding => Kind == RemoteStatusKind.Seeding;

    /// <summary>Errors that need the user (REMOTE 4.4): retried every 5 minutes, at once on <i>Retry</i> or an edit.</summary>
    public bool NeedsUser => Kind is RemoteStatusKind.AuthFailed or RemoteStatusKind.HostKeyUnknown or RemoteStatusKind.UnsupportedSystem
        or RemoteStatusKind.NotExecutable or RemoteStatusKind.CannotVerify or RemoteStatusKind.InvalidDestination
        or RemoteStatusKind.SshMissing or RemoteStatusKind.UploadFailed or RemoteStatusKind.VersionMismatch;

    /// <summary>Failures retried with the normal back-off (REMOTE 4.4).</summary>
    public bool IsRetrying => Kind is RemoteStatusKind.Unreachable or RemoteStatusKind.ProtocolError or RemoteStatusKind.Exited;

    /// <summary><c>connected</c> or <c>seeding</c>: the relay is running and its agents are current.</summary>
    public bool IsLive => Kind is RemoteStatusKind.Connected or RemoteStatusKind.Seeding;
}

/// <summary>What the UI shows for one host: the status plus its timing facts (DESIGN 13 status line and tooltip).</summary>
/// <param name="RetryAt">When the next attempt starts; null when none is planned.</param>
/// <param name="LastErrorAt">When the last failure happened.</param>
/// <param name="ClockOffsetMs">The estimated clock offset <c>δ = remote − local</c> (REMOTE 6); null before <c>hello</c>.</param>
/// <param name="HistoryTooLarge"><c>historyTooLarge</c> (REMOTE 8.3), from the history store, not from the connection.</param>
public sealed record RemoteHostStatus(
    RemoteStatus Status,
    DateTimeOffset? RetryAt = null,
    DateTimeOffset? LastErrorAt = null,
    long? ClockOffsetMs = null,
    string? RelayVersion = null,
    string? RelayArch = null,
    bool HistoryTooLarge = false)
{
    /// <summary><c>remote.clock.skewed</c>: <c>|δ| ≥ 5 s</c> (REMOTE 6).</summary>
    public bool IsClockSkewed => ClockOffsetMs is { } offset && RemoteClockEstimator.IsSkewed(offset);
}

/// <summary>The stage a command ran in, for <see cref="RemoteStatusClassifier"/>.</summary>
public enum RemoteStage
{
    Probe,
    Upload,
    Run,
}

/// <summary>Maps how a command ended to a status (REMOTE 4.5). stderr is inspected here and nowhere else; it is never logged.</summary>
public static class RemoteStatusClassifier
{
    public const int MaxStderrBytes = 64 * 1024;

    private static readonly string[] UnreachableMarkers =
    [
        "Could not resolve", "timed out", "Connection refused", "No route", "Network is unreachable", "Connection closed", "Connection reset",
    ];

    public static RemoteStatus Classify(int exitStatus, ReadOnlySpan<byte> stderr, RemoteStage stage)
    {
        var text = Encoding.UTF8.GetString(stderr.Length > MaxStderrBytes ? stderr[^MaxStderrBytes..] : stderr);
        if (text.Contains("Host key verification failed", StringComparison.Ordinal))
        {
            return RemoteStatus.HostKeyUnknown;
        }

        if (exitStatus == 255)
        {
            // "ssh: connect to host h port 22: Permission denied" is the local firewall refusing the connection (WSAEACCES on
            // Windows), not a refused login ("user@h: Permission denied (publickey)"): every connect failure is unreachable.
            if (text.Contains("connect to host ", StringComparison.Ordinal))
            {
                return RemoteStatus.Unreachable;
            }

            if (text.Contains("Permission denied", StringComparison.Ordinal))
            {
                return RemoteStatus.AuthFailed;
            }

            // An empty stderr is a dropped connection too: LogLevel=ERROR hides "Connection to … closed by remote host", and the relay
            // never exits 255 (REMOTE 4.5).
            return string.IsNullOrWhiteSpace(text) || UnreachableMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))
                ? RemoteStatus.Unreachable
                : RemoteStatus.Exited(exitStatus);
        }

        switch (stage)
        {
            case RemoteStage.Run when exitStatus == 126 || text.Contains("Permission denied", StringComparison.Ordinal)
                || text.Contains("cannot execute", StringComparison.Ordinal):
                return RemoteStatus.NotExecutable;
            case RemoteStage.Upload when exitStatus == RemoteCommand.NoHashToolStatus:
                return RemoteStatus.CannotVerify;
            case RemoteStage.Upload when exitStatus != 0:
                return RemoteStatus.UploadFailed;
            default:
                return RemoteStatus.Exited(exitStatus);
        }
    }
}
