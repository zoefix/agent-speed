using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Costs of the Usage History window (DESIGN 9, METRICS 9.4, 9.7, 9.8), ported from the macOS history checks.</summary>
public class HistoryCostTests
{
    private static readonly DayRange Week = new(D(2026, 10, 15), Today);

    private static readonly SourceSet Claude = SourceSet.Of(SourceKind.ClaudeCode);

    /// <summary>Codex has more output, Claude Code more cost; Gemini has output but no cost data.</summary>
    private static readonly DailyUsage[] Flipped =
    [
        Usage("2026-10-20", SourceKind.Codex, output: 1000, requests: 3,
            models: [new ModelUsage("a", 2, 900) { Cost = new CostTally(1_000_000) }, new ModelUsage("b", 1, 100) { Cost = new CostTally(3_000_000) }])
            with { Cost = new CostTally(4_000_000) },
        Usage("2026-10-20", SourceKind.ClaudeCode, output: 100, requests: 2,
            models: [new ModelUsage("opus", 1, 100) { Cost = new CostTally(2_000_000) }]) with { Cost = new CostTally(5_000_000) },
        Usage("2026-10-20", SourceKind.Gemini, output: 50, requests: 1),
    ];

    // Days and totals.

    [Fact]
    public void ADayMixingRecordsWithoutCostCountsTheirRequestsAsUnpriced()
    {
        var day = DayOf(CostDays(), D(2026, 10, 21));
        Assert.Equal(HistoryCostKind.Priced, day.Cost.Kind);
        Assert.True(day.Cost.IsPartial);
        Assert.Equal(2_250_000, day.Cost.MicroUsd);
        Assert.Equal(5, day.Cost.Unpriced);
        Assert.Equal(new CostTally(2_250_000, 5), day.TotalCost);
        Assert.Equal(3, day.PriceVersion);
    }

    [Fact]
    public void ADayWithoutCostDataIsUnavailableAndOneWithNothingPricedIsUnknown()
    {
        var days = CostDays();
        var gone = DayOf(days, D(2026, 10, 18));
        Assert.Equal(HistoryCostKind.Unavailable, gone.Cost.Kind);
        Assert.Null(gone.TotalCost);
        Assert.Null(gone.PriceVersion);
        Assert.Null(gone.Cost.SortValue);

        var unpriced = DayOf(days, D(2026, 10, 19));
        Assert.Equal(HistoryCostKind.Unknown, unpriced.Cost.Kind);
        Assert.False(unpriced.Cost.IsPartial);
        Assert.Equal(0, unpriced.Cost.MicroUsd);
        Assert.Null(unpriced.Cost.SortValue);
        Assert.Equal(3, unpriced.PriceVersion);
    }

    [Fact]
    public void ARangeTotalIsTheSumOfItsDays()
    {
        var days = CostDays();
        var totals = HistoryAggregator.Totals(days);
        Assert.Equal(days.Sum(d => d.Cost.MicroUsd), totals.Cost.MicroUsd);
        Assert.Equal(7_750_000, totals.Cost.MicroUsd);
        Assert.Equal(3, totals.CostDays);
        Assert.Equal(4, totals.ActiveDays);
        Assert.True(totals.Cost.IsPartial);
        Assert.Equal(1 + 5 + 2 + 1, totals.Cost.Unpriced);
        Assert.Equal(0, HistoryAggregator.Totals([]).CostDays);
        Assert.Equal(HistoryCost.None, HistoryAggregator.Totals([]).Cost);
    }

    [Fact]
    public void SubscriptionSourcesKeepTheirTokensButAddNoCost()
    {
        var day = DayOf(CostDays(Claude), D(2026, 10, 20));
        Assert.Equal(HistoryCostKind.Priced, day.Cost.Kind);
        Assert.False(day.Cost.IsPartial);
        Assert.Equal(4_000_000, day.Cost.MicroUsd);
        Assert.Equal(Claude, day.Cost.Excluded);
        Assert.Equal(20, day.Output);
        Assert.Equal(6, day.Requests);
        Assert.Equal(10, day.OutputOf(SourceKind.ClaudeCode));
        Assert.Equal(0, day.Sources.Single(s => s.Source == SourceKind.ClaudeCode).Cost.MicroUsd);
        Assert.Equal(0, day.CostMicroUsdOf(SourceKind.ClaudeCode));
        Assert.Equal(4_000_000, day.CostMicroUsdOf(SourceKind.Codex));
        Assert.Equal(4_000_000, day.Value(HistoryChartMetric.Cost));
        Assert.Equal(20, day.Value(HistoryChartMetric.Output));
    }

    [Fact]
    public void ADayOfOnlyExcludedSourcesIsASubscription()
    {
        var day = DayOf(CostDays(Claude), D(2026, 10, 18));
        Assert.Equal(HistoryCostKind.Subscription, day.Cost.Kind);
        Assert.Equal(Claude, day.Cost.Excluded);
        Assert.Null(day.Cost.SortValue);
        Assert.Equal(1, day.Requests);
        Assert.Equal(10, day.Output);
    }

    [Fact]
    public void TheStoredCostOfADayIgnoresExclusion()
    {
        var day = DayOf(CostDays(Claude), D(2026, 10, 20));
        Assert.Equal(new CostTally(5_500_000, 1), day.TotalCost);
        Assert.Equal(3, day.PriceVersion);
        Assert.Null(DayOf(CostDays(Claude), D(2026, 10, 18)).PriceVersion);
    }

    [Fact]
    public void ModelCostsLeaveOutExcludedSources()
    {
        var day = DayOf(CostDays(Claude), D(2026, 10, 20));
        Assert.Equal(new ModelCost(new CostTally(4_000_000), 4), day.CostOf("m"));
        Assert.Equal(new ModelCost(null, 0), day.CostOf("mystery"));
        Assert.Null(day.CostOf("nope"));
        Assert.Equal(new ModelCost(new CostTally(1_500_000, 1), 2), DayOf(CostDays(), D(2026, 10, 20)).CostOf("mystery"));
    }

    [Fact]
    public void CostKindsAndSortValues()
    {
        Assert.Equal((HistoryCostKind.Priced, false, 0L, (double?)0), Describe(HistoryCost.None));
        Assert.Equal((HistoryCostKind.Unavailable, false, 0L, (double?)null), Describe(new HistoryCost(null, 3)));
        Assert.Equal((HistoryCostKind.Unknown, false, 0L, (double?)null), Describe(new HistoryCost(new CostTally(0, 3), 3)));
        Assert.Equal((HistoryCostKind.Priced, true, 2_000_000L, (double?)2_000_000), Describe(new HistoryCost(new CostTally(2_000_000, 1), 3)));
        Assert.Equal((HistoryCostKind.Subscription, false, 0L, (double?)null), Describe(new HistoryCost(null, 0, Claude)));
        Assert.Equal(HistoryCostKind.Priced, new HistoryCost(new CostTally(5), 2, Claude).Kind);

        static (HistoryCostKind, bool, long, double?) Describe(HistoryCost cost) => (cost.Kind, cost.IsPartial, cost.MicroUsd, cost.SortValue);
    }

    [Fact]
    public void AddingKeepsExcludedSourcesAndMergesLikeTheStore()
    {
        var sum = new HistoryCost(null, 0, Claude).Add(new HistoryCost(new CostTally(1_000_000), 2));
        Assert.Equal(new HistoryCost(new CostTally(1_000_000), 2, Claude), sum);
        Assert.Equal(HistoryCostKind.Priced, sum.Kind);

        var mixed = new HistoryCost(null, 3).Add(new HistoryCost(new CostTally(1_000_000), 2));
        Assert.Equal(new CostTally(1_000_000, 3), mixed.Tally);
        Assert.Equal(5, mixed.Requests);
        Assert.True(mixed.IsPartial);
    }

    [Fact]
    public void MergedFollowsTheStoreRule()
    {
        Assert.Equal(new CostTally(5, 1), HistoryCostMath.Merged(null, 0, new CostTally(5, 1), 2));
        Assert.Equal(new CostTally(5, 1), HistoryCostMath.Merged(new CostTally(5, 1), 2, null, 0));
        Assert.Null(HistoryCostMath.Merged(null, 2, null, 3));
        Assert.Null(HistoryCostMath.Merged(CostTally.Zero, 0, null, 4));
        Assert.Equal(new CostTally(5, 3), HistoryCostMath.Merged(new CostTally(5), 2, null, 3));
        Assert.Equal(new CostTally(5, 3), HistoryCostMath.Merged(null, 2, new CostTally(5, 1), 3));
        Assert.Equal(new CostTally(3, 1), HistoryCostMath.Merged(new CostTally(1, 1), 2, new CostTally(2), 1));
    }

    // Subscription options.

    [Fact]
    public void ASubscriptionSourceSelectedDirectlyShowsItsApiEquivalentCost()
    {
        var options = new HistoryCostOptions(Claude);
        Assert.Equal(Claude, options.Excluded(null));
        Assert.True(options.Excluded(SourceKind.ClaudeCode).IsEmpty);
        Assert.True(options.Excluded(SourceKind.Codex).IsEmpty);
        Assert.True((options with { IncludeSubscriptions = true }).Excluded(null).IsEmpty);
        Assert.True(new HistoryCostOptions().Excluded(null).IsEmpty);

        Assert.Equal("history.summary.costApi", options.CostLabelKey(SourceKind.ClaudeCode));
        Assert.Equal("history.summary.cost", options.CostLabelKey(SourceKind.Codex));
        Assert.Equal("history.summary.cost", options.CostLabelKey(null));
        Assert.Equal("history.summary.cost", new HistoryCostOptions().CostLabelKey(SourceKind.ClaudeCode));
    }

    [Fact]
    public void TheSubscriptionToggleIsOfferedOnceASourceIsMarked()
    {
        Assert.False(new HistoryCostOptions().OffersSubscriptionToggle);
        Assert.True(new HistoryCostOptions(Claude).OffersSubscriptionToggle);
        Assert.True(new HistoryCostOptions(Claude, IncludeSubscriptions: true).OffersSubscriptionToggle);
    }

    [Fact]
    public void ChartMetricTitles()
    {
        Assert.Equal("history.chart.output", HistoryChartMetrics.TitleKey(HistoryChartMetric.Output));
        Assert.Equal("history.chart.cost", HistoryChartMetrics.TitleKey(HistoryChartMetric.Cost));
        Assert.Equal(HistoryChartMetric.Output, new HistoryCostOptions().Metric);
    }

    // Unknown prices.

    [Fact]
    public void UnpricedModelsListUnknownPricesOfTheCountedSources()
    {
        Assert.Equal([new UnpricedModel("m", SourceKind.Codex, 2)], HistoryAggregator.UnpricedModels(CostRecords, null, Week, Claude));
        Assert.Equal(
            [new UnpricedModel("m", SourceKind.Codex, 2), new UnpricedModel("mystery", SourceKind.ClaudeCode, 1)],
            HistoryAggregator.UnpricedModels(CostRecords, null, Week));
        Assert.Equal([new UnpricedModel("mystery", SourceKind.ClaudeCode, 1)],
            HistoryAggregator.UnpricedModels(CostRecords, SourceKind.ClaudeCode, Week));
        Assert.Empty(HistoryAggregator.UnpricedModels(CostRecords, null, DayRange.Single(D(2026, 10, 21))));
    }

    [Fact]
    public void UnpricedModelsSkipRecordsWithoutCostDataAndKeepUnnamedRequests()
    {
        DailyUsage[] records =
        [
            Usage("2026-10-21", SourceKind.Codex, requests: 5, models: [new ModelUsage(" neko ", 2, 5) { Cost = new CostTally(0, 2) }])
                with { Cost = new CostTally(1_000_000, 3) },
            Usage("2026-10-21", SourceKind.ClaudeCode, requests: 4, models: [new ModelUsage("ghost", 4, 5) { Cost = new CostTally(0, 4) }]),
            Usage("2026-10-01", SourceKind.Codex, requests: 7) with { Cost = new CostTally(0, 7) },
        ];

        Assert.Equal(
            [new UnpricedModel("neko", SourceKind.Codex, 2), new UnpricedModel(string.Empty, SourceKind.Codex, 1)],
            HistoryAggregator.UnpricedModels(records, null, Week));
        Assert.Empty(HistoryAggregator.UnpricedModels(records, null, Week, SourceSet.Of(SourceKind.Codex)));
        Assert.Empty(HistoryAggregator.UnpricedModels(records, SourceKind.ClaudeCode, Week));
    }

    // Single-day breakdowns.

    [Fact]
    public void ABreakdownByCostSortsAndSizesByCost()
    {
        var breakdown = DayBreakdown.BySource(CostRecords, D(2026, 10, 20), HistoryChartMetric.Cost, Claude);
        Assert.Equal(HistoryChartMetric.Cost, breakdown.Metric);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode], breakdown.Rows.Select(r => r.Source));
        Assert.Equal(1, breakdown.Rows[0].Fraction);
        Assert.Equal(1, breakdown.Rows[0].Share);
        Assert.Equal(HistoryCostKind.Subscription, breakdown.Rows[^1].Cost.Kind);
        Assert.Equal(Claude, breakdown.Rows[^1].Cost.Excluded);
        Assert.Equal(0, breakdown.Rows[^1].Fraction);
        Assert.Equal(10, breakdown.Rows[^1].Output);
    }

    [Fact]
    public void BreakdownsBySourceFollowTheChosenMetric()
    {
        var day = D(2026, 10, 20);
        var byOutput = DayBreakdown.BySource(Flipped, day);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini], byOutput.Rows.Select(r => r.Source));
        Assert.Equal(HistoryChartMetric.Output, byOutput.Metric);

        var byCost = DayBreakdown.BySource(Flipped, day, HistoryChartMetric.Cost);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex, SourceKind.Gemini], byCost.Rows.Select(r => r.Source));
        Assert.Equal([1, 0.8, 0], byCost.Rows.Select(r => r.Fraction));
        Assert.Equal([5 / 9.0, 4 / 9.0, 0], byCost.Rows.Select(r => r.Share), new RoundedComparer());
        Assert.Equal([5_000_000L, 4_000_000, 0], byCost.Rows.Select(r => r.Cost.MicroUsd));
        Assert.Equal(HistoryCostKind.Unavailable, byCost.Rows[^1].Cost.Kind);
        Assert.Equal(1150, byCost.Output);
    }

    [Fact]
    public void BreakdownsByModelFollowTheChosenMetric()
    {
        var day = D(2026, 10, 20);
        Assert.Equal(["a", "b"], DayBreakdown.ByModel(Flipped, day, SourceKind.Codex).Rows.Select(r => r.Model));

        var byCost = DayBreakdown.ByModel(Flipped, day, SourceKind.Codex, HistoryChartMetric.Cost);
        Assert.Equal(["b", "a"], byCost.Rows.Select(r => r.Model));
        Assert.Equal([1, 1 / 3.0], byCost.Rows.Select(r => r.Fraction), new RoundedComparer());
        Assert.Equal([3_000_000L, 1_000_000], byCost.Rows.Select(r => r.Cost.MicroUsd));
        Assert.All(byCost.Rows, r => Assert.False(r.OtherModels));
    }

    [Fact]
    public void CostNoNamedModelAccountsForGoesToOtherModels()
    {
        var day = D(2026, 10, 20);
        Assert.Equal(["opus"], DayBreakdown.ByModel(Flipped, day, SourceKind.ClaudeCode).Rows.Select(r => r.Model));

        var byCost = DayBreakdown.ByModel(Flipped, day, SourceKind.ClaudeCode, HistoryChartMetric.Cost);
        Assert.Equal([false, true], byCost.Rows.Select(r => r.OtherModels));
        Assert.Equal([2_000_000L, 3_000_000], byCost.Rows.Select(r => r.Cost.MicroUsd));
        Assert.Equal([2 / 3.0, 1], byCost.Rows.Select(r => r.Fraction), new RoundedComparer());
    }

    [Fact]
    public void ASourceSelectedDirectlyIsNeverExcludedFromItsBreakdown()
    {
        var breakdown = DayBreakdown.For(CostRecords, DayRange.Single(D(2026, 10, 20)), SourceKind.ClaudeCode, HistoryChartMetric.Cost, Claude);
        Assert.NotNull(breakdown);
        Assert.Equal(BreakdownKind.Models, breakdown.Kind);
        var row = Assert.Single(breakdown.Rows);
        Assert.Equal("mystery", row.Model);
        Assert.Equal(HistoryCostKind.Priced, row.Cost.Kind);
        Assert.True(row.Cost.IsPartial);
        Assert.Equal(1_500_000, row.Cost.MicroUsd);
        Assert.Null(DayBreakdown.For(CostRecords, Week, null, HistoryChartMetric.Cost, Claude));
    }

    // Chart bars.

    [Fact]
    public void CostBarsAreInDisplayUnitsWithoutSpeeds()
    {
        var range = new DayRange(D(2026, 10, 17), Today);
        var bars = HistoryBars.From(CostDays(Claude), range, null, HistoryChartMetric.Cost, Twd(For(LanguageCodes.ZhHant)));

        Assert.Equal(HistoryChartMetric.Cost, bars.Metric);
        Assert.Equal([SourceKind.Codex], bars.Order);
        Assert.Equal(129.6, bars.ValueOf(D(2026, 10, 20), SourceKind.Codex), 9);
        Assert.Equal(72.9, bars.ValueOf(Today, SourceKind.Codex), 9);
        Assert.Equal(129.6, bars.Max, 9);
        Assert.All(bars.Speeds, s => Assert.Null(s));
        Assert.False(bars.HasSpeed);
        Assert.Equal([false, true, true, true, true], Enumerable.Range(0, bars.Count).Select(bars.HasData));
        Assert.Equal(0, bars.Total(2));
    }

    [Fact]
    public void TheCostChartHidesTheSpeedOfMeasuredDays()
    {
        DailyUsage[] measured =
        [
            Usage("2026-10-20", SourceKind.Codex, output: 3000, requests: 4, measuredMs: 15_000) with { Cost = new CostTally(3_000_000) },
        ];
        var days = HistoryAggregator.Daily(measured, null);
        var range = DayRange.Single(D(2026, 10, 20));
        Assert.True(HistoryBars.From(days, range, null).HasSpeed);

        var bars = HistoryBars.From(days, range, null, HistoryChartMetric.Cost, Money(For(LanguageCodes.En)));
        Assert.False(bars.HasSpeed);
        Assert.Null(bars.Speed(0));
        Assert.Equal([3.0], bars.Values(0));
    }

    [Fact]
    public void CostBarsOfOneSourceUseTheDayCost()
    {
        var range = new DayRange(D(2026, 10, 19), Today);
        var days = HistoryAggregator.Daily(CostRecords, SourceKind.Codex, range);
        var bars = HistoryBars.From(days, range, SourceKind.Codex, HistoryChartMetric.Cost, Money(For(LanguageCodes.En)));
        Assert.Equal([SourceKind.Codex], bars.Order);
        Assert.Equal([0.0], bars.Values(0));
        Assert.Equal([4.0], bars.Values(1));
        Assert.Equal([2.25], bars.Values(2));
        Assert.True(bars.HasData(0));
    }

    [Fact]
    public void ScalingMultipliesEveryValueAndKeepsTheFrame()
    {
        var range = new DayRange(D(2026, 10, 17), Today);
        var bars = HistoryBars.From(CostDays(Claude), range, null, HistoryChartMetric.Cost, Twd(For(LanguageCodes.ZhHant)));
        var half = bars.Scaled(0.5);
        Assert.Equal(range, half.Range);
        Assert.Equal(bars.Order, half.Order);
        Assert.Equal(HistoryChartMetric.Cost, half.Metric);
        Assert.Equal(64.8, half.ValueOf(D(2026, 10, 20), SourceKind.Codex), 9);
        Assert.Equal(64.8, half.Max, 9);
        Assert.Equal([false, true, true, true, true], Enumerable.Range(0, half.Count).Select(half.HasData));

        var output = HistoryBars.From(HistoryAggregator.Daily(Flipped, null), DayRange.Single(D(2026, 10, 20)), null);
        var doubled = output.Scaled(2);
        Assert.Equal(output.Speeds, doubled.Speeds);
        Assert.True(doubled.HasSpeed);
        Assert.Equal(output.Values(0).Select(v => v * 2), doubled.Values(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ScalingByANonPositiveOrNonFiniteFactorGivesZeros(double factor)
    {
        var range = new DayRange(D(2026, 10, 17), Today);
        var scaled = HistoryBars.From(CostDays(Claude), range, null, HistoryChartMetric.Cost, Money(For(LanguageCodes.En))).Scaled(factor);
        Assert.Equal(0, scaled.Max);
        Assert.All(Enumerable.Range(0, scaled.Count), i => Assert.All(scaled.Values(i), v => Assert.Equal(0, v)));
        Assert.Equal([false, true, true, true, true], Enumerable.Range(0, scaled.Count).Select(scaled.HasData));
    }

    [Fact]
    public void TheCostMetricNeedsTheDisplayCurrency()
    {
        Assert.Throws<ArgumentNullException>(() => HistoryBars.From(CostDays(), Week, null, HistoryChartMetric.Cost));
        Assert.Equal(HistoryChartMetric.Cost, HistoryBars.Empty(Week, HistoryChartMetric.Cost).Metric);
    }

    [Fact]
    public void StackOrderFollowsTheMetric()
    {
        var days = HistoryAggregator.Daily(Flipped, null);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini], HistoryChartMath.StackOrder(days));
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex], HistoryChartMath.StackOrder(days, HistoryChartMetric.Cost));
        Assert.Equal([SourceKind.Codex], HistoryChartMath.StackOrder(CostDays(Claude), HistoryChartMetric.Cost));
    }

    // Summary card.

    [Fact]
    public void TheFifthCardShowsTheRangeCostInTheDisplayCurrency()
    {
        var loc = For(LanguageCodes.ZhHant);
        var options = new HistoryCostOptions(Claude, Metric: HistoryChartMetric.Cost);
        var days = HistoryAggregator.Daily(CostRecords, null, Week, options.Excluded(null));
        var cards = HistorySummary.Build(days, [], Week, Today, loc, Twd(loc), options.CostLabelKey(null));

        Assert.Equal(5, cards.Count);
        var card = cards[^1];
        Assert.Equal(SummaryMetric.Cost, card.Metric);
        Assert.Equal("≥NT$203", card.Value);
        Assert.Equal("費用（API 等值）", card.Label);
        Assert.Equal(string.Empty, card.Unit);
        Assert.Equal("≥NT$203（US$6.25）" + Environment.NewLine + loc.Format("history.unpricedDay", 2), card.FullValue);
        Assert.Equal(loc["history.costFrozen"], card.Note);
        Assert.True(card.NeutralChange);
        Assert.Equal(loc.Format("a11y.orb.costAtLeast", "203 新臺幣"), card.Spoken);
        Assert.Null(card.Highlight);
    }

    [Fact]
    public void ThePerDayAverageDividesByDaysWithCostData()
    {
        var loc = For(LanguageCodes.ZhHant);
        var days = HistoryAggregator.Daily(CostRecords, null, Week, Claude);
        Assert.Equal(4, HistoryAggregator.Totals(days).ActiveDays);

        var card = HistorySummary.CostCard(days, [], Week, loc, Twd(loc));
        Assert.Equal("日均 NT$67", card.Secondary);
        Assert.Null(card.Change);
        Assert.Equal("history.summary.cost", HistorySummary.LabelKey(SummaryMetric.Cost));
    }

    [Fact]
    public void TheCostSparklineBreaksOnlyOnDaysWithoutCostData()
    {
        var loc = For(LanguageCodes.ZhHant);
        var counted = HistorySummary.CostCard(HistoryAggregator.Daily(CostRecords, null, Week, Claude), [], Week, loc, Twd(loc));
        Assert.Equal(Week.DayCount, counted.Spark.Count);
        Assert.Equal([0, 0, 0, 0, 0, 129.6, 72.9], counted.Spark.Select(v => Math.Round(v!.Value, 9)));

        var all = HistorySummary.CostCard(HistoryAggregator.Daily(CostRecords, null, Week), [], Week, loc, Money(loc));
        Assert.Null(all.Spark[3]);
        Assert.Equal(0, all.Spark[4]);
        Assert.Equal(5.5, all.Spark[5]!.Value, 9);
    }

    [Fact]
    public void ASingleDayComparesItsCostWithTheDayBefore()
    {
        var loc = For(LanguageCodes.ZhHant);
        var range = DayRange.Single(Today);
        var days = HistoryAggregator.Daily(CostRecords, null, range);
        var before = HistoryAggregator.Daily(CostRecords, null, DayRange.Single(D(2026, 10, 20)));
        var card = HistorySummary.CostCard(days, before, range, loc, Money(loc));

        Assert.Equal("≥US$2.25", card.Value);
        Assert.Equal((2.25 - 5.5) / 5.5, card.Change!.Value, 9);
        Assert.Equal("比前一天 −59%", card.Secondary);
        Assert.True(card.NeutralChange);
        Assert.Equal(6, card.Highlight);

        var gone = HistoryAggregator.Daily(CostRecords, null, DayRange.Single(D(2026, 10, 18)));
        var withoutCostBefore = HistorySummary.CostCard(days, gone, range, loc, Money(loc));
        Assert.Null(withoutCostBefore.Change);
        Assert.Equal(string.Empty, withoutCostBefore.Secondary);
    }

    [Fact]
    public void TheCostCardWithoutAnAmountSaysWhy()
    {
        var loc = For(LanguageCodes.ZhHant);
        var money = Money(loc);
        Assert.Equal(("–", loc.Format("history.unpricedDay", 2), loc["cost.unknown"]), Parts(19));
        Assert.Equal(("–", loc["history.costUnavailable"], loc["cost.unknown"]), Parts(18));
        Assert.Equal((loc["cost.subscription.tag"], loc.Format("cost.subscriptionExcluded", "Claude Code"), loc["cost.subscription.tag"]),
            Parts(18, Claude));

        (string, string, string?) Parts(int day, SourceSet excluded = default)
        {
            var range = DayRange.Single(D(2026, 10, day));
            var card = HistorySummary.CostCard(HistoryAggregator.Daily(CostRecords, null, range, excluded), [], range, loc, money);
            Assert.True(card.NeutralChange);
            Assert.Equal(string.Empty, card.Secondary);
            return (card.Value, card.FullValue, card.Spoken);
        }
    }

    [Fact]
    public void TheCostCardOfASubscriptionSourceReadsApiEquivalent()
    {
        var loc = For(LanguageCodes.ZhHant);
        var options = new HistoryCostOptions(Claude);
        var source = SourceKind.ClaudeCode;
        var days = HistoryAggregator.Daily(CostRecords, source, Week, options.Excluded(source));
        var card = HistorySummary.Build(days, [], Week, Today, loc, Twd(loc), options.CostLabelKey(source))[^1];
        Assert.Equal("API 等值費用", card.Label);
        Assert.Equal("≥NT$49", card.Value);
    }

    private sealed class RoundedComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;

        public int GetHashCode(double obj) => 0;
    }
}
