using System.Windows;
using AgentSpeed.Controls;
using AgentSpeed.Core.Lan;
using AgentSpeed.Lan;
using AgentSpeed.Settings.Lan;

namespace AgentSpeed.App;

/// <summary>
/// The local network feed's surfaces (DESIGN 14): the controller fed by every engine tick, Settings → API → 區域網路 with its
/// Add / Edit sheet and confirmations, and the developer capture's operations for them.
/// </summary>
internal sealed partial class AppController
{
    private LanController? _lan;
    private LanTargetDialog? _lanDialog;

    private void CreateLan()
    {
        LanLog.SetSink(line => AppLog.Info(line));
        _lan = new LanController(_ui, _settings, _hub, AppPaths.Version);
    }

    /// <summary>With <c>--demo-speed</c> the feed takes the pinned demo values, never the real agents.</summary>
    private void StartLan() => _lan?.Start(demo: _launch.DemoSpeedValues is { Count: > 0 });

    private LanPaneViewModel? CreateLanPane() => _lan is null
        ? null
        : new LanPaneViewModel(_lan, _loc, _ui, EditLanTarget, ConfirmRemoveLanTarget, ConfirmRegenerateLanKey) { ReduceMotion = _theme.ReduceMotion };

    /// <summary>The Add / Edit sheet over Settings (or the screen); an enabled target sends from the next tick once saved.</summary>
    private void EditLanTarget(LanTarget? target)
    {
        if (_lan is not { } lan || (target is null && !lan.Settings.CanAdd))
        {
            return;
        }

        var dialog = new LanTargetDialog(new LanTargetDialogViewModel(lan, _loc, _ui, target));
        _lanDialog = dialog;
        try
        {
            dialog.ShowOver(_settingsWindow);
        }
        finally
        {
            _lanDialog = null;
        }
    }

    private bool ConfirmRemoveLanTarget(string name)
    {
        var target = _lan?.Settings.Targets.FirstOrDefault(t => t.DisplayName == name);
        var detail = target is null ? string.Empty : (target.Kind == LanTargetKind.Udp ? "UDP · " : "HTTP · ") + target.Address;
        return ConfirmDialog.Show(_settingsWindow, _loc["app.name"], _loc.Format("lan.target.remove.confirm", name), detail,
            _loc["remote.remove.button"], _loc["history.cancel"]);
    }

    private bool ConfirmRegenerateLanKey() => ConfirmDialog.Show(_settingsWindow, _loc["app.name"], _loc["lan.key.regenerate.confirm"],
        _loc["lan.key.regenerate.detail"], _loc["lan.key.regenerate"].TrimEnd('…', '.'), _loc["history.cancel"]);

    /// <summary>
    /// Developer capture operations for the local network feed (only with <c>--capture-dir</c>): <c>lan-segment</c> and
    /// <c>report-segment</c> (the API pane's segment), <c>lan-server-on</c> / <c>-off</c>, <c>lan-add</c>, <c>lan-edit-N</c>
    /// (the N-th target, from 1), <c>lan-kind-udp</c> / <c>-http</c>, <c>lan-type-address</c>, <c>lan-type-name</c> and
    /// <c>lan-type-key</c> (the text of <c>type.txt</c>), <c>lan-test</c>, <c>lan-save</c>, <c>lan-cancel</c>, <c>lan-off-N</c> /
    /// <c>lan-on-N</c>, <c>network-off</c> / <c>network-on</c> (the master switch) and <c>lan-dump</c> (the statuses in the log, never a key).
    /// </summary>
    private bool RunLanCaptureOperation(string op)
    {
        if (_lan is not { } lan)
        {
            return false;
        }

        switch (op)
        {
            case "lan-segment" or "report-segment":
                _capturePage = "report";
                OpenSettings("report");
                if (_settingsModel is { } model)
                {
                    model.ApiShowsLan = op == "lan-segment";
                }

                return true;
            case "lan-server-on" or "lan-server-off":
                lan.SetServerEnabled(op == "lan-server-on");
                return true;
            case "lan-add":
                _ui.BeginInvoke(() => EditLanTarget(null));
                return true;
            case "lan-kind-udp":
                _lanDialog?.KindUdpButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                return true;
            case "lan-kind-http":
                _lanDialog?.KindHttpButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                return true;
            case "lan-type-address" when TypedText() is { } address:
                SetText(_lanDialog?.AddressBox, address);
                return true;
            case "lan-type-name" when TypedText() is { } name:
                SetText(_lanDialog?.NameBox, name);
                return true;
            case "lan-type-key" when TypedText() is { } key:
                SetText(_lanDialog?.KeyBox, key);
                return true;
            case "lan-test":
                Invoke(_lanDialog?.TestButton);
                return true;
            case "lan-save":
                Invoke(_lanDialog?.SaveButton);
                return true;
            case "lan-cancel":
                Invoke(_lanDialog?.CancelButton);
                return true;
            case "lan-dump":
                DumpLan(lan);
                return true;
            case "network-off" or "network-on":
                _settings.Update(s => s with { NetworkEnabled = op == "network-on" });
                return true;
        }

        if (Numbered(op, "lan-edit-") is { } edit && lan.Settings.Targets.ElementAtOrDefault(edit - 1) is { } edited)
        {
            _ui.BeginInvoke(() => EditLanTarget(edited));
            return true;
        }

        if ((Numbered(op, "lan-off-") ?? Numbered(op, "lan-on-")) is { } index && lan.Settings.Targets.ElementAtOrDefault(index - 1) is { } target)
        {
            lan.SetTargetEnabled(target.Id, op.StartsWith("lan-on-", StringComparison.Ordinal));
            return true;
        }

        return false;
    }

    private void DumpLan(LanController lan)
    {
        var server = lan.ServerStatus;
        AppLog.Info($"E2E: lan server={server.Kind} port={server.Port} devices={server.Devices} addresses={string.Join(',', lan.Addresses)}");
        var index = 0;
        foreach (var target in lan.Settings.Targets)
        {
            var status = lan.TargetStatus(target.Id);
            AppLog.Info($"E2E: lan target {++index} kind={target.Kind} enabled={target.Enabled} status={status.Kind} code={status.HttpCode}");
        }
    }

    /// <summary>The open Add / Edit sheet's content for <c>dialog</c> captures.</summary>
    private FrameworkElement? LanCaptureTarget() => _lanDialog?.Content as FrameworkElement;

    /// <summary>A target row's actions for <c>menu-lan</c>.</summary>
    private System.Windows.Controls.ContextMenu? LanCaptureMenu()
    {
        if (_settingsModel?.Lan is not { } pane || pane.Targets.FirstOrDefault() is not { } row)
        {
            return null;
        }

        var menu = new System.Windows.Controls.ContextMenu();
        LanMenus.FillTarget(menu, pane, row, _loc);
        return menu;
    }
}
