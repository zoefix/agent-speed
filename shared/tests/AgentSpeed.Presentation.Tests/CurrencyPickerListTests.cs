using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class CurrencyPickerListTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static CostDisplay Display(string language, string currency = Currency.UsdCode) =>
        new(new Localizer(Table, language)) { Settings = CostSettings.Default with { Currency = currency } };

    [Fact]
    public void WithoutQueryCommonComesFirstThenEveryOtherCurrency()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.ZhHant), null);

        Assert.Equal(["USD", "TWD", "HKD", "MOP", "CNY", "JPY", "EUR", "GBP", "KRW", "SGD"], rows.Take(10).Select(r => r.Code));
        Assert.All(rows.Take(10), row => Assert.Equal("常用", row.Group));
        Assert.All(rows.Skip(10), row => Assert.Equal("所有貨幣", row.Group));
        Assert.Equal(CurrencyTable.Bundled.Currencies.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Code).Distinct().Count());
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHans, "CNY")]
    [InlineData(LanguageCodes.Ja, "JPY")]
    [InlineData(LanguageCodes.En, "CNY")]
    public void HomeCurrencyFollowsUsd(string language, string second)
    {
        var rows = CurrencyPickerList.Rows(Display(language), string.Empty);

        Assert.Equal("USD", rows[0].Code);
        Assert.Equal(second, rows[1].Code);
    }

    [Fact]
    public void TheCurrentCurrencyCarriesTheCheckAndIsHighlightedFirst()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.ZhHant, "TWD"), null);

        var current = Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal("TWD", current.Code);
        Assert.Equal(1, CurrencyPickerList.InitialIndex(rows));
    }

    [Fact]
    public void RowsReadCodeAndLocalizedNameWithTheLanguagesSymbol()
    {
        var twd = CurrencyPickerList.Rows(Display(LanguageCodes.ZhHant), null).Single(r => r.Code == "TWD");
        Assert.Equal("新臺幣", twd.Name);
        Assert.Equal("NT$", twd.Symbol);
        Assert.Equal("TWD, 新臺幣", twd.AccessibleName);

        Assert.Equal("$", CurrencyPickerList.Rows(Display(LanguageCodes.En), null)[0].Symbol);
        Assert.Equal("US$", CurrencyPickerList.Rows(Display(LanguageCodes.ZhHant), null)[0].Symbol);
        Assert.Equal("¥", CurrencyPickerList.Rows(Display(LanguageCodes.Ja), null).Single(r => r.Code == "JPY").Symbol);
    }

    [Fact]
    public void SearchingCollapsesTheGroupsIntoOneRankedList()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.En), "nt");

        Assert.Equal("TWD", rows[0].Code);
        Assert.All(rows, row => Assert.Equal(string.Empty, row.Group));
    }

    [Theory]
    [InlineData(LanguageCodes.En, "yen", "JPY")]
    [InlineData(LanguageCodes.Ja, "円", "JPY")]
    [InlineData(LanguageCodes.ZhHant, "臺幣", "TWD")]
    [InlineData(LanguageCodes.ZhHant, "dollar", "USD")]
    [InlineData(LanguageCodes.En, "ＥＵＲ", "EUR")]
    [InlineData(LanguageCodes.Ja, "ドル", "USD")]
    public void SearchFindsCodesNamesAndSymbols(string language, string query, string code)
    {
        Assert.Contains(code, CurrencyPickerList.Rows(Display(language), query).Select(r => r.Code));
    }

    [Fact]
    public void CodeMatchesRankAboveNameMatches()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.En), "dol");

        Assert.True(rows.Count > 5);
        Assert.All(rows, row => Assert.Contains("Dollar", row.Name, StringComparison.Ordinal));
        Assert.Equal("USD", rows[0].Code);
    }

    [Fact]
    public void NoMatchLeavesAnEmptyListWithoutHighlight()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.En), "qqqq");

        Assert.Empty(rows);
        Assert.Equal(-1, CurrencyPickerList.InitialIndex(rows));
    }

    [Fact]
    public void WhitespaceIsNotASearch()
    {
        Assert.False(CurrencyPickerList.IsSearching("   "));
        Assert.Equal("常用", CurrencyPickerList.Rows(Display(LanguageCodes.ZhHant), "   ")[0].Group);
    }

    [Fact]
    public void WithoutTheCurrentCurrencyTheFirstRowIsHighlighted()
    {
        var rows = CurrencyPickerList.Rows(Display(LanguageCodes.En, "TWD"), "euro");

        Assert.Equal(0, CurrencyPickerList.InitialIndex(rows));
    }

    [Theory]
    [InlineData(-1, 1, 5, 0)]
    [InlineData(-1, -1, 5, 4)]
    [InlineData(0, -1, 5, 0)]
    [InlineData(4, 1, 5, 4)]
    [InlineData(1, 1, 5, 2)]
    [InlineData(2, 8, 5, 4)]
    [InlineData(3, -8, 5, 0)]
    [InlineData(9, 1, 5, 0)]
    [InlineData(0, 1, 0, -1)]
    public void ArrowsMoveOneRowAndStopAtTheEnds(int index, int step, int count, int expected)
    {
        Assert.Equal(expected, CurrencyPickerList.Move(index, step, count));
    }
}
