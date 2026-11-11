using AgentSpeed.Controls;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Remote;
using AgentSpeed.Settings.Remote;

namespace AgentSpeed.App;

/// <summary>
/// The remote hosts' surfaces (DESIGN 13): the shared model every surface reads, Settings → 遠端主機 with its dialogs, the
/// dashboard's Hosts section, the history's host filter, and the subsets whose peaks the monitor reports.
/// </summary>
internal sealed partial class AppController
{
    private RemoteHostsModel? _remoteHosts;

    private void CreateRemoteHosts()
    {
        _remoteHosts = new RemoteHostsModel(_settings, _historyHub, _speed, _ui);
        _remoteHosts.Changed += (_, _) => OnRemoteHostsChanged();
        _dashboardModel.RemoteHosts = _remoteHosts;
    }

    /// <summary>A status, the hosts or a too-large flag changed: the dashboard, the orbs' names and the history follow.</summary>
    private void OnRemoteHostsChanged()
    {
        _dashboardDirty = true;
        FlushDashboard();
    }

    /// <summary>
    /// The subsets of the orbs and boards (METRICS 6.8, DESIGN 13) whose measured peak every snapshot reports, so a limited orb
    /// or board has its own peak and celebrates it.
    /// </summary>
    private void RequestSubsets(AppSettings settings)
    {
        try
        {
            _monitor?.SetSubsets(SubsetRequests.For(settings));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            AppLog.Warn("The orbs' subsets could not be requested.", ex);
        }
    }

    private RemoteHostsPaneViewModel CreateRemotePane() =>
        new(_remoteHosts!, _loc, _ui, EditRemoteHost, ConfirmRemoveRemoteHost) { ReduceMotion = _theme.ReduceMotion };

    /// <summary>The Add / Edit dialog over Settings (or the screen); an enabled host connects as soon as it is saved.</summary>
    private void EditRemoteHost(RemoteHost? host)
    {
        if (_remoteHosts is not { } hosts || (host is null && !hosts.Document.CanAdd))
        {
            return;
        }

        var dialog = new RemoteHostDialog(new RemoteHostDialogViewModel(hosts, _loc, host, new HostQuotaLink(this)), _theme.ReduceMotion);
        _remoteDialog = dialog;
        try
        {
            dialog.ShowOver(_settingsWindow);
        }
        finally
        {
            _remoteDialog = null;
        }
    }

    /// <summary>The dialog open now, for the developer capture.</summary>
    private RemoteHostDialog? _remoteDialog;

    /// <summary>
    /// Turning a host's <c>quotaClaudeCli</c> on (DESIGN 13 <i>Host quotas</i>): asks over the editor with the host's name and the
    /// settings path on the host. The switch is saved with the host; the relay edits the file once it applies the config.
    /// </summary>
    private bool ConfirmHostBridge(string hostName, string settingsPath) =>
        ConfirmDialog.ShowAccent(_remoteDialog is { IsVisible: true } ? _remoteDialog : _settingsWindow, _loc["app.name"],
            _loc.Format("remote.quota.claudeCli.confirm.title", hostName), _loc.Format("remote.quota.claudeCli.confirm.message", settingsPath),
            _loc["settings.quota.claudeCli.confirm.ok"], _loc["history.cancel"]);

    /// <summary><i>Reconnect</i> under a host's replaced status line: its relay runs QUOTAS 5.2 again (REMOTE 11.3).</summary>
    private void ReconnectHostBridge(string hostId)
    {
        try
        {
            _remote?.Coordinator.ReapplyQuota(hostId);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The host editor's view of the quotas: the service's state and changes, the confirmation and Reconnect.</summary>
    private sealed class HostQuotaLink(AppController app) : IRemoteHostQuota
    {
        public event Action? Changed
        {
            add
            {
                if (app._quota is { } quota)
                {
                    quota.Changed += value;
                }
            }

            remove
            {
                if (app._quota is { } quota)
                {
                    quota.Changed -= value;
                }
            }
        }

        public HostQuotaState Of(string hostId) => app.Quota.HostOf(hostId);

        public bool ConfirmBridge(string hostName, string settingsPath) => app.ConfirmHostBridge(hostName, settingsPath);

        public void Reconnect(string hostId) => app.ReconnectHostBridge(hostId);
    }

    /// <summary>
    /// <c>remote.remove.confirm</c> with <c>remote.remove.files</c>, <c>remote.quota.removeNote</c> for a host whose bridge is on
    /// (REMOTE 2.4), and <i>Also delete its usage history</i> (off).
    /// </summary>
    private (bool Confirmed, bool DeleteHistory) ConfirmRemoveRemoteHost(string name, RemoteHost host) =>
        ConfirmDialog.ShowWithOption(_settingsWindow, _loc["app.name"], _loc.Format("remote.remove.confirm", name),
            RemoteHostQuotaText.RemovalBody(host, _loc), _loc["remote.remove.button"], _loc["history.cancel"], _loc["remote.remove.deleteHistory"]);

    /// <summary>A dashboard Hosts row: the Usage History window on that host, every source.</summary>
    private void ShowHostHistory(string host)
    {
        if (_quitting)
        {
            return;
        }

        _dashboard?.Dismiss();
        StartHistory();
        _history?.Open(null, host: host);
    }
}
