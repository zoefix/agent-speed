using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Tests.Quota.Network;
using static AgentSpeed.Core.Tests.Quota.Routes.QuotaVendorHarness;

namespace AgentSpeed.Core.Tests.Quota.Routes;

/// <summary>The rules of QUOTAS 9.1 and NETWORK 9 for the vendor readers, with a scripted transport.</summary>
public sealed class QuotaVendorServiceTests
{
    private const long Minute = 60_000;

    private static QuotaVendorHarness Harness()
    {
        var h = new QuotaVendorHarness();
        h.Scripted.Answer = r => r.Url.AbsolutePath switch
        {
            "/coding/v1/usages" => ScriptedQuotaTransport.Json(Body("kimiCurrent")),
            "/v1internal:loadCodeAssist" => ScriptedQuotaTransport.Json(Body("geminiProject")),
            "/v1internal:retrieveUserQuota" => ScriptedQuotaTransport.Json(Body("gemini")),
            "/api/billing/limits" => ScriptedQuotaTransport.Json(Body("droid")),
            "/api/monitor/usage/quota/limit" => ScriptedQuotaTransport.Json(Body("zcode")),
            "/v1/billing" => ScriptedQuotaTransport.Json(Body("grokApi")),
            "/api/v2/me/usages/big_model_credits" => ScriptedQuotaTransport.Json(Body("qoder")),
            "/billing/meter/get-user-resource-summary" => ScriptedQuotaTransport.Json(Body("workbuddy")),
            _ when r.IsLoopback => ScriptedQuotaTransport.Json(Body("antigravity")),
            _ => QuotaHttpResponse.Of(404),
        };
        h.Browser.Sessions["qoder.com"] = new BrowserSession("qoder.com", "sid=a1", "UA-browser", BrowserKind.Firefox);
        h.Browser.Sessions["workbuddy.cn"] = new BrowserSession("workbuddy.cn", "wb=1", "UA-browser", BrowserKind.Firefox);
        h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("http", 42100, "csrf-1")]));
        return h;
    }

    [Fact]
    public async Task ADisabledOrSwitchedOffRouteReadsNothing()
    {
        using var h = Harness();
        var readings = await h.RoundAsync(On(), dashboard: true, local: true);
        Assert.Empty(readings.Routes);
        Assert.Empty(h.Scripted.Requests);
        Assert.Equal(0, h.VariableReads);
        Assert.Equal(0, h.Keys.Reads);
        Assert.Equal(0, h.Browser.Reads);
        Assert.Equal(0, h.Locator.Calls);

        // Once on, the same routes ask.
        readings = await h.RoundAsync(AllRoutes, dashboard: true, local: true);
        Assert.NotNull(readings.Snapshot(SourceKind.Kimi));
        Assert.True(h.Scripted.Count(QuotaHosts.Kimi) == 1);

        // Off again: the state goes with it, and nothing more is read.
        var before = (h.Scripted.Requests.Count, h.VariableReads, h.Keys.Reads, h.Browser.Reads, h.Locator.Calls);
        h.Now += 20 * Minute;
        readings = await h.RoundAsync(On(), dashboard: true, local: true);
        Assert.Empty(readings.Routes);
        Assert.Equal(before, (h.Scripted.Requests.Count, h.VariableReads, h.Keys.Reads, h.Browser.Reads, h.Locator.Calls));
    }

    [Fact]
    public async Task NetworkOffAsksNothingAndReadsNoCredential()
    {
        using var h = Harness();
        var routes = QuotaRoutes.All.Where(r => !r.IsLocal()).ToHashSet();
        var readings = await h.RoundAsync(routes, network: false, dashboard: true);
        Assert.Empty(h.Scripted.Requests);
        Assert.Equal(0, h.VariableReads);
        Assert.Equal(0, h.Keys.Reads);
        Assert.Equal(0, h.Browser.Reads);
        Assert.Equal(0, h.Locator.Calls);
        foreach (var route in QuotaRoutes.All.Where(r => !r.IsLocal()))
        {
            Assert.Equal(QuotaStatusKind.NetworkOff, readings.Status(route)?.Kind);
        }
    }

    [Fact]
    public async Task NothingAsksWhileTheAgentIsNotRunningAndTheDashboardIsClosed()
    {
        using var h = Harness();
        var readings = await h.RoundAsync(AllRoutes, running: new HashSet<SourceKind>());
        Assert.Empty(h.Scripted.Requests);
        Assert.Equal(0, h.Keys.Reads);
        Assert.Equal(0, h.Browser.Reads);
        Assert.Equal(QuotaStatusKind.NotRunning, readings.Status(QuotaRoute.Kimi)?.Kind);

        readings = await h.RoundAsync(AllRoutes, running: new HashSet<SourceKind> { SourceKind.Kimi });
        Assert.Equal(1, h.Scripted.Count(QuotaHosts.Kimi));
        Assert.Single(h.Scripted.Requests);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.True(readings.SignedIn(SourceKind.Kimi));
    }

    [Fact]
    public async Task CadenceFiveMinutesFifteenAfterAFailureAndNeverAfterARefusal()
    {
        using var h = Harness();
        var kimi = On(QuotaRoute.Kimi);
        await h.RoundAsync(kimi);
        h.Now += Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(1, h.Scripted.Count(QuotaHosts.Kimi));
        h.Now += 4 * Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(2, h.Scripted.Count(QuotaHosts.Kimi));

        h.Scripted.Answer = _ => QuotaHttpResponse.Of(500);
        h.Now += 5 * Minute;
        var readings = await h.RoundAsync(kimi);
        Assert.Equal(3, h.Scripted.Count(QuotaHosts.Kimi));
        Assert.Equal(QuotaStatusKind.Failed, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.NotNull(readings.Snapshot(SourceKind.Kimi));
        h.Now += 10 * Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(3, h.Scripted.Count(QuotaHosts.Kimi));
        h.Now += 5 * Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(4, h.Scripted.Count(QuotaHosts.Kimi));

        h.Scripted.Answer = _ => QuotaHttpResponse.Of(401);
        h.Now += 15 * Minute;
        readings = await h.RoundAsync(kimi);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.Equal(QuotaHosts.Kimi, readings.Status(QuotaRoute.Kimi)?.Host);
        h.Now += 60 * Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(5, h.Scripted.Count(QuotaHosts.Kimi));

        // The same token written again is still refused; a new one asks at once.
        h.WriteKimi("kimi-tok", (h.Now / 1000) + 7200);
        h.Now += Minute;
        await h.RoundAsync(kimi);
        Assert.Equal(5, h.Scripted.Count(QuotaHosts.Kimi));
        h.Scripted.Answer = _ => ScriptedQuotaTransport.Json(Body("kimiCurrent"));
        h.WriteKimi("kimi-tok-2", (h.Now / 1000) + 7200);
        readings = await h.RoundAsync(kimi);
        Assert.Equal(6, h.Scripted.Count(QuotaHosts.Kimi));
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.Equal("Bearer kimi-tok-2", h.Scripted.Requests[^1].Header("Authorization"));
    }

    [Fact]
    public async Task ARedirectOrATimeoutIsAFailure()
    {
        using var h = Harness();
        h.Scripted.Answer = _ => throw new QuotaHttpException(QuotaHttpFailure.Redirected, "redirect refused");
        var readings = await h.RoundAsync(On(QuotaRoute.Droid));
        Assert.Equal(QuotaStatusKind.Failed, readings.Status(QuotaRoute.Droid)?.Kind);
        Assert.Null(readings.Snapshot(SourceKind.Droid));
    }

    [Fact]
    public async Task AnExpiredTokenAsksNothingAndKeepsTheLastSnapshot()
    {
        using var h = Harness();
        var kimi = On(QuotaRoute.Kimi);
        await h.RoundAsync(kimi);
        h.WriteKimi("kimi-tok", (h.Now / 1000) - 10);
        h.Now += 6 * Minute;
        var readings = await h.RoundAsync(kimi);
        Assert.Equal(1, h.Scripted.Count(QuotaHosts.Kimi));
        Assert.Equal(QuotaStatusKind.Expired, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.False(readings.SignedIn(SourceKind.Kimi));
        Assert.NotNull(readings.Snapshot(SourceKind.Kimi));
    }

    [Fact]
    public async Task GroksTokenIsNotUsedWithinAMinuteOfItsExpiry()
    {
        using var h = Harness();
        h.WriteGrok("grok-key", DateTimeOffset.FromUnixTimeMilliseconds(h.Now + 30_000).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        var readings = await h.RoundAsync(On(QuotaRoute.GrokBuild, QuotaRoute.GrokBuildLive));
        Assert.Equal(0, h.Scripted.Count(QuotaHosts.Grok));
        Assert.Equal(QuotaStatusKind.Expired, readings.Status(QuotaRoute.GrokBuildLive)?.Kind);

        h.WriteGrok("grok-key", "2026-11-12T00:00:00Z");
        readings = await h.RoundAsync(On(QuotaRoute.GrokBuild, QuotaRoute.GrokBuildLive));
        var request = Assert.Single(h.Scripted.Requests);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/billing?format=credits", request.Url.AbsoluteUri);
        Assert.Equal("Bearer grok-key", request.Header("Authorization"));
        Assert.Equal("xai-grok-cli", request.Header("x-xai-token-auth"));
        Assert.NotNull(readings.Snapshot(SourceKind.GrokBuild));
    }

    [Fact]
    public async Task AnotherAccountDropsTheOldNumbers()
    {
        using var h = Harness();
        var gemini = On(QuotaRoute.Gemini);
        Assert.NotNull((await h.RoundAsync(gemini)).Snapshot(SourceKind.Gemini));
        h.Scripted.Answer = _ => QuotaHttpResponse.Of(500);
        h.WriteGemini("other-account", h.Now + 7_200_000);
        var readings = await h.RoundAsync(gemini);
        Assert.Null(readings.Snapshot(SourceKind.Gemini));
        Assert.Equal(QuotaStatusKind.Failed, readings.Status(QuotaRoute.Gemini)?.Kind);
    }

    [Fact]
    public async Task GeminiLearnsItsProjectOncePerToken()
    {
        using var h = Harness();
        var gemini = On(QuotaRoute.Gemini);
        await h.RoundAsync(gemini);
        h.Now += 5 * Minute;
        await h.RoundAsync(gemini);
        var paths = h.Scripted.Requests.Select(r => r.Url.AbsolutePath).ToList();
        Assert.Equal(["/v1internal:loadCodeAssist", "/v1internal:retrieveUserQuota", "/v1internal:retrieveUserQuota"], paths);
        Assert.Equal("""{"project":"proj-123"}""", System.Text.Encoding.UTF8.GetString(h.Scripted.Requests[^1].Body!));
    }

    [Fact]
    public async Task TheZcodeKeyIsReadOnlyWhenItsRequestIsDue()
    {
        using var h = Harness();
        var zcode = On(QuotaRoute.Zcode);
        var readings = await h.RoundAsync(zcode);
        Assert.Equal(1, h.Keys.Reads);
        Assert.Equal("https://api.z.ai/api/monitor/usage/quota/limit", Assert.Single(h.Scripted.Requests).Url.AbsoluteUri);
        Assert.Equal(["Authorization", "User-Agent"], h.Scripted.Requests[0].Headers.Select(p => p.Key));
        Assert.NotNull(readings.Snapshot(SourceKind.ZCode));
        h.Now += 2 * Minute;
        await h.RoundAsync(zcode);
        Assert.Equal(1, h.Keys.Reads);

        // A new key (or region) forgets the route and asks at once, on the region's host.
        h.Keys.Save("zk-2");
        h.Service.Forget(QuotaRoute.Zcode);
        readings = await h.RoundAsync(zcode, region: ZaiRegion.BigmodelCn);
        Assert.Equal(2, h.Keys.Reads);
        Assert.Equal(QuotaHosts.Bigmodel, h.Scripted.Requests[^1].Url.Host);
        Assert.Equal("Bearer zk-2", h.Scripted.Requests[^1].Header("Authorization"));

        // Without a key the field says what to do: no status line, no request.
        h.Keys.Remove();
        h.Service.Forget(QuotaRoute.Zcode);
        readings = await h.RoundAsync(zcode);
        Assert.Null(readings.Status(QuotaRoute.Zcode));
        Assert.Equal(2, h.Scripted.Count(QuotaHosts.Zai) + h.Scripted.Count(QuotaHosts.Bigmodel));
    }

    [Fact]
    public async Task AntigravityFallsBackToGetUserStatusAndForgetsARefusedToken()
    {
        using var h = Harness();
        h.Scripted.Answer = r => r.Url.AbsolutePath.EndsWith("RetrieveUserQuotaSummary", StringComparison.Ordinal)
            ? QuotaHttpResponse.Of(404)
            : ScriptedQuotaTransport.Json(Body("antigravityStatus"));
        var route = On(QuotaRoute.Antigravity);
        var readings = await h.RoundAsync(route);
        Assert.Equal(1, h.Locator.Calls);
        Assert.Equal(["RetrieveUserQuotaSummary", "GetUserStatus"], h.Scripted.Requests.Select(r => r.Url.Segments[^1]));
        Assert.Equal("Gemini 3 Pro", Assert.Single(readings.Snapshot(SourceKind.Antigravity)!.Windows).Name);
        Assert.True(readings.SignedIn(SourceKind.Antigravity));

        h.Now += Minute;
        await h.RoundAsync(route);
        Assert.Equal(1, h.Locator.Calls);

        h.Scripted.Answer = _ => QuotaHttpResponse.Of(403);
        h.Now += 5 * Minute;
        readings = await h.RoundAsync(route);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Equal("csrf-1", h.Scripted.Requests[^1].Header("X-Codeium-Csrf-Token"));
        h.Scripted.Answer = _ => ScriptedQuotaTransport.Json(Body("antigravity"));
        h.Now += 60 * Minute;
        readings = await h.RoundAsync(route);
        Assert.Null(h.Scripted.Requests[^1].Header("X-Codeium-Csrf-Token"));
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
    }

    [Fact]
    public async Task BrowserSessionsAreReadOnlyWhenDueAndARefusedOneIsNotSentAgain()
    {
        using var h = Harness();
        var qoder = On(QuotaRoute.Qoder);
        var readings = await h.RoundAsync(qoder, running: new HashSet<SourceKind> { SourceKind.QoderCli });
        Assert.Equal(1, h.Browser.Reads);
        var request = Assert.Single(h.Scripted.Requests);
        Assert.Equal("sid=a1", request.Header("Cookie"));
        Assert.Equal("https://qoder.com", request.Header("Origin"));
        Assert.Equal("https://qoder.com/", request.Header("Referer"));
        Assert.Equal(readings.Snapshot(SourceKind.Qoder)!.Windows, readings.Snapshot(SourceKind.QoderCli)!.Windows);
        Assert.Equal(SourceKind.QoderCli, readings.Snapshot(SourceKind.QoderCli)!.Source);

        h.Now += Minute;
        await h.RoundAsync(qoder);
        Assert.Equal(1, h.Browser.Reads);

        h.Scripted.Answer = _ => QuotaHttpResponse.Of(401);
        h.Now += 5 * Minute;
        readings = await h.RoundAsync(qoder);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Qoder)?.Kind);
        Assert.False(readings.SignedIn(SourceKind.Qoder));
        h.Now += 15 * Minute;
        readings = await h.RoundAsync(qoder);
        Assert.Equal(3, h.Browser.Reads);
        Assert.Equal(2, h.Scripted.Requests.Count);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Qoder)?.Kind);

        h.Browser.Sessions["qoder.com"] = new BrowserSession("qoder.com", "sid=new", "UA-browser", BrowserKind.Firefox);
        h.Scripted.Answer = _ => ScriptedQuotaTransport.Json(Body("qoder"));
        h.Now += 15 * Minute;
        readings = await h.RoundAsync(qoder);
        Assert.Equal(3, h.Scripted.Requests.Count);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Qoder)?.Kind);
    }

    [Theory]
    [InlineData(BrowserSessionMiss.NoSession, QuotaStatusKind.NoBrowser)]
    [InlineData(BrowserSessionMiss.Locked, QuotaStatusKind.BrowserLocked)]
    [InlineData(BrowserSessionMiss.Protected, QuotaStatusKind.BrowserProtected)]
    public async Task NoSessionSaysWhy(BrowserSessionMiss miss, QuotaStatusKind expected)
    {
        using var h = Harness();
        h.Browser.Sessions.Clear();
        h.Browser.Miss = miss;
        var readings = await h.RoundAsync(On(QuotaRoute.WorkBuddy));
        Assert.Equal(expected, readings.Status(QuotaRoute.WorkBuddy)?.Kind);
        Assert.Empty(h.Scripted.Requests);
    }

    [Fact]
    public async Task WorkBuddySendsTheBrowsersUserAgent()
    {
        using var h = Harness();
        await h.RoundAsync(On(QuotaRoute.WorkBuddy));
        var request = Assert.Single(h.Scripted.Requests);
        Assert.Equal("https://www.workbuddy.cn/billing/meter/get-user-resource-summary", request.Url.AbsoluteUri);
        Assert.Equal("UA-browser", request.Header("User-Agent"));
        Assert.Equal("wb=1", request.Header("Cookie"));
    }

    [Fact]
    public async Task TurningTheBrowserRoutesOffForgetsRefusals()
    {
        using var h = Harness();
        await h.RoundAsync(On(QuotaRoute.Qoder));
        Assert.Equal(0, h.Browser.Resets);
        await h.RoundAsync(On(QuotaRoute.Kimi));
        Assert.True(h.Browser.Resets > 0);
    }

    [Fact]
    public async Task TheStatusLineComesBackAfterThePause()
    {
        using var h = Harness();
        var kimi = On(QuotaRoute.Kimi);
        await h.RoundAsync(kimi);
        Assert.Equal(QuotaStatusKind.NetworkOff, (await h.RoundAsync(kimi, network: false)).Status(QuotaRoute.Kimi)?.Kind);
        h.Now += Minute;
        var readings = await h.RoundAsync(kimi);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Kimi)?.Kind);
        Assert.Equal(1, h.Scripted.Count(QuotaHosts.Kimi));
    }

    [Fact]
    public async Task NoCredentialFileIsNoCredential()
    {
        using var h = Harness();
        File.Delete(Path.Combine(h.Home, ".factory", ".env"));
        var readings = await h.RoundAsync(On(QuotaRoute.Droid));
        Assert.Equal(QuotaStatusKind.NoCredential, readings.Status(QuotaRoute.Droid)?.Kind);
        Assert.Empty(h.Scripted.Requests);
        h.Variables["FACTORY_API_KEY"] = " fk-env ";
        await h.RoundAsync(On(QuotaRoute.Droid));
        Assert.Equal("Bearer fk-env", Assert.Single(h.Scripted.Requests).Header("Authorization"));
    }
}
