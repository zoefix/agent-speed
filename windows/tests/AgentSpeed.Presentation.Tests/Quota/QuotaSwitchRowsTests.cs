using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Quota;

/// <summary>The phase 2 quota switches of Settings → Data Sources (DESIGN 5, 額度, phase 2).</summary>
public sealed class QuotaSwitchRowsTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static Localizer Loc(string language) => new(Table, language);

    [Fact]
    public void EveryRouteHasOneSwitchInItsSourcesGroup()
    {
        var placed = LocationFolders.All.SelectMany(folder => QuotaSwitchRows.Of(folder).Select(route => (folder, route))).ToList();
        Assert.Equal(QuotaRoutes.All.Order(), placed.Select(p => p.route).Order());
        foreach (var (folder, route) in placed)
        {
            Assert.Contains(route.Source(), folder.Sources());
        }

        Assert.Equal([QuotaRoute.GrokBuild, QuotaRoute.GrokBuildLive], QuotaSwitchRows.Of(LocationFolder.GrokBuild));
        Assert.Equal([QuotaRoute.Qoder], QuotaSwitchRows.Of(LocationFolder.Qoder));
        Assert.Empty(QuotaSwitchRows.Of(LocationFolder.QoderCli));
        Assert.Empty(QuotaSwitchRows.Of(LocationFolder.WorkBuddyAi));
        Assert.Empty(QuotaSwitchRows.Of(LocationFolder.Codex));
        Assert.Empty(QuotaSwitchRows.Of(LocationFolder.Claude));
    }

    [Fact]
    public void EveryTitleAndCaptionReadsInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = Loc(language);
            foreach (var route in QuotaRoutes.All)
            {
                var key = QuotaSwitchRows.TitleKey(route);
                Assert.True(Table.Contains(key), key);
                var hint = QuotaSwitchRows.Hint(route, loc);
                Assert.DoesNotContain("%@", hint, StringComparison.Ordinal);
                Assert.DoesNotContain("settings.quota", hint, StringComparison.Ordinal);
                Assert.Equal(route == QuotaRoute.GrokBuildLive, hint.Length == 0);
                Assert.Equal(route != QuotaRoute.GrokBuildLive, QuotaSwitchRows.HasNote(route));
                Assert.Equal(route == QuotaRoute.GrokBuildLive, QuotaSwitchRows.IsIndented(route));
            }
        }
    }

    [Fact]
    public void TheBrowserRoutesNameTheirSiteAndHost()
    {
        var en = Loc(LanguageCodes.En);
        Assert.Equal("Reads your Qoder sign-in from the browsers on this computer to ask qoder.com for your quota; kept in memory and dropped after use.",
            QuotaSwitchRows.Hint(QuotaRoute.Qoder, en));
        Assert.Contains("www.workbuddy.cn", QuotaSwitchRows.Hint(QuotaRoute.WorkBuddy, Loc(LanguageCodes.ZhHant)), StringComparison.Ordinal);
        Assert.Equal(("Qoder", "qoder.com"), QuotaSwitchRows.Site(QuotaRoute.Qoder));
        Assert.Equal(("WorkBuddy", "www.workbuddy.cn"), QuotaSwitchRows.Site(QuotaRoute.WorkBuddy));
        Assert.All(QuotaRoutes.All.Where(r => !r.ReadsBrowser()), r => Assert.Null(QuotaSwitchRows.Site(r)));
    }

    [Fact]
    public void ZcodeSaysWindowsKeepsTheKey()
    {
        Assert.Contains("認證管理員", QuotaSwitchRows.Hint(QuotaRoute.Zcode, Loc(LanguageCodes.ZhHant)), StringComparison.Ordinal);
        Assert.Contains("凭据管理器", QuotaSwitchRows.Hint(QuotaRoute.Zcode, Loc(LanguageCodes.ZhHans)), StringComparison.Ordinal);
        Assert.Contains("Credential Manager", QuotaSwitchRows.Hint(QuotaRoute.Zcode, Loc(LanguageCodes.En)), StringComparison.Ordinal);
        Assert.Contains("資格情報マネージャー", QuotaSwitchRows.Hint(QuotaRoute.Zcode, Loc(LanguageCodes.Ja)), StringComparison.Ordinal);
        foreach (var language in LanguageCodes.All)
        {
            var hint = QuotaSwitchRows.Hint(QuotaRoute.Zcode, Loc(language));
            Assert.DoesNotContain("鑰匙圈", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("钥匙串", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("keychain", hint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("キーチェーン", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWindowsConsentNamesTheSiteAndWhatChromeAndEdgeAllow()
    {
        foreach (var language in LanguageCodes.All)
        {
            var message = Loc(language).Format("win.settings.quota.cookies.confirm.message", "Qoder", "qoder.com");
            Assert.Contains("Qoder", message, StringComparison.Ordinal);
            Assert.Contains("qoder.com", message, StringComparison.Ordinal);
            Assert.Contains("Firefox", message, StringComparison.Ordinal);
            Assert.Contains("Edge", message, StringComparison.Ordinal);
            Assert.DoesNotContain("macOS", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GroksLiveRequestKeepsItsOwnValueButNeedsTheLogsSwitch()
    {
        var settings = AppSettings.Default with { Quota = QuotaSettings.Default with { GrokBuild = false, GrokBuildLive = true } };
        Assert.True(QuotaSwitchRows.StoredOn(QuotaRoute.GrokBuildLive, settings.Quota));
        Assert.False(QuotaSwitchRows.StoredOn(QuotaRoute.GrokBuild, settings.Quota));
        Assert.False(QuotaSwitchRows.CanSwitch(QuotaRoute.GrokBuildLive, settings));
        Assert.True(QuotaSwitchRows.CanSwitch(QuotaRoute.GrokBuild, settings));
        Assert.True(QuotaSwitchRows.CanSwitch(QuotaRoute.GrokBuildLive, settings with { Quota = settings.Quota with { GrokBuild = true } }));
    }

    [Fact]
    public void ADisabledSourceCannotBeSwitched()
    {
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Kimi, false).WithSourceEnabled(SourceKind.GrokBuild, false);
        Assert.False(QuotaSwitchRows.CanSwitch(QuotaRoute.Kimi, settings));
        Assert.False(QuotaSwitchRows.CanSwitch(QuotaRoute.GrokBuild, settings));
        Assert.False(QuotaSwitchRows.CanSwitch(QuotaRoute.GrokBuildLive, settings));
        Assert.True(QuotaSwitchRows.CanSwitch(QuotaRoute.Gemini, settings));
        Assert.All(QuotaRoutes.All, r => Assert.True(QuotaSwitchRows.CanSwitch(r, AppSettings.Default)));
    }

    [Fact]
    public void QodersSwitchCountsWhileEitherQoderSourceIsOn()
    {
        var ideOff = AppSettings.Default.WithSourceEnabled(SourceKind.Qoder, false);
        Assert.True(QuotaSwitchRows.CanSwitch(QuotaRoute.Qoder, ideOff));
        Assert.True(QuotaSwitchRows.CanSwitch(QuotaRoute.Qoder, AppSettings.Default.WithSourceEnabled(SourceKind.QoderCli, false)));
        Assert.False(QuotaSwitchRows.CanSwitch(QuotaRoute.Qoder, ideOff.WithSourceEnabled(SourceKind.QoderCli, false)));
    }

    [Fact]
    public void TheStoredDefaultsShowAsTheSpecSays()
    {
        var quota = QuotaSettings.Default;
        var on = QuotaRoutes.All.Where(r => QuotaSwitchRows.StoredOn(r, quota)).ToHashSet();
        Assert.Equal(new HashSet<QuotaRoute> { QuotaRoute.GrokBuild, QuotaRoute.Devin, QuotaRoute.Antigravity, QuotaRoute.Kimi, QuotaRoute.Gemini, QuotaRoute.Droid }, on);
    }
}
