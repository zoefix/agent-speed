using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Tests.Quota.Estimate;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// The <c>tokenUsage</c> of quota entries (REPORTING-EXTENSIONS 12): the <c>quotaTokenUsage</c> extension in the document,
/// the member only while it is advertised and last in each entry, the same entries on both transports, the size rule that
/// drops it before <c>quotas</c>, null blocks for unmeasured spend, no raw model name, and the reference's strict check.
/// </summary>
public sealed class ReportQuotaTokenUsageTests
{
    private static readonly ReportServiceUrl Service = ReportServiceUrl.Parse("https://example.com")!;

    private static readonly ReportEnvelope Envelope = new(
        new ReportClientInfo("1.3.0", "Windows", 11), "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", "Tester",
        "c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", 7, 1_794_121_212_345, 5000, ReportActivity.Active, 0);

    private static readonly ReportSample Sample = ReportSample.Empty;

    private static readonly ReportWindowContent Content = new ReportWindow(Sample).Content(Sample);

    private static readonly ReportOptions Shared = new(ReportSections.All.ToHashSet())
    {
        Extensions = new(PerfScore: true, QuotaEstimate: true, QuotaTokenUsage: true),
    };

    private static ReportOptions Plain => Shared with { Extensions = Shared.Extensions with { QuotaTokenUsage = false } };

    private static IReadOnlyList<QuotaReportEntry> PipelineEntries(string name)
    {
        var input = QuotaEstimatePipelineTests.Case("tokens", name).GetProperty("input");
        var (_, evaluation) = QuotaEstimatePipelineTests.Evaluate(input);
        return evaluation.Entries(input.GetProperty("now").GetInt64(), input.GetProperty("origin").GetString()!);
    }

    private static JsonElement Parse(byte[] data) => JsonDocument.Parse(data).RootElement.Clone();

    private static List<JsonElement> Quotas(byte[] data) => [.. Parse(data).GetProperty("quotas").EnumerateArray()];

    // Discovery

    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("\"1\"", false)]
    [InlineData("2", false)]
    [InlineData("1.5", false)]
    [InlineData("0", false)]
    [InlineData("{}", false)]
    public void TheExtensionIsOnOnlyForTheInteger1AndIsKeptWithTheStoredDocument(string? value, bool expected)
    {
        var raw = JsonNode.Parse(ReportSpec.Example("discovery.json"))!.AsObject();
        raw["extensions"] = JsonNode.Parse(value is null
            ? """{"quotaEstimate":1}"""
            : $$"""{"quotaEstimate":1,"quotaTokenUsage":{{value}}}""");
        var discovery = ReportDiscovery.Parse(Encoding.UTF8.GetBytes(raw.ToJsonString()), Service, out var problem);
        Assert.Null(problem);
        Assert.Equal(expected, discovery!.Extensions.QuotaTokenUsage);
        Assert.True(discovery.Extensions.QuotaEstimate);

        var stored = discovery.ToJson();
        Assert.Equal(expected, stored["extensions"]!["quotaTokenUsage"] is not null);
        Assert.Equal(discovery.Extensions, ReportDiscovery.Parse(stored.ToCompactBytes(), Service, out _)!.Extensions);
    }

    // Body

    [Fact]
    public void EntriesCarryTheCountsOnlyWhileAdvertisedAndLast()
    {
        var entries = PipelineEntries("Codex and Claude accounts");
        Assert.Equal(5, entries.Count);
        var plain = Quotas(ReportBody.Data(Envelope, Content, Sample, Plain with { Quotas = entries }));
        Assert.All(plain, e => Assert.False(e.TryGetProperty("tokenUsage", out _)));

        var http = ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries });
        var carried = Quotas(http);
        Assert.Equal(5, carried.Count);
        Assert.All(carried, e => Assert.Equal("tokenUsage", e.EnumerateObject().Last().Name));
        Assert.All(carried.Where(e => e.GetProperty("source").GetString() == "codex"),
            e => Assert.Equal(JsonValueKind.Null, e.GetProperty("tokenUsage").GetProperty("cacheWriteInputTokens").ValueKind));
        Assert.All(carried.Where(e => e.GetProperty("source").GetString() != "codex"),
            e => Assert.Equal(JsonValueKind.Null, e.GetProperty("tokenUsage").GetProperty("reasoningTokens").ValueKind));
        Assert.DoesNotContain("claude-mystery-1", Encoding.UTF8.GetString(http), StringComparison.Ordinal);
        Assert.Contains(carried, e => e.GetProperty("tokenUsage").GetProperty("models").EnumerateArray()
            .Any(m => m.GetProperty("model").ValueKind == JsonValueKind.Null));

        // The WebSocket body (with realtime last) carries the same entries.
        var stream = Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, Stream = new ReportStreamBody(120, 65_536) }));
        Assert.Equal(Parse(http).GetProperty("quotas").GetRawText(), stream.GetProperty("quotas").GetRawText());
        Assert.Equal("realtime", stream.EnumerateObject().Last().Name);

        // A service without quotaEstimate gets neither.
        var none = Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, Extensions = new(QuotaTokenUsage: true) }));
        Assert.False(none.TryGetProperty("quotas", out _));
    }

    [Fact]
    public void ABodyTooLargeDropsTheCountsFirstAndKeepsTheQuotas()
    {
        var entries = PipelineEntries("Codex and Claude accounts");
        var without = ReportBody.Data(Envelope, Content, Sample, Plain with { Quotas = entries }).Length;
        Assert.True(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries }).Length > without);

        var fitted = ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, BodyLimit = without });
        var kept = Quotas(fitted);
        Assert.Equal(5, kept.Count);
        Assert.All(kept, e => Assert.False(e.TryGetProperty("tokenUsage", out _)));
        Assert.True(Parse(fitted).TryGetProperty("models", out _));
        Assert.True(Parse(fitted).TryGetProperty("sources", out _));

        var streamWithout = ReportBody.Data(Envelope, Content, Sample, Plain with { Quotas = entries, Stream = new ReportStreamBody(null, 65_536) });
        var limit = ReportWebSocketProtocol.MessageBytes(streamWithout);
        var stream = ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, Stream = new ReportStreamBody(null, limit) });
        Assert.All(Quotas(stream), e => Assert.False(e.TryGetProperty("tokenUsage", out _)));

        // Still too large without the counts: quotas go next, as before.
        var bare = ReportBody.Data(Envelope, Content, Sample, Shared).Length;
        Assert.False(Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, BodyLimit = bare })).TryGetProperty("quotas", out _));
    }

    [Fact]
    public void QuotasSentWithoutTheirCountsCountAsSent()
    {
        var entries = PipelineEntries("Codex and Claude accounts");
        var limit = ReportBody.Data(Envelope, Content, Sample, Plain with { Quotas = entries }).Length;
        var run = new ReportQuotaRun();
        var first = run.Fitted(Envelope, Content, Sample, Shared with { BodyLimit = limit }, 0, true, () => entries);
        Assert.Equal(5, first.Json["quotas"] is null ? 0 : Quotas(first.Data).Count);
        var second = run.Fitted(Envelope, Content, Sample, Shared, 10_000, true, () => entries);
        Assert.Null(second.Json["quotas"]);
    }

    [Fact]
    public void UnmeasuredCountsAreNullNeverZero()
    {
        var entries = PipelineEntries("a response recorded without counts");
        Assert.Equal([true, true, false], entries.Select(e => e.TokenUsage is null));
        Assert.True(entries[0].UsedMicroUsd > 0, "the spend still counts the response");
        var quotas = Quotas(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries }));
        Assert.Equal(JsonValueKind.Null, quotas[0].GetProperty("tokenUsage").ValueKind);

        var idle = Assert.Single(PipelineEntries("zero usage"));
        Assert.Equal((QuotaTokenCounts.Zero, 0), (idle.TokenUsage!.Counts, idle.TokenUsage.Models.Count));
    }

    // The reference

    [Fact]
    public void ReferenceAcceptsBodiesWithTokenUsage()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        var entries = PipelineEntries("Codex and Claude accounts");
        var bodies = new List<byte[]>
        {
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = entries, Stream = new ReportStreamBody(120, 65_536) }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = PipelineEntries("a response recorded without counts") }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = PipelineEntries("zero usage") }),
        };
        var failures = ReferencePython.StrictErrors(bodies);
        Assert.True(failures.Count == 0, string.Join('\n', failures));

        var broken = JsonNode.Parse(bodies[0])!;
        var usage = broken["quotas"]![0]!["tokenUsage"]!;
        usage["models"]![0]!["inputTokens"] = usage["models"]![0]!["inputTokens"]!.GetValue<long>() + 1;
        Assert.Contains(ReferencePython.StrictErrors([Encoding.UTF8.GetBytes(broken.ToJsonString())]),
            e => e.Contains("/quotas/0/tokenUsage/models", StringComparison.Ordinal));
    }
}
