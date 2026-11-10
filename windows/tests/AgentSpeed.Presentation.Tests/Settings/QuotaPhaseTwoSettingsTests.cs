using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The phase 2 quota switches (DESIGN 5 <i>額度, phase 2</i>) and QUOTAS 9.1's rule that a disabled source reads nothing.</summary>
public sealed class QuotaPhaseTwoSettingsTests
{
    [Fact]
    public void DefaultsFollowTheRouteTable()
    {
        var quota = QuotaSettings.Default;
        Assert.True(quota.GrokBuild && quota.Devin && quota.Antigravity && quota.Kimi && quota.Gemini && quota.Droid);
        Assert.False(quota.GrokBuildLive || quota.Zcode || quota.Qoder || quota.WorkBuddy);
        Assert.Equal(ZaiRegion.Global, quota.ZcodeRegion);
        Assert.Equal(
            [QuotaRoute.GrokBuild, QuotaRoute.Devin, QuotaRoute.Antigravity, QuotaRoute.Kimi, QuotaRoute.Gemini, QuotaRoute.Droid],
            AppSettings.Default.QuotaRoutes.Order());
    }

    [Fact]
    public void TheSwitchesRoundTripUnderTheirStoredNames()
    {
        var settings = AppSettings.Default with
        {
            Quota = QuotaSettings.Default with
            {
                GrokBuildLive = true, Devin = false, Zcode = true, ZcodeRegion = ZaiRegion.BigmodelCn, Qoder = true, WorkBuddy = true, Kimi = false,
            },
        };
        var json = SettingsSerializer.Serialize(settings);
        using var saved = JsonDocument.Parse(json);
        var root = saved.RootElement;
        Assert.True(root.GetProperty("quotaGrokBuildLive").GetBoolean());
        Assert.False(root.GetProperty("quotaDevin").GetBoolean());
        Assert.Equal("bigmodel-cn", root.GetProperty("quotaZcodeRegion").GetString());
        Assert.True(root.GetProperty("quotaQoder").GetBoolean());
        Assert.True(root.GetProperty("quotaWorkBuddy").GetBoolean());
        Assert.DoesNotContain("zcode-api-key", json, StringComparison.Ordinal);
        Assert.Equal(settings.Quota, SettingsSerializer.Deserialize(json).Quota);
    }

    [Fact]
    public void MissingOrForeignValuesTakeTheirDefaults()
    {
        var settings = SettingsSerializer.Deserialize("""{"quotaZcodeRegion":"mars","quotaKimi":"yes"}""");
        Assert.Equal(ZaiRegion.Global, settings.Quota.ZcodeRegion);
        Assert.True(settings.Quota.Kimi);
    }

    [Fact]
    public void ADisabledSourceShowsNoQuotaAndAsksNothing()
    {
        var on = AppSettings.Default with { Quota = QuotaSettings.Default with { GrokBuildLive = true, Qoder = true, Zcode = true } };
        Assert.True(on.ShowsQuota(SourceKind.Kimi));
        Assert.Contains(QuotaRoute.GrokBuildLive, on.QuotaRoutes);

        var off = on.WithSourceEnabled(SourceKind.Kimi, false).WithSourceEnabled(SourceKind.GrokBuild, false).WithSourceEnabled(SourceKind.ZCode, false)
            .WithSourceEnabled(SourceKind.Codex, false);
        Assert.False(off.ShowsQuota(SourceKind.Kimi));
        Assert.False(off.ShowsQuota(SourceKind.Codex));
        Assert.DoesNotContain(QuotaRoute.Kimi, off.QuotaRoutes);
        Assert.DoesNotContain(QuotaRoute.GrokBuild, off.QuotaRoutes);
        Assert.DoesNotContain(QuotaRoute.GrokBuildLive, off.QuotaRoutes);
        Assert.DoesNotContain(QuotaRoute.Zcode, off.QuotaRoutes);

        // Qoder's one account reads while either Qoder source is enabled.
        var qoderIdeOff = on.WithSourceEnabled(SourceKind.Qoder, false);
        Assert.Contains(QuotaRoute.Qoder, qoderIdeOff.QuotaRoutes);
        Assert.DoesNotContain(QuotaRoute.Qoder, qoderIdeOff.WithSourceEnabled(SourceKind.QoderCli, false).QuotaRoutes);
    }

    [Fact]
    public void HostsReadTheSameSwitchesWithClaudeCodeTheirOwn()
    {
        var settings = AppSettings.Default with { Quota = QuotaSettings.Default with { GrokBuildLive = true, ClaudeCli = true } };
        var switches = settings.RemoteQuotaSwitches;
        Assert.True(switches.GrokLive);
        Assert.True(switches.ClaudeCode);
        Assert.False(switches.Sources.Contains(SourceKind.ClaudeCode));
        Assert.True(switches.Sources.Contains(SourceKind.Kimi));
        Assert.False(switches.Sources.Contains(SourceKind.ZCode));

        var disabled = settings.WithSourceEnabled(SourceKind.Kimi, false).WithSourceEnabled(SourceKind.ClaudeCode, false)
            .WithSourceEnabled(SourceKind.GrokBuild, false).RemoteQuotaSwitches;
        Assert.False(disabled.Sources.Contains(SourceKind.Kimi));
        Assert.False(disabled.ClaudeCode);
        Assert.False(disabled.GrokLive);

        var host = new RemoteHost("devbox", "H") { QuotaClaudeCli = true };
        var config = RemoteQuotaConfig.ForHost(host, switches, networkEnabled: true, SourceSet.Everything);
        Assert.True(config.Sources.Contains(SourceKind.ClaudeCode));
        Assert.True(config.ClaudeBridge);
    }
}
