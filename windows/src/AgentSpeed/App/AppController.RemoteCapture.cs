using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentSpeed.Controls;
using AgentSpeed.Menus;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Settings.Remote;

namespace AgentSpeed.App;

/// <summary>
/// Developer capture operations for the remote hosts (only with <c>--capture-dir</c>). They drive the real dialogs through
/// their UI Automation patterns, as a screen reader or a test tool would: <c>remote-add</c>, <c>remote-edit-N</c> (the N-th
/// host, from 1), <c>remote-remove-N</c>, <c>remote-dest-…</c>, <c>remote-name-…</c> (letters, digits and <c>-</c>),
/// <c>remote-advanced</c>, <c>remote-test</c>, <c>remote-save</c>, <c>dialog-option</c>, <c>dialog-confirm</c>,
/// <c>dialog-cancel</c>, <c>history-host-N</c> (0: this computer), <c>history-host-all</c>, <c>history-width-N</c>, <c>retry-N</c>, <c>remote-off-N</c>,
/// <c>remote-folder-test</c>, <c>orb-only-host-O-H</c>, <c>history-csv</c> and <c>e2e-dump</c>.
/// </summary>
internal sealed partial class AppController
{
    private bool RunRemoteCaptureOperation(string op)
    {
        if (_remoteHosts is not { } hosts)
        {
            return false;
        }

        switch (op)
        {
            case "remote-add":
                _ui.BeginInvoke(() => EditRemoteHost(null));
                return true;
            case "remote-advanced":
                Invoke(_remoteDialog?.AdvancedButton);
                return true;
            case "remote-test":
                Invoke(_remoteDialog?.TestButton);
                return true;
            case "remote-save":
                Invoke(_remoteDialog?.SaveButton);
                return true;
            case "dialog-confirm":
                Invoke(Dialog<ConfirmDialog>()?.ConfirmButton);
                return true;
            case "dialog-cancel":
                Invoke(_remoteDialog?.CancelButton ?? Dialog<ConfirmDialog>()?.CancelButton);
                return true;
            case "remote-type-dest" when TypedText() is { } destination:
                SetText(_remoteDialog?.DestinationBox, destination);
                return true;
            case "remote-type-name" when TypedText() is { } name:
                SetText(_remoteDialog?.NameBox, name);
                return true;
            case "invite-decline" when _dashboard?.ReportInvitation is Dashboard.ReportInvitationCard card:
                Invoke(card.DeclineButton);
                return true;
            case "quit":
                // The quick menu's Quit (MenuAction.Quit), after this capture request is answered.
                _ui.BeginInvoke(() => MenuBuilder.Run(new MenuAction.Quit(), this));
                return true;
            case "remote-folder-test":
                SetText(_remoteDialog?.FolderBox, "~/.cache/agent-speed-remote-ui");
                return true;
            case "history-host-all":
                _history?.Open(null);
                return true;
            case "e2e-dump":
                DumpForEndToEnd();
                return true;
            case "history-csv" when _launch.CaptureDir is { } folder && _history?.ExportCsv() is { } csv:
                File.WriteAllBytes(Path.Combine(folder, "history.csv"), csv.Bytes);
                return true;
            case "dialog-option" when Dialog<ConfirmDialog>() is { } confirm:
                (new CheckBoxAutomationPeer(confirm.Option).GetPattern(PatternInterface.Toggle) as IToggleProvider)?.Toggle();
                return true;
        }

        if (Numbered(op, "remote-edit-") is { } edit && hosts.Document.Hosts.ElementAtOrDefault(edit - 1) is { } edited)
        {
            _ui.BeginInvoke(() => EditRemoteHost(edited));
            return true;
        }

        if (Numbered(op, "remote-remove-") is { } remove && hosts.Document.Hosts.ElementAtOrDefault(remove - 1) is { } removed
            && _settingsModel?.Remote is { } pane && pane.Rows.FirstOrDefault(r => r.Id == removed.Id) is { } row)
        {
            _ui.BeginInvoke(() => pane.RemoveCommand.Execute(row));
            return true;
        }

        // orb-only-host-O-H: the O-th orb's Hosts menu, H-th remote host chosen (from all hosts it becomes the only one).
        if (op.StartsWith("orb-only-host-", StringComparison.Ordinal) && op["orb-only-host-".Length..].Split('-') is [var o, var h]
            && int.TryParse(o, out var orbIndex) && int.TryParse(h, out var hostIndex)
            && Settings.Orbs.Orbs.ElementAtOrDefault(orbIndex - 1) is { } orb && hosts.Document.Hosts.ElementAtOrDefault(hostIndex - 1) is { } only)
        {
            MenuBuilder.Run(new MenuAction.ToggleOrbHost(orb.Id, only.Id), this);
            return true;
        }

        if (Numbered(op, "remote-notes-") is { } notes && hosts.Document.Hosts.ElementAtOrDefault(notes - 1) is { } noted)
        {
            hosts.PreviewNotes(noted.Id);
            return true;
        }

        if (Numbered(op, "remote-on-") is { } on && hosts.Document.Hosts.ElementAtOrDefault(on - 1) is { } enabled)
        {
            hosts.SetEnabled(enabled.Id, true);
            return true;
        }

        if (Numbered(op, "remote-copy-") is { } copy && hosts.Document.Hosts.ElementAtOrDefault(copy - 1) is { } copied
            && _settingsModel?.Remote is { } copyPane && copyPane.Rows.FirstOrDefault(r => r.Id == copied.Id) is { } copyRow)
        {
            copyPane.CopyCommand.Execute(copyRow);
            return true;
        }

        if (Numbered(op, "remote-off-") is { } off && hosts.Document.Hosts.ElementAtOrDefault(off - 1) is { } disabled)
        {
            hosts.SetEnabled(disabled.Id, false);
            return true;
        }

        if (Numbered(op, "retry-") is { } retry && hosts.Document.Hosts.ElementAtOrDefault(retry - 1) is { } retried)
        {
            hosts.RetryNow(retried.Id);
            return true;
        }

        if (op.StartsWith("remote-dest-", StringComparison.Ordinal))
        {
            SetText(_remoteDialog?.DestinationBox, op["remote-dest-".Length..]);
            return true;
        }

        if (op.StartsWith("remote-name-", StringComparison.Ordinal))
        {
            SetText(_remoteDialog?.NameBox, op["remote-name-".Length..]);
            return true;
        }

        if (Numbered(op, "history-host-") is { } index)
        {
            var id = index == 0 ? Core.Remote.RemoteHost.LocalId : hosts.Document.Hosts.ElementAtOrDefault(index - 1)?.Id;
            if (id is not null)
            {
                ShowHostHistory(id);
            }

            return true;
        }

        if (Numbered(op, "history-width-") is { } width && _history?.CaptureContent is { } content && Window.GetWindow(content) is { } window)
        {
            window.Width = Math.Max(window.MinWidth, width);
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>e2e-dump</c>: every host as the snapshot sees it and every orb's reading, in the log (numbers and ids only), so an
    /// end-to-end run can check that an orb limited to a host reads only that host.
    /// </summary>
    private void DumpForEndToEnd()
    {
        var snapshot = _speed.Snapshot;
        foreach (var host in snapshot.Hosts)
        {
            AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"E2E: host {host.Id} connected={host.IsConnected} live={host.Live.Output:0.0} today={host.Today.Output} sessions={host.Sessions} status={host.Remote?.Status.Code ?? "-"}"));
        }

        foreach (var host in _remoteHosts?.Document.Hosts ?? [])
        {
            var status = _remoteHosts!.StatusOf(host);
            var retry = status.RetryAt is { } at ? (at - DateTimeOffset.Now).TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "-";
            AppLog.Info($"E2E: remote {host.Id} dest={host.Destination} enabled={host.Enabled} status={status.Status.Code} retryIn={retry} line=\"{Presentation.Remote.RemoteStatusText.Sentence(status, host.Destination, _remoteHosts.SessionsOf(host.Id), DateTimeOffset.Now, _loc)}\"");
        }

        foreach (var group in _historyHub.Days.GroupBy(d => d.Host))
        {
            AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"E2E: history host={group.Key} rows={group.Count()} output={group.Sum(d => d.Totals.Output)}"));
        }

        foreach (var agent in snapshot.Agents)
        {
            AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"E2E: agent host={agent.Host} source={agent.Source} phase={agent.Phase} live={agent.Live.Output:0.0}"));
        }

        foreach (var (id, feed) in _orbs?.Feeds ?? new Dictionary<string, Presentation.Orb.OrbFeed>())
        {
            var hosts = feed.Hosts is { } set ? string.Join('+', set.Order(StringComparer.Ordinal)) : "all";
            AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"E2E: orb {id} hosts={hosts} state={feed.Reading.State} value={feed.Value:0.0} today={feed.Reading.Today.Output} agents={feed.Reading.Agents.Count} peak={feed.Reading.MeasuredPeakOutput?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}"));
        }
    }

    /// <summary>The open dialog's content for <c>dialog</c> captures: the Add / Edit dialog, else a confirmation.</summary>
    private FrameworkElement? RemoteCaptureTarget(string target) => target == "dialog"
        ? (_remoteDialog?.Content ?? Dialog<ConfirmDialog>()?.Content) as FrameworkElement
        : null;

    /// <summary>
    /// The remote hosts' menus for <c>menu-host</c> (the first host row's actions), <c>menu-orb-hosts</c> and
    /// <c>menu-board-hosts</c> (the first orb's or board's Hosts submenu, drawn as its own menu).
    /// </summary>
    private ContextMenu? RemoteCaptureMenu(string kind)
    {
        if (kind == "menu-host")
        {
            if (_settingsModel?.Remote is not { } pane || pane.Rows.FirstOrDefault() is not { } row)
            {
                return null;
            }

            var menu = new ContextMenu();
            RemoteHostActionsMenu.Fill(menu, pane, row, _loc);
            return menu;
        }

        var level = kind switch
        {
            "menu-orb-hosts" when Settings.Orbs.Orbs.FirstOrDefault() is { } orb =>
                OrbMenuModel.Build(orb.Id, Settings, SourceAvailability.From(_speed.Snapshot.Sources), _loc, SourceTrafficAvailable,
                    _remoteHosts?.MenuStates(_loc)),
            "menu-board-hosts" when Settings.Boards.Boards.FirstOrDefault() is { } board =>
                BoardMenuModel.Build(board.Id, Settings, SourceAvailability.From(_speed.Snapshot.Sources), _loc, _remoteHosts?.MenuStates(_loc)),
            _ => null,
        };
        var hostsRow = level?.Rows.FirstOrDefault(r => r.Label == _loc["remote.hosts"] && r.Submenu is not null);
        return hostsRow?.Submenu is { } submenu ? MenuBuilder.Build(submenu, this, _loc) : null;
    }

    /// <summary>The text of <c>type.txt</c> in the capture folder (letters a request line cannot carry, such as <c>@</c>).</summary>
    private string? TypedText()
    {
        if (_launch.CaptureDir is not { } folder)
        {
            return null;
        }

        var path = Path.Combine(folder, "type.txt");
        return File.Exists(path) && new FileInfo(path).Length <= 1024 ? File.ReadAllText(path).Trim() : null;
    }

    private static int? Numbered(string op, string prefix) =>
        op.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(op[prefix.Length..], out var n) ? n : null;

    private static T? Dialog<T>()
        where T : Window =>
        Application.Current?.Windows.OfType<T>().LastOrDefault(w => w.IsVisible);

    private static void Invoke(ButtonBase? button)
    {
        if (button is not null && UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
        }
    }

    private static void SetText(TextBox? box, string text)
    {
        if (box is not null && UIElementAutomationPeer.CreatePeerForElement(box)?.GetPattern(PatternInterface.Value) is IValueProvider value)
        {
            box.Focus();
            value.SetValue(text);
        }
    }
}
