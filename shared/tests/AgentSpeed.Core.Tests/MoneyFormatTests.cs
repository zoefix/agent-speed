using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core.Money;

namespace AgentSpeed.Core.Tests;

public sealed class MoneyFormatTests
{
    public static TheoryData<int> FixtureCases()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < LoadCases().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Fact]
    public void TheFixtureHasEveryForm()
    {
        var cases = LoadCases();
        Assert.True(cases.Count >= 40, $"{cases.Count} cases");
        Assert.Contains(cases, c => c.Form == MoneyForm.Rate);
        Assert.Contains(cases, c => c.Form == MoneyForm.Amount);
        Assert.Contains(cases, c => c.CostMicroUsd is not null);
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void EveryFixtureCase(int index)
    {
        var spec = LoadCases()[index];
        var currency = CurrencyTable.Bundled.Find(spec.Currency);
        Assert.NotNull(currency);
        var value = spec.Value is { } text ? Dec(text) : MoneyFormat.Amount(spec.CostMicroUsd!.Value, Dec(spec.Rate!));
        var got = MoneyFormat.Format(value, currency, spec.Language, spec.Form);
        Assert.Equal((spec.Number, spec.Text), (got.Number, got.Text));
        Assert.Equal(currency.SymbolFor(spec.Language), got.Symbol);
    }

    [Fact]
    public void LiveRatesEnterThroughTheirShortestText()
    {
        var value = MoneyFormat.Rate(4200, Dec("6.67476"));
        Assert.Equal(Dec("0.02803399200"), value);
        Assert.Equal("0.028", MoneyFormat.Number(value, 2, MoneyForm.Rate));
        Assert.Equal(0m, MoneyFormat.Rate(-1, 1));
        Assert.Equal(0m, MoneyFormat.Rate(double.NaN, 1));
        Assert.Equal(0m, MoneyFormat.Rate(double.PositiveInfinity, 1));
        Assert.Equal(Dec("0.00000000001234"), MoneyFormat.Rate(0.000_012_34, 1));
        Assert.Equal(Dec("0.0000001"), MoneyFormat.Rate(0.1, 1));
    }

    [Fact]
    public void RoundingIntoTheNextRow()
    {
        Assert.Equal("1.00", MoneyFormat.Number(Dec("0.9996"), 2, MoneyForm.Amount));
        Assert.Equal("100", MoneyFormat.Number(Dec("99.96"), 2, MoneyForm.Rate));
        Assert.Equal("1.00M", MoneyFormat.Number(Dec("999999.6"), 0, MoneyForm.Rate));
        Assert.Equal("1.00B", MoneyFormat.Number(Dec("999700000"), 2, MoneyForm.Amount));
        Assert.Equal("1,235B", MoneyFormat.Number(Dec("1234500000000"), 2, MoneyForm.Amount));
    }

    [Fact]
    public void NegativeAndHugeValuesStayReadable()
    {
        Assert.Equal("0.00", MoneyFormat.Number(-5, 2, MoneyForm.Amount));
        Assert.Equal("0", MoneyFormat.Number(-5, 2, MoneyForm.Rate));
        Assert.Equal(0m, MoneyFormat.Amount(-100, 32));
        Assert.Equal(decimal.MaxValue, MoneyFormat.Amount(long.MaxValue, decimal.MaxValue));
        Assert.EndsWith("B", MoneyFormat.Number(decimal.MaxValue, 2, MoneyForm.Amount), StringComparison.Ordinal);
        Assert.DoesNotContain("E", MoneyFormat.Number(Dec("0.00000123"), 2, MoneyForm.Rate), StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolsEndingInALetterTakeANoBreakSpace()
    {
        Assert.Equal("CHF 0.42", MoneyFormat.WithSymbol("CHF", "0.42"));
        Assert.Equal("฿1.20", MoneyFormat.WithSymbol("฿", "1.20"));
        Assert.Equal("<Rp 0.000001", MoneyFormat.WithSymbol("Rp", "<0.000001"));
        Assert.Equal("MOP$1.20", MoneyFormat.WithSymbol("MOP$", "1.20"));
        Assert.Equal("<$0.000001", MoneyFormat.WithSymbol("$", MoneyFormat.BelowSmallest));
    }

    [Fact]
    public void CsvColumns()
    {
        Assert.Equal("0.051565", MoneyFormat.CsvUsd(51565));
        Assert.Equal("0.000000", MoneyFormat.CsvUsd(0));
        Assert.Equal("12345.678901", MoneyFormat.CsvUsd(12_345_678_901));
        Assert.Equal("1235.02", MoneyFormat.Csv(37_940_000, Dec("32.552"), 0));
        Assert.Equal("0.0280", MoneyFormat.Csv(4200, Dec("6.67476"), 2));
        Assert.Equal("0.0000", MoneyFormat.Csv(5, 1, 2));
        Assert.Equal("0.000000", MoneyFormat.CsvUsd(-3));
    }

    [Fact]
    public void RateStatusNumberHasAtLeastFourSignificantDigits()
    {
        Assert.Equal("32.41", MoneyFormat.Significant(Dec("32.411205")));
        Assert.Equal("158.1", MoneyFormat.Significant(Dec("158.100039")));
        Assert.Equal("0.8903", MoneyFormat.Significant(Dec("0.890292")));
        Assert.Equal("25,842", MoneyFormat.Significant(Dec("25841.9")));
        Assert.Equal("6.689", MoneyFormat.Significant(Dec("6.689327")));
        Assert.Equal("1.000", MoneyFormat.Significant(1));
        Assert.Equal("10.00", MoneyFormat.Significant(Dec("9.99999")));
        Assert.Equal("0", MoneyFormat.Significant(0));
        Assert.Equal("1,323", MoneyFormat.Significant(Dec("1323.05")));
        Assert.Equal("32.55", MoneyFormat.Significant(Dec("32.552")));
        Assert.Equal("0.000001000", MoneyFormat.Significant(Dec("0.000001")));
    }

    [Fact]
    public void RateStatusLine()
    {
        var table = CurrencyTable.Bundled;
        var usd = table.Find("USD")!;
        var twd = table.Find("TWD")!;
        var line = MoneyFormat.WithSymbol(usd.SymbolFor("zh-Hant"), "1") + " = "
            + MoneyFormat.WithSymbol(twd.SymbolFor("zh-Hant"), MoneyFormat.Significant(Dec("32.411205")));
        Assert.Equal("US$1 = NT$32.41", line);
    }

    internal static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static List<MoneyCase> LoadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("money-format.json")));
        var cases = new List<MoneyCase>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases.Add(new MoneyCase(
                item.GetProperty("form").GetString() == "rate" ? MoneyForm.Rate : MoneyForm.Amount,
                item.TryGetProperty("value", out var value) ? value.GetString() : null,
                item.TryGetProperty("costMicroUsd", out var micro) ? micro.GetInt64() : null,
                item.TryGetProperty("rate", out var rate) ? rate.GetString() : null,
                item.GetProperty("currency").GetString()!,
                item.GetProperty("language").GetString()!,
                item.GetProperty("number").GetString()!,
                item.GetProperty("text").GetString()!));
        }

        return cases;
    }

    private sealed record MoneyCase(
        MoneyForm Form,
        string? Value,
        long? CostMicroUsd,
        string? Rate,
        string Currency,
        string Language,
        string Number,
        string Text);
}
