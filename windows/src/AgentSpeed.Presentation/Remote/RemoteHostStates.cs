using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Remote;

/// <summary>
/// The remote hosts as the UI sees them (DESIGN 13): this computer's name, the pickers' choices and order, and the status a
/// host shows, where the settings decide off, network off and an invalid destination at once.
/// </summary>
public static class RemoteHostStates
{
    /// <summary>This computer in every host list (<c>remote.local</c> says "This Mac"; Windows has its own word).</summary>
    public const string LocalKey = "win.remote.local";

    public static string LocalName(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc[LocalKey];
    }

    /// <summary>Any remote host is configured: every host label, picker and section waits for the first one.</summary>
    public static bool HasHosts(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.RemoteHosts.Hosts.IsEmpty;
    }

    /// <summary>This computer, then every remote host in settings order with its display name; empty without remote hosts.</summary>
    public static IReadOnlyList<OrbHostChoice> Choices(RemoteHostsDocument document, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Hosts.IsEmpty ? [] : OrbHostChoice.Of(document, LocalName(loc));
    }

    /// <summary><c>local</c>, then the remote host ids in settings order (the pickers' rule and the stored order).</summary>
    public static IReadOnlyList<string> Order(RemoteHostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [RemoteHost.LocalId, .. document.Hosts.Select(h => h.Id)];
    }

    /// <summary>
    /// The status the UI shows for <paramref name="host"/>: off, network off and an invalid destination come from the
    /// settings at once; otherwise the connection's last status (connecting before its first one). A history batch beyond the
    /// bounds (REMOTE 8.3) is noted whatever the connection does.
    /// </summary>
    public static RemoteHostStatus Effective(RemoteHost host, bool networkEnabled, RemoteHostStatus? live, bool historyTooLarge)
    {
        ArgumentNullException.ThrowIfNull(host);
        RemoteHostStatus status;
        if (!networkEnabled)
        {
            status = new RemoteHostStatus(RemoteStatus.NetworkOff);
        }
        else if (!host.Enabled)
        {
            status = new RemoteHostStatus(RemoteStatus.Disabled);
        }
        else if (!host.IsConnectable)
        {
            status = new RemoteHostStatus(RemoteStatus.InvalidDestination);
        }
        else if (live is null || live.Status.Kind is RemoteStatusKind.Disabled or RemoteStatusKind.NetworkOff)
        {
            status = new RemoteHostStatus(RemoteStatus.Connecting);
        }
        else
        {
            status = live;
        }

        return status.HistoryTooLarge == historyTooLarge ? status : status with { HistoryTooLarge = historyTooLarge };
    }
}
