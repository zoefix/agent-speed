using AgentSpeed.Linux.Services;
using Avalonia.Controls;

namespace AgentSpeed.Linux.UI;

internal sealed partial class MainWindow
{
    private Avalonia.Controls.TextBlock _updateStatus = Text("", 12);
    private bool _updaterSubscribed;
    private Control About()
    {
        var updater = _app.Updater;
        var status = _updateStatus = Text(updater.Status ?? "", 12);
        if (!_updaterSubscribed) { _updaterSubscribed = true; updater.Changed += () => _updateStatus.Text = updater.Status; }
        return Stack(Text($"Agent Speed {_app.Version}", 24), Text("Ubuntu 24.04 · GNOME · X11 · x64\nMIT © zoefix", 12), status,
            Toggle(L("update.auto"), _app.Settings.Current.AutoCheckUpdates, v => _app.Settings.Update(s => s with { AutoCheckUpdates = v })),
            Button(Tr("Check for updates", "检查更新", "更新を確認"), () => _ = updater.CheckAsync()),
            Button(Tr("Download and install verified update…", "下载校验并安装更新…"), async () =>
            {
                if (await updater.DownloadAsync() is not { } path) return;
                if (!await Confirm(Tr("Install the verified update using the system package manager?", "通过系统包管理器安装已校验的更新？"))) return;
                try
                {
                    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pkexec") { ArgumentList = { "/usr/bin/apt-get", "install", "-y", path }, UseShellExecute = false });
                    if (process is not null) { await process.WaitForExitAsync(); if (process.ExitCode == 0) _app.Restart(); }
                }
                catch (System.ComponentModel.Win32Exception) { status.Text = Tr("Open the downloaded .deb with your package manager.", "请使用包管理器打开已下载的 .deb 文件。"); }
            }),
            Button(L("update.skip"), updater.Skip),
            Button(Tr("Clear usage history…", "清除用量历史…"), async () =>
            { if (await Confirm(Tr("Clear stored usage history? Available local logs will be analyzed again.", "清除已保存的历史？仍存在的本地记录会重新分析。"))) { _app.Traffic.Monitor.ClearHistory(); _app.History.Clear(); } }),
            Button(Tr("Quit Agent Speed", "退出 Agent Speed", "終了"), _app.Quit));
    }
    internal void ShowUpdate(string version) => _notice.Text = _app.Loc.Format("update.available.short", version);
}
