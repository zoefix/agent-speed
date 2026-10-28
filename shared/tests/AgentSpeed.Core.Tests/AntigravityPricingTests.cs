using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests;

public sealed class AntigravityPricingTests
{
    private const long At = 1_792_849_200_000;

    [Theory]
    [InlineData("Gemini 3.8 Flash", "gemini-3.8-flash", 518)]
    [InlineData("Gemini 4 Argon", "gemini-4-argon", 1290)]
    public void DisplayNameFallbackReachesCanonicalPricing(string display, string canonical, long expected)
    {
        var response = Parse(responseModel: null, display);
        Assert.Equal(display, response.Model);
        Assert.Null(response.Provider);
        Assert.Equal(new TokenCounts(Input: 1000, CachedInput: 900, Output: 100), response.Tokens);
        Assert.Equal(canonical, PriceBook.Bundled.ReportModelId(response.Model, response.Provider));
        Assert.Equal(expected, PriceBook.Bundled.Cost(response));
    }

    [Theory]
    [InlineData("gemini-4-argon", "Gemini 3.8 Flash", "gemini-4-argon", 1290L)]
    [InlineData("gemini-3.8-flash", "Gemini 4 Argon", "gemini-3.8-flash", 518L)]
    [InlineData("gemini-internal-router", "Gemini 4 Argon", "unknown", null)]
    public void TheResponseModelTakesPriorityOverTheDisplayName(string responseModel, string display, string canonical, long? expected)
    {
        var response = Parse(responseModel, display);
        Assert.Equal(responseModel, response.Model);
        Assert.Equal(canonical, PriceBook.Bundled.ReportModelId(response.Model, response.Provider));
        Assert.Equal(expected, PriceBook.Bundled.Cost(response));
    }

    [Theory]
    [InlineData("Gemini Unannounced Model")]
    [InlineData("gemini-pro-default")]
    [InlineData("gemini-3.1-flash")]
    [InlineData("neko-model-gemini-4-argon")]
    [InlineData("codex-auto-review")]
    public void UnconfirmedDisplayNamesAndInternalIdsStayUnknown(string display)
    {
        var response = Parse(responseModel: null, display);
        Assert.Equal(ReportModelIds.Unknown, PriceBook.Bundled.ReportModelId(response.Model, response.Provider));
        Assert.Null(PriceBook.Bundled.Cost(response));
    }

    [Theory]
    [InlineData("Gemini 4 Argon", "2026-12-01T00:00Z", 2580)]
    [InlineData("Gemini 3.8 Flash", "2027-01-01T00:00Z", 1035)]
    public void DisplayNamesRetainTheirDatedPriceChanges(string display, string at, long expected)
    {
        var response = Parse(responseModel: null, display) with { EndMs = UtcDay.InstantMs(at)!.Value };
        Assert.Equal(expected, PriceBook.Bundled.Cost(response));
    }

    private static ParsedResponse Parse(string? responseModel, string display)
    {
        var generation = Pb.Message(
            Pb.Field(1, Pb.Message(responseModel is null ? [] : Pb.Field(19, responseModel), Pb.Field(21, display))),
            Pb.Field(2, Pb.Varint(1)));
        var metadata = AgBlobs.Step(At - 2000, At,
            usage: AgBlobs.Usage(input: 100, output: 100, cacheRead: 900, responseId: "priced-antigravity-response"));
        var parser = new AntigravityParser("/fixtures/3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7b.db", new Deduper());
        var batch = Assert.IsType<AntigravityBatch>(parser.NewBatch());
        batch.Generations.Add(AntigravityDecoder.Generation(0, generation));
        batch.Steps.Add(new AntigravityStepRow(1, StepCodes.PlannerResponse, StepCodes.Done, AntigravityDecoder.Step(metadata)));
        var sink = new RecordingSink();
        batch.ApplyTo(sink);
        var emission = Assert.Single(sink.Emissions);
        Assert.True(emission.IsCompletion);
        Assert.Equal(SourceKind.Antigravity, emission.Response.Source);
        return emission.Response;
    }
}
