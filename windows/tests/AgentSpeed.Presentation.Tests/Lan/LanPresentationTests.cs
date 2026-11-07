using AgentSpeed.Core.Lan;
using AgentSpeed.Presentation.Lan;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Lan;

/// <summary>DESIGN 14's status lines and dots, and the <c>lan</c> value in the settings file (LAN 2.1).</summary>
public sealed class LanPresentationTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();
    private static readonly Localizer En = new(Table, LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Table, LanguageCodes.ZhHant);

    [Fact]
    public void ServerLines()
    {
        Assert.Equal("Waiting for devices", LanStatusText.Server(new LanServerStatus(LanServerStatusKind.Listening, 7247), En));
        Assert.Equal("Connected · 1 device", LanStatusText.Server(new LanServerStatus(LanServerStatusKind.Listening, 7247, 1), En));
        Assert.Equal("已連線 · 3 台裝置", LanStatusText.Server(new LanServerStatus(LanServerStatusKind.Listening, 7247, 3), ZhHant));
        Assert.Equal("連接埠 7247 已被其他程式使用，30 秒後重試", LanStatusText.Server(new LanServerStatus(LanServerStatusKind.PortInUse, 7247), ZhHant));
        Assert.Equal(RemoteDotKind.Progress, LanStatusText.ServerDot(new LanServerStatus(LanServerStatusKind.Listening, 7247)));
        Assert.Equal(RemoteDotKind.Connected, LanStatusText.ServerDot(new LanServerStatus(LanServerStatusKind.Listening, 7247, 2)));
        Assert.Equal(RemoteDotKind.Retrying, LanStatusText.ServerDot(new LanServerStatus(LanServerStatusKind.PortInUse, 7247)));
        Assert.Equal(RemoteDotKind.Attention, LanStatusText.ServerDot(new LanServerStatus(LanServerStatusKind.Failed, 7247)));
        Assert.Equal(RemoteDotKind.Idle, LanStatusText.ServerDot(new LanServerStatus(LanServerStatusKind.NetworkOff, 7247)));
    }

    [Fact]
    public void TargetLines()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("已送出 · 剛剛", LanStatusText.Target(new LanTargetStatus(LanTargetStatusKind.Ok, LastSuccess: now), 0, now, ZhHant));
        Assert.Equal("Sent · 12s ago", LanStatusText.Target(new LanTargetStatus(LanTargetStatusKind.Ok, LastSuccess: now.AddSeconds(-12)), 0, now, En));
        Assert.Equal("Can’t connect — retrying in 4 s", LanStatusText.Target(new LanTargetStatus(LanTargetStatusKind.Unreachable, RetryAtMs: 4000), 1, now, En));
        Assert.Equal("The device answered 404", LanStatusText.Target(new LanTargetStatus(LanTargetStatusKind.HttpStatus, 404), 0, now, En));
        Assert.Equal("Can’t reach the host", LanStatusText.Test(new LanTargetStatus(LanTargetStatusKind.Unreachable), 0, En));
        Assert.Equal("無法連線", LanStatusText.Test(new LanTargetStatus(LanTargetStatusKind.Unreachable), 0, ZhHant));
        Assert.Equal("Sent", LanStatusText.Test(new LanTargetStatus(LanTargetStatusKind.Ok), 0, En));
        Assert.Equal(RemoteDotKind.Attention, LanStatusText.TargetDot(new LanTargetStatus(LanTargetStatusKind.NotLocal)));
        Assert.Equal(RemoteDotKind.Retrying, LanStatusText.TargetDot(new LanTargetStatus(LanTargetStatusKind.Failed)));
        Assert.Equal(RemoteDotKind.Idle, LanStatusText.TargetDot(LanTargetStatus.Off));
    }

    [Fact]
    public void TheLanValueRoundTripsThroughTheSettingsFile()
    {
        var lan = LanSettings.Default.WithServerEnabled(true)
            .Saving(LanSettings.NewTarget(null, "Desk", LanTargetKind.Udp, "192.168.1.255:7247", "s3cret")!);
        var settings = AppSettings.Default with { Lan = lan };
        var text = SettingsSerializer.Serialize(settings);
        Assert.Contains("\"lan\"", text, StringComparison.Ordinal);
        Assert.Equal(lan, SettingsSerializer.Deserialize(text).Lan);
        Assert.DoesNotContain("\"lan\"", SettingsSerializer.Serialize(AppSettings.Default), StringComparison.Ordinal);
        Assert.Equal(LanSettings.Default, SettingsSerializer.Deserialize("{\"lan\":{\"version\":2}}").Lan);
    }
}
