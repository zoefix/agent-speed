using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Core.Traffic.Helper;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Traffic on the taskbar, the dashboard, Settings and the menus (DESIGN 10).</summary>
public class TrafficSurfaceTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer Hant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static TrafficSnapshot Snapshot(ProcessTrafficState? state = null) => new()
    {
        Enabled = true,
        MachineLive = new TrafficRate(1_280_000, 64_000),
        ProcessState = state ?? ProcessTrafficState.Running(1),
        Sources = new Dictionary<SourceKind, SourceTraffic>
        {
            [SourceKind.Codex] = new(SourceKind.Codex, new TrafficSplit(new TrafficBytes(2_000_000, 100_000), new TrafficBytes(48_200_000, 0)),
                TrafficBytes.Zero, new TrafficRate(24_000, 1_200), new TrafficRate(400, 1_200)),
            [SourceKind.ClaudeCode] = new(SourceKind.ClaudeCode, new TrafficSplit(new TrafficBytes(9_000_000, 3_100_000), TrafficBytes.Zero),
                TrafficBytes.Zero, TrafficRate.Zero, TrafficRate.Zero),
            [SourceKind.Gemini] = new(SourceKind.Gemini, TrafficSplit.Zero, TrafficBytes.Zero, TrafficRate.Zero, TrafficRate.Zero),
        },
    };

    [Fact]
    public void TheTaskbarBlockFollowsTheSetting()
    {
        var settings = AppSettings.Default with { Traffic = TrafficSettings.Default with { Bar = BarTraffic.Machine } };
        var block = BarTrafficBlock.From(settings, Snapshot(), En)!;
        Assert.Equal("1.28", block.Rx!.Value.Number);
        Assert.Equal("MB/s", block.Rx!.Value.Unit);
        Assert.Equal("64.0", block.Tx!.Value.Number);
        Assert.Equal("downloading 1.28 MB per second, uploading 64.0 KB per second", block.Spoken);

        Assert.Null(BarTrafficBlock.From(AppSettings.Default, Snapshot(), En));
        Assert.Null(BarTrafficBlock.From(settings with { BarMode = BarDisplayMode.GlyphOnly }, Snapshot(), En));
        Assert.Null(BarTrafficBlock.From(settings with { Traffic = settings.Traffic with { Enabled = false } }, Snapshot(), En));
    }

    [Fact]
    public void TheAgentsBlockReadsDashesWithoutPerProcessData()
    {
        var settings = AppSettings.Default with { Traffic = TrafficSettings.Default with { Bar = BarTraffic.Agents } };
        var agents = BarTrafficBlock.From(settings, Snapshot(), En)!;
        Assert.Equal("24.0", agents.Rx!.Value.Number);
        Assert.Equal("KB/s", agents.Rx!.Value.Unit);

        var missing = BarTrafficBlock.From(settings, Snapshot(ProcessTrafficState.Unavailable(TrafficUnavailableReason.HelperMissing)), En)!;
        Assert.Null(missing.Rx);
        Assert.Null(missing.Tx);
        Assert.Null(missing.Spoken);

        var bits = BarTrafficBlock.From(settings with { Traffic = settings.Traffic with { Unit = TrafficUnit.Bits } }, Snapshot(), En)!;
        Assert.Equal("192", bits.Rx!.Value.Number);
        Assert.Equal("Kb/s", bits.Rx!.Value.Unit);
    }

    [Fact]
    public void DashboardRowsAreSortedByTodayAndQuietDirectionsReadDashes()
    {
        var view = DashboardTraffic.From(Snapshot(), includeTools: true, bits: false, En);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode], view.Rows.Select(r => r.Source));
        var codex = view.Rows[0];
        Assert.Equal("↓ 50.2 MB · ↑ 100 KB", codex.TodayText.Replace(TrafficFormat.NoBreakSpace, ' '));
        Assert.Equal("24.0", codex.Rx!.Value.Number);
        Assert.Equal("1.20", codex.Tx!.Value.Number);
        Assert.Null(view.Rows[1].Rx);
        Assert.Equal(En["traffic.includesTools"], view.Caption);
        Assert.Null(view.Unavailable);

        // Without tools Codex drops below Claude Code, and its download falls under 0.5 KB/s.
        var own = DashboardTraffic.From(Snapshot(), includeTools: false, bits: false, En);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex], own.Rows.Select(r => r.Source));
        Assert.Null(own.Rows[1].Rx);
        Assert.Equal(En["traffic.excludesTools"], own.Caption);
    }

    [Fact]
    public void UnavailableTrafficReplacesTheRowsWithTheReason()
    {
        var view = DashboardTraffic.From(Snapshot(ProcessTrafficState.Unavailable(TrafficUnavailableReason.HelperMissing)), true, false, Hant);
        Assert.Empty(view.Rows);
        Assert.Equal(Hant["traffic.unavailable.helperMissing"], view.Unavailable);
        Assert.Equal(Hant["traffic.helper.install"], view.HelperButton);
        Assert.Equal(string.Empty, view.Caption);
        Assert.Equal("1.28", view.MachineRx!.Value.Number);

        var outdated = DashboardTraffic.From(Snapshot(ProcessTrafficState.Unavailable(TrafficUnavailableReason.HelperOutdated)), true, false, En);
        Assert.Equal(En["traffic.helper.update"], outdated.HelperButton);
        Assert.Null(DashboardTraffic.From(Snapshot(ProcessTrafficState.Unavailable(TrafficUnavailableReason.Denied)), true, false, En).HelperButton);

        var empty = DashboardTraffic.From(new TrafficSnapshot { Enabled = true, ProcessState = ProcessTrafficState.Standby }, true, false, En);
        Assert.Equal(En["traffic.empty"], empty.Caption);
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(12_000, 20_000)]
    [InlineData(20_000, 20_000)]
    [InlineData(31_000, 50_000)]
    [InlineData(51_000, 100_000)]
    [InlineData(1_400_000, 2_000_000)]
    public void TheSparklineScaleIsAOneTwoFiveStep(double peak, double top) => Assert.Equal(top, DashboardTraffic.ScaleTop(peak));

    [Fact]
    public void TheHelperStatusNamesEachState()
    {
        var running = ProcessTrafficState.Running(1);
        var bundled = HelperProtocol.BundledVersion;
        Assert.Equal(TrafficHelperStatusKind.NotInstalled, TrafficHelperStatus.From(false, false, null, false, running, null, En).Kind);
        var notAllowed = TrafficHelperStatus.From(true, false, bundled, true, running, null, En);
        Assert.Equal(TrafficHelperStatusKind.NotInstalled, notAllowed.Kind);
        Assert.True(notAllowed.ShowsInstall);
        Assert.False(notAllowed.ShowsRemove);

        var idle = TrafficHelperStatus.From(true, true, bundled, false, ProcessTrafficState.Standby, null, En);
        Assert.Equal(En["traffic.helper.idle"], idle.Text);
        Assert.Equal(TrafficHelperDot.Neutral, idle.Dot);
        Assert.True(idle.ShowsRemove);

        var up = TrafficHelperStatus.From(true, true, bundled, true, running, null, En);
        Assert.Equal(En.Format("traffic.helper.running", bundled.ToString()), up.Text);
        Assert.Equal(TrafficHelperDot.Success, up.Dot);

        var old = TrafficHelperStatus.From(true, true, new HelperVersion(0, 9, 0), false, running, null, En);
        Assert.Equal(TrafficHelperStatusKind.Outdated, old.Kind);
        Assert.Equal(TrafficHelperDot.Caution, old.Dot);
        Assert.True(old.ShowsUpdate);

        var denied = TrafficHelperStatus.From(true, true, bundled, true, ProcessTrafficState.Unavailable(TrafficUnavailableReason.Denied), "x", En);
        Assert.Equal(En.Format("traffic.helper.error", En["traffic.unavailable.denied"]), denied.Text);
        Assert.Equal(TrafficHelperDot.Critical, denied.Dot);

        var failed = TrafficHelperStatus.From(true, true, bundled, false,
            ProcessTrafficState.Unavailable(TrafficUnavailableReason.HelperStopped), "Access is denied.", En);
        Assert.Equal(En.Format("traffic.helper.error", "Access is denied."), failed.Text);
    }

    [Fact]
    public void TrafficSettingsRoundTrip()
    {
        var settings = AppSettings.Default with
        {
            Traffic = new TrafficSettings { Enabled = false, IncludeTools = false, Unit = TrafficUnit.Bits, Bar = BarTraffic.Agents },
        };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(settings.Traffic, back.Traffic);
        Assert.Equal(TrafficSettings.Default, SettingsSerializer.Deserialize("{}").Traffic);
        Assert.Equal(BarTraffic.Off, SettingsSerializer.Deserialize("""{"traffic":{"bar":"later"}}""").Traffic.Bar);
    }

    [Fact]
    public void AnOrbsTrafficIsStoredAndOlderOrbsShowTheirSources()
    {
        Assert.Equal(OrbTraffic.Sources, OrbTraffics.Parse(null));
        Assert.Equal(OrbTraffic.Machine, OrbTraffics.Parse("machine"));
        Assert.Equal(OrbTraffic.Off, OrbTraffics.Parse("someday"));

        var orb = new OrbDefinition("A") { Traffic = OrbTraffic.Machine };
        var settings = AppSettings.Default with { Orbs = new OrbsDocument([orb]) };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(OrbTraffic.Machine, back.Orbs.Orbs[0].Traffic);

        // Add, Duplicate and Split copy it; Merge keeps the target's.
        var added = OrbEditing.Add(settings.Orbs, "A", SourceSet.Everything, () => "B")!;
        Assert.Equal(OrbTraffic.Machine, added.Document.Find("B")!.Traffic);
        var duplicated = OrbEditing.Duplicate(settings.Orbs, "A", () => "C")!;
        Assert.Equal(OrbTraffic.Machine, duplicated.Document.Find("C")!.Traffic);
        var two = OrbEditing.SetTraffic(added.Document, "B", OrbTraffic.Off);
        Assert.Equal(OrbTraffic.Off, OrbEditing.Merge(two, "B")!.Orbs[0].Traffic);
    }

    [Fact]
    public void TheOrbMenuOffersTheTrafficReadout()
    {
        var settings = AppSettings.Default with { Orbs = new OrbsDocument([new OrbDefinition("A")]) };
        var menu = OrbMenuModel.Build("A", settings, SourceAvailability.All, En)!;
        var traffic = menu.Rows.Single(r => r.Label == En["orbs.traffic"]);
        Assert.True(traffic.IsEnabled);
        var choices = traffic.Submenu!.Rows.ToArray();
        Assert.Equal([En["orbs.traffic.sources"], En["orbs.traffic.machine"], En["orbs.traffic.off"]], choices.Select(r => r.Label));
        Assert.True(choices[0].IsChecked);
        Assert.Null(choices[0].Detail);
        Assert.Equal(new MenuAction.SetOrbTraffic("A", OrbTraffic.Machine), choices[1].Action);

        var unavailable = OrbMenuModel.Build("A", settings, SourceAvailability.All, En, sourceTraffic: false)!;
        Assert.Equal(En["traffic.unavailable.short"], unavailable.Rows.Single(r => r.Label == En["orbs.traffic"]).Submenu!.Rows.First().Detail);

        var off = OrbMenuModel.Build("A", settings with { Traffic = TrafficSettings.Default with { Enabled = false } }, SourceAvailability.All, En)!;
        Assert.False(off.Rows.Single(r => r.Label == En["orbs.traffic"]).IsEnabled);
    }
}
