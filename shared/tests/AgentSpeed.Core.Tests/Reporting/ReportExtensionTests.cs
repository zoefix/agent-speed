using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

public sealed class ReportExtensionTests
{
    public static IEnumerable<object[]> Capabilities()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("execution-mode-cases.json")));
        return document.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()!, c.Clone() }).ToArray();
    }
    [Theory]
    [MemberData(nameof(Capabilities))]
    public void SharedCapabilityFixturesRequireAnExactIntegerAndModelsPermission(string id, JsonElement value)
    {
        var raw = JsonNode.Parse(ReportSpec.Example("discovery.json"))!.AsObject();
        raw["extensions"] = JsonNode.Parse(value.GetProperty("extensions").GetRawText());
        var service = ReportServiceUrl.Parse("https://example.com")!;
        var discovery = ReportDiscovery.Parse(System.Text.Encoding.UTF8.GetBytes(raw.ToJsonString()), service, out var problem);
        Assert.Null(problem);
        Assert.NotNull(discovery);
        var models = value.GetProperty("modelsEnabled").GetBoolean();
        Assert.Equal(value.GetProperty("expected").GetBoolean(), discovery.Extensions.ExecutionModes && models);
        var roundtrip = ReportDiscovery.Parse(discovery.ToJson().ToCompactBytes(), service, out _);
        Assert.Equal(discovery.Extensions, roundtrip!.Extensions);
        Assert.NotEmpty(id);
    }

    [Fact]
    public void LedgerFreezesFirstModeAndWindowsConserveEachIndependentDimension()
    {
        var ledger = new ReportLedger();
        var initial = new ParsedResponse("stream", SourceKind.ClaudeCode, "a", "m", 1000, 2000, new TokenCounts(Output: 10), Final: false);
        ledger.Count(initial, "m");
        var baseline = ledger.Counters.Copy();
        var late = initial with { EndMs = 4000, Tokens = new TokenCounts(Output: 100), Final = true, Modes = ExecutionModeNative.ClaudeSpeed("fast") };
        ledger.Grow(late, "m");
        ledger.Grow(late, "m");
        Assert.Equal(1, ledger.Counters.Total.Requests);
        var delta = ledger.Counters.Since(baseline);
        Assert.Equal((0L, 90L, 100L, 3000L), Numbers(delta.Total));
        foreach (var kind in Enum.GetValues<ExecutionModeKind>())
        {
            var row = Assert.Single(delta.ByMode, p => p.Key.Kind == kind);
            Assert.Equal(("unknown", "unknown"), (row.Key.Mode, row.Key.Evidence));
            Assert.Equal(Numbers(delta.Total), Numbers(row.Value));
        }
        var model = Model(ledger.Counters, new(true, true));
        Assert.Equal(1, model.GetProperty("requests").GetInt64());
        Assert.Equal(5, model.GetProperty("modeUsage").GetArrayLength());
        Assert.Equal("claudeCode", Assert.Single(model.GetProperty("harnessRequests").EnumerateArray()).GetProperty("source").GetString());
    }

    [Fact]
    public void HarnessSplitsOneModelAcrossSourcesAndExtensionsAreAbsentForLegacyOrPrivacyOff()
    {
        var counters = new ReportCounters();
        counters.Add(new(Requests: 3, Output: 90), new("m", SourceKind.ClaudeCode), ExecutionModeNative.ClaudeSpeed("fast"));
        counters.Add(new(Requests: 2, Output: 30), new("m", SourceKind.ClaudeDesktop), ExecutionModeNative.ClaudeSpeed("standard"));
        var model = Model(counters, new(true, true));
        Assert.Equal(new long[] { 3, 2 }, model.GetProperty("harnessRequests").EnumerateArray().Select(r => r.GetProperty("requests").GetInt64()));
        foreach (var kind in model.GetProperty("modeUsage").EnumerateArray().GroupBy(r => r.GetProperty("kind").GetString()))
        {
            Assert.Equal(5, kind.Sum(r => r.GetProperty("requests").GetInt64()));
            Assert.Equal(120, kind.Sum(r => r.GetProperty("output").GetInt64()));
        }
        Assert.False(Model(counters, new()).TryGetProperty("modeUsage", out _));
        Assert.False(Model(counters, new()).TryGetProperty("harnessRequests", out _));
        using var doc = JsonDocument.Parse(Body(counters, new(true, true), models: false));
        Assert.False(doc.RootElement.TryGetProperty("models", out _));
    }

    [Fact]
    public void TooManyHarnessesOrModeRowsOmitTheWholeOptionalBreakdown()
    {
        var counters = new ReportCounters();
        foreach (var source in Enum.GetValues<SourceKind>().Take(9)) counters.Add(new(Requests: 1), new("m", source));
        Assert.False(Model(counters, new(true, true)).TryGetProperty("harnessRequests", out _));
        foreach (var i in Enumerable.Range(0, 65)) counters.Add(new(Requests: 1), new("m", SourceKind.Codex),
            ExecutionModes.Of(ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, "variant-" + i, ExecutionModeEvidence.Selected, "openai")));
        Assert.False(Model(counters, new(true, true)).TryGetProperty("modeUsage", out _));
    }

    [Fact]
    public void ProfileEndpointCapabilitiesAreIndependentAndIdsCannotEscapeThePath()
    {
        Assert.False(ReportProfileEndpoint.Allows(new(), "GET", "/v1/profile"));
        Assert.True(ReportProfileEndpoint.Allows(new(ProfileAvatar: true), "GET", "/v1/profile"));
        Assert.False(ReportProfileEndpoint.Allows(new(ProfileAvatar: true), "POST", "/v1/profile/identity"));
        Assert.False(ReportProfileEndpoint.Allows(new(SharedIdentity: true), "PUT", "/v1/profile/avatar"));
        Assert.True(ReportProfileEndpoint.Allows(new(SharedIdentity: true), "POST", "/v1/profile/pairings/11111111-1111-4111-8111-111111111111/approve"));
        Assert.False(ReportProfileEndpoint.Allows(new(SharedIdentity: true), "POST", "/v1/profile/pairings/../../approve"));
        Assert.False(ReportProfileEndpoint.Allows(new(SharedIdentity: true), "GET", "/v1/profile/pairings/11111111-1111-4111-8111-111111111111?x=1"));
    }

    private static (long, long, long, long) Numbers(ReportUsage usage) => (usage.Requests, usage.Output, usage.MeasuredOutput, usage.MeasuredMs);
    private static JsonElement Model(ReportCounters counters, ReportExtensions extensions)
    {
        using var doc = JsonDocument.Parse(Body(counters, extensions));
        return Assert.Single(doc.RootElement.GetProperty("models").EnumerateArray()).Clone();
    }
    private static byte[] Body(ReportCounters counters, ReportExtensions extensions, bool models = true)
    {
        var content = new ReportWindowContent(counters, new Dictionary<SourceKind, Core.Traffic.TrafficBytes>(), Rate.Zero, 0, null);
        var envelope = new ReportEnvelope(new("1.3.0", "Windows", 11), "11111111-1111-4111-8111-111111111111", "Tester", "22222222-2222-4222-8222-222222222222", 1, 10000, 5000, ReportActivity.Active, 0);
        return ReportBody.Data(envelope, content, ReportSample.Empty, new ReportOptions(models ? new HashSet<ReportSection> { ReportSection.Models } : []) { Extensions = extensions });
    }
}
