using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>REPORTING 3: discovery documents, the vectors' <c>discovery</c> cases, and consent pinning.</summary>
public sealed class ReportDiscoveryTests
{
    private const string Repo = "https://github.com/zoefix/agent-speed";

    private static ReportServiceUrl Service(string text = "https://example.com") => ReportServiceUrl.Parse(text)!;

    private static JsonObject Example() => JsonNode.Parse(ReportSpec.Example("discovery.json"))!.AsObject();

    /// <summary><c>ReferenceService.discovery(scheme)</c> of <c>spec/tools/report_reference.py</c> for <c>127.0.0.1:8787</c>.</summary>
    private static JsonObject Reference(string scheme = "http") => new()
    {
        ["protocol"] = "agent-speed-report",
        ["versions"] = new JsonArray(1),
        ["service"] = new JsonObject
        {
            ["name"] = "Agent Speed reference server",
            ["description"] = "Local test server (spec/tools/report_server.py).",
            ["terms"] = Repo,
            ["privacy"] = Repo,
            ["retentionDays"] = 0,
        },
        ["api"] = $"{scheme}://127.0.0.1:8787",
        ["apiKey"] = new JsonObject { ["mode"] = "none" },
        ["username"] = new JsonObject { ["minLength"] = 2, ["maxLength"] = 32, ["charset"] = "unicode", ["spaces"] = true },
        ["limits"] = new JsonObject { ["intervalSeconds"] = 5, ["heartbeatSeconds"] = 60, ["maxAgeSeconds"] = 30, ["maxBodyBytes"] = 16_384, ["maxModels"] = 16 },
        ["sections"] = new JsonArray("sources", "models", "cost", "traffic"),
    };

    /// <summary><paramref name="document"/> with <paramref name="path"/> (dotted) set to a raw JSON literal, or removed when null.</summary>
    private static byte[] Edited(JsonObject document, string path, string? rawJson)
    {
        var copy = JsonNode.Parse(document.ToJsonString())!.AsObject();
        var keys = path.Split('.');
        var target = copy;
        foreach (var key in keys[..^1])
        {
            if (target[key] is not JsonObject child)
            {
                child = new JsonObject();
                target[key] = child;
            }

            target = child;
        }

        if (rawJson is null)
        {
            target.Remove(keys[^1]);
            return Encoding.UTF8.GetBytes(copy.ToJsonString());
        }

        const string marker = "\"__raw_marker__\"";
        target[keys[^1]] = "__raw_marker__";
        return Encoding.UTF8.GetBytes(copy.ToJsonString().Replace(marker, rawJson, StringComparison.Ordinal));
    }

    private static string Literal(string text) => JsonSerializer.Serialize(text);

    private static ReportDiscovery Parsed(byte[] data, string service = "https://example.com") =>
        ReportDiscovery.Parse(data, Service(service), out var problem) ?? throw new Xunit.Sdk.XunitException("not parsed: " + problem);

    private static ReportDiscoveryProblem? Problem(byte[] data, string service = "https://example.com")
    {
        var parsed = ReportDiscovery.Parse(data, Service(service), out var problem);
        Assert.True(parsed is null == problem is not null);
        return problem;
    }

    [Fact]
    public void ExampleDocument()
    {
        var discovery = Parsed(ReportSpec.Example("discovery.json"));
        Assert.Equal([1], discovery.Versions);
        Assert.True(discovery.SupportsThisClient);
        Assert.Equal("Example Leaderboard", discovery.Name);
        Assert.Equal("Live token speed of AI coding agents, updated every few seconds.", discovery.Summary);
        Assert.Equal("https://example.com/", discovery.Homepage);
        Assert.Equal("https://example.com/terms", discovery.Terms);
        Assert.Equal("https://example.com/privacy", discovery.Privacy);
        Assert.Equal("https://example.com/live", discovery.Leaderboard);
        Assert.Equal("privacy@example.com", discovery.Contact);
        Assert.Equal(30, discovery.RetentionDays);
        Assert.Equal("https://api.example.com/agent-speed", discovery.Api.Text);
        Assert.Equal(ReportApiKeyMode.Optional, discovery.ApiKeyMode);
        Assert.Equal("https://example.com/account/keys", discovery.ApiKeyUrl);
        Assert.Equal(new ReportUsernameRules(2, 24, ReportUsernameCharset.Unicode, true), discovery.UsernameRules);
        Assert.Equal(ReportLimits.Standard, discovery.Limits);
        Assert.Equal(16_384, discovery.Limits.SendLimitBytes);
        Assert.Equal(ReportSections.All.ToHashSet(), discovery.Sections.ToHashSet());
    }

    /// <summary>Codex Radar's live document (2026-10-23): the API on a sibling host with a path prefix, a retired section, no retention.</summary>
    [Fact]
    public void CodexRadarDocument()
    {
        const string text = """
            {"protocol":"agent-speed-report","versions":[1],
             "service":{"name":"速蹬雷达","description":"社区贡献的实时速蹬天梯、全站吞吐与模型实测均速。",
              "homepage":"https://deng.codexradar.com/","terms":"https://deng.codexradar.com/agent-speed/terms/",
              "privacy":"https://deng.codexradar.com/agent-speed/privacy/","leaderboard":"https://deng.codexradar.com/",
              "retentionDays":0},
             "api":"https://speed-api.codexradar.com/api/agent-speed","apiKey":{"mode":"none"},
             "username":{"minLength":2,"maxLength":32,"charset":"unicode","spaces":true},
             "limits":{"intervalSeconds":5,"heartbeatSeconds":60,"maxAgeSeconds":30,"maxBodyBytes":16384,"maxModels":16},
             "sections":["sources","models","cost","traffic","machineTraffic"]}
            """;
        var service = Service("https://deng.codexradar.com");
        var discovery = Parsed(Encoding.UTF8.GetBytes(text), service.Text);
        Assert.Equal("速蹬雷达", discovery.Name);
        Assert.Equal("https://speed-api.codexradar.com/api/agent-speed", discovery.Api.Text);
        Assert.Equal("speed-api.codexradar.com", discovery.Api.Host);
        Assert.Equal("https://speed-api.codexradar.com/api/agent-speed/v1/reports", discovery.Api.Endpoint(ReportEndpoint.Reports).AbsoluteUri);
        Assert.Equal("/api/agent-speed/v1/reports", ReportUrls.RequestPath(discovery.Api.Endpoint(ReportEndpoint.Reports)));
        Assert.Equal(ReportApiKeyMode.None, discovery.ApiKeyMode);
        Assert.Equal(0, discovery.RetentionDays);
        Assert.Equal(ReportSections.All.ToHashSet(), discovery.Sections.ToHashSet());
        Assert.Null(ReportInvite.Gap(discovery, service, "zoe", "codexradar.com"));
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(discovery, service, "zoe"));
    }

    [Fact]
    public void ReferenceServerDocumentOverLoopback()
    {
        var discovery = Parsed(Encoding.UTF8.GetBytes(Reference().ToJsonString()), "http://127.0.0.1:8787");
        Assert.Equal("Agent Speed reference server", discovery.Name);
        Assert.True(discovery.Api.IsLoopbackHttp);
        Assert.Equal("http://127.0.0.1:8787", discovery.Api.Text);
        Assert.Equal("api", Problem(Encoding.UTF8.GetBytes(Reference().ToJsonString()), "https://example.com")!.Field);
        Assert.Equal("api", Problem(Encoding.UTF8.GetBytes(Reference().ToJsonString()), "https://127.0.0.1:8787")!.Field);
    }

    [Fact]
    public void VectorCases()
    {
        var cases = ReportSpec.Vector("discovery").EnumerateArray().ToList();
        Assert.Equal(15, cases.Count);
        foreach (var item in cases)
        {
            var name = item.Text("name");
            var fetched = item.Text("fetchedFrom").Replace("/.well-known/agent-speed-report", string.Empty, StringComparison.Ordinal);
            var service = ReportServiceUrl.Parse(fetched);
            Assert.NotNull(service);
            var data = Encoding.UTF8.GetBytes(item.GetProperty("document").GetRawText());
            var expect = item.GetProperty("expect");
            var discovery = ReportDiscovery.Parse(data, service, out var problem);
            if (expect.TryGetProperty("invalid", out var invalid))
            {
                Assert.True(discovery is null, $"{name}: accepted");
                Assert.True(problem!.Kind == "invalid" && problem.Field == invalid.GetString(), $"{name}: {problem}");
                continue;
            }

            Assert.True(discovery is not null, $"{name}: {problem}");
            foreach (var field in expect.EnumerateObject())
            {
                Assert.True(Canonical(field.Value) == Field(field.Name, discovery), $"{name}: {field.Name} = {Field(field.Name, discovery)}");
            }
        }
    }

    /// <summary>The reference's name for a parsed field, as comparable text.</summary>
    private static string Field(string key, ReportDiscovery discovery) => key switch
    {
        "api" => discovery.Api.Text,
        "apiKeyMode" => ReportSections.Name(discovery.ApiKeyMode),
        "apiKeyUrl" => discovery.ApiKeyUrl ?? "null",
        "homepage" => discovery.Homepage ?? "null",
        "leaderboard" => discovery.Leaderboard ?? "null",
        "retentionDays" => discovery.RetentionDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
        "sections" => string.Join(',', ReportSections.All.Where(discovery.Sections.Contains).Select(ReportSections.Name)),
        "limits" => string.Join(',',
            $"heartbeatSeconds={discovery.Limits.HeartbeatSeconds}", $"intervalSeconds={discovery.Limits.IntervalSeconds}",
            $"maxAgeSeconds={discovery.Limits.MaxAgeSeconds}", $"maxBodyBytes={discovery.Limits.MaxBodyBytes}", $"maxModels={discovery.Limits.MaxModels}"),
        "username" => string.Join(',',
            $"charset={(discovery.UsernameRules.Charset == ReportUsernameCharset.Ascii ? "ascii" : "unicode")}",
            $"maxLength={discovery.UsernameRules.MaxLength}", $"minLength={discovery.UsernameRules.MinLength}",
            $"spaces={(discovery.UsernameRules.Spaces ? "true" : "false")}"),
        _ => "unknown key " + key,
    };

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(Canonical)),
        JsonValueKind.Object => string.Join(',', value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={Canonical(p.Value)}")),
        _ => value.GetRawText(),
    };

    [Fact]
    public void MinimalDocumentUsesDefaults()
    {
        var minimal = """{"protocol":"agent-speed-report","versions":[1],"api":"https://api.example.com","service":{"name":"Board","terms":"https://example.com/t","privacy":"https://example.com/p"}}""";
        var discovery = Parsed(Encoding.UTF8.GetBytes(minimal));
        Assert.Null(discovery.Summary);
        Assert.Null(discovery.RetentionDays);
        Assert.Null(discovery.Contact);
        Assert.Equal(ReportApiKeyMode.None, discovery.ApiKeyMode);
        Assert.Equal(ReportUsernameRules.Standard, discovery.UsernameRules);
        Assert.Equal(ReportLimits.Standard, discovery.Limits);
        Assert.Equal(4, discovery.Sections.Count);
    }

    [Fact]
    public void UnknownFieldsVersionsAndSections()
    {
        var future = Edited(Example(), "future", """{"anything":[1,2,3]}""");
        Assert.Equal("Example Leaderboard", Parsed(future).Name);
        var versions = Parsed(Edited(Example(), "versions", "[2,3]"));
        Assert.Equal([2, 3], versions.Versions);
        Assert.False(versions.SupportsThisClient);
        var some = Parsed(Edited(Example(), "sections", """["cost","sources","cost","telemetry"]"""));
        Assert.Equal(new HashSet<ReportSection> { ReportSection.Cost, ReportSection.Sources }, some.Sections.ToHashSet());
        Assert.Empty(Parsed(Edited(Example(), "sections", "[]")).Sections);
        Assert.Equal(4, Parsed(Edited(Example(), "sections", null)).Sections.Count);
    }

    [Fact]
    public void NumbersAreClampedAndHugeIntegersSaturate()
    {
        var document = Example();
        foreach (var (path, value) in new[]
                 {
                     ("limits.intervalSeconds", "1"), ("limits.heartbeatSeconds", "5000"), ("limits.maxAgeSeconds", "0"),
                     ("limits.maxBodyBytes", "1"), ("limits.maxModels", "-3"), ("service.retentionDays", "99999"),
                     ("username.minLength", "0"), ("username.maxLength", "100"),
                 })
        {
            document = JsonNode.Parse(Edited(document, path, value))!.AsObject();
        }

        var discovery = Parsed(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Equal(new ReportLimits(5, 900, 10, 4096, 0), discovery.Limits);
        Assert.Equal(4096, discovery.Limits.SendLimitBytes);
        Assert.Equal(3650, discovery.RetentionDays);
        Assert.Equal((1, 32), (discovery.UsernameRules.MinLength, discovery.UsernameRules.MaxLength));

        var huge = JsonNode.Parse(Edited(Example(), "limits.maxBodyBytes", "100000000000000000000000000000"))!.AsObject();
        var saturated = Parsed(Edited(huge, "service.retentionDays", "-100000000000000000000000000000"));
        Assert.Equal(65_536, saturated.Limits.MaxBodyBytes);
        Assert.Equal(0, saturated.RetentionDays);

        var interval = JsonNode.Parse(Edited(Example(), "limits.intervalSeconds", "120"))!.AsObject();
        Assert.Equal(120, Parsed(Edited(interval, "limits.heartbeatSeconds", "30")).Limits.HeartbeatSeconds);
        Assert.Equal(300, new ReportLimits(300, 10).HeartbeatSeconds);
    }

    [Theory]
    [InlineData("limits.intervalSeconds", "7.5")]
    [InlineData("limits.heartbeatSeconds", "120.0")]
    [InlineData("limits.maxModels", "1e1")]
    [InlineData("service.retentionDays", "30.0")]
    [InlineData("username.maxLength", "20.5")]
    public void NonIntegerNumbersAreInvalid(string path, string literal)
    {
        Assert.Equal(ReportDiscoveryProblem.Invalid(path), Problem(Edited(Example(), path, literal)));
    }

    [Theory]
    [InlineData("protocol", "protocol")]
    [InlineData("versions", "versions")]
    [InlineData("service", "service")]
    [InlineData("service.name", "service.name")]
    [InlineData("service.terms", "service.terms")]
    [InlineData("service.privacy", "service.privacy")]
    [InlineData("api", "api")]
    public void MissingRequiredField(string path, string field)
    {
        Assert.Equal(ReportDiscoveryProblem.Invalid(field), Problem(Edited(Example(), path, null)));
    }

    [Theory]
    [InlineData("protocol", "\"agent-speed\"", "protocol")]
    [InlineData("protocol", "1", "protocol")]
    [InlineData("versions", "[]", "versions")]
    [InlineData("versions", "1", "versions")]
    [InlineData("versions", "[\"1\"]", "versions")]
    [InlineData("versions", "[1.5]", "versions")]
    [InlineData("service", "\"Example\"", "service")]
    [InlineData("service.name", "5", "service.name")]
    [InlineData("service.name", "\" \u200B\u202E \"", "service.name")]
    [InlineData("service.description", "5", "service.description")]
    [InlineData("service.homepage", "[\"https://example.com\"]", "service.homepage")]
    [InlineData("service.terms", "\"http://example.com/terms\"", "service.terms")]
    [InlineData("service.terms", "\"example.com/terms\"", "service.terms")]
    [InlineData("service.privacy", "\"https://user@example.com/privacy\"", "service.privacy")]
    [InlineData("service.contact", "null", "service.contact")]
    [InlineData("service.retentionDays", "\"30\"", "service.retentionDays")]
    [InlineData("service.retentionDays", "true", "service.retentionDays")]
    [InlineData("api", "\"http://127.0.0.1:8787\"", "api")]
    [InlineData("api", "\"http://api.example.com\"", "api")]
    [InlineData("api", "\"https://api.example.com/?v=1\"", "api")]
    [InlineData("api", "\"https://api.example.com/#x\"", "api")]
    [InlineData("api", "\"https://key@api.example.com\"", "api")]
    [InlineData("api", "\"api.example.com\"", "api")]
    [InlineData("api", "443", "api")]
    [InlineData("apiKey", "\"required\"", "apiKey")]
    [InlineData("apiKey.mode", "\"sometimes\"", "apiKey.mode")]
    [InlineData("apiKey.mode", "1", "apiKey.mode")]
    [InlineData("username", "[1]", "username")]
    [InlineData("username.charset", "\"latin\"", "username.charset")]
    [InlineData("username.spaces", "\"yes\"", "username.spaces")]
    [InlineData("username.minLength", "\"2\"", "username.minLength")]
    [InlineData("limits", "5", "limits")]
    [InlineData("limits.intervalSeconds", "\"5\"", "limits.intervalSeconds")]
    [InlineData("limits.maxModels", "null", "limits.maxModels")]
    [InlineData("sections", "\"cost\"", "sections")]
    [InlineData("sections", "[\"cost\", 1]", "sections")]
    public void InvalidField(string path, string json, string field)
    {
        Assert.Equal(ReportDiscoveryProblem.Invalid(field), Problem(Edited(Example(), path, json)));
    }

    [Fact]
    public void TextIsPlainAndCapped()
    {
        var name = JsonNode.Parse(Edited(Example(), "service.name", Literal("  Speed\u202E\u200B \n\t Board\u0007 ")))!.AsObject();
        var description = JsonNode.Parse(Edited(name, "service.description", Literal(string.Concat(Enumerable.Repeat("word ", 100)))))!.AsObject();
        var discovery = Parsed(Edited(description, "service.contact", Literal(new string('c', 300))));
        Assert.Equal("Speed Board", discovery.Name);
        Assert.Equal(199, discovery.Summary!.EnumerateRunes().Count());
        Assert.EndsWith("word", discovery.Summary, StringComparison.Ordinal);
        Assert.Equal(254, discovery.Contact!.Length);
        var longName = Parsed(Edited(Example(), "service.name", Literal(new string('名', 50))));
        Assert.Equal(40, longName.Name.EnumerateRunes().Count());
        Assert.Equal("abc d", ReportPlainText.Clean("a\u2066b\u2069\uFEFFc\u2028d", 40));

        var empty = JsonNode.Parse(Edited(Example(), "service.description", Literal(" \u200B ")))!.AsObject();
        var blank = Parsed(Edited(empty, "service.contact", Literal(string.Empty)));
        Assert.Null(blank.Summary);
        Assert.Null(blank.Contact);
    }

    [Fact]
    public void LinksAreHttpsOnly()
    {
        var homepage = JsonNode.Parse(Edited(Example(), "service.homepage", Literal("http://example.com/")))!.AsObject();
        var leaderboard = JsonNode.Parse(Edited(homepage, "service.leaderboard", Literal("javascript:alert(1)")))!.AsObject();
        var discovery = Parsed(Edited(leaderboard, "apiKey.url", Literal("ftp://example.com/keys")));
        Assert.Null(discovery.Homepage);
        Assert.Null(discovery.Leaderboard);
        Assert.Null(discovery.ApiKeyUrl);
        Assert.Equal("Example Leaderboard", discovery.Name);

        Assert.Null(Parsed(Edited(Reference(), "service.leaderboard", Literal("http://127.0.0.1:8787/live")), "http://127.0.0.1:8787").Leaderboard);
        Assert.Equal(ReportDiscoveryProblem.Invalid("service.terms"),
            Problem(Edited(Reference(), "service.terms", Literal("http://127.0.0.1:8787/terms")), "http://127.0.0.1:8787"));
        Assert.Equal(ReportDiscoveryProblem.Invalid("service.privacy"),
            Problem(Edited(Reference(), "service.privacy", Literal("http://localhost:8787/privacy")), "http://127.0.0.1:8787"));
        Assert.Equal("https://api.example.com/agent-speed",
            Parsed(Edited(Example(), "api", Literal("https://API.Example.com:443/agent-speed/"))).Api.Text);
    }

    [Fact]
    public void NotJsonAndSizeLimit()
    {
        var service = Service();
        foreach (var text in new[] { "not json", "[1]", string.Empty, "<!doctype html><html></html>" })
        {
            Assert.Null(ReportDiscovery.Parse(Encoding.UTF8.GetBytes(text), service, out var problem));
            Assert.Equal(ReportDiscoveryProblem.NotJson, problem);
            Assert.True(problem!.IsOtherDocument);
        }

        Assert.True(ReportDiscoveryProblem.Invalid("protocol").IsOtherDocument);
        Assert.False(ReportDiscoveryProblem.Invalid("service.terms").IsOtherDocument);

        var example = ReportSpec.Example("discovery.json");
        var exact = example.Concat(Enumerable.Repeat((byte)0x20, ReportDiscovery.MaxBytes - example.Length)).ToArray();
        Assert.Equal(65_536, exact.Length);
        Assert.NotNull(ReportDiscovery.Parse(exact, service, out _));
        Assert.Null(ReportDiscovery.Parse([.. exact, 0x20], service, out var tooLarge));
        Assert.Equal(ReportDiscoveryProblem.TooLarge, tooLarge);
    }

    [Fact]
    public void StoredFormRoundTrips()
    {
        foreach (var (data, service) in new[] { (ReportSpec.Example("discovery.json"), "https://example.com"), (Encoding.UTF8.GetBytes(Reference().ToJsonString()), "http://127.0.0.1:8787") })
        {
            var discovery = Parsed(data, service);
            var again = Parsed(discovery.ToJson().ToCompactBytes(), service);
            Assert.Equal(discovery.ToJson().ToCompactString(), again.ToJson().ToCompactString());
            Assert.Equal(ReportConsentPin.Of(discovery), ReportConsentPin.Of(again));
        }

        var assumed = ReportDiscovery.Assumed(Service("https://deng.codexradar.com"), "Codex Radar");
        Assert.NotNull(assumed);
        Assert.Equal("https://deng.codexradar.com", assumed.Api.Text);
        Assert.Equal(4, assumed.Sections.Count);
        Assert.Equal(ReportLimits.Standard, assumed.Limits);
    }

    [Fact]
    public void DiscoveryOutcomesFollowTheStatus()
    {
        var service = Service();
        var example = ReportSpec.Example("discovery.json");
        Assert.IsType<ReportDiscoveryOutcome.Document>(ReportApi.DiscoveryOutcome(200, example, null, "\"v1\"", service));
        Assert.Equal("\"v1\"", ((ReportDiscoveryOutcome.Document)ReportApi.DiscoveryOutcome(200, example, null, "\"v1\"", service)).ETag);
        Assert.IsType<ReportDiscoveryOutcome.NotSupported>(ReportApi.DiscoveryOutcome(200, Encoding.UTF8.GetBytes("<html>"), null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.NotSupported>(ReportApi.DiscoveryOutcome(200, Edited(Example(), "protocol", "\"x\""), null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.Invalid>(ReportApi.DiscoveryOutcome(200, Edited(Example(), "service.terms", null), null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.NotModified>(ReportApi.DiscoveryOutcome(304, [], "\"v1\"", null, service));
        Assert.IsType<ReportDiscoveryOutcome.Failed>(ReportApi.DiscoveryOutcome(304, [], null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.Gone>(ReportApi.DiscoveryOutcome(410, [], null, null, service));
        Assert.Equal(new ReportDiscoveryOutcome.Failed(ReportFailure.Unavailable), ReportApi.DiscoveryOutcome(429, [], null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.NotSupported>(ReportApi.DiscoveryOutcome(404, [], null, null, service));
        Assert.IsType<ReportDiscoveryOutcome.NotSupported>(ReportApi.DiscoveryOutcome(403, [], null, null, service));
        Assert.Equal(new ReportDiscoveryOutcome.Failed(ReportFailure.Unavailable), ReportApi.DiscoveryOutcome(503, [], null, null, service));
        Assert.Equal(new ReportDiscoveryOutcome.Failed(ReportFailure.Unavailable), ReportApi.DiscoveryOutcome(302, [], null, null, service));
    }

    private static bool Review(string path, string? rawJson, bool hasApiKey = false)
    {
        var pin = ReportConsentPin.Of(Parsed(ReportSpec.Example("discovery.json")));
        return pin.NeedsReview(Parsed(Edited(Example(), path, rawJson)), hasApiKey);
    }

    [Fact]
    public void ConsentPinning()
    {
        var discovery = Parsed(ReportSpec.Example("discovery.json"));
        var pin = ReportConsentPin.Of(discovery);
        Assert.Equal("Example Leaderboard", pin.Name);
        Assert.Equal("https://api.example.com/agent-speed", pin.Api);
        Assert.Equal(["cost", "models", "sources", "traffic"], pin.Sections);
        Assert.Equal(30, pin.RetentionDays);
        Assert.Equal(ReportApiKeyMode.Optional, pin.ApiKeyMode);
        Assert.False(pin.NeedsReview(discovery, false));
        Assert.False(pin.NeedsReview(discovery, true));

        Assert.True(Review("service.name", Literal("Example Board")));
        Assert.True(Review("service.terms", Literal("https://example.com/terms-2026")));
        Assert.True(Review("service.privacy", Literal("https://example.org/privacy")));
        Assert.True(Review("api", Literal("https://collect.example.com/agent-speed")));
        Assert.True(Review("api", Literal("https://api.example.com/agent-speed/v2")));
        Assert.True(Review("api", Literal("https://api.example.com:8443/agent-speed")));
        Assert.False(Review("service.name", Literal("  Example\u200B Leaderboard ")));
        Assert.False(Review("api", Literal("https://API.example.com:443/agent-speed/")));

        Assert.True(Review("service.retentionDays", "31"));
        Assert.True(Review("service.retentionDays", null));
        Assert.False(Review("service.retentionDays", "7"));
        Assert.False(Review("service.retentionDays", "30"));
        var unstated = ReportConsentPin.Of(Parsed(Edited(Example(), "service.retentionDays", null)));
        Assert.False(unstated.NeedsReview(discovery, false));

        Assert.True(Review("apiKey.mode", Literal("required")));
        Assert.False(Review("apiKey.mode", Literal("required"), hasApiKey: true));
        Assert.False(Review("apiKey.mode", Literal("none")));
        Assert.False(Review("apiKey", null));

        var fewer = Parsed(Edited(Example(), "sections", """["cost","sources"]"""));
        Assert.False(pin.NeedsReview(fewer, false));
        var narrow = ReportConsentPin.Of(fewer);
        Assert.True(narrow.NeedsReview(Parsed(Edited(Example(), "sections", """["cost","sources","models"]""")), false));
        Assert.False(narrow.NeedsReview(Parsed(Edited(Example(), "sections", """["sources","unknown"]""")), false));

        Assert.False(Review("limits.intervalSeconds", "10"));
        Assert.False(Review("service.description", Literal("Something else")));
        Assert.False(Review("service.leaderboard", Literal("https://example.com/other")));
        Assert.False(Review("username.maxLength", "16"));
    }
}
