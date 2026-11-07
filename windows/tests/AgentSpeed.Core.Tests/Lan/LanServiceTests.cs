using System.Net;
using System.Text.Json;
using AgentSpeed.Core.Lan;

namespace AgentSpeed.Core.Tests.Lan;

/// <summary>The feed's lifecycle (LAN 2.2, 3.3) and the settings document's edits (LAN 2.1).</summary>
public sealed class LanServiceTests
{
    private static readonly LanTickValues Busy = new(new LanLiveValues(42, 0, 1, 0, 1), new Dictionary<SourceKind, LanLiveValues>());

    private long _clock = 10_000;

    private LanService NewService() => new(new LanServiceOptions { BindAddress = IPAddress.Loopback, Clock = () => Interlocked.Read(ref _clock) });

    private static LanSettings ServerOn(int port = 0) => LanSettings.Default.WithServerEnabled(true) with
    {
        Server = LanSettings.Default.WithServerEnabled(true).Server with { Port = port },
    };

    [Fact]
    public void TurningTheServerOnCreatesTheKeyAndTheInstance()
    {
        var settings = LanSettings.Default.WithServerEnabled(true);
        Assert.True(LanKey.IsValid(settings.Server.Key));
        Assert.True(LanSettings.IsUuid(settings.Instance));
        Assert.Equal(settings.Server.Key, settings.WithServerEnabled(false).WithServerEnabled(true).Server.Key);
        Assert.NotEqual(settings.Server.Key, settings.WithNewKey().Server.Key);
        Assert.Equal(settings.Instance, settings.WithNewKey().Instance);
        Assert.Equal(settings, LanSettingsJson.Parse(LanSettingsJson.ToJson(settings)));
    }

    [Fact]
    public void TargetsAreAddedEditedAndLimitedToEight()
    {
        var settings = LanSettings.Default;
        for (var i = 0; i < 9; i++)
        {
            settings = settings.Saving(LanSettings.NewTarget(null, $"  device {i}  ", LanTargetKind.Udp, $" 192.168.1.{i + 1}:7247 ", "  key  ")!);
        }

        Assert.Equal(LanSettings.MaxTargets, settings.Targets.Length);
        Assert.False(settings.CanAdd);
        Assert.Equal("device 0", settings.Targets[0].Name);
        Assert.Equal("192.168.1.1:7247", settings.Targets[0].Address);
        Assert.Equal("key", settings.Targets[0].Key);
        var edited = settings.Saving(settings.Targets[3] with { Name = "Desk" });
        Assert.Equal("Desk", edited.Targets[3].Name);
        Assert.Equal(7, edited.Removing(edited.Targets[0].Id).Targets.Length);
        Assert.False(edited.SettingEnabled(edited.Targets[1].Id, false).Targets[1].Enabled);
        Assert.Null(LanSettings.NewTarget(null, "x", LanTargetKind.Http, "https://192.168.1.5/", null));
    }

    [Fact]
    public async Task TheFeedRunsWhileTheServerListensAndStopsWithTheMasterSwitch()
    {
        using var service = NewService();
        var settings = ServerOn();
        service.Apply(settings, networkEnabled: true, "Test Box");
        Assert.Equal(LanServerStatusKind.Listening, service.ServerStatus.Kind);
        service.Tick(Busy, 1_794_042_000_000);
        Assert.True(service.FeedRunning);
        Assert.Equal(1, service.Latest!.Seq);
        Interlocked.Add(ref _clock, 500);
        service.Tick(Busy, 1_794_042_000_500);
        Assert.Equal(2, service.Latest!.Seq);

        var port = service.BoundPort;
        var live = await LanTestClient.RequestAsync(port, $"GET /v1/live?key={settings.Server.Key} HTTP/1.1\r\n\r\n");
        var body = JsonDocument.Parse(live[(live.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement;
        Assert.Equal("Test Box", body.GetProperty("name").GetString());
        Assert.Equal(settings.Instance, body.GetProperty("instance").GetString());
        Assert.Equal(service.Run, body.GetProperty("run").GetString());
        Assert.Equal(2, body.GetProperty("seq").GetInt64());

        service.Apply(settings, networkEnabled: false, "Test Box");
        Assert.Equal(LanServerStatusKind.NetworkOff, service.ServerStatus.Kind);
        Assert.False(service.FeedRunning);
        Assert.Null(service.Latest);
        service.Tick(Busy, 1_794_042_001_000);
        Assert.Null(service.Latest);

        service.Apply(settings, networkEnabled: true, "Test Box");
        service.Tick(Busy, 1_794_042_001_500);
        Assert.Equal(3, service.Latest!.Seq);
        Assert.Single(service.Latest.History);
    }

    [Fact]
    public void TheFeedStopsWhenNothingWantsIt()
    {
        using var service = NewService();
        var target = LanSettings.NewTarget(null, string.Empty, LanTargetKind.Udp, "127.0.0.1:9", null)!;
        var settings = LanSettings.Default.Saving(target);
        service.Apply(settings, networkEnabled: true, "Box");
        service.Tick(Busy, 0);
        Assert.True(service.FeedRunning);
        service.Apply(settings.SettingEnabled(target.Id, false), networkEnabled: true, "Box");
        Assert.False(service.FeedRunning);
        Assert.Equal(LanTargetStatusKind.Off, service.TargetStatus(target.Id).Kind);
        Assert.Equal(LanServerStatusKind.Off, service.ServerStatus.Kind);
    }

    [Fact]
    public void ComputerNamesAreTrimmedAndCut()
    {
        Assert.Equal(LanText.DefaultComputerName, LanText.ComputerName("  "));
        Assert.Equal(64, LanText.ScalarCount(LanText.ComputerName(new string('名', 70))));
        Assert.Equal("é", LanText.CutUtf8("éa", 2));
        Assert.Equal(string.Empty, LanText.CutUtf8("名", 2));
    }
}
