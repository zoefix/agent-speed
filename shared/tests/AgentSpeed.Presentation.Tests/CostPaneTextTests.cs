using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class CostPaneTextTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private static readonly RateStatusInput Online = new(NetworkEnabled: true, IsRefreshing: false, FailureReason: null, IsStale: false);

    private static readonly PriceCheckInput Idle = new(true, false, PriceCheckKind.None, null, null, false);

    private static readonly PriceTableInfo Table12 = new(12, "2026-10-22", 85, IsBundled: false);

    private static CostDisplay Display(string language = LanguageCodes.En, string currency = Currency.UsdCode, string? provider = null)
    {
        var display = new CostDisplay(new Localizer(Table, language)) { Settings = CostSettings.Default with { Currency = currency } };
        if (provider is not null)
        {
            var rates = new Dictionary<string, decimal> { ["USD"] = 1m, ["TWD"] = 32.55m, ["JPY"] = 151.2m };
            var day = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var set = new ExchangeRateSet(provider, "https://" + provider + "/", day, day + 3_600_000, rates);
            display.Rates = new RatesInUse(set, display.Currencies.FallbackRates);
        }

        return display;
    }

    // Currency and live value

    [Fact]
    public void CurrencyButtonReadsLabelCodeAndName()
    {
        Assert.Equal("貨幣, TWD, 新臺幣", CostPaneText.CurrencyButtonName(Display(LanguageCodes.ZhHant, "TWD")));
        Assert.Equal("Currency, USD, US Dollar", CostPaneText.CurrencyButtonName(Display()));
    }

    [Fact]
    public void LiveUnitCostCarriesTheCurrencySymbol()
    {
        Assert.Equal("Cost ($/s)", CostPaneText.LiveUnitCost(Display()));
        Assert.Equal("金額（NT$/s）", CostPaneText.LiveUnitCost(Display(LanguageCodes.ZhHant, "TWD")));
        Assert.Equal("金額（US$/s）", CostPaneText.LiveUnitCost(Display(LanguageCodes.Ja)));
    }

    // Rate status

    [Fact]
    public void RateStatusIsHiddenForUsd()
    {
        Assert.False(RateStatusView.For(Display(), Online).IsShown);
        Assert.True(RateStatusView.For(Display(currency: "TWD"), Online).IsShown);
    }

    [Fact]
    public void BundledRatesSayBuiltInWithTheirDate()
    {
        var display = Display(currency: "TWD");
        var view = RateStatusView.For(display, Online);

        Assert.Equal($"Using built-in rates ({display.RateDateText})", view.Line);
        Assert.False(view.IsFailure);
        Assert.False(view.ShowsAttribution);
        Assert.True(view.CanRefresh);
        Assert.Equal("Refresh Rates", view.RefreshLabel);
        Assert.False(view.ShowsNetworkOff);
    }

    [Fact]
    public void FetchedRatesShowOneDollarTheProviderAndTheAttribution()
    {
        var display = Display(currency: "TWD", provider: ExchangeRates.ErApiHost);
        var view = RateStatusView.For(display, Online);

        Assert.Equal($"$1 = NT$32.55 · {display.RateDateText} · open.er-api.com", view.Line);
        Assert.True(view.ShowsAttribution);
    }

    [Fact]
    public void OtherProvidersNeedNoAttribution()
    {
        var view = RateStatusView.For(Display(currency: "JPY", provider: "api.frankfurter.dev"), Online);

        Assert.EndsWith("api.frankfurter.dev", view.Line, StringComparison.Ordinal);
        Assert.Contains("JP¥151.2", view.Line, StringComparison.Ordinal);
        Assert.False(view.ShowsAttribution);
    }

    [Fact]
    public void AFailedRefreshReplacesTheStatusAndHidesStale()
    {
        var view = RateStatusView.For(Display(currency: "TWD"), Online with { FailureReason = "HTTP 503", IsStale = true });

        Assert.Equal("Couldn't refresh rates: HTTP 503", view.Line);
        Assert.True(view.IsFailure);
        Assert.False(view.ShowsStale);
    }

    [Fact]
    public void OldRatesSayStale()
    {
        Assert.True(RateStatusView.For(Display(currency: "TWD"), Online with { IsStale = true }).ShowsStale);
        Assert.False(RateStatusView.For(Display(currency: "TWD"), Online).ShowsStale);
    }

    [Fact]
    public void RefreshingDisablesTheButtonAndSaysSo()
    {
        var view = RateStatusView.For(Display(currency: "TWD"), Online with { IsRefreshing = true });

        Assert.False(view.CanRefresh);
        Assert.Equal("Refreshing…", view.RefreshLabel);
    }

    [Fact]
    public void WithTheNetworkOffRatesCannotBeRefreshed()
    {
        var view = RateStatusView.For(Display(currency: "TWD"), Online with { NetworkEnabled = false });

        Assert.False(view.CanRefresh);
        Assert.True(view.ShowsNetworkOff);
    }

    // Price table

    [Fact]
    public void PriceStatusNamesVersionDateAndModelCount()
    {
        var loc = new Localizer(Table, LanguageCodes.En);
        var date = CostPaneText.UpdatedDate("2026-10-22", loc.Culture);

        Assert.Equal("10/22/2026", date);
        Assert.Equal($"Version 12 · updated {date} · 85 models", CostPaneText.PriceStatus(Table12, loc));
        Assert.Equal("版本 12 · 2026/10/22 更新 · 85 個模型", CostPaneText.PriceStatus(Table12, new Localizer(Table, LanguageCodes.ZhHant)));
    }

    [Fact]
    public void AnUnreadableDateIsShownAsIs()
    {
        Assert.Equal("soon", CostPaneText.UpdatedDate("soon", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheBundledTableIsMarkedBuiltIn()
    {
        var info = PriceTableInfo.Of(PriceBook.Bundled, PriceBook.Bundled);

        Assert.True(info.IsBundled);
        Assert.Equal(PriceBook.Bundled.Version, info.Version);
        Assert.Equal(PriceBook.Bundled.Models.Count, info.ModelCount);
        Assert.True(PriceTableView.For(info, null, Idle, Now, Display()).IsBundled);
    }

    [Fact]
    public void LastSyncIsRelativeOrNever()
    {
        var display = Display();

        Assert.Equal("Not synced yet — using the built-in table", CostPaneText.LastSync(null, Now, display, TimeZoneInfo.Utc));
        Assert.Equal("Last synced just now", CostPaneText.LastSync(Now.AddSeconds(-30).ToUnixTimeMilliseconds(), Now, display, TimeZoneInfo.Utc));
        Assert.Equal("Last synced 5m ago", CostPaneText.LastSync(Now.AddMinutes(-5).ToUnixTimeMilliseconds(), Now, display, TimeZoneInfo.Utc));
        Assert.Equal("上次同步：剛剛",
            CostPaneText.LastSync(Now.AddSeconds(-5).ToUnixTimeMilliseconds(), Now, Display(LanguageCodes.ZhHant), TimeZoneInfo.Utc));
    }

    public static TheoryData<PriceCheckInput, string> CheckResults => new()
    {
        { Idle, string.Empty },
        { Idle with { NewerSchemaOffered = true }, "A newer price table format is available — update Agent Speed to use it" },
        { Idle with { IsChecking = true, LastCheck = PriceCheckKind.Failed }, "Checking…" },
        { Idle with { LastCheck = PriceCheckKind.UpToDate }, "The price table is up to date" },
        { Idle with { LastCheck = PriceCheckKind.Updated, LastCheckVersion = 13 }, "Updated to version 13" },
        { Idle with { LastCheck = PriceCheckKind.NewerSchema }, "A newer price table format is available — update Agent Speed to use it" },
        { Idle with { LastCheck = PriceCheckKind.Failed, LastCheckReason = "HTTP 404" }, "Couldn't get the price table: HTTP 404" },
    };

    [Theory]
    [MemberData(nameof(CheckResults))]
    public void CheckResultFollowsTheLastManualCheck(PriceCheckInput check, string expected)
    {
        Assert.Equal(expected, CostPaneText.CheckResult(check, new Localizer(Table, LanguageCodes.En)));
    }

    [Fact]
    public void ChecksNeedTheNetworkAndNoRoundRunning()
    {
        var online = PriceTableView.For(Table12, null, Idle, Now, Display());
        Assert.True(online.CanCheck);
        Assert.True(online.CanToggleAutoSync);
        Assert.False(online.ShowsNetworkOff);

        Assert.False(PriceTableView.For(Table12, null, Idle with { IsChecking = true }, Now, Display()).CanCheck);

        var offline = PriceTableView.For(Table12, null, Idle with { NetworkEnabled = false }, Now, Display());
        Assert.False(offline.CanCheck);
        Assert.False(offline.CanToggleAutoSync);
        Assert.True(offline.ShowsNetworkOff);
    }

    // Unpriced models

    [Fact]
    public void UnpricedModelsAreListedOnceInSourceOrder()
    {
        SourceStatus Source(SourceKind kind, params string[] models) =>
            new(kind, true, string.Empty, true, 0, default, default, 0) { UnpricedModels = models };

        var models = CostPaneText.UnpricedModels(
        [
            Source(SourceKind.Codex, "gpt-x", "local-a"),
            Source(SourceKind.ClaudeCode),
            Source(SourceKind.OpenCode, "local-a", "mystery-1"),
        ]);

        Assert.Equal(["gpt-x", "local-a", "mystery-1"], models);
        Assert.Equal("Models without a price today: gpt-x, local-a, mystery-1",
            CostPaneText.UnknownToday(models, new Localizer(Table, LanguageCodes.En)));
        Assert.Equal("今天價格未知的模型：gpt-x、local-a、mystery-1", CostPaneText.UnknownToday(models, new Localizer(Table, LanguageCodes.ZhHant)));
        Assert.Equal(string.Empty, CostPaneText.UnknownToday([], new Localizer(Table, LanguageCodes.En)));
    }
}
