using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Remote;

/// <summary>
/// What a host's status says in words (DESIGN 13 status line, REMOTE 4.5): one sentence per status with the one thing the
/// user can do, the retry countdown, the clock and history notes, the tooltip and the dashboard's short word.
/// </summary>
public static class RemoteStatusText
{
    /// <summary>Windows names <c>ssh.exe</c>'s absence by its optional feature (the shared sentence names <c>/usr/bin/ssh</c>).</summary>
    public const string SshMissingKey = "win.remote.status.sshMissing";

    /// <summary>The status line. <paramref name="sessions"/>: today's main sessions on the host.</summary>
    public static string Sentence(RemoteHostStatus status, string destination, int sessions, DateTimeOffset now, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        var kind = status.Status.Kind;
        if (kind == RemoteStatusKind.Connected)
        {
            return sessions <= 0 ? loc["remote.status.connected"]
                : loc.Format(sessions == 1 ? "remote.status.connected.one" : "remote.status.connected.agents", sessions);
        }

        var text = Base(status.Status, destination, loc);
        return status.Status.IsRetrying ? Retrying(text, status.RetryAt, now, loc) : text;
    }

    /// <summary>The plain sentence of a status, without its countdown.</summary>
    public static string Base(RemoteStatus status, string destination, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            RemoteStatusKind.Connecting => loc["remote.status.connecting"],
            RemoteStatusKind.Installing => loc["remote.status.installing"],
            RemoteStatusKind.Seeding => loc.Format("remote.status.seeding", Percent(status.Fraction)),
            RemoteStatusKind.Connected => loc["remote.status.connected"],
            RemoteStatusKind.Unreachable => loc["remote.status.unreachable"],
            RemoteStatusKind.AuthFailed => loc["remote.status.authFailed"],
            RemoteStatusKind.HostKeyUnknown => loc.Format("remote.status.hostKeyUnknown", SshCommandText.Arguments(destination)),
            RemoteStatusKind.UnsupportedSystem => loc.Format("remote.status.unsupportedSystem", status.System ?? string.Empty),
            RemoteStatusKind.CannotVerify => loc["remote.status.cannotVerify"],
            RemoteStatusKind.UploadFailed => loc["remote.status.uploadFailed"],
            RemoteStatusKind.NotExecutable => loc["remote.status.notExecutable"],
            RemoteStatusKind.VersionMismatch => loc["remote.status.versionMismatch"],
            RemoteStatusKind.ProtocolError => loc["remote.status.protocolError"],
            RemoteStatusKind.Exited => loc["remote.status.exited"],
            RemoteStatusKind.SshMissing => loc[SshMissingKey],
            RemoteStatusKind.InvalidDestination => loc["remote.status.invalidDestination"],
            RemoteStatusKind.NetworkOff => loc["remote.status.networkOff"],
            _ => loc["remote.status.disabled"],
        };
    }

    /// <summary><c>無法連線，10 秒後重試</c>; at the deadline (the next attempt is starting) <c>無法連線，正在重試…</c>.</summary>
    public static string Retrying(string text, DateTimeOffset? retryAt, DateTimeOffset now, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (retryAt is not { } at)
        {
            return text;
        }

        var seconds = (int)Math.Ceiling((at - now).TotalSeconds);
        return seconds >= 1 ? loc.Format("remote.status.retryIn", text, seconds) : loc.Format("remote.status.retryingNow", text);
    }

    /// <summary>The clock note under a connected host's line when <c>|δ| ≥ 5 s</c> (REMOTE 6): seconds below two minutes.</summary>
    public static string? ClockNote(RemoteHostStatus status, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        if (status.Status.Kind != RemoteStatusKind.Connected || !status.IsClockSkewed || status.ClockOffsetMs is not { } offset)
        {
            return null;
        }

        var seconds = (long)Math.Round(Math.Abs((double)offset) / 1000, MidpointRounding.AwayFromZero);
        return seconds < 120
            ? loc.Format("remote.clock.skewed", seconds)
            : loc.Format("remote.clock.skewedMinutes", (long)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>The note under a host's line while its history cannot be synced (<c>historyTooLarge</c>, REMOTE 8.3).</summary>
    public static string? HistoryNote(RemoteHostStatus status, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        return status.HistoryTooLarge ? loc["remote.status.historyTooLarge"] : null;
    }

    /// <summary>
    /// The status's tooltip: the helper's version while it runs, else the last error's time and, for <c>exited</c>, the exit
    /// status. Null when there is nothing to add.
    /// </summary>
    public static string? Tooltip(RemoteHostStatus status, DateTimeOffset now, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new List<string>(2);
        switch (status.Status.Kind)
        {
            case RemoteStatusKind.Connected or RemoteStatusKind.Seeding:
                if (status.RelayVersion is { Length: > 0 } version && status.RelayArch is { Length: > 0 } arch)
                {
                    lines.Add(loc.Format("remote.status.relay", version, arch));
                }

                break;
            case RemoteStatusKind.Connecting or RemoteStatusKind.Installing or RemoteStatusKind.Disabled or RemoteStatusKind.NetworkOff:
                break;
            default:
                if (status.LastErrorAt is { } at)
                {
                    lines.Add(loc.Format("remote.status.lastError", Time(at, now, loc)));
                }

                if (status.Status.Kind == RemoteStatusKind.Exited)
                {
                    lines.Add(loc.Format("remote.status.exitCode", status.Status.ExitStatus));
                }

                break;
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>The short word of a host that is not connected (dashboard rows, pickers, menus).</summary>
    public static string Word(RemoteStatus status, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return status.Kind switch
        {
            RemoteStatusKind.Connected => loc["remote.status.connected"],
            RemoteStatusKind.Disabled => loc["remote.status.disabled"],
            RemoteStatusKind.NetworkOff => loc["remote.word.offline"],
            _ => RemoteDots.Of(status) switch
            {
                RemoteDotKind.Progress => loc["remote.word.connecting"],
                RemoteDotKind.Retrying => loc["remote.word.retrying"],
                _ => loc["remote.word.attention"],
            },
        };
    }

    /// <summary>The field error of a destination or relay folder (REMOTE 2.1).</summary>
    public static string FieldError(RemoteFieldIssue issue, bool folder, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return issue switch
        {
            RemoteFieldIssue.Empty => loc["remote.error.empty"],
            RemoteFieldIssue.TooLong => loc.Format("remote.error.tooLong",
                folder ? RemoteHostValidation.FolderLimit : RemoteHostValidation.DestinationLimit),
            RemoteFieldIssue.LeadingDash => loc["remote.error.leadingDash"],
            RemoteFieldIssue.ForbiddenCharacter => loc[folder ? "remote.error.folderCharacter" : "remote.error.forbiddenCharacter"],
            RemoteFieldIssue.NotAbsolute => loc["remote.error.notAbsolute"],
            _ => loc["remote.error.unsafeComponent"],
        };
    }

    private static int Percent(double fraction) => (int)Math.Floor(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * 100);

    /// <summary><c>14:32</c> today, else the short date and time, in the app's language.</summary>
    private static string Time(DateTimeOffset at, DateTimeOffset now, Localizer loc)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? local.ToString("t", loc.Culture) : local.ToString("g", loc.Culture);
    }
}
