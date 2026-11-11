using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Tests.Quota.Network;

/// <summary>The quota transport against a stub on 127.0.0.1 (NETWORK 9): headers, redirects, the cap, loopback TLS.</summary>
public sealed class QuotaTransportTests
{
    private static QuotaHttpTransport Transport() => new(QuotaHttpTransport.LoopbackHandler(), QuotaHttpTransport.LoopbackHandler());

    [Fact]
    public async Task OnlyTheListedHeadersCrossTheWire()
    {
        using var server = new QuotaTestServer();
        using var transport = Transport();
        var request = QuotaRequests.Antigravity(AntigravityCall.Summary, "http", server.Port, "csrf-1", "AgentSpeed/1.0 (Windows)")!;
        server.Answer = _ => new WireAnswer(200, """{"ok":true}""", ["Date: Tue, 10 Nov 2026 20:00:00 GMT"]);
        var response = await transport.SendAsync(request);
        Assert.Equal(200, response.Status);
        Assert.Equal(1794340800000, response.DateMs);
        var wire = Assert.Single(server.Requests);
        Assert.Equal("POST", wire.Method);
        Assert.Equal("/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary", wire.Path);
        Assert.Equal(["accept-language", "connect-protocol-version", "content-length", "content-type", "host", "user-agent", "x-codeium-csrf-token"],
            wire.HeaderNames.Order(StringComparer.Ordinal));
        Assert.Equal("*", wire.Header("Accept-Language"));
        Assert.Equal("csrf-1", wire.Header("X-Codeium-Csrf-Token"));
        Assert.Equal("application/json", wire.Header("Content-Type"));
        Assert.Equal("""{"forceRefresh":true}""", wire.BodyText);
    }

    [Fact]
    public async Task ARedirectIsRefused()
    {
        using var server = new QuotaTestServer();
        using var transport = Transport();
        server.Answer = _ => new WireAnswer(302, "", ["Location: http://127.0.0.1:1/elsewhere"]);
        var failure = await Assert.ThrowsAsync<QuotaHttpException>(() => transport.SendAsync(Loopback(server)));
        Assert.Equal(QuotaHttpFailure.Redirected, failure.Failure);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task TheCapHoldsByLengthAndWhileStreaming()
    {
        using var server = new QuotaTestServer();
        using var transport = Transport();
        var big = "{\"a\":\"" + new string('x', QuotaHttpRequest.MaxBytes) + "\"}";
        server.Answer = _ => new WireAnswer(200, big);
        Assert.Equal(QuotaHttpFailure.TooLarge, (await Assert.ThrowsAsync<QuotaHttpException>(() => transport.SendAsync(Loopback(server)))).Failure);
        server.Answer = _ => new WireAnswer(200, big, Chunked: true);
        Assert.Equal(QuotaHttpFailure.TooLarge, (await Assert.ThrowsAsync<QuotaHttpException>(() => transport.SendAsync(Loopback(server)))).Failure);
    }

    [Fact]
    public async Task OtherStatusesCarryNoBody()
    {
        using var server = new QuotaTestServer();
        using var transport = Transport();
        server.Answer = _ => new WireAnswer(401, """{"error":"secret detail"}""");
        var response = await transport.SendAsync(Loopback(server));
        Assert.Equal(401, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task LoopbackAcceptsASelfSignedCertificateAndVendorsDoNot()
    {
        using var server = new QuotaTestServer(tls: true);
        using (var transport = Transport())
        {
            var response = await transport.SendAsync(QuotaRequests.Antigravity(AntigravityCall.Summary, "https", server.Port, null, "UA")!);
            Assert.Equal(200, response.Status);
            Assert.Null(Assert.Single(server.Requests).Header("X-Codeium-Csrf-Token"));
        }

        using var vendor = new QuotaHttpTransport(QuotaHttpTransport.VendorHandler(), QuotaHttpTransport.VendorHandler());
        var failure = await Assert.ThrowsAsync<QuotaHttpException>(() => vendor.SendAsync(QuotaRequests.Antigravity(AntigravityCall.Summary, "https", server.Port, null, "UA")!));
        Assert.Equal(QuotaHttpFailure.Transport, failure.Failure);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/api/oauth/usage")]
    [InlineData("https://claude.ai/api/organizations/x/usage")]
    [InlineData("http://api.kimi.com/coding/v1/usages")]
    [InlineData("https://api.kimi.com:8443/coding/v1/usages")]
    [InlineData("https://user:pw@api.kimi.com/coding/v1/usages")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost:4000/")]
    [InlineData("https://evil.example/")]
    public async Task OnlyNetworkNineHostsAreAsked(string url)
    {
        Assert.False(QuotaHosts.Permits(new Uri(url)));
        using var transport = new QuotaHttpTransport();
        var failure = await Assert.ThrowsAsync<QuotaHttpException>(() =>
            transport.SendAsync(new QuotaHttpRequest(HttpMethod.Get, new Uri(url), [new("User-Agent", "UA")])));
        Assert.Equal(QuotaHttpFailure.DisallowedUrl, failure.Failure);
    }

    [Fact]
    public void TheVendorHostsArePermitted()
    {
        foreach (var host in QuotaHosts.Remote)
        {
            Assert.True(QuotaHosts.Permits(new Uri($"https://{host}/x")), host);
        }

        Assert.True(QuotaHosts.Permits(new Uri("http://127.0.0.1:42100/x")));
        Assert.True(QuotaHosts.Permits(new Uri("https://127.0.0.1:42100/x")));
    }

    [Fact]
    public void ARequestNeverPrintsItsHeaders()
    {
        var request = QuotaRequests.KimiUsages("secret-token", "UA");
        Assert.Equal("GET api.kimi.com", request.ToString());
        Assert.DoesNotContain("secret", request.ToString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), QuotaRequests.Antigravity(AntigravityCall.Summary, "http", 1, null, "UA")!.Timeout);
    }

    private static QuotaHttpRequest Loopback(QuotaTestServer server) => new(HttpMethod.Get, server.Url("/x"), [new("User-Agent", "UA")]);
}
