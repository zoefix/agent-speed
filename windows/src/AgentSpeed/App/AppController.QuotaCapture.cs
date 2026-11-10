using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Quota;

namespace AgentSpeed.App;

/// <summary>The developer capture's quota operations (only with <c>--capture-dir</c>).</summary>
internal sealed partial class AppController
{
    /// <summary>
    /// Developer capture operations (only with <c>--capture-dir</c>): <c>quota-demo</c> (Claude Desktop 7d + Fable 7d, Claude
    /// Code 5h + 7d, Codex 7d, all running), <c>quota-demo-two</c> (Claude Desktop and Codex), <c>quota-demo-off</c>,
    /// <c>quota-cli-&lt;waiting|ok|replaced|unreadable|failed&gt;</c> (Settings' status line), <c>quota-cli-off</c>,
    /// <c>quota-invite</c> (the dashboard's Claude Code invitation), <c>quota-confirm</c> (the bridge's dialog) and
    /// <c>quota-cli-install</c> / <c>quota-cli-uninstall</c> (the bridge itself, without the dialog).
    /// </summary>
    private bool RunQuotaCaptureOperation(string op)
    {
        if (_quota is not { } quota || !op.StartsWith("quota", StringComparison.Ordinal))
        {
            return false;
        }

        if (RunQuotaSettingsCapture(op) || RunQuotaSurfaceCapture(op))
        {
            return true;
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

    /// <summary>Settings → Data Sources' phase 2 captures (<c>AppController.QuotaCapture.Settings.cs</c>).</summary>
    private partial bool RunQuotaSettingsCapture(string op);

    /// <summary>The dashboard, Hosts, host editor and orb captures (<c>AppController.QuotaCapture.Surfaces.cs</c>).</summary>
    private partial bool RunQuotaSurfaceCapture(string op);
}
