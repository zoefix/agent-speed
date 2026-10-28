using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests;

public sealed class DeepSeekFlashPricingTests
{
    private const string DatedModel = "deepseek-v4.1-flash-1017";
    private const string CanonicalModel = "deepseek-flash";
    private const long At = 1_792_849_200_000;

    private static readonly TokenCounts Usage = new(Input: 1000, CachedInput: 900, Output: 100);

    [Theory]
    [InlineData(DatedModel)]
    [InlineData("deepseek-v4.1-flash")]
    [InlineData(CanonicalModel)]
    public void FlashNamesShareCanonicalReportingAndCachedInputCost(string model)
    {
        var table = PriceBook.Bundled;
        Assert.Equal(CanonicalModel, table.ReportModelId(model, "deepseek"));
        Assert.Equal(78, table.Cost(model, "deepseek", Usage, At, fast: false));
        Assert.Equal(210, table.Cost(model, "deepseek", Usage with { CachedInput = 0 }, At, fast: false));
        Assert.Equal(63, table.Cost(model, "deepseek", Usage with { CachedInput = 1000 }, At, fast: false));
    }

    [Theory]
    [InlineData("2026-04-26T12:14Z", 67)]
    [InlineData("2026-04-26T12:15Z", 45)]
    [InlineData("2026-08-16T16:00Z", 94)]
    [InlineData("2026-09-10T03:59Z", 189)]
    [InlineData("2026-09-10T04:00Z", 78)]
    [InlineData("2026-10-23T02:00Z", 155)]
    [InlineData("2026-10-23T04:00Z", 78)]
    [InlineData("2026-10-24T02:00Z", 78)]
    [InlineData("2026-10-01T02:00Z", 78)]
    public void TheDatedAliasKeepsHistoricalPeriodsAndOffPeakRules(string at, long expected)
    {
        var table = PriceBook.Bundled;
        var endMs = UtcDay.InstantMs(at);
        Assert.NotNull(endMs);
        Assert.Equal(expected, table.Cost(DatedModel, "deepseek", Usage, endMs, fast: false));
        Assert.Equal(expected, table.Cost("deepseek-v4.1-flash", "deepseek", Usage, endMs, fast: false));
    }

    [Theory]
    [InlineData("deepseek-v4.1-flash-9999")]
    [InlineData("deepseek-v4.1-flash-1017-other")]
    [InlineData("deepseek-unrecognized-model")]
    public void OtherDeepSeekNamesRemainUnknownAndUnpriced(string model)
    {
        var table = PriceBook.Bundled;
        Assert.Equal(ReportModelIds.Unknown, table.ReportModelId(model, "deepseek"));
        Assert.Null(table.Cost(model, "deepseek", Usage, At, fast: false));
    }

    [Fact]
    public void ANewlyRecognizedLiveResponseAddsOnlyItsPreviouslyMissingCost()
    {
        var current = PriceBook.Bundled;
        var previous = WithoutDatedFlashAlias(current);
        var book = new PriceBook([previous]);
        var today = new UtcDay(At).DaysSinceEpoch * UtcDay.MsPerDay;
        var day = new UtcDay(At).Label;
        var engine = new SpeedEngine(today, book, TimeZoneInfo.Utc);
        var response = new ParsedResponse("flash-response", SourceKind.DeepSeek, "flash-agent", DatedModel,
            At - 2000, At, Usage, Provider: "deepseek");
        engine.Upsert(response, isCompletion: true, seeded: false, liveNowMs: At);
        var before = engine.Compute(At, today, observeLive: true).Report.Counters;
        Assert.Equal(1, before.Total.Requests);
        Assert.Equal(1, before.Total.UnpricedRequests);
        Assert.Equal(1000, before.Total.Input);
        Assert.Equal(900, before.Total.CachedInput);
        Assert.Equal(100, before.Total.Output);
        Assert.Equal(0, before.Total.CostMicroUsd);
        Assert.Equal(ReportModelIds.Unknown, previous.ReportModelId(DatedModel, "deepseek"));

        Assert.True(book.Add(current));
        var after = engine.Compute(At + 1, today, observeLive: true).Report.Counters;
        var added = after.Since(before).Total;
        Assert.Equal(78, added.CostMicroUsd);
        Assert.Equal(0, added.Requests);
        Assert.Equal(0, added.Input);
        Assert.Equal(0, added.CachedInput);
        Assert.Equal(0, added.Output);
        Assert.Equal(1, after.Total.UnpricedRequests);
        Assert.Equal(previous.Version, book.VersionForDay(day));

        engine.Upsert(response, isCompletion: true, seeded: false, liveNowMs: At + 2);
        var repeated = engine.Compute(At + 2, today, observeLive: true).Report.Counters;
        Assert.True(repeated.Since(after).Total.IsZero);
        Assert.Equal(78, repeated.Total.CostMicroUsd);
        Assert.Equal(1, repeated.Total.Requests);
    }

    private static PriceTable WithoutDatedFlashAlias(PriceTable current)
    {
        var table = JsonNode.Parse(current.Data)!.AsObject();
        table["version"] = current.Version - 1;
        var model = table["models"]!.AsArray().First(m => (string?)m!["id"] == CanonicalModel)!;
        var aliases = model["aliases"]!.AsArray();
        aliases.Remove(aliases.Single(a => (string?)a == DatedModel));
        var tests = table["tests"]!.AsArray();
        foreach (var test in tests.Where(t => (string?)t!["model"] == DatedModel).ToArray())
        {
            tests.Remove(test);
        }

        var result = PriceTable.Load(Encoding.UTF8.GetBytes(table.ToJsonString()));
        Assert.Null(result.Error);
        return Assert.IsType<PriceTable>(result.Table);
    }
}
