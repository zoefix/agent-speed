using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Settings.Remote;

/// <summary>What the host editor's Claude Code bridge needs from the app (DESIGN 13 <i>Host quotas</i>, REMOTE 11.3).</summary>
internal interface IRemoteHostQuota
{
    /// <summary>The quotas changed (on the UI thread): the bridge's state line follows.</summary>
    event Action? Changed;

    /// <summary>A host's quota lines and bridge state as last reported.</summary>
    HostQuotaState Of(string hostId);

    /// <summary>Asks <c>remote.quota.claudeCli.confirm.title</c> / <c>.message</c> over the editor; true to turn the bridge on.</summary>
    bool ConfirmBridge(string hostName, string settingsPath);

    /// <summary><c>settings.quota.cli.reconnect</c>: the host's relay runs QUOTAS 5.2 again.</summary>
    void Reconnect(string hostId);
}
