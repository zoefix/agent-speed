using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class CostDisplayTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static CostDisplay Display(string language = LanguageCodes.En, string currency = "USD", decimal? twd = null)
    {
        var loc = new Localizer(Table, language);
        RatesInUse? rates = twd is { } rate
            ? new RatesInUse(
                new ExchangeRateSet("open.er-api.com", "https://open.er-api.com/v6/latest/USD", 1_792_627_200_000, 1_792_630_000_000,
                    new Dictionary<string, decimal> { ["USD"] = 1, ["TWD"] = rate }),
                CurrencyTable.Bundled.FallbackRates)
            : null;
        return new CostDisplay(loc, CurrencyTable.Bundled, rates) { Settings = CostSettings.Default with { Currency = currency } };
    }

    private static SourceStatus Source(SourceKind kind, double liveOutput, LiveCost liveCost, CostTally today, long requests = 10) =>
        new(kind, true, "", true, 1, new Rate(liveOutput, liveOutput), new TokenTotals(Requests: requests, Output: 100), 0)
        {
            LiveCost = liveCost,
            TodayCost = today,
        };

    private static CostReading Reading(double microUsdPerSecond, double share, double liveOutput, CostState state, params string[] models) =>
        new(microUsdPerSecond, share, liveOutput, new CostTally(4_200_000), 10, models, [], state);

    [Fact]
    public void PricedLiveMoneyShowsTheRateAndTodaysSummary()
    {
        var money = Display().Live(Reading(4200, 1, 50, CostState.Priced), 3);
        Assert.Equal(("0.0042", "$/s", "$0.0042", false, false), (money.Number, money.Unit, money.Inline, money.AtLeast, money.Unknown));
        Assert.Equal("Today $4.20 · 3 agents", money.HoverLine);
        Assert.Equal("0.0042 US Dollar", money.SpokenAmount);
    }

    [Fact]
    public void PartlyPricedMoneyCarriesAtLeastAndTheHoverSaysSo()
    {
        var display = Display();
        var money = display.Live(Reading(4200, 0.5, 50, CostState.Partial), 1);
        Assert.True(money.AtLeast);
        Assert.Equal("≥0.0042", money.NumberText);
        Assert.Equal(display.Localizer["cost.partial"], money.HoverLine);
        Assert.Equal(display.Localizer.Format("a11y.orb.costAtLeast", "0.0042 US Dollar"), money.SpokenAmount);
    }

    [Fact]
    public void NothingPricedIsADashWithTheUnknownModels()
    {
        var display = Display();
        var money = display.Live(Reading(0, 0, 50, CostState.NothingPriced, "neko-1", "neko-2", "neko-3"), 1);
        Assert.Equal(("–", "–", true), (money.Number, money.Inline, money.Unknown));
        Assert.Equal(display.Localizer.Format("cost.unknownModels", "neko-1, neko-2 +1"), money.HoverLine);
        Assert.Equal(("–", "$/s"), CostDisplay.BarValue(money, showUnit: true));
    }

    [Fact]
    public void SubscriptionOnlyIsAKnownZero()
    {
        var display = Display();
        var money = display.Live(Reading(9000, 1, 50, CostState.SubscriptionOnly), 1);
        Assert.Equal(("0", 0.0, false), (money.Number, money.Value, money.AtLeast));
        Assert.Equal(display.Localizer["cost.subscriptionOnly"], money.HoverLine);
    }

    [Fact]
    public void TheBarShowsTheSymbolInFrontWhenTheUnitIsHidden()
    {
        var money = Display().Live(Reading(4200, 0.5, 50, CostState.Partial), 1);
        Assert.Equal(("≥0.0042", "$/s"), CostDisplay.BarValue(money, showUnit: true));
        Assert.Equal(("≥$0.0042", "$/s"), CostDisplay.BarValue(money, showUnit: false));
    }

    [Fact]
    public void TooltipsAddUsdForAnotherCurrency()
    {
        Assert.Equal("NT$1,235（US$37.94）", Display(LanguageCodes.ZhHant, "TWD", 32.55m).AmountWithUsd(37_940_000));
        Assert.Equal("NT$1,235 ($37.94)", Display(LanguageCodes.En, "TWD", 32.55m).AmountWithUsd(37_940_000));
        Assert.Equal("$37.94", Display().AmountWithUsd(37_940_000));
    }

    [Fact]
    public void TalliesShowAtLeastOrADash()
    {
        var display = Display();
        Assert.Equal("$12.34", display.TallyText(new CostTally(12_340_000), 5));
        Assert.Equal("≥$12.34", display.TallyText(new CostTally(12_340_000, 2), 5));
        Assert.Equal("–", display.TallyText(new CostTally(0, 5), 5));
        Assert.Equal("–", display.TallyText(null, 5));
        Assert.Equal(display.Localizer["cost.unknown"], display.SpokenTally(null, 5));
    }

    [Fact]
    public void TheRateStatusUsesFourSignificantDigitsAndTheFallbackBeforeTheFirstFetch()
    {
        var fetched = Display(LanguageCodes.ZhHant, "TWD", 32.4123m);
        Assert.StartsWith("US$1 = NT$32.41 · 2026/10/22 · open.er-api.com", fetched.RateStatus);
        Assert.NotNull(fetched.HistoryNote);

        var fallback = Display(LanguageCodes.ZhHant, "TWD");
        Assert.Equal(fallback.Localizer.Format("rates.fallback", fallback.RateDateText), fallback.RateStatus);
        Assert.Null(Display().HistoryNote);
    }

    [Fact]
    public void NumericDatesPadMonthAndDay()
    {
        var date = new DateTime(2026, 1, 5);
        Assert.Equal("01/05/2026", CostDisplay.NumericDate(date, System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("2026/01/05", CostDisplay.NumericDate(date, System.Globalization.CultureInfo.GetCultureInfo("zh-TW")));
    }

    [Fact]
    public void LiveAccessibilityReadsTheCurrencyName()
    {
        var display = Display(LanguageCodes.ZhHant);
        var money = display.Live(Reading(4200, 1, 50, CostState.Priced), 1);
        Assert.Equal(display.Localizer.Format("a11y.orb.cost", "0.0042", display.CurrencyName, "高鐵"), display.LiveAccessibility(money, "高鐵"));
        Assert.Equal(display.Localizer.Format("a11y.orb.costNamed", "Codex", "0.0042", display.CurrencyName, "高鐵"),
            display.LiveAccessibility(money, "高鐵", "Codex"));
        var unknown = display.Live(Reading(0, 0, 5, CostState.NothingPriced), 1);
        Assert.Equal(display.Localizer.Format("a11y.orb.costUnknown", "高鐵"), display.LiveAccessibility(unknown, "高鐵"));
    }

    [Fact]
    public void TheSnapshotsBillableSourcesDriveTheLiveValue()
    {
        var display = Display();
        display.Settings = display.Settings with { Subscriptions = SourceSet.Of(SourceKind.ClaudeCode) };
        var snapshot = SpeedSnapshot.Empty with
        {
            Sources =
            [
                Source(SourceKind.Codex, 100, new LiveCost(2000, 100), new CostTally(1_000_000)),
                Source(SourceKind.ClaudeCode, 50, new LiveCost(9000, 50), new CostTally(5_000_000)),
            ],
        };
        Assert.Equal("0.002", display.Live(snapshot).Number);

        display.Settings = display.Settings with { IncludeSubscriptions = true };
        Assert.Equal("0.011", display.Live(snapshot).Number);
    }

    [Fact]
    public void TodaysCostSummarySortsPartsAndListsExcludedSources()
    {
        var display = Display();
        display.Settings = display.Settings with { Subscriptions = SourceSet.Of(SourceKind.Gemini) };
        var snapshot = SpeedSnapshot.Empty with
        {
            Sources =
            [
                Source(SourceKind.Codex, 0, default, new CostTally(1_000_000)),
                Source(SourceKind.ClaudeCode, 0, default, new CostTally(3_000_000, 2)),
                Source(SourceKind.Gemini, 0, default, new CostTally(9_000_000)),
                Source(SourceKind.Omp, 0, default, new CostTally(0), requests: 0),
            ],
        };
        var summary = TodayCostSummary.From(snapshot, display);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex], summary.Parts.Select(p => p.Source));
        Assert.Equal(1.0, summary.Parts[0].Share);
        Assert.Equal(1 / 3.0, summary.Parts[1].Share, 6);
        Assert.Equal("≥$4.00", summary.AmountText);
        Assert.Equal([SourceKind.Gemini], summary.Excluded);
        Assert.Equal(display.Localizer.Format("cost.unpriced", 2), summary.UnpricedText);
        Assert.NotNull(summary.ExcludedText);
        Assert.Null(summary.RateLine);
    }

    [Fact]
    public void TheTaskbarMoneyBlockShowsTheLiveRateAndTodaysTotal()
    {
        var display = Display();
        var snapshot = SpeedSnapshot.Empty with
        {
            Sources =
            [
                Source(SourceKind.Codex, 100, new LiveCost(2000, 50), new CostTally(128_400_000, 1)),
            ],
        };
        var both = AppSettings.Default with { BarSecondary = true };
        Assert.Null(BarSecondary.From(AppSettings.Default, snapshot, display));
        Assert.Null(BarSecondary.From(both with { BarMode = BarDisplayMode.GlyphOnly }, snapshot, display));
        Assert.Null(BarSecondary.From(both with { Cost = CostSettings.Default with { LiveUnit = LiveUnit.Cost } }, snapshot, display));

        var block = BarSecondary.From(both, snapshot, display)!;
        Assert.Equal(new BarMoneyLine("now", "≥0.002", "$/s", false), block.Live);
        Assert.Equal(new BarMoneyLine("today", "≥128.40", "$", false), block.Today);
        Assert.Equal(display.Localizer.Format("a11y.bar.money", display.Localizer.Format("a11y.orb.costAtLeast", "0.002 US Dollar"),
            display.Localizer.Format("a11y.orb.costAtLeast", "128.40 US Dollar")), block.Spoken);

        var empty = BarSecondary.From(both, SpeedSnapshot.Empty, display)!;
        Assert.Equal(("0", true), (empty.Live.Number, empty.Live.Dim));
        Assert.Equal(("0.00", true), (empty.Today.Number, empty.Today.Dim));

        var unpriced = SpeedSnapshot.Empty with { Sources = [Source(SourceKind.Codex, 100, default, new CostTally(0, 10))] };
        var dash = BarSecondary.From(both, unpriced, display)!;
        Assert.Equal(("–", true, "–", true), (dash.Live.Number, dash.Live.Dim, dash.Today.Number, dash.Today.Dim));
    }

    [Fact]
    public void TheSecondValueSettingRoundTripsAndDefaultsOff()
    {
        Assert.False(AppSettings.Default.BarSecondary);
        var json = SettingsSerializer.Serialize(AppSettings.Default with { BarSecondary = true });
        Assert.True(SettingsSerializer.Deserialize(json).BarSecondary);
        Assert.False(SettingsSerializer.Deserialize("{}").BarSecondary);
    }

    [Fact]
    public void AnEmptyDayIsAKnownZero() =>
        Assert.Equal("$0.00", TodayCostSummary.From(SpeedSnapshot.Empty, Display()).AmountText);

    [Fact]
    public void ASubscriptionSourceRowShowsItsTagUnlessIncluded()
    {
        var display = Display();
        var codex = Source(SourceKind.Codex, 0, default, new CostTally(4_200_000));
        display.Settings = display.Settings with { Subscriptions = SourceSet.Of(SourceKind.Codex) };
        Assert.Equal(display.Localizer["cost.subscription.tag"], SourceCost.Today(codex, display).Text);
        display.Settings = display.Settings with { IncludeSubscriptions = true };
        Assert.Equal("$4.20", SourceCost.Today(codex, display).Text);
    }

    [Fact]
    public void ASourceRowsLiveMoneyIsItsOwnApiEquivalentRate()
    {
        var display = Display();
        display.Settings = display.Settings with { Subscriptions = SourceSet.Of(SourceKind.Codex) };
        var codex = Source(SourceKind.Codex, 100, new LiveCost(2000, 100), CostTally.Zero);
        Assert.Equal("0.002", SourceCost.Live(codex, display)!.Number);
        Assert.Null(SourceCost.Live(Source(SourceKind.Codex, 0, default, CostTally.Zero), display));
        Assert.Null(SourceCost.Live(Source(SourceKind.Codex, 100, default, CostTally.Zero), display));
    }
}
