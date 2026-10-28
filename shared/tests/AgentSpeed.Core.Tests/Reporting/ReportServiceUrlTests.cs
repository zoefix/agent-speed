using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>REPORTING 2.1, 2.2 and 5.3: service URL normalization, links, authority and request paths.</summary>
public sealed class ReportServiceUrlTests
{
    [Theory]
    [InlineData("codexradar.com", "https://codexradar.com")]
    [InlineData("https://Example.com/", "https://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM", "https://example.com")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("\texample.com\n", "https://example.com")]
    [InlineData("https://example.com/agentspeed/", "https://example.com/agentspeed")]
    [InlineData("https://example.com/AgentSpeed//", "https://example.com/AgentSpeed")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("https://example.com:443/x", "https://example.com/x")]
    [InlineData("https://example.com:8443", "https://example.com:8443")]
    [InlineData("example.com:8443/board", "https://example.com:8443/board")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787")]
    [InlineData("http://127.0.0.1:80/", "http://127.0.0.1")]
    [InlineData("http://[::1]:8787", "http://[::1]:8787")]
    [InlineData("http://LOCALHOST:8787/", "http://localhost:8787")]
    [InlineData("https://[2001:DB8::1]:8443/x", "https://[2001:db8::1]:8443/x")]
    [InlineData("https://bücher.example/x", "https://xn--bcher-kva.example/x")]
    [InlineData("https://BÜCHER.example", "https://xn--bcher-kva.example")]
    [InlineData("https://example.com/排行", "https://example.com/%E6%8E%92%E8%A1%8C")]
    [InlineData("https://example.com/a%2Fb", "https://example.com/a%2Fb")]
    [InlineData("localhost:8787", "https://localhost:8787")]
    [InlineData("https://deng.codexradar.com", "https://deng.codexradar.com")]
    public void Normalizes(string input, string expected)
    {
        Assert.True(ReportServiceUrl.TryParse(input, out var url, out var problem), $"{input}: {problem}");
        Assert.Equal(expected, url!.Text);
    }

    [Theory]
    [InlineData("http://example.com", ReportUrlProblem.NotHttps)]
    [InlineData("http://127.0.0.2:8787", ReportUrlProblem.NotHttps)]
    [InlineData("http://localhost.example.com", ReportUrlProblem.NotHttps)]
    [InlineData("ftp://example.com", ReportUrlProblem.NotHttps)]
    [InlineData("ws://example.com", ReportUrlProblem.NotHttps)]
    [InlineData("https://user@example.com", ReportUrlProblem.Invalid)]
    [InlineData("https://user:secret@example.com", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com?q", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/?", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com#f", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/#", ReportUrlProblem.Invalid)]
    [InlineData("", ReportUrlProblem.Invalid)]
    [InlineData("   ", ReportUrlProblem.Invalid)]
    [InlineData("https://", ReportUrlProblem.Invalid)]
    [InlineData("https:///path", ReportUrlProblem.Invalid)]
    [InlineData("https://exa mple.com", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/a b", ReportUrlProblem.Invalid)]
    [InlineData("https://exa\u200Bmple.com", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/\u202Ex", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com\\x", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/a\\b", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/a%zz", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/../x", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/a/%2e%2E", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com/./x", ReportUrlProblem.Invalid)]
    [InlineData("https://a..b", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com./x", ReportUrlProblem.Invalid)]
    [InlineData("https://exa_mple.com", ReportUrlProblem.Invalid)]
    [InlineData("https://-example.com", ReportUrlProblem.Invalid)]
    [InlineData("https://ex%41mple.com", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com:0", ReportUrlProblem.Invalid)]
    [InlineData("https://example.com:99999", ReportUrlProblem.Invalid)]
    [InlineData("mailto:zoe@example.com", ReportUrlProblem.Invalid)]
    public void Rejects(string input, ReportUrlProblem expected)
    {
        Assert.False(ReportServiceUrl.TryParse(input, out var url, out var problem), $"{input} → {url}");
        Assert.Equal(expected, problem);
        Assert.Null(ReportServiceUrl.Parse(input));
    }

    [Fact]
    public void LengthIsCapped()
    {
        const string Prefix = "https://example.com/";
        var fits = Prefix + new string('a', 2048 - Prefix.Length);
        Assert.Equal(2048, ReportServiceUrl.Parse(fits)!.Text.Length);
        Assert.Null(ReportServiceUrl.Parse(fits + "a"));
    }

    [Fact]
    public void DerivedParts()
    {
        var plain = ReportServiceUrl.Parse("https://Example.com:8443/AgentSpeed/")!;
        Assert.Equal("example.com", plain.Host);
        Assert.Equal("example.com:8443", plain.Authority);
        Assert.Equal("https://example.com:8443", plain.Origin);
        Assert.False(plain.IsLoopbackHttp);

        var loopback = ReportServiceUrl.Parse("http://127.0.0.1:8787")!;
        Assert.True(loopback.IsLoopbackHttp);
        Assert.Equal("127.0.0.1", loopback.Host);
        Assert.Equal("127.0.0.1:8787", loopback.Authority);
        Assert.Equal("http://127.0.0.1:8787", loopback.Origin);

        var ipv6 = ReportServiceUrl.Parse("http://[::1]:8787")!;
        Assert.True(ipv6.IsLoopbackHttp);
        Assert.Equal("[::1]", ipv6.Host);
        Assert.Equal("[::1]:8787", ipv6.Authority);
        Assert.False(ReportServiceUrl.Parse("https://localhost")!.IsLoopbackHttp);
    }

    [Fact]
    public void DiscoveryUrlsAndEndpoints()
    {
        Assert.Equal("https://example.com/.well-known/agent-speed-report", ReportServiceUrl.Parse("https://example.com")!.DiscoveryUrl.AbsoluteUri);
        Assert.Equal("https://example.com/agentspeed/.well-known/agent-speed-report", ReportServiceUrl.Parse("https://example.com/agentspeed")!.DiscoveryUrl.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:8787/.well-known/agent-speed-report", ReportServiceUrl.Parse("http://127.0.0.1:8787/")!.DiscoveryUrl.AbsoluteUri);

        var api = ReportServiceUrl.Parse("https://api.example.com/agent-speed/")!;
        Assert.Equal("https://api.example.com/agent-speed/v1/reports", api.Endpoint(ReportEndpoint.Reports).AbsoluteUri);
        Assert.Equal("http://127.0.0.1:8787/v1/devices", ReportServiceUrl.Parse("http://127.0.0.1:8787")!.Endpoint(ReportEndpoint.Devices).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => api.Endpoint("/v2/reports"));
        Assert.Equal("/v1/devices/abc/key", ReportEndpoint.Key("abc"));
        Assert.Equal("/v1/devices/abc", ReportEndpoint.Device("abc"));
    }

    [Fact]
    public void ApiUrls()
    {
        Assert.Equal("https://api.example.com/agent-speed", ReportServiceUrl.ParseApi("https://api.example.com/agent-speed/", false)?.Text);
        Assert.Equal("https://api.example.com", ReportServiceUrl.ParseApi("https://API.example.com:443", false)?.Text);
        Assert.Equal("http://127.0.0.1:8787", ReportServiceUrl.ParseApi("http://127.0.0.1:8787", true)?.Text);
        Assert.NotNull(ReportServiceUrl.ParseApi("https://api.example.com", true));
        Assert.Null(ReportServiceUrl.ParseApi("http://127.0.0.1:8787", false));
        Assert.Null(ReportServiceUrl.ParseApi("http://api.example.com", true));
        Assert.Null(ReportServiceUrl.ParseApi("api.example.com", false));
        Assert.Null(ReportServiceUrl.ParseApi(" https://api.example.com", false));
        Assert.Null(ReportServiceUrl.ParseApi("https://api.example.com/?x", false));
        Assert.Null(ReportServiceUrl.ParseApi("https://api.example.com/#x", false));
        Assert.Null(ReportServiceUrl.ParseApi("https://key@api.example.com", false));
    }

    [Fact]
    public void AuthorityAndPathRules()
    {
        Assert.Equal("api.example.com", ReportUrls.Authority(new Uri("https://API.Example.com:443/x")));
        Assert.Equal("127.0.0.1", ReportUrls.Authority(new Uri("http://127.0.0.1:80/x")));
        Assert.Equal("api.example.com:80", ReportUrls.Authority(new Uri("https://api.example.com:80/x")));
        Assert.Equal("[::1]:8787", ReportUrls.Authority(new Uri("http://[::1]:8787/v1/reports")));
        Assert.Equal("xn--bcher-kva.example", ReportUrls.Authority(new Uri("https://bücher.example/v1")));
        Assert.Equal("/", ReportUrls.RequestPath(new Uri("https://example.com")));
        Assert.Equal("/a%2Fb/", ReportUrls.RequestPath(new Uri("https://example.com/a%2Fb/")));
        Assert.Equal("/v1/x?a=1&b=%20", ReportUrls.RequestPath(new Uri("https://example.com/v1/x?a=1&b=%20")));
    }

    [Fact]
    public void HttpsLinks()
    {
        Assert.Equal("https://example.com/terms", ReportUrls.HttpsLink("https://example.com/terms"));
        Assert.NotNull(ReportUrls.HttpsLink("https://example.com/live?tab=now#top"));
        Assert.NotNull(ReportUrls.HttpsLink("https://bücher.example/x"));
        Assert.NotNull(ReportUrls.HttpsLink("https://example.com/terms?lang=en#top"));
        foreach (var text in new[]
                 {
                     null, string.Empty, "http://example.com/terms", "http://127.0.0.1:8787/terms", "HTTPS://example.com", "javascript:alert(1)",
                     "https://user@example.com/", "https://example.com?x", "https://example.com/a b", "https://example.com/\u202Egpj.exe", "https://",
                     "https://example.com/" + new string('a', 2048), "http://[::1]/terms", "http://localhost:8787/terms",
                 })
        {
            Assert.Null(ReportUrls.HttpsLink(text));
        }
    }

    [Fact]
    public void EqualityIsByNormalizedText()
    {
        Assert.Equal(ReportServiceUrl.Parse("https://Example.com/"), ReportServiceUrl.Parse("example.com"));
        Assert.NotEqual(ReportServiceUrl.Parse("https://example.com/a"), ReportServiceUrl.Parse("https://example.com/A"));
        Assert.Single(new HashSet<ReportServiceUrl> { ReportServiceUrl.Parse("example.com")!, ReportServiceUrl.Parse("https://example.com:443")! });
    }
}
