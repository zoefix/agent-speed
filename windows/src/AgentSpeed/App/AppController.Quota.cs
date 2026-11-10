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
            _quota = new QuotaService(_ui, () => _monitor?.CodexQuota.Snapshot);
            _quota.Changed += OnQuotaChanged;
            _quota.Apply(Settings.Quota);
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
        if (previous.Quota != current.Quota)
        {
            _quota?.Apply(current.Quota);
            _dashboardModel.QuotaSettings = current.Quota;
            _settingsModel?.SourcesPane.UpdateQuota(Quota, current.Quota);
        }

        UpdateQuotaDemand();
    }

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

    /// <summary>
    /// Developer capture operations (only with <c>--capture-dir</c>): <c>quota-demo</c> (Claude Desktop 7d + Fable 7d, Claude
    /// Code 5h + 7d, Codex 7d, all running), <c>quota-demo-two</c> (Claude Desktop and Codex), <c>quota-demo-off</c>,
    /// <c>quota-cli-&lt;waiting|ok|replaced|unreadable|failed&gt;</c> (Settings' status line), <c>quota-cli-off</c>,
    /// <c>quota-invite</c> (the dashboard's Claude Code invitation), <c>quota-confirm</c> (the bridge's dialog) and
    /// <c>quota-cli-install</c> / <c>quota-cli-uninstall</c> (the bridge itself, without the dialog).
    /// </summary>
    private bool RunQuotaCaptureOperation(string op)
    {
        if (_quota is not { } quota || !op.StartsWith("quota-", StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (op)
        {
            case "quota-demo" or "quota-demo-two":
                var demo = QuotaDemo.State(now, two: op == "quota-demo-two");
                _settings.Update(s => s with { Quota = s.Quota with { Codex = true, ClaudeDesktop = true, ClaudeCli = op == "quota-demo" } });
                quota.SetDemo(demo);
                break;
            case "quota-demo-off":
                quota.SetDemo(null);
                break;
            case "quota-invite":
                _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = false, CliInviteDismissed = false } });
                quota.SetDemo(QuotaDemo.State(now, two: false) with
                {
                    Snapshots = QuotaDemo.State(now, two: false).Snapshots.Remove(SourceKind.ClaudeCode),
                    CliStatus = ClaudeCliBridgeStatus.Off,
                });
                break;
            case "quota-cli-install":
                // The bridge as the Settings switch installs it, without the dialog (end-to-end checks on a test machine).
                AppLog.Info($"Quota capture: install -> {quota.TurnOnBridge()}");
                _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = true } });
                break;
            case "quota-cli-uninstall":
                DisableClaudeCliQuota();
                break;
            case "quota-cli-off":
                _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = false } });
                _settingsModel?.SourcesPane.ShowCliFailure(null);
                break;
            case "quota-confirm":
                _ui.BeginInvoke(() => EnableClaudeCliQuota());
                break;
            case "quota-confirm-cancel":
                foreach (var dialog in System.Windows.Application.Current.Windows.OfType<ConfirmDialog>().ToList())
                {
                    dialog.Close();
                }

                break;
            case "quota-cli-unreadable" or "quota-cli-failed":
                _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = false } });
                _settingsModel?.SourcesPane.ShowCliFailure(_loc.Format(op == "quota-cli-unreadable" ? "settings.quota.cli.unreadable" : "settings.quota.cli.failed",
                    AbbreviatedHome(quota.Bridge.Paths.SettingsFile)));
                break;
            case "quota-cli-waiting" or "quota-cli-ok" or "quota-cli-replaced":
                _settingsModel?.SourcesPane.ShowCliFailure(null);
                _settings.Update(s => s with { Quota = s.Quota with { ClaudeCli = true } });
                quota.SetDemo(QuotaDemo.State(now, two: false) with
                {
                    CliStatus = op switch
                    {
                        "quota-cli-waiting" => ClaudeCliBridgeStatus.Waiting,
                        "quota-cli-ok" => ClaudeCliBridgeStatus.Ok,
                        _ => ClaudeCliBridgeStatus.Replaced,
                    },
                    CliFileTime = op == "quota-cli-ok" ? DateTimeOffset.FromUnixTimeMilliseconds(now - 4 * 60_000) : null,
                });
                break;
            default:
                return false;
        }

        return true;
    }
}

/// <summary>The developer capture's fixed quotas, around <c>now</c>.</summary>
internal static class QuotaDemo
{
    public static QuotaState State(long now, bool two)
    {
        const long Hour = 3_600_000;
        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        snapshots[SourceKind.ClaudeDesktop] = new QuotaSnapshot(SourceKind.ClaudeDesktop, now - 6 * 60_000,
        [
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, now + 2 * 24 * Hour + 5 * Hour),
            QuotaWindow.Of(QuotaWindowKind.ModelWeek, 27, now + 2 * 24 * Hour + 5 * Hour, "Fable"),
        ]);
        snapshots[SourceKind.Codex] = new QuotaSnapshot(SourceKind.Codex, now - 2 * 60_000,
            [QuotaWindow.Of(QuotaWindowKind.SevenDay, 23, now + 5 * 24 * Hour + 9 * Hour)]);
        if (!two)
        {
            snapshots[SourceKind.ClaudeCode] = new QuotaSnapshot(SourceKind.ClaudeCode, now - 60_000,
            [
                QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, now + 3 * Hour + 12 * 60_000),
                QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, now + 19 * Hour + 40 * 60_000),
            ]);
        }

        return new QuotaState
        {
            Snapshots = snapshots.ToImmutable(),
            Active = [.. snapshots.Keys],
            CliStatus = two ? ClaudeCliBridgeStatus.Off : ClaudeCliBridgeStatus.Ok,
            CliFileTime = two ? null : DateTimeOffset.FromUnixTimeMilliseconds(now - 60_000),
        };
    }
}
