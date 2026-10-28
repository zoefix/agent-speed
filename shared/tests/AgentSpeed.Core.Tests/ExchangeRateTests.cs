using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Tests;

public sealed class ExchangeRateTests
{
    private static readonly HashSet<string> Supported = ["USD", "CNY", "TWD", "JPY", "EUR"];

    public static TheoryData<int> ParseCases() => Indexes("parse");

    public static TheoryData<int> AcceptCases() => Indexes("accept");

    [Fact]
    public void TheFixtureCoversEveryShape()
    {
        using var document = Load();
        var root = document.RootElement;
        Assert.True(Supported.SetEquals(root.GetProperty("supported").EnumerateArray().Select(e => e.GetString()!)));
        var parse = root.GetProperty("parse").EnumerateArray().ToList();
        Assert.True(parse.Count >= 6);
        foreach (var id in new[] { "open.er-api.com", "currency-api", "frankfurter" })
        {
            Assert.Contains(parse, c => c.GetProperty("provider").GetString() == id);
        }

        Assert.True(root.GetProperty("accept").GetArrayLength() >= 8);
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseCase(int index)
    {
        using var document = Load();
        var spec = document.RootElement.GetProperty("parse")[index];
        var name = spec.GetProperty("name").GetString();
        var shape = ExchangeRates.ShapeOf(spec.GetProperty("provider").GetString()!);
        Assert.NotNull(shape);
        Assert.Equal(spec.GetProperty("provider").GetString(), ExchangeRates.IdOf(shape.Value));
        var body = Encoding.UTF8.GetBytes(spec.GetProperty("body").GetRawText());
        var expect = spec.GetProperty("expect");
        if (expect.ValueKind == JsonValueKind.Null)
        {
            Assert.Throws<FormatException>(() => ExchangeRates.Parse(shape.Value, body, Supported));
            return;
        }

        var parsed = ExchangeRates.Parse(shape.Value, body, Supported);
        Assert.Equal(expect.GetProperty("dateMs").GetInt64(), parsed.DateMs);
        var expected = expect.GetProperty("rates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(expected.Keys.Order(), parsed.Rates.Keys.Order());
        foreach (var (code, text) in expected)
        {
            Assert.True(parsed.Rates[code].ToString(CultureInfo.InvariantCulture) == text, $"{name} {code}: {parsed.Rates[code]} != {text}");
        }
    }

    [Theory]
    [MemberData(nameof(AcceptCases))]
    public void AcceptCase(int index)
    {
        using var document = Load();
        var spec = document.RootElement.GetProperty("accept")[index];
        ExchangeRates.Reference? last = spec.TryGetProperty("last", out var l)
            ? new ExchangeRates.Reference(l.GetProperty("dateMs").GetInt64(), l.GetProperty("acceptedMs").GetInt64(), Decimals(l.GetProperty("rates"))!)
            : null;
        var verdict = ExchangeRates.Check(
            spec.GetProperty("dateMs").GetInt64(),
            Decimals(spec.GetProperty("rates"))!,
            spec.GetProperty("selected").GetString()!,
            spec.GetProperty("nowMs").GetInt64(),
            last,
            spec.TryGetProperty("fallback", out var fallback) ? Decimals(fallback) : null,
            spec.TryGetProperty("other", out var other) ? Decimals(other) : null);
        Assert.True(verdict.IsAccepted == spec.GetProperty("accept").GetBoolean(), $"{spec.GetProperty("name").GetString()}: {verdict}");
    }

    [Fact]
    public void StaleReferencesNeedASecondProvider()
    {
        var verdict = ExchangeRates.Check(
            100 * ExchangeRates.DayMs, Rates(("TWD", 45m)), "TWD", 100 * ExchangeRates.DayMs,
            new ExchangeRates.Reference(0, 0, Rates(("TWD", 32m))), Rates(("TWD", 32m)));
        Assert.Equal(ExchangeRates.Verdict.NeedsSecondProvider, verdict);
        var missing = ExchangeRates.Check(
            ExchangeRates.DayMs, Rates(("TWD", 45m)), "TWD", ExchangeRates.DayMs,
            new ExchangeRates.Reference(0, ExchangeRates.DayMs, Rates(("JPY", 158m))), Rates(("TWD", 32m)));
        Assert.Equal(ExchangeRates.VerdictKind.NeedsSecondProvider, missing.Kind);
        var bounds = ExchangeRates.Check(0, Rates(("TWD", 64m)), "TWD", 0, null, Rates(("TWD", 32m)));
        Assert.True(bounds.IsAccepted, "x2 is still inside the bound");
        Assert.Equal("outside x0.5..x2", ExchangeRates.Check(0, Rates(("TWD", 64.0001m)), "TWD", 0, null, Rates(("TWD", 32m))).Reason);
        Assert.True(ExchangeRates.Check(0, Rates(("TWD", 16m)), "TWD", 0, null, Rates(("TWD", 32m))).IsAccepted);
    }

    [Fact]
    public void DatesTakeAsciiDigitsAndRealDaysOnly()
    {
        foreach (var date in new[] { "２０２６-10-22", "٢٠٢٦-10-22", "2026-02-30", "2026-10-22T00:00Z", "2026-10-2", "20261022" })
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["date"] = date, ["usd"] = new { twd = 32.5 } }));
            Assert.Throws<FormatException>(() => ExchangeRates.Parse(ExchangeRates.Shape.Fawaz, body, Supported));
        }

        var leap = ExchangeRates.Parse(ExchangeRates.Shape.Fawaz, """{"date":"2028-02-29","usd":{"twd":32.5}}"""u8, Supported);
        Assert.Equal(1_835_395_200_000, leap.DateMs);
    }

    [Fact]
    public void NumbersReadLikePython()
    {
        var parsed = ExchangeRates.Parse(
            ExchangeRates.Shape.ErApi,
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351.9,"rates":{"TWD":3.25e1,"JPY":158,"eur":0.20000000000000001,"CNY":6.67476008}}"""u8,
            Supported);
        Assert.Equal(1_792_627_351_000, parsed.DateMs);
        Assert.Equal(32.5m, parsed.Rates["TWD"]);
        Assert.Equal(158m, parsed.Rates["JPY"]);
        Assert.Equal("0.2", parsed.Rates["EUR"].ToString(CultureInfo.InvariantCulture));
        Assert.Equal("6.67476008", parsed.Rates["CNY"].ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1m, parsed.Rates["USD"]);
    }

    [Fact]
    public void BadValuesRejectTheResponse()
    {
        string[] bodies =
        [
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":true}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":null}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":1e400}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":100000000000000000000000000000000}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":0}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":[]}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":0,"rates":{}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":"1792627351","rates":{}}""",
            """{"result":"success","base_code":"EUR","time_last_update_unix":1792627351,"rates":{}}""",
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"TWD":32}}x""",
            """[1]""",
        ];
        foreach (var body in bodies)
        {
            Assert.Throws<FormatException>(() => ExchangeRates.Parse(ExchangeRates.Shape.ErApi, Encoding.UTF8.GetBytes(body), Supported));
        }

        var unsupported = ExchangeRates.Parse(
            ExchangeRates.Shape.ErApi,
            """{"result":"success","base_code":"USD","time_last_update_unix":1792627351,"rates":{"XAU":"gold","TWD":32}}"""u8,
            Supported);
        Assert.Equal(["TWD", "USD"], unsupported.Rates.Keys.Order());
    }

    [Fact]
    public void RepeatedKeysKeepTheLastValue()
    {
        var parsed = ExchangeRates.Parse(
            ExchangeRates.Shape.Fawaz,
            """{"date":"2026-10-21","date":"2026-10-22","usd":{"twd":-1,"twd":32.55,"TWD":32.6}}"""u8,
            Supported);
        Assert.Equal(1_792_627_200_000, parsed.DateMs);
        Assert.Equal(32.6m, parsed.Rates["TWD"]);
    }

    [Fact]
    public void FrankfurterRatesMayBeMissingButNotMalformed()
    {
        var none = ExchangeRates.Parse(ExchangeRates.Shape.Frankfurter, """{"base":"USD","date":"2026-10-21","rates":null}"""u8, Supported);
        Assert.Equal(["USD"], none.Rates.Keys);
        var usd = ExchangeRates.Parse(ExchangeRates.Shape.Frankfurter, """{"base":"USD","date":"2026-10-21","rates":{"USD":-3,"JPY":157.88}}"""u8, Supported);
        Assert.Equal(1m, usd.Rates["USD"]);
        Assert.Throws<FormatException>(() =>
            ExchangeRates.Parse(ExchangeRates.Shape.Frankfurter, """{"base":"USD","date":"2026-10-21","rates":[1]}"""u8, Supported));
        Assert.Throws<FormatException>(() =>
            ExchangeRates.Parse(ExchangeRates.Shape.Frankfurter, """{"base":"EUR","date":"2026-10-21","rates":{}}"""u8, Supported));
    }

    [Fact]
    public void RatesInUseFallBackToTheBundledRates()
    {
        var fallback = CurrencyTable.Bundled.FallbackRates;
        var bundled = new RatesInUse(null, fallback);
        Assert.True(bundled.IsFallback);
        Assert.False(bundled.NeedsAttribution);
        Assert.Equal(fallback.Rates["TWD"], bundled.Rate("TWD"));
        Assert.Equal(1m, bundled.Rate("USD"));
        Assert.Equal(fallback.DateMs, bundled.DateMs);
        Assert.Null(bundled.FetchedMs);
        var accepted = new RatesInUse(
            new ExchangeRateSet("open.er-api.com", "https://open.er-api.com/v6/latest/USD", 0, 0, Rates(("USD", 1m), ("TWD", 32m))),
            fallback);
        Assert.True(accepted.NeedsAttribution);
        Assert.False(accepted.IsFallback);
        Assert.Equal(32m, accepted.Rate("TWD"));
        Assert.Equal(fallback.Rates["JPY"], accepted.Rate("JPY"));
        Assert.Equal(1m, accepted.Rate("XXX"));
        Assert.False(accepted.IsStale(7 * ExchangeRates.DayMs));
        Assert.True(accepted.IsStale(7 * ExchangeRates.DayMs + 1));
    }

    [Fact]
    public void StoredRatesRoundTripExactly()
    {
        var set = new ExchangeRateSet(
            "cdn.jsdelivr.net", "https://x", 5, 6,
            Rates(("USD", 1m), ("CNY", MoneyFormatTests.Dec("6.67476008")), ("IDR", MoneyFormatTests.Dec("18040.7")), ("BTC", MoneyFormatTests.Dec("0.0000091"))));
        var stored = StoredRates.From(set, new Dictionary<string, string> { ["https://x"] = "\"abc\"" });
        var json = stored.ToJson();
        var decoded = StoredRates.FromJson(json);
        Assert.NotNull(decoded);
        Assert.Equal(set, decoded.ToRateSet());
        Assert.Equal("\"abc\"", decoded.ETags["https://x"]);
        var text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"CNY\": \"6.67476008\"", text, StringComparison.Ordinal);
        Assert.Contains("\"BTC\": \"0.0000091\"", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"date\"", StringComparison.Ordinal) < text.IndexOf("\"url\"", StringComparison.Ordinal), "sorted keys");
    }

    [Fact]
    public void StoredRatesNeedEveryField()
    {
        Assert.Null(StoredRates.FromJson("""{"provider":"p","url":"u","date":1,"fetchedMs":2,"etags":{}}"""u8));
        Assert.Null(StoredRates.FromJson("""{"provider":"p","url":"u","date":1.5,"fetchedMs":2,"etags":{},"rates":{}}"""u8));
        Assert.Null(StoredRates.FromJson("""{"provider":"p","url":"u","date":1,"fetchedMs":2,"etags":{},"rates":{"USD":1}}"""u8));
        Assert.NotNull(StoredRates.FromJson("""{"provider":"p","url":"u","date":1,"fetchedMs":2,"etags":{},"rates":{"USD":"1"}}"""u8));
        var garbage = StoredRates.FromJson("""{"provider":"p","url":"u","date":1,"fetchedMs":2,"etags":{},"rates":{"USD":"1","TWD":"x","JPY":"-3"}}"""u8)!;
        Assert.Equal(["USD"], garbage.ToRateSet().Rates.Keys);
    }

    internal static Dictionary<string, decimal> Rates(params (string Code, decimal Rate)[] pairs) =>
        pairs.ToDictionary(p => p.Code, p => p.Rate, StringComparer.Ordinal);

    private static Dictionary<string, decimal>? Decimals(JsonElement element) =>
        element.ValueKind != JsonValueKind.Object
            ? null
            : element.EnumerateObject().ToDictionary(p => p.Name, p => MoneyFormatTests.Dec(p.Value.GetString()!), StringComparer.Ordinal);

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("rates-cases.json")));

    private static TheoryData<int> Indexes(string section)
    {
        using var document = Load();
        var data = new TheoryData<int>();
        for (var i = 0; i < document.RootElement.GetProperty(section).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }
}
