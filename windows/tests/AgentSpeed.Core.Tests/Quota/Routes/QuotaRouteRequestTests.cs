using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Tests.Quota.Network;
using AgentSpeed.Core.Tests.Quota.Vendors;
using static AgentSpeed.Core.Tests.Quota.Routes.QuotaVendorHarness;

namespace AgentSpeed.Core.Tests.Quota.Routes;

/// <summary>
/// Every vendor route's request over real HTTP to a stub on 127.0.0.1 (QUOTAS 9.2–9.9): method, path, exactly the
/// headers its section lists, the body, and the fixture answer read back as the route's snapshot.
/// </summary>
public sealed class QuotaRouteRequestTests
{
    private const string Ua = "AgentSpeed/9.9.9 (Windows)";

    /// <summary>Headers any request carries on the wire besides the section's: the host, the language and a body's length.</summary>
    private static readonly string[] Transport = ["host", "accept-language"];

    [Fact]
    public async Task EachRouteAsksItsVendorWithItsOwnHeadersOnly()
    {
        using var server = new QuotaTestServer();
        server.Answer = r => new WireAnswer(200, r.Path.Split('?')[0] switch
        {
            "/v1/billing" => Body("grokApi"),
            "/coding/v1/usages" => Body("kimiCurrent"),
            "/v1internal:loadCodeAssist" => Body("geminiProject"),
            "/v1internal:retrieveUserQuota" => Body("gemini"),
            "/api/billing/limits" => Body("droid"),
            "/api/monitor/usage/quota/limit" => Body("zcode"),
            "/api/v2/me/usages/big_model_credits" => Body("qoder"),
            "/billing/meter/get-user-resource-summary" => Body("workbuddy"),
            "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary" => Body("antigravity"),
            _ => "{}",
        });
        using var transport = new StubbedVendorTransport(server);
        using var h = new QuotaVendorHarness(transport);
        h.Browser.Sessions["qoder.com"] = new BrowserSession("qoder.com", "sid=a1; csrf=b2", "UA-chrome", BrowserKind.Chrome);
        h.Browser.Sessions["workbuddy.cn"] = new BrowserSession("workbuddy.cn", "wb=1", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) UA-browser", BrowserKind.Edge);
        h.Locator.Found.Add(new AntigravityServer(77, [new AntigravityEndpoint("http", server.Port, "csrf-x")]));

        var routes = QuotaRoutes.All.Where(r => r != QuotaRoute.GrokBuild && r != QuotaRoute.Devin).ToHashSet();
        var readings = await h.RoundAsync(routes);

        var wire = server.Requests.ToDictionary(r => r.Path.Split('?')[0]);
        Assert.Equal(9, wire.Count);
        AssertWire(wire["/v1/billing"], "GET", ["accept", "authorization", "x-xai-token-auth", "user-agent"],
            ("Authorization", "Bearer grok-key"), ("x-xai-token-auth", "xai-grok-cli"), ("Accept", "application/json"), ("User-Agent", Ua));
        AssertWire(wire["/coding/v1/usages"], "GET", ["accept", "authorization", "user-agent"], ("Authorization", "Bearer kimi-tok"));
        AssertWire(wire["/v1internal:loadCodeAssist"], "POST", ["authorization", "content-type", "user-agent", "content-length"],
            ("Authorization", "Bearer gem-tok"), ("Content-Type", "application/json"));
        Assert.Equal("""{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}""", wire["/v1internal:loadCodeAssist"].BodyText);
        Assert.Equal("""{"project":"proj-123"}""", wire["/v1internal:retrieveUserQuota"].BodyText);
        AssertWire(wire["/api/billing/limits"], "GET", ["accept", "authorization", "user-agent"], ("Authorization", "Bearer fk-1"));
        AssertWire(wire["/api/monitor/usage/quota/limit"], "GET", ["authorization", "user-agent"], ("Authorization", "Bearer zk-1"));
        AssertWire(wire["/api/v2/me/usages/big_model_credits"], "GET", ["cookie", "origin", "referer", "accept", "user-agent"],
            ("Cookie", "sid=a1; csrf=b2"), ("Origin", "https://qoder.com"), ("Referer", "https://qoder.com/"), ("User-Agent", Ua));
        AssertWire(wire["/billing/meter/get-user-resource-summary"], "POST", ["content-type", "cookie", "user-agent", "content-length"],
            ("Cookie", "wb=1"), ("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) UA-browser"));
        Assert.Equal("{}", wire["/billing/meter/get-user-resource-summary"].BodyText);
        AssertWire(wire["/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary"], "POST",
            ["connect-protocol-version", "content-type", "x-codeium-csrf-token", "user-agent", "content-length"], ("X-Codeium-Csrf-Token", "csrf-x"));

        // Every request named a NETWORK 9 host before the stub stood in for it; none went to Anthropic.
        Assert.Equal(
            new[] { QuotaHosts.Grok, QuotaHosts.Kimi, QuotaHosts.Gemini, QuotaHosts.Factory, QuotaHosts.Zai, QuotaHosts.Qoder, QuotaHosts.WorkBuddy, QuotaHosts.Loopback }
                .Order(StringComparer.Ordinal),
            transport.Sent.Select(r => r.Url.Host).Distinct().Order(StringComparer.Ordinal));
        Assert.DoesNotContain(transport.Sent, r => r.Url.Host.Contains("anthropic", StringComparison.Ordinal) || r.Url.Host.Contains("claude", StringComparison.Ordinal));

        QuotaVendorFixtureTests.AssertWindows(Vendor("grokApi").GetProperty("expected"), readings.Snapshot(SourceKind.GrokBuild)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("kimiCurrent").GetProperty("expected"), readings.Snapshot(SourceKind.Kimi)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("gemini").GetProperty("expected"), readings.Snapshot(SourceKind.Gemini)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("droid").GetProperty("expected"), readings.Snapshot(SourceKind.Droid)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("zcode").GetProperty("expected"), readings.Snapshot(SourceKind.ZCode)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("qoder").GetProperty("expected"), readings.Snapshot(SourceKind.Qoder)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("workbuddy").GetProperty("expected"), readings.Snapshot(SourceKind.WorkBuddy)!.Windows);
        QuotaVendorFixtureTests.AssertWindows(Vendor("antigravity").GetProperty("expected"), readings.Snapshot(SourceKind.Antigravity)!.Windows);
        foreach (var route in routes)
        {
            Assert.Equal(QuotaStatusKind.Ok, readings.Status(route)?.Kind);
            Assert.Equal(Start, readings.Status(route)?.AtMs);
        }
    }

    private static void AssertWire(WireRequest request, string method, string[] listed, params (string Name, string Value)[] values)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal(listed.Concat(Transport).Order(StringComparer.Ordinal), request.HeaderNames.Order(StringComparer.Ordinal));
        foreach (var (name, value) in values)
        {
            Assert.Equal(value, request.Header(name));
        }
    }
}
