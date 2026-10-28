using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Money formatting, accessibility text and the hover card of the Usage History window (DESIGN 9).</summary>
public class HistoryMoneyTests
{
    private static readonly SourceSet Claude = SourceSet.Of(SourceKind.ClaudeCode);

    /// <summary>A measured, partly priced day of two sources with a free model (2026-10-20, peak 300 tok/s).</summary>
    private static readonly DailyUsage[] TooltipRecords =
    [
        Usage("2026-10-20", SourceKind.Codex, output: 3000, requests: 4, measuredMs: 15_000, peak: 300,
            models: [new ModelUsage("gpt", 3, 2000, 2000, 10_000) { Cost = new CostTally(3_000_000) },
                new ModelUsage("mini", 1, 1000, 1000, 5_000) { Cost = CostTally.Zero }]) with { Cost = new CostTally(3_000_000) },
        Usage("2026-10-20", SourceKind.ClaudeCode, output: 1000, requests: 2, measuredMs: 10_000,
            models: [new ModelUsage("opus", 2, 1000, 1000, 10_000) { Cost = new CostTally(1_000_000, 1) }])
            with { Cost = new CostTally(1_000_000, 1) },
    ];

    private static DaySummary TooltipDay => Assert.Single(HistoryAggregator.Daily(TooltipRecords, null));

    // Amounts.

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "NT$32（US$1.00）")]
    [InlineData(LanguageCodes.ZhHans, "NT$32（US$1.00）")]
    [InlineData(LanguageCodes.Ja, "NT$32（US$1.00）")]
    [InlineData(LanguageCodes.En, "NT$32 ($1.00)")]
    public void AmountsShowUsDollarsBesideAnotherCurrency(string language, string expected)
    {
        Assert.Equal(expected, Twd(For(language)).AmountWithUsd(1_000_000));
    }

    [Fact]
    public void UsDollarAmountsStandAlone()
    {
        Assert.Equal("$1.00", Money(For(LanguageCodes.En)).AmountWithUsd(1_000_000));
        Assert.Equal("US$1.00", Money(For(LanguageCodes.ZhHant)).AmountWithUsd(1_000_000));
    }

    [Fact]
    public void AmountsConvertWithTheOneRateInUse()
    {
        var twd = Twd(For(LanguageCodes.ZhHant));
        Assert.False(twd.IsUsd);
        Assert.Equal(32.4m, twd.Rate);
        Assert.Equal("NT$", twd.Symbol);
        Assert.Equal("NT$203", twd.Amount(6_250_000));
        Assert.Equal("NT$0", twd.Amount(0));
        Assert.Equal(129.6, twd.Value(4_000_000), 9);

        var usd = Money(For(LanguageCodes.En));
        Assert.True(usd.IsUsd);
        Assert.Equal(1m, usd.Rate);
        Assert.Equal("$4.20", usd.Amount(4_200_000));
        Assert.Equal("$0.00", usd.Amount(0));
        Assert.Equal("$1,234.57", usd.Amount(1_234_567_891));
    }

    [Fact]
    public void CostTextReadsAtLeastADashOrTheTag()
    {
        var money = Money(For(LanguageCodes.En));
        Assert.Equal("$4.00", money.Text(new HistoryCost(new CostTally(4_000_000), 4), "tag"));
        Assert.Equal("≥$2.25", money.Text(new HistoryCost(new CostTally(2_250_000, 5), 8), "tag"));
        Assert.Equal("–", money.Text(new HistoryCost(new CostTally(0, 2), 2), "tag"));
        Assert.Equal("–", money.Text(new HistoryCost(null, 1), "tag"));
        Assert.Equal("tag", money.Text(new HistoryCost(null, 0, Claude), "tag"));
        Assert.Equal("$0.00", money.Text(HistoryCost.None, "tag"));
    }

    [Fact]
    public void SpokenAmountsUseTheCurrencyName()
    {
        var loc = For(LanguageCodes.ZhHant);
        var usd = Money(loc);
        Assert.Equal("12.34 美元", usd.Spoken(12_340_000));
        Assert.Equal("203 新臺幣", Twd(loc).Spoken(6_250_000));

        Assert.Equal("至少 2.25 美元", HistoryText.SpokenCost(new HistoryCost(new CostTally(2_250_000, 5), 8), usd, loc));
        Assert.Equal("4.00 美元", HistoryText.SpokenCost(new HistoryCost(new CostTally(4_000_000), 4), usd, loc));
        Assert.Equal(loc["cost.unknown"], HistoryText.SpokenCost(new HistoryCost(new CostTally(0, 2), 2), usd, loc));
        Assert.Equal(loc["cost.unknown"], HistoryText.SpokenCost(new HistoryCost(null, 1), usd, loc));
        Assert.Equal(loc["cost.subscription.tag"], HistoryText.SpokenCost(new HistoryCost(null, 0, Claude), usd, loc));
    }

    // Axis labels.

    [Theory]
    [InlineData(2.5, 2.5, "$2.5")]
    [InlineData(0.25, 0.25, "$0.25")]
    [InlineData(1500, 500, "$1.5k")]
    [InlineData(0, 1, "$0")]
    [InlineData(2000, 1000, "$2k")]
    [InlineData(120, 20, "$120")]
    [InlineData(7.5, 2.5, "$7.5")]
    [InlineData(0.05, 0.05, "$0.05")]
    [InlineData(0.025, 0.025, "$0.025")]
    [InlineData(-3, 1, "$0")]
    [InlineData(double.NaN, 1, "$0")]
    public void AxisLabelsUseAsFewDigitsAsTheStepNeeds(double value, double step, string expected)
    {
        Assert.Equal(expected, Money(For(LanguageCodes.En)).AxisLabel(value, step));
    }

    [Fact]
    public void AxisLabelsUseTheDisplaySymbol()
    {
        Assert.Equal("NT$1.5k", Twd(For(LanguageCodes.ZhHant)).AxisLabel(1500, 500));
        Assert.Equal("NT$0", Twd(For(LanguageCodes.ZhHant)).AxisLabel(0, 500));
        Assert.Equal("¥120", Money(For(LanguageCodes.Ja), "JPY", 150).AxisLabel(120, 20));
        Assert.Equal("JP¥120", Money(For(LanguageCodes.ZhHant), "JPY", 150).AxisLabel(120, 20));
        Assert.Equal("CHF 0.5", Money(For(LanguageCodes.En), "CHF", 0.9m).AxisLabel(0.5, 0.5));
    }

    // CSV money.

    [Fact]
    public void CsvColumnAndAmountOfTheDisplayCurrency()
    {
        Assert.Null(Money(For(LanguageCodes.En)).CsvColumn);
        Assert.Equal("cost_twd", Twd(For(LanguageCodes.En)).CsvColumn);
        Assert.Equal("178.20", Twd(For(LanguageCodes.En)).CsvAmount(5_500_000));
        Assert.Equal("cost_eur", Money(For(LanguageCodes.En), "EUR", 0.9m).CsvColumn);
        Assert.Equal("4.9500", Money(For(LanguageCodes.En), "EUR", 0.9m).CsvAmount(5_500_000));
        Assert.Equal("826.38", Money(For(LanguageCodes.En), "JPY", 150.25m).CsvAmount(5_500_000));
    }

    // Text.

    [Fact]
    public void ModelCostText()
    {
        var loc = For(LanguageCodes.ZhHant);
        var usd = Money(For(LanguageCodes.En));
        Assert.Equal(loc["cost.free"], HistoryText.ModelCostText(new ModelCost(CostTally.Zero, 3), usd, loc));
        Assert.Equal(loc["cost.unknown"], HistoryText.ModelCostText(new ModelCost(null, 3), usd, loc));
        Assert.Equal(loc["cost.unknown"], HistoryText.ModelCostText(new ModelCost(new CostTally(0, 3), 3), usd, loc));
        Assert.Equal(loc["cost.subscription.tag"], HistoryText.ModelCostText(new ModelCost(null, 0), usd, loc));
        Assert.Equal(loc["cost.subscription.tag"], HistoryText.ModelCostText(null, usd, loc));
        Assert.Equal("≥$1.23", HistoryText.ModelCostText(new ModelCost(new CostTally(1_234_000, 1), 3), usd, loc));
        Assert.Equal("$1.23", HistoryText.ModelCostText(new ModelCost(new CostTally(1_234_000), 3), usd, loc));
        Assert.Equal("NT$40", HistoryText.ModelCostText(new ModelCost(new CostTally(1_234_000), 3), Twd(loc), loc));
    }

    [Fact]
    public void CostNotesSayWhyAValueIsPartialOrMissing()
    {
        var loc = For(LanguageCodes.En);
        Assert.Null(HistoryText.CostNote(new HistoryCost(new CostTally(4_000_000), 4), loc));
        Assert.Null(HistoryText.CostNote(HistoryCost.None, loc));
        Assert.Equal(loc.Format("history.unpricedDay", 5), HistoryText.CostNote(new HistoryCost(new CostTally(2_250_000, 5), 8), loc));
        Assert.Equal(loc.Format("history.unpricedDay", 2), HistoryText.CostNote(new HistoryCost(new CostTally(0, 2), 2), loc));
        Assert.Equal(loc["history.costUnavailable"], HistoryText.CostNote(new HistoryCost(null, 1), loc));
        Assert.Equal("Subscriptions not included: Codex, Claude Code",
            HistoryText.CostNote(new HistoryCost(null, 0, SourceSet.Of(SourceKind.ClaudeCode).With(SourceKind.Codex)), loc));
    }

    [Fact]
    public void SourceListsFollowSourceOrderAndTheLanguage()
    {
        var sources = SourceSet.Of(SourceKind.ClaudeCode).With(SourceKind.Codex);
        Assert.Equal("Codex、Claude Code", HistoryText.SourceList(sources, For(LanguageCodes.ZhHant)));
        Assert.Equal("Codex, Claude Code", HistoryText.SourceList(sources, For(LanguageCodes.En)));
        Assert.Equal(string.Empty, HistoryText.SourceList([], For(LanguageCodes.En)));
    }

    [Fact]
    public void ADayReadsItsCostWithTheCurrencyName()
    {
        var loc = For(LanguageCodes.ZhHant);
        var day = DayOf(CostDays(), Today);
        var cost = HistoryText.Labelled(loc["history.table.cost"], "至少 2.25 美元", loc);

        var text = HistoryText.Accessible(day, Today, loc, Money(loc));
        Assert.Contains(cost, text, StringComparison.Ordinal);
        var input = text.IndexOf(HistoryText.Labelled(loc["history.table.input"], SpeedFormat.Tokens(200), loc), StringComparison.Ordinal);
        var requests = text.IndexOf(HistoryText.Labelled(loc["history.table.requests"], "8", loc), StringComparison.Ordinal);
        Assert.True(input >= 0 && requests >= 0, text);
        Assert.InRange(text.IndexOf(cost, StringComparison.Ordinal), input + 1, requests - 1);

        Assert.DoesNotContain(loc["history.table.cost"], HistoryText.Accessible(day, Today, loc), StringComparison.Ordinal);
    }

    [Fact]
    public void ABreakdownBarReadsItsCost()
    {
        var loc = For(LanguageCodes.En);
        var row = DayBreakdown.BySource(CostRecords, D(2026, 10, 20), HistoryChartMetric.Cost).Rows[0];
        var cost = HistoryText.Labelled("Cost", "4.00 US Dollar", loc);
        Assert.Contains(cost, HistoryText.BreakdownValue(row, loc, Money(loc)), StringComparison.Ordinal);
        Assert.Contains(cost, HistoryText.Accessible(row, loc, Money(loc)), StringComparison.Ordinal);
        Assert.DoesNotContain("Cost", HistoryText.BreakdownValue(row, loc), StringComparison.Ordinal);
    }

    // Hover card.

    [Fact]
    public void TheOutputTooltipEndsItsFiguresWithTheCost()
    {
        var loc = For(LanguageCodes.En);
        var day = TooltipDay;
        var order = HistoryChartMath.StackOrder([day]);
        var content = HistoryTooltipText.Build(day, order, allSources: true, HistoryChartMetric.Output, Today, Money(loc), loc);

        Assert.Equal(
            [
                ("Output", SpeedFormat.Tokens(4000), true, false),
                ("Codex", SpeedFormat.Tokens(3000), false, false),
                ("Claude Code", SpeedFormat.Tokens(1000), false, false),
                ("Input (incl. cache)", SpeedFormat.Tokens(100_000), false, false),
                ("Requests", "6", false, false),
                ("Avg speed", "160 tok/s", false, false),
                ("Peak", "300 tok/s", false, false),
                ("Cost", "≥$4.00", false, false),
                (loc.Format("history.unpricedDay", 1), string.Empty, false, true),
            ],
            content.Rows.Select(r => (r.Label, r.Value, r.Strong, r.IsNote)));
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode], content.Rows.Where(r => r.Source is not null).Select(r => r.Source!.Value));
        Assert.Equal(
            [("gpt", "50%", "200 tok/s", "$3.00"), ("opus", "25%", "100 tok/s", "≥$1.00"), ("mini", "25%", "200 tok/s", loc["cost.free"])],
            content.Models.Select(m => (m.Name, m.Percent, m.Speed, m.Cost)));
        Assert.Equal(HistoryText.LongDate(D(2026, 10, 20), loc), content.Title);
        Assert.Equal(string.Empty, content.Badge);
    }

    [Fact]
    public void TheCostTooltipLeadsWithTheCost()
    {
        var loc = For(LanguageCodes.En);
        var day = TooltipDay;
        var order = HistoryChartMath.StackOrder([day], HistoryChartMetric.Cost);
        var content = HistoryTooltipText.Build(day, order, allSources: true, HistoryChartMetric.Cost, D(2026, 10, 20), Twd(loc), loc);

        Assert.Equal(
            [
                ("Cost", "≥NT$130 ($4.00)", true, false),
                (loc.Format("history.unpricedDay", 1), string.Empty, false, true),
                ("Codex", "NT$97", false, false),
                ("Claude Code", "NT$32", false, false),
                ("Output", SpeedFormat.Tokens(4000), false, false),
            ],
            content.Rows.Take(5).Select(r => (r.Label, r.Value, r.Strong, r.IsNote)));
        Assert.Single(content.Rows, r => r.Strong);
        Assert.All(content.Models, m => Assert.Equal(string.Empty, m.Speed));
        Assert.Equal(["NT$97", "≥NT$32", loc["cost.free"]], content.Models.Select(m => m.Cost));
        Assert.Equal(loc["history.inProgress"], content.Badge);
    }

    [Fact]
    public void TheTooltipAlwaysHasACostLine()
    {
        var loc = For(LanguageCodes.En);
        var money = Money(loc);
        var gone = DayOf(CostDays(), D(2026, 10, 18));
        var rows = HistoryTooltipText.Build(gone, [SourceKind.ClaudeCode], allSources: true, HistoryChartMetric.Output, Today, money, loc).Rows;
        var cost = rows.Single(r => r.Label == "Cost");
        Assert.Equal("–", cost.Value);
        Assert.Equal(loc["history.costUnavailable"], rows[^1].Label);
        Assert.True(rows[^1].IsNote);

        var subscription = DayOf(CostDays(Claude), D(2026, 10, 18));
        var content = HistoryTooltipText.Build(subscription, [], allSources: true, HistoryChartMetric.Cost, Today, money, loc);
        Assert.Equal(("Cost", "Subscription", true), (content.Rows[0].Label, content.Rows[0].Value, content.Rows[0].Strong));
        Assert.Equal(loc.Format("cost.subscriptionExcluded", "Claude Code"), content.Rows[1].Label);
        Assert.Equal(loc["cost.subscription.tag"], Assert.Single(content.Models).Cost);

        var priced = DayOf(CostDays(Claude), D(2026, 10, 20));
        var plain = HistoryTooltipText.Build(priced, [SourceKind.Codex], allSources: true, HistoryChartMetric.Cost, Today, money, loc);
        Assert.Equal(("Cost", "$4.00"), (plain.Rows[0].Label, plain.Rows[0].Value));
        Assert.DoesNotContain(plain.Rows, r => r.IsNote);
        Assert.DoesNotContain(plain.Rows, r => r.Source is not null);
    }
}
