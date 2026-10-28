using AgentSpeed.Core.Reporting;
using AgentSpeed.Reporting;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentSpeed.Linux.UI;

internal sealed partial class MainWindow
{
    private const int ReportingSettingsTabIndex = 6;
    private TextBlock _reportStatus = Text("", 12);
    private ContentControl _consent = new();
    private TextBlock _profileStatus = Text("", 12);
    private StackPanel _profileDevices = Stack();
    private Image _avatar = new() { Width = 80, Height = 80 };
    private ReportPairing? _pairing;
    private bool _inviter;
    private bool _profileBusy;
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly PairingRequestVersion _pairingPollVersion = new();
    private readonly PairingRequestVersion _pairingOperationVersion = new();
    private int _pairingMutations;
    private PairingServiceContext? _pairingServiceContext;
    private bool _reportSubscribed;
    private sealed record PairingServiceContext(string? Service, string? Identity, string? DeviceId, string? Api)
    {
        public static PairingServiceContext From(ReportController report) =>
            new(report.SelectedService?.Text, report.Identity?.Id, report.Identity?.DeviceId, report.Discovery?.Api.Text);
    }

    private Control Reporting()
    {
        (_avatar.Source as IDisposable)?.Dispose();
        _reportStatus = Text("", 12); _consent = new(); _profileStatus = Text("", 12);
        _profileDevices = Stack(); _avatar = new() { Width = 80, Height = 80 };
        var report = _app.Report;
        if (!_reportSubscribed)
        {
            _reportSubscribed = true;
            _pairingServiceContext = PairingServiceContext.From(report);
            report.Changed += (_, _) =>
            {
                var context = PairingServiceContext.From(report);
                if (_pairingServiceContext != context)
                {
                    _pairingServiceContext = context;
                    _pairingPollVersion.Next();
                    _pairingOperationVersion.Next();
                    _pairing = null; _inviter = false;
                    ShowPairing();
                }
                _reportStatus.Text = report.Status.Text(_app.Loc, DateTimeOffset.UtcNow);
                UpdatePairingTimer();
            };
            report.ConsentChanged += (_, _) => RenderConsent();
            _app.Settings.Changed += OnPairingSettingsChanged;
            _pairingTimer.Tick += async (_, _) =>
            {
                if (_profileBusy) return;
                if (!CanPollPairing() || _pairingMutations > 0) { UpdatePairingTimer(); return; }
                var current = _pairing!;
                var version = _pairingPollVersion.Current;
                _profileBusy = true;
                try
                {
                    var response = await report.ProfileRequestAsync("GET", "/v1/profile/pairings/" + current.Id);
                    if (response?.Status == 200 && ReportProfileJson.Pairing(response.Body, report.ProfileNow) is { } next)
                    {
                        _pairingPollVersion.TryApply(version, () =>
                        {
                            if (_pairing?.Id != current.Id) return;
                            _pairing = next with { Code = _inviter ? current.Code : null };
                            ShowPairing();
                        });
                    }
                }
                finally { _profileBusy = false; UpdatePairingTimer(); }
            };
            Closed += (_, _) =>
            {
                _pairingTimer.Stop();
                _pairingPollVersion.Next(); _pairingOperationVersion.Next();
                _app.Settings.Changed -= OnPairingSettingsChanged;
            };
        }
        _reportStatus.Text = report.Status.Text(_app.Loc, DateTimeOffset.UtcNow);
        RenderConsent();
        var url = new TextBox { Text = report.SelectedService?.Text ?? "", PlaceholderText = "https://…" };
        var key = new TextBox { PasswordChar = '●', PlaceholderText = Tr("API key (optional)", "API 密钥（可选）") };
        var panel = Stack(Text(Tr("Reporting is optional and off by default. Review the service and shared fields before starting.",
                "实时上报可选，默认关闭。开始前请核对服务地址与分享字段。"), 12),
            Toggle(Tr("Enable live reporting", "启用实时上报", "ライブ共有を有効化"), report.Preferences.Enabled, report.SetEnabled),
            _reportStatus, url, Button(Tr("Use this service", "使用此服务"), () => report.CommitCustomUrl(url.Text ?? "")),
            Button(Tr("Review service and consent", "查看服务与分享授权"), () => report.BeginConsent(false)), _consent,
            Input(Tr("Username", "用户名", "ユーザー名"), report.Preferences.Username, report.CommitUsername), key,
            Row(Button(Tr("Save API key", "保存 API 密钥"), () => { report.SetApiKey(key.Text ?? ""); key.Text = ""; }),
                Button(Tr("Clear API key", "清除 API 密钥"), report.ClearApiKey)),
            Row(Button(Tr("Pause 1 hour", "暂停一小时"), () => report.Pause(TimeSpan.FromHours(1))), Button(Tr("Resume", "恢复"), report.Resume),
                Button(Tr("Preview outgoing data", "预览上报数据"), ShowReportPreview)),
            Button(Tr("Forget this device…", "让服务忘记此设备…"), async () =>
            { if (await Confirm(Tr("Forget this device?", "让服务忘记此设备？"))) report.ForgetDevice(); }),
            Text(Tr("Profile and devices", "头像与多设备", "プロフィールとデバイス"), 20), _avatar,
            _profileStatus, Button(Tr("Refresh profile", "刷新个人资料"), () => _ = RefreshProfile()),
            Input(Tr("Display name", "显示名称"), "", v => _ = ProfileChange("PATCH", "/v1/profile", ReportJson.Object(("displayName", ReportJson.Of(v))).ToCompactBytes())),
            Row(Button(Tr("Choose avatar…", "选择头像…"), () => _ = UploadAvatar()), Button(Tr("Remove avatar", "移除头像"), () => _ = ProfileChange("DELETE", "/v1/profile/avatar"))),
            Button(Tr("Create shared identity", "创建多设备身份"), () => _ = ProfileChange("POST", "/v1/profile/identity", "{}"u8.ToArray())), _profileDevices,
            Button(Tr("Create pairing code", "生成配对码"), async () =>
            {
                var version = BeginPairingMutation();
                try
                {
                    var response = await report.ProfileRequestAsync("POST", "/v1/profile/pairings", "{}"u8.ToArray());
                    TryApplyPairingOperation(version, () =>
                    {
                        _pairing = response is { Status: 201 } created ? ReportProfileJson.Pairing(created.Body, report.ProfileNow) : null;
                        _inviter = true; ShowPairing();
                    });
                }
                finally { EndPairingMutation(); }
            }));
        var code = new TextBox { PlaceholderText = Tr("Pairing code from the original device", "原设备生成的一次性配对码") };
        panel.Children.Add(code);
        panel.Children.Add(Button(Tr("Join with code", "输入配对码加入"), async () =>
        {
            if (ReportProfileJson.NormalizeCode(code.Text) is not { } normalized) return;
            var version = BeginPairingMutation();
            try
            {
                var response = await report.ProfileRequestAsync("POST", "/v1/profile/pairings/claim", ReportJson.Object(("code", ReportJson.Of(normalized))).ToCompactBytes());
                TryApplyPairingOperation(version, () =>
                {
                    _pairing = response is { Status: 202 } claimed ? ReportProfileJson.Pairing(claimed.Body, report.ProfileNow) : null;
                    code.Text = ""; _inviter = false; ShowPairing();
                });
            }
            finally { EndPairingMutation(); }
        }));
        panel.Children.Add(Button(Tr("Approve requesting device…", "批准请求配对的设备…"), async () =>
        {
            if (!_inviter || _pairing is not { State: "pending", Claimant: { } claimant } pairing) return;
            if (!await Confirm($"{Tr("Approve pairing?", "批准此设备的配对请求？")}\n{claimant.Platform} · {claimant.DeviceId}")) return;
            if (_pairing?.Id != pairing.Id) return;
            var version = BeginPairingMutation();
            try
            {
                var response = await report.ProfileRequestAsync("POST", "/v1/profile/pairings/" + pairing.Id + "/approve", "{}"u8.ToArray());
                if (response is { Status: 200 } approved && TryApplyPairingOperation(version, () =>
                    {
                        if (_pairing?.Id != pairing.Id) return;
                        _pairing = ReportProfileJson.Pairing(approved.Body, report.ProfileNow); ShowPairing();
                    }))
                    await RefreshProfile();
            }
            finally { EndPairingMutation(); }
        }));
        panel.Children.Add(Button(Tr("Cancel pairing", "取消配对"), async () =>
        {
            if (_pairing is not { } pairing) return;
            var version = BeginPairingMutation();
            try
            {
                var result = await report.ProfileRequestAsync("DELETE", "/v1/profile/pairings/" + pairing.Id);
                if (result?.Status == 204)
                    TryApplyPairingOperation(version, () =>
                    {
                        if (_pairing?.Id != pairing.Id) return;
                        _pairing = null; _inviter = false; _profileStatus.Text = "";
                    });
            }
            finally { EndPairingMutation(); }
        }));
        panel.Children.Add(Button(Tr("Unlink this device…", "解绑此设备…"), async () =>
        { if (await Confirm(Tr("Unlink this device? Its recorded history is retained.", "解绑此设备？已入账的历史记录仍会保留。"))) await UnlinkIdentity(); }));
        return panel;
    }
    private void RenderConsent()
    {
        var report = _app.Report;
        if (report.Consent is not { } draft) { _consent.Content = null; return; }
        var username = new TextBox { Text = draft.Username };
        var apiKey = new TextBox { PasswordChar = '●', Text = draft.ApiKey };
        var checks = Stack(Text(draft.Discovery.Name, 18), Text(draft.Service.Text, 12), username, apiKey);
        var sections = draft.Sections.ToHashSet();
        foreach (var section in Enum.GetValues<ReportSection>())
            checks.Children.Add(Toggle(section.ToString(), sections.Contains(section), v => { if (v) sections.Add(section); else sections.Remove(section); }));
        checks.Children.Add(Text(draft.Failure is { } failure ? L(failure) : "", 12));
        checks.Children.Add(Row(Button(Tr("Start sharing", "开始分享", "共有を開始"), () =>
        {
            report.EditConsent(d => d with { Username = username.Text ?? "", ApiKey = apiKey.Text ?? "", Sections = sections });
            report.StartReporting(); apiKey.Text = "";
        }), Button(Tr("Cancel", "取消", "キャンセル"), report.CancelConsent)));
        _consent.Content = Card(checks);
    }
    private void ShowReportPreview()
    {
        var report = _app.Report; report.OpenPreview();
        var box = new TextBox { Text = report.Preview()?.CopyText ?? "", IsReadOnly = true, AcceptsReturn = true };
        var window = new Window { Title = Tr("Outgoing report", "上报数据预览"), Width = 720, Height = 600, Content = box };
        window.Closed += (_, _) => report.ClosePreview(); window.Show(this);
    }
    private void ShowPairing()
    {
        _profileStatus.Text = _pairing is { } p ? $"{p.State}   {p.Code}\n{p.Claimant?.Platform}   {p.ExpiresAt:t}" : Tr("Pairing unavailable.", "暂时无法配对。");
        UpdatePairingTimer();
    }

    private void OnPairingSettingsChanged(object? sender, AgentSpeed.Services.SettingsChangedEventArgs e) => UpdatePairingTimer();

    private bool CanPollPairing() => ShouldPollPairing(IsVisible, _page == "settings",
        _settingsTabs?.SelectedIndex == ReportingSettingsTabIndex, _app.Report.NetworkEnabled, _pairing, _app.Report.ProfileNow);

    internal static bool ShouldPollPairing(bool windowVisible, bool settingsPage, bool reportingTabSelected, bool networkEnabled,
        ReportPairing? pairing, DateTimeOffset now) =>
        windowVisible && settingsPage && reportingTabSelected && networkEnabled
        && pairing is { State: "waiting" or "pending" } active && active.ExpiresAt > now;

    private void UpdatePairingTimer()
    {
        if (!CanPollPairing())
        {
            _pairingTimer.Stop();
            _pairingPollVersion.Next();
        }
        else if (_pairingMutations > 0) _pairingTimer.Stop();
        else _pairingTimer.Start();
    }

    private long BeginPairingMutation()
    {
        _pairingMutations++;
        _pairingPollVersion.Next();
        var version = _pairingOperationVersion.Next();
        UpdatePairingTimer();
        return version;
    }

    private bool TryApplyPairingOperation(long version, Action apply) =>
        _pairingOperationVersion.TryApply(version, () =>
        {
            _pairingPollVersion.Next();
            apply();
        });

    private void EndPairingMutation()
    {
        _pairingMutations--;
        UpdatePairingTimer();
    }

    private async Task UnlinkIdentity()
    {
        var version = BeginPairingMutation();
        try
        {
            var response = await _app.Report.ProfileRequestAsync("DELETE", "/v1/profile/identity");
            if (response?.Status is >= 200 and < 300)
            {
                if (TryApplyPairingOperation(version, () =>
                    {
                        _pairing = null; _inviter = false; ShowPairing();
                    }))
                    await RefreshProfile();
            }
            else
            {
                _pairingOperationVersion.TryApply(version, () => _profileStatus.Text = L("profile.failed"));
            }
        }
        finally { EndPairingMutation(); }
    }
    private async Task RefreshProfile()
    {
        var report = _app.Report;
        if (!report.CanManageProfile) { _profileStatus.Text = Tr("Enable reporting with a service that supports profiles.", "请先向支持个人资料的服务启用上报。"); return; }
        var response = await report.ProfileRequestAsync("GET", "/v1/profile");
        var profile = response?.Status == 200 ? ReportProfileJson.Profile(response.Body, report.UsernameRules) : null;
        if (profile is null) { _profileStatus.Text = L("profile.failed"); return; }
        _profileStatus.Text = profile.DisplayName;
        _profileDevices.Children.Clear();
        foreach (var device in profile.Devices) _profileDevices.Children.Add(Text($"{device.Platform} · {(device.IsCurrent ? Tr("This device", "此设备") : device.DeviceId)} · {(device.Online ? "●" : "○")}", 12));
        if (profile.Avatar is not null)
        {
            var avatar = await report.ProfileRequestAsync("GET", "/v1/profile/avatar");
            if (avatar?.Status == 200 && ReportAvatarImage.IsUpload(avatar.Body))
            {
                using var stream = new MemoryStream(avatar.Body);
                var prior = _avatar.Source as IDisposable; _avatar.Source = new Avalonia.Media.Imaging.Bitmap(stream); prior?.Dispose();
            }
        }
        else { var prior = _avatar.Source as IDisposable; _avatar.Source = null; prior?.Dispose(); }
    }
    private async Task ProfileChange(string method, string path, byte[]? body = null)
    {
        var result = await _app.Report.ProfileRequestAsync(method, path, body);
        if (result?.Status is >= 200 and < 300) await RefreshProfile(); else _profileStatus.Text = L("profile.failed");
    }
    private async Task UploadAvatar()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false,
            FileTypeFilter = [new("PNG / JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }] });
        if (files.Count == 0) return;
        await using var input = await files[0].OpenReadAsync();
        if (input.Length > ReportAvatarImage.MaxInputBytes) { _profileStatus.Text = L("profile.failed"); return; }
        using var memory = new MemoryStream(); await input.CopyToAsync(memory);
        var bytes = memory.ToArray(); var header = ReportAvatarImage.ReadHeader(bytes);
        if (header is not { Width: > 0 and <= 8192, Height: > 0 and <= 8192 }) { _profileStatus.Text = L("profile.failed"); return; }
        using var image = SkiaSharp.SKBitmap.Decode(bytes); if (image is null) return;
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256, 256));
        var side = Math.Min(image.Width, image.Height);
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        using var paint = new SkiaSharp.SKPaint { IsAntialias = true };
        surface.Canvas.DrawBitmap(image, new SkiaSharp.SKRect((image.Width - side) / 2f, (image.Height - side) / 2f, (image.Width + side) / 2f, (image.Height + side) / 2f), new(0, 0, 256, 256), paint);
        using var snapshot = surface.Snapshot(); using var png = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var upload = png.ToArray();
        if (ReportAvatarImage.IsUpload(upload) && await Confirm(Tr("Upload this avatar to your reporting service?", "将所选头像上传到上报服务？")))
            await ProfileChange("PUT", "/v1/profile/avatar", upload);
    }
    private async Task<bool> Confirm(string text)
    {
        var dialog = new Window { Title = "Agent Speed", Width = 420, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new Border { Padding = new Avalonia.Thickness(20), Child = Stack(Text(text), Row(
            Button(Tr("Confirm", "确认", "確認"), () => dialog.Close(true)), Button(Tr("Cancel", "取消", "キャンセル"), () => dialog.Close(false)))) };
        return await dialog.ShowDialog<bool>(this);
    }
}
