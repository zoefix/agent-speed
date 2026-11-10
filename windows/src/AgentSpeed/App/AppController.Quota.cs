using System.Collections.Immutable;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Quota;
using AgentSpeed.Settings.Sources;

namespace AgentSpeed.App;

/// <summary>
/// Quotas (QUOTAS; DESIGN 4, 2a.6, 5): the service started with the monitor, its state on the dashboard, the orbs and
/// Settings, the Claude Code bridge with its confirmation, and the developer capture's quota operations.
/// </summary>
internal sealed partial class AppController
{
    private QuotaService? _quota;

    /// <summary>The quotas as the surfaces show them now.</summary>
    internal QuotaState Quota => _quota?.State ?? QuotaState.Empty;

    private void StartQuota()
    {
        try
        {
            _quota = new QuotaService(_ui, () => _monitor?.CodexQuota.Snapshot, sandboxed: _launch.CaptureDir is not null);
            _quota.Changed += OnQuotaChanged;
            _quota.Apply(Settings);
            UpdateQuotaDemand();
            _quota.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("The quota readers failed to start.", ex);
        }
    }

    private void OnQuotaChanged()
    {
        var state = Quota;
        _dashboardModel.Quota = state;
        _dashboardModel.QuotaSettings = Settings.Quota;
        _orbs?.UpdateQuota(state);
        _settingsModel?.SourcesPane.UpdateQuota(state, Settings.Quota);
        _dashboardDirty = true;
        FlushDashboard();
    }

    private void OnQuotaSettingsChanged(AppSettings previous, AppSettings current)
    {
        _quota?.Apply(current);
        if (previous.Quota != current.Quota)
        {
            _dashboardModel.QuotaSettings = current.Quota;
            _settingsModel?.SourcesPane.UpdateQuota(Quota, current.Quota);
        }

        if (previous.RemoteHosts != current.RemoteHosts || previous.RemoteQuotaSwitches != current.RemoteQuotaSwitches)
        {
            var switches = current.RemoteQuotaSwitches;
            _quota?.PruneHosts(current.RemoteHosts, (source, host) => HostShows(source, host, switches));
        }

        UpdateQuotaDemand();
    }

    /// <summary>A host's line from the remote event thread (REMOTE 11.1): kept while its source shows on that host.</summary>
    private void OnRemoteQuota(object? sender, Core.Remote.RemoteQuotaEventArgs e)
    {
        var settings = Settings;
        var shows = e.Report is not { } report
            || (settings.RemoteHosts.Hosts.FirstOrDefault(h => h.Id == e.HostId) is { } host && HostShows(report.Source, host, settings.RemoteQuotaSwitches));
        _quota?.ApplyRemote(e, shows);
    }

    /// <summary>A disabled source shows no quota from any host either (QUOTAS 9.1); Claude Code is the host's own bridge.</summary>
    private static bool HostShows(SourceKind source, Core.Remote.RemoteHost host, Core.Remote.RemoteQuotaSwitches switches) =>
        source == SourceKind.ClaudeCode ? host.QuotaClaudeCli && switches.ClaudeCode : switches.Sources.Contains(source);

    /// <summary>Some character orb has the 額度 card on: the running apps are listed every 15 s (QUOTAS 6).</summary>
    private void UpdateQuotaDemand() =>
        _quota?.SetCardDemand(Settings.Orbs.Orbs.Any(o => o.Visible && o.Style == OrbStyle.Character && o.CharacterReadouts.Quota));

    /// <summary>
    /// Turning <c>quotaClaudeCli</c> on (DESIGN 5): asks first, then installs the bridge; a failure leaves the switch off and
    /// shows why. Returns whether the switch is on now.
    /// </summary>
    internal bool EnableClaudeCliQuota()
    {
        if (_quota is not { } quota)
        {
            return false;
        }

        var path = AbbreviatedHome(quota.Bridge.Paths.SettingsFile);
        if (!ConfirmDialog.ShowAccent(_settingsWindow is { IsVisible: true } ? _settingsWindow : null, _loc["app.name"],
                _loc["settings.quota.claudeCli.confirm.title"], _loc.Format("settings.quota.claudeCli.confirm.message", path),
                _loc["settings.quota.claudeCli.confirm.ok"], _loc["history.cancel"]))
        {
            return false;
        }

        var status = quota.TurnOnBridge();
        if (status is ClaudeCliBridgeStatus.Failed or ClaudeCliBridgeStatus.Unreadable)
        {
            _settingsModel?.SourcesPane.ShowCliFailure(status == ClaudeCliBridgeStatus.Unreadable
                ? _loc.Format("settings.quota.cli.unreadable", path)
                : _loc.Format("settings.quota.cli.failed", path));
            return false;
        }

        _settingsModel?.SourcesPane.ShowCliFailure(null);
        _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = true } });
        return true;
    }

    /// <summary>Turning <c>quotaClaudeCli</c> off restores the previous status line at once (QUOTAS 5.3).</summary>
    internal void DisableClaudeCliQuota()
    {
        _quota?.TurnOffBridge();
        _settingsModel?.SourcesPane.ShowCliFailure(null);
        _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = false } });
    }

    /// <summary>The status line was replaced by another setting: <c>settings.quota.cli.reconnect</c> runs QUOTAS 5.2 again.</summary>
    internal void ReconnectClaudeCliQuota()
    {
        if (_quota?.TurnOnBridge() is ClaudeCliBridgeStatus.Failed or ClaudeCliBridgeStatus.Unreadable)
        {
            _settingsModel?.SourcesPane.ShowCliFailure(_loc.Format("settings.quota.cli.failed", AbbreviatedHome(_quota.Bridge.Paths.SettingsFile)));
        }
    }

    /// <summary>Settings opened: the quota switches show their state and the bridge's status is checked now (QUOTAS 5.4).</summary>
    private void AttachQuotaPane(SourcesPaneViewModel pane)
    {
        pane.QuotaActions ??= new QuotaActions(this);
        if (_quota is { } quota)
        {
            pane.CliSettingsPath = AbbreviatedHome(quota.Bridge.Paths.SettingsFile);
            quota.RefreshBridgeStatus();
        }

        pane.UpdateQuota(Quota, Settings.Quota);
    }

    private sealed class QuotaActions(AppController app) : IQuotaSettingsActions
    {
        public void Set(QuotaSwitch quota, bool on) => app._settings.Update(s => s with
        {
            Quota = quota == QuotaSwitch.Codex ? s.Quota with { Codex = on } : s.Quota with { ClaudeDesktop = on },
        });

        public bool EnableClaudeCli() => app.EnableClaudeCliQuota();

        public void DisableClaudeCli() => app.DisableClaudeCliQuota();

        public void ReconnectClaudeCli() => app.ReconnectClaudeCliQuota();
    }

    internal void DismissCliInvitation() => _settings.Update(s => s with { Quota = s.Quota with { CliInviteDismissed = true } });

    /// <summary>The bridge's open confirmation, for the developer capture's <c>dialog</c> request.</summary>
    private static System.Windows.FrameworkElement? QuotaCaptureTarget() =>
        System.Windows.Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault()?.Content as System.Windows.FrameworkElement;

    /// <summary>A path under the home folder with <c>~</c> in its place.</summary>
    private static string AbbreviatedHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        return home.Length > 0 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }
}
