using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Tests.Quota;

/// <summary>The status lines under the phase 2 quota switches (DESIGN 5, 額度, phase 2).</summary>
public sealed class QuotaStatusTextTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private const long Minute = 60_000;

    // 2026-11-10 06:00 UTC.
    private const long Now = 1_794_290_400_000;

    private static Localizer Loc(string language) => new(Table, language);

    private static readonly QuotaRouteStatus[] EveryKind =
    [
        .. Enum.GetValues<QuotaStatusKind>().Select(kind => kind switch
        {
            QuotaStatusKind.Ok => QuotaRouteStatus.Ok(Now - 4 * Minute),
            QuotaStatusKind.Denied => QuotaRouteStatus.Denied(QuotaHosts.Gemini),
            _ => QuotaRouteStatus.Of(kind),
        }),
    ];

    [Fact]
    public void EveryKindReadsInEveryLanguageForEveryRoute()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = Loc(language);
            foreach (var route in QuotaRoutes.All)
            {
                foreach (var status in EveryKind)
                {
                    var text = QuotaStatusText.Text(status, route, Now, loc, zone: TimeZoneInfo.Utc);
                    Assert.False(string.IsNullOrWhiteSpace(text));
                    Assert.DoesNotContain("%@", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("settings.quota", text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void EveryKindHasItsOwnSentence()
    {
        var loc = Loc(LanguageCodes.En);
        var texts = EveryKind.Select(s => QuotaStatusText.Text(s, QuotaRoute.Kimi, Now, loc, zone: TimeZoneInfo.Utc)).ToList();
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheWindowsBrowserStatusesFollowTheLanguage()
    {
        foreach (var kind in new[] { QuotaStatusKind.BrowserLocked, QuotaStatusKind.BrowserProtected })
        {
            var texts = LanguageCodes.All.Select(l => QuotaStatusText.Text(QuotaRouteStatus.Of(kind), QuotaRoute.Qoder, Now, Loc(l))).ToList();
            Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
            Assert.All(texts, t => Assert.Contains("Firefox", t, StringComparison.Ordinal));
        }

        Assert.Equal("The browser locks its cookies while it runs; close it or use Firefox",
            QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.BrowserLocked), QuotaRoute.WorkBuddy, Now, Loc(LanguageCodes.En)));
        Assert.Equal("Chrome／Edge 的 Cookie 受應用程式綁定加密保護，無法讀取；可改用 Firefox",
            QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.BrowserProtected), QuotaRoute.Qoder, Now, Loc(LanguageCodes.ZhHant)));
    }

    [Fact]
    public void NamesTheAgentTheSiteAndTheHost()
    {
        var en = Loc(LanguageCodes.En);
        var zh = Loc(LanguageCodes.ZhHant);
        Assert.Equal("The sign-in has expired; waiting for Kimi Code CLI to renew it",
            QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.Expired), QuotaRoute.Kimi, Now, en));
        Assert.Equal("登入已過期，等 Qoder 重新整理後再查詢", QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.Expired), QuotaRoute.Qoder, Now, zh));
        Assert.Equal("Antigravity 沒有在執行", QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.NotRunning), QuotaRoute.Antigravity, Now, zh));
        Assert.Equal("Grok Build is not running", QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.NotRunning), QuotaRoute.GrokBuildLive, Now, en));
        Assert.Equal("No Qoder sign-in found in your browsers", QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.NoBrowser), QuotaRoute.Qoder, Now, en));
        Assert.Equal("在瀏覽器裡找不到 WorkBuddy 的登入", QuotaStatusText.Text(QuotaRouteStatus.Of(QuotaStatusKind.NoBrowser), QuotaRoute.WorkBuddy, Now, zh));
        Assert.Equal("cloudcode-pa.googleapis.com 拒絕了請求，登入變更後再試",
            QuotaStatusText.Text(QuotaRouteStatus.Denied(QuotaHosts.Gemini), QuotaRoute.Gemini, Now, zh));
    }

    [Fact]
    public void ADenialWithoutItsHostNamesTheRoutesHost()
    {
        var en = Loc(LanguageCodes.En);
        var denied = QuotaRouteStatus.Of(QuotaStatusKind.Denied);
        Assert.Equal("open.bigmodel.cn refused the request; trying again once the sign-in changes",
            QuotaStatusText.Text(denied, QuotaRoute.Zcode, Now, en, ZaiRegion.BigmodelCn));
        Assert.StartsWith(QuotaHosts.Zai + " ", QuotaStatusText.Text(denied, QuotaRoute.Zcode, Now, en), StringComparison.Ordinal);
        Assert.StartsWith(QuotaHosts.Factory + " ", QuotaStatusText.Text(denied, QuotaRoute.Droid, Now, en), StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessReadsTheTimeTodayAndTheDateOtherwise()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = Loc(language);
            var today = Now - 4 * Minute;
            var earlier = Now - 2 * 24 * 60 * Minute;
            var todayText = QuotaStatusText.Text(QuotaRouteStatus.Ok(today), QuotaRoute.Devin, Now, loc, zone: TimeZoneInfo.Utc);
            var earlierText = QuotaStatusText.Text(QuotaRouteStatus.Ok(earlier), QuotaRoute.Devin, Now, loc, zone: TimeZoneInfo.Utc);
            Assert.Equal(loc.Format("settings.quota.status.ok", QuotaText.When(today, Now, loc, TimeZoneInfo.Utc)), todayText);
            Assert.Equal(loc.Format("settings.quota.status.ok", QuotaText.When(earlier, Now, loc, TimeZoneInfo.Utc)), earlierText);
            Assert.DoesNotContain("2026", todayText, StringComparison.Ordinal);
            Assert.Contains("2026", earlierText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AStatusShowsOnlyWhileTheRouteRuns()
    {
        var failed = QuotaRouteStatus.Of(QuotaStatusKind.Failed);
        Assert.Null(QuotaStatusText.Shown(QuotaRoute.Kimi, failed, runs: false, networkEnabled: true, zcodeKeySaved: true));
        Assert.Null(QuotaStatusText.Shown(QuotaRoute.Kimi, failed, runs: false, networkEnabled: false, zcodeKeySaved: true));
        Assert.Equal(failed, QuotaStatusText.Shown(QuotaRoute.Kimi, failed, runs: true, networkEnabled: true, zcodeKeySaved: false));
        Assert.Null(QuotaStatusText.Shown(QuotaRoute.Kimi, null, runs: true, networkEnabled: true, zcodeKeySaved: false));
    }

    [Fact]
    public void ZcodeSaysNothingBeforeItsKeyIsSaved()
    {
        var ok = QuotaRouteStatus.Ok(Now);
        Assert.Null(QuotaStatusText.Shown(QuotaRoute.Zcode, ok, runs: true, networkEnabled: true, zcodeKeySaved: false));
        Assert.Null(QuotaStatusText.Shown(QuotaRoute.Zcode, ok, runs: true, networkEnabled: false, zcodeKeySaved: false));
        Assert.Equal(ok, QuotaStatusText.Shown(QuotaRoute.Zcode, ok, runs: true, networkEnabled: true, zcodeKeySaved: true));
    }

    [Fact]
    public void TheNetworkSwitchSpeaksForEveryRouteThatAsksOverTheNetwork()
    {
        var ok = QuotaRouteStatus.Ok(Now);
        foreach (var route in QuotaRoutes.All)
        {
            var shown = QuotaStatusText.Shown(route, ok, runs: true, networkEnabled: false, zcodeKeySaved: true);
            Assert.Equal(route.IsLocal() ? ok : QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff), shown);
            Assert.Equal(route.IsLocal() ? null : QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff),
                QuotaStatusText.Shown(route, null, runs: true, networkEnabled: false, zcodeKeySaved: true));
        }
    }

    [Fact]
    public void ProblemsLeadWithTheCautionGlyph()
    {
        Assert.Equal(QuotaStatusGlyph.Ok, QuotaStatusText.Glyph(QuotaRouteStatus.Ok(Now)));
        Assert.Equal(QuotaStatusGlyph.Paused, QuotaStatusText.Glyph(QuotaRouteStatus.Of(QuotaStatusKind.NotRunning)));
        Assert.Equal(QuotaStatusGlyph.NetworkOff, QuotaStatusText.Glyph(QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff)));
        foreach (var status in EveryKind)
        {
            Assert.Equal(status.IsProblem, QuotaStatusText.Glyph(status) == QuotaStatusGlyph.Problem);
        }
    }

    [Fact]
    public void QoderIsNamedForBothOfItsSources()
    {
        Assert.Equal("Qoder", QuotaStatusText.AgentName(QuotaRoute.Qoder, Loc(LanguageCodes.En)));
        Assert.Equal("Factory Droid", QuotaStatusText.AgentName(QuotaRoute.Droid, Loc(LanguageCodes.Ja)));
        Assert.Equal("Grok Build", QuotaStatusText.AgentName(QuotaRoute.GrokBuildLive, Loc(LanguageCodes.ZhHans)));
    }
}
