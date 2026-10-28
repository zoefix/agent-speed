using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSpeed.Core.Money;

namespace AgentSpeed.Core.Tests;

public sealed class CurrencyTableTests
{
    private static readonly CurrencyTable Table = CurrencyTable.Bundled;
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> CurrencyNames = new(LoadNames);

    /// <summary><c>validate_currencies</c> of <c>spec/tools/prices.py</c>.</summary>
    [Fact]
    public void BundledTableFollowsTheRules()
    {
        var currencies = Table.Currencies;
        Assert.Equal(38, currencies.Count);
        Assert.Equal(currencies.Count, currencies.Select(c => c.Code).Distinct().Count());
        Assert.NotNull(Table.Find("USD"));
        foreach (var currency in currencies)
        {
            Assert.Matches(new Regex("^[A-Z]{3}$"), currency.Code);
            Assert.InRange(currency.Digits, 0, 3);
            Assert.False(string.IsNullOrEmpty(currency.Symbol));
            Assert.Equal(currency.Symbol.Trim(), currency.Symbol);
            Assert.All(currency.Symbols.Keys, language => Assert.Contains(language, CurrencyTable.Languages));
        }

        var ranks = currencies.Where(c => c.Common is not null).Select(c => c.Common!.Value).Order().ToList();
        Assert.Equal(Enumerable.Range(1, ranks.Count), ranks);
        var fallback = Table.FallbackRates;
        Assert.StartsWith("https://", fallback.Source, StringComparison.Ordinal);
        Assert.Equal(1m, fallback.Rates["USD"]);
        Assert.True(fallback.Rates.Keys.ToHashSet().SetEquals(Table.Codes));
        Assert.All(fallback.Rates.Values, rate => Assert.True(rate > 0));
        Assert.Equal(1_792_627_200_000, fallback.DateMs);
        Assert.Equal(32.552m, fallback.Rates["TWD"]);
        Assert.Equal(18040.7m, fallback.Rates["IDR"]);
    }

    [Fact]
    public void EmbeddedCurrencyTableIsTheSpecAsset()
    {
        using var stream = typeof(CurrencyTable).Assembly.GetManifestResourceStream(CurrencyTable.ResourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal(File.ReadAllBytes(RepoSpec.PathOf("assets/currencies.json")), buffer.ToArray());
    }

    [Fact]
    public void EveryCurrencyHasANameInEveryLanguage()
    {
        foreach (var currency in Table.Currencies)
        {
            Assert.True(CurrencyNames.Value.TryGetValue("currency." + currency.Code, out var entry), "currency." + currency.Code);
            foreach (var language in CurrencyTable.Languages)
            {
                Assert.False(string.IsNullOrEmpty(entry!.GetValueOrDefault(language)), $"currency.{currency.Code} {language}");
            }
        }
    }

    [Fact]
    public void SymbolsFollowTheLanguage()
    {
        var usd = Table.Find("USD")!;
        Assert.Equal("$", usd.SymbolFor("en"));
        Assert.Equal("US$", usd.SymbolFor("zh-Hant"));
        var cny = Table.Find("CNY")!;
        Assert.Equal("¥", cny.SymbolFor("zh-Hans"));
        Assert.Equal("CN¥", cny.SymbolFor("ja"));
        var jpy = Table.Find("JPY")!;
        Assert.Equal("¥", jpy.SymbolFor("ja"));
        Assert.Equal("JP¥", jpy.SymbolFor("en"));
        Assert.Equal("USD", Table.CurrencyOrUsd("XXX").Code);
        Assert.Equal("TWD", Table.CurrencyOrUsd("TWD").Code);
        Assert.Null(Table.Find("twd"));
    }

    [Fact]
    public void CommonGroupPutsUsdThenTheHomeCurrencies()
    {
        Assert.Equal(["USD", "TWD", "HKD", "MOP"], Codes(Table.CommonGroup("zh-Hant")).Take(4));
        Assert.Equal(["USD", "CNY", "TWD"], Codes(Table.CommonGroup("zh-Hans")).Take(3));
        Assert.Equal(["USD", "JPY", "CNY"], Codes(Table.CommonGroup("ja")).Take(3));
        Assert.Equal(["USD", "CNY", "TWD", "HKD", "JPY", "EUR", "GBP", "KRW", "SGD"], Codes(Table.CommonGroup("en")));
        Assert.Equal(10, Table.CommonGroup("zh-Hant").Count);
    }

    [Fact]
    public void OtherGroupIsSortedByLocalizedName()
    {
        foreach (var language in CurrencyTable.Languages)
        {
            var other = Table.OtherGroup(language, c => Name(c, language));
            var compare = CultureInfo.GetCultureInfo(language).CompareInfo;
            for (var i = 1; i < other.Count; i++)
            {
                Assert.True(compare.Compare(Name(other[i - 1], language), Name(other[i], language), CompareOptions.IgnoreCase) <= 0,
                    $"{language}: {other[i - 1].Code} before {other[i].Code}");
            }

            var common = Codes(Table.CommonGroup(language)).ToHashSet();
            Assert.Equal(Table.Currencies.Count, common.Count + other.Count);
            Assert.All(other, c => Assert.DoesNotContain(c.Code, common));
        }

        Assert.Equal(["ARS", "AUD"], Codes(Table.OtherGroup("en", c => Name(c, "en"))).Take(2));
    }

    [Fact]
    public void SearchMatchesWordStartsCodesFirst()
    {
        var dollars = Search("dol", "en");
        Assert.Contains("USD", dollars);
        Assert.Contains("TWD", dollars);
        Assert.Contains("HKD", dollars);
        Assert.Contains("AUD", dollars);
        Assert.DoesNotContain("EUR", dollars);
        Assert.Equal(["JPY"], Search("yen", "zh-Hant"));
        Assert.Contains("JPY", Search("円", "ja"));
        Assert.Contains("TWD", Search("nt", "zh-Hant"));
        Assert.Contains("TWD", Search("NT", "en"));
        Assert.Equal("TWD", Search("TW", "en")[0]);
        Assert.Equal("USD", Search("ｕｓｄ", "en")[0]);
        Assert.Equal("USD", Search("美元", "zh-Hant")[0]);
        Assert.Empty(Search("zzz", "en"));
        Assert.Empty(Search("  ", "en"));
        Assert.Empty(Search("", "en"));
        Assert.Equal("ARS", Search("ar", "en")[0]);
        Assert.Contains("CHF", Search("chf", "ja"));
        Assert.DoesNotContain("USD", Search("ollar", "en"));
    }

    [Fact]
    public void SearchKeepsThePickerOrderWithinARank()
    {
        var dollars = Search("dollar", "zh-Hant");
        var picker = Codes(Table.CommonGroup("zh-Hant")).Concat(Codes(Table.OtherGroup("zh-Hant", c => Name(c, "zh-Hant")))).ToList();
        Assert.Equal(dollars.OrderBy(picker.IndexOf), dollars);
        Assert.Equal("USD", dollars[0]);
    }

    [Fact]
    public void FoldIgnoresCaseWidthAndDiacritics()
    {
        Assert.Equal("usd", CurrencyTable.Fold("ＵＳＤ"));
        Assert.Equal("cafe", CurrencyTable.Fold("Café"));
        Assert.Equal("nt$", CurrencyTable.Fold("ＮＴ＄"));
        Assert.Equal("円", CurrencyTable.Fold("円"));
        Assert.True(CurrencyTable.WordStarts(CurrencyTable.Runes("new taiwan dollar"), CurrencyTable.Runes("tai")));
        Assert.False(CurrencyTable.WordStarts(CurrencyTable.Runes("new taiwan dollar"), CurrencyTable.Runes("aiw")));
        Assert.True(CurrencyTable.WordStarts(CurrencyTable.Runes("新臺幣"), CurrencyTable.Runes("臺幣")));
        Assert.True(CurrencyTable.WordStarts(CurrencyTable.Runes("hk$"), CurrencyTable.Runes("hk")));
    }

    [Fact]
    public void ParseRejectsBrokenTables()
    {
        Assert.Throws<FormatException>(() => CurrencyTable.Parse("{"u8));
        Assert.Throws<FormatException>(() => CurrencyTable.Parse("""{"schema": 2, "currencies": []}"""u8));
        Assert.Throws<FormatException>(() => CurrencyTable.Parse("""{"schema": 1, "currencies": [{"code": "USD"}]}"""u8));
        Assert.Throws<FormatException>(() => CurrencyTable.Parse(
            """{"schema": 1, "currencies": [{"code": "USD", "digits": 2, "symbol": "$"}]}"""u8));
        Assert.Throws<FormatException>(() => CurrencyTable.Parse(
            """{"schema": 1, "currencies": [], "fallbackRates": {"date": "2026-10-22", "source": "https://x", "rates": {"USD": 0}}}"""u8));
        var table = CurrencyTable.Parse(Encoding.UTF8.GetBytes(
            """{"schema": 1, "currencies": [{"code": "USD", "digits": 2, "symbol": "US$", "symbols": {"en": "$", "ja": 5}, "common": 1}], "fallbackRates": {"date": "2026-10-22", "source": "https://x", "rates": {"USD": 1, "TWD": 32.5520031}}}"""));
        Assert.Equal("$", table.Find("USD")!.SymbolFor("en"));
        Assert.Equal("US$", table.Find("USD")!.SymbolFor("ja"));
        Assert.Equal(32.5520031m, table.FallbackRates.Rates["TWD"]);
    }

    internal static string Name(Currency currency, string language) =>
        CurrencyNames.Value.TryGetValue("currency." + currency.Code, out var entry) && entry.TryGetValue(language, out var name)
            ? name
            : currency.Code;

    private static List<string> Search(string query, string language) =>
        Codes(Table.Search(query, language, c => Name(c, language), c => Name(c, "en")));

    private static List<string> Codes(IEnumerable<Currency> currencies) => [.. currencies.Select(c => c.Code)];

    private static Dictionary<string, Dictionary<string, string>> LoadNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RepoSpec.PathOf("strings.json")));
        var names = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("currency.", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Object)
            {
                names[property.Name] = property.Value.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            }
        }

        return names;
    }
}

/// <summary>The repository's <c>spec</c> folder (strings and assets are not copied to the test output) and embedded assets.</summary>
internal static class RepoSpec
{
    private static readonly Lazy<string> Root = new(Locate);

    public static string PathOf(string relative) =>
        Path.Combine(Root.Value, "spec", relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A manifest resource of AgentSpeed.Core (<c>AgentSpeed.Prices.json</c>, <c>AgentSpeed.Currencies.json</c>).</summary>
    public static byte[] Embedded(string name)
    {
        using var stream = typeof(CurrencyTable).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing resource " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Walks up from the test output, then (for builds with an artifacts path outside the repository) from this source
    /// file's folder.
    /// </summary>
    private static string Locate()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(SourceFile()) })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "spec", "assets", "currencies.json")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("spec/assets/currencies.json not found above " + AppContext.BaseDirectory);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
