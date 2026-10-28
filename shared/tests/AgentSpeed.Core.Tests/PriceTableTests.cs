using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Core.Tests;

/// <summary>The price table, model resolution, response cost and day bindings (METRICS 9.1–9.3, 9.7; NETWORK 3).</summary>
public sealed class PriceTableTests
{
    private const string BundledResource = "AgentSpeed.Prices.json";

    private static readonly Lazy<JsonObject> Cases = new(() =>
        JsonNode.Parse(File.ReadAllBytes(FixtureLocator.PathOf("price-cases.json")))!.AsObject());

    private static readonly Lazy<IReadOnlyDictionary<string, PriceTable>> FixtureTables = new(() =>
        Cases.Value["tables"]!.AsObject().ToDictionary(t => t.Key, t => Fixture((string)t.Value!), StringComparer.Ordinal));

    /// <summary>A fixture table under <c>spec/fixtures</c>, which must validate.</summary>
    internal static PriceTable Fixture(string relative)
    {
        var result = PriceTable.Load(File.ReadAllBytes(FixtureLocator.PathOf(relative)));
        return result.Table ?? throw new InvalidOperationException(relative + ": " + result.Error);
    }

    // Bundled assets

    [Fact]
    public void TheBundledTableValidatesWithItsOwnTests()
    {
        var table = PriceBook.Bundled;
        using var spec = JsonDocument.Parse(File.ReadAllBytes(SpecAsset("prices.json")));
        Assert.Equal(spec.RootElement.GetProperty("version").GetInt32(), table.Version);
        Assert.Equal(spec.RootElement.GetProperty("models").GetArrayLength(), table.Models.Count);
        Assert.Equal(spec.RootElement.GetProperty("tests").GetArrayLength(), table.Tests.Count);
        Assert.NotEmpty(table.Tests);
        Assert.All(table.Tests, test => Assert.Equal(test.Expect, table.Label(table.Resolver.Resolve(test.Model, test.Provider))));
    }

    [Fact]
    public void TheEmbeddedAssetIsTheSpecFile()
    {
        using var stream = typeof(PriceBook).Assembly.GetManifestResourceStream(BundledResource);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal(File.ReadAllBytes(SpecAsset("prices.json")), buffer.ToArray());
        Assert.Equal(buffer.ToArray(), PriceBook.Bundled.Data);
    }

    // Fixture cases (METRICS 9.2, 9.3, 9.7)

    [Fact]
    public void FixtureTablesValidate()
    {
        var tables = FixtureTables.Value;
        Assert.Equal(2, tables.Count);
        Assert.Equal(1, tables["v1"].Version);
        Assert.Equal(2, tables["v2"].Version);
    }

    [Fact]
    public void ResolveCases()
    {
        var cases = Cases.Value["resolve"]!.AsArray();
        Assert.True(cases.Count >= 40, $"{cases.Count} resolve cases");
        var mismatches = new List<string>();
        for (var index = 0; index < cases.Count; index++)
        {
            var spec = cases[index]!;
            var table = FixtureTables.Value[(string)spec["table"]!];
            var got = table.Label(table.Resolver.Resolve((string?)spec["model"], (string?)spec["provider"]));
            if (got != (string?)spec["expect"])
            {
                mismatches.Add($"resolve[{index}] {spec["model"]?.ToJsonString()}: expected {spec["expect"]} got {got}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void CostCases()
    {
        var cases = Cases.Value["cost"]!.AsArray();
        Assert.True(cases.Count >= 20, $"{cases.Count} cost cases");
        var mismatches = new List<string>();
        foreach (var spec in cases.Select(c => c!))
        {
            var name = (string?)spec["name"];
            var table = FixtureTables.Value[(string)spec["table"]!];
            var tokens = Tokens(spec["tokens"]!);
            var fast = (bool?)spec["fast"] ?? false;
            var at = UtcDay.InstantMs((string?)spec["at"]);
            var model = (string?)spec["model"];
            var provider = (string?)spec["provider"];
            long? pico = null;
            int? tier = null;
            var resolution = table.Resolver.Resolve(model, provider);
            switch (resolution.Kind)
            {
                case ResolutionKind.Priced:
                    if (table.Models[resolution.Index].PricingFor(at, resolution.Fast || fast) is { } pricing)
                    {
                        (pico, tier) = ResponseCost.PicoUsd(tokens, pricing);
                    }

                    break;
                case ResolutionKind.Free:
                    pico = 0;
                    break;
            }

            var micro = table.Cost(model, provider, tokens, at, fast);
            var expect = spec["expect"]!.AsObject();
            if (expect.ContainsKey("costPicoUsd") && pico != (long?)expect["costPicoUsd"])
            {
                mismatches.Add($"{name} costPicoUsd: expected {expect["costPicoUsd"]} got {pico}");
            }

            if (micro != (long?)expect["costMicroUsd"])
            {
                mismatches.Add($"{name} costMicroUsd: expected {expect["costMicroUsd"]} got {micro}");
            }

            if (expect.ContainsKey("tier") && tier != (int?)expect["tier"])
            {
                mismatches.Add($"{name} tier: expected {expect["tier"]} got {tier}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void DayCases()
    {
        var cases = Cases.Value["days"]!.AsArray();
        Assert.NotEmpty(cases);
        var mismatches = new List<string>();
        foreach (var spec in cases.Select(c => c!))
        {
            var name = (string?)spec["name"];
            var bindings = spec["bindings"]!.AsObject().ToDictionary(b => b.Key, b => (int)b.Value!, StringComparer.Ordinal);
            var book = new PriceBook(FixtureTables.Value.Values, bindings);
            var day = (string)spec["date"]!;
            var cost = book.Cost((string?)spec["model"], (string?)spec["provider"], Tokens(spec["tokens"]!),
                UtcDay.InstantMs((string?)spec["at"]), fast: false, day);
            if (cost != (long?)spec["costMicroUsd"])
            {
                mismatches.Add($"{name} cost: expected {spec["costMicroUsd"]?.ToJsonString() ?? "null"} got {cost}");
            }

            if (book.VersionForDay(day) != (int)spec["version"]!)
            {
                mismatches.Add($"{name} version: expected {spec["version"]} got {book.VersionForDay(day)}");
            }
        }

        Assert.Empty(mismatches);
    }

    // Rejections (NETWORK 3 validation)

    [Fact]
    public void ANewerSchemaIsReportedAsSuch()
    {
        var table = BundledJson();
        table["schema"] = 2;
        var error = Load(table).Error;
        Assert.NotNull(error);
        Assert.Equal((PriceTableErrorKind.UnsupportedSchema, 2), (error.Kind, error.Schema));
        Assert.True(error.IsNewerSchema);

        table["schema"] = 0;
        Assert.False(Load(table).Error?.IsNewerSchema ?? true);
        table.Remove("schema");
        var missing = Load(table).Error!;
        Assert.Equal((PriceTableErrorKind.UnsupportedSchema, (int?)null), (missing.Kind, missing.Schema));
        Assert.False(missing.IsNewerSchema);
    }

    [Fact]
    public void NotJsonAndOversizedAreRejected()
    {
        Assert.Equal(PriceTableErrorKind.NotJson, PriceTable.Load("[1, 2"u8).Error?.Kind);
        Assert.Equal(PriceTableErrorKind.NotJson, PriceTable.Load("[]"u8).Error?.Kind);
        Assert.Equal(PriceTableErrorKind.NotJson, PriceTable.Load("{}{}"u8).Error?.Kind);
        Assert.Equal(PriceTableErrorKind.NotJson, PriceTable.Load(new byte[PriceTable.MaxBytes]).Error?.Kind);
        Assert.Equal(PriceTableErrorKind.TooLarge, PriceTable.Load(new byte[PriceTable.MaxBytes + 1]).Error?.Kind);
    }

    [Fact]
    public void TooManyDecimalsAreRejected() =>
        Assert.Contains(Errors(LoadWithPrice("1.0000001")), e => e.Contains("more than 6 decimals", StringComparison.Ordinal));

    [Fact]
    public void OutOfRangePricesAreRejected() =>
        Assert.Contains(Errors(LoadWithPrice("10001")), e => e.Contains("out of range", StringComparison.Ordinal));

    /// <summary>Python's <c>Decimal(repr(float))</c> or exact integer reading of a price literal (cross-checked with prices.py).</summary>
    [Theory]
    [InlineData("1e-7", null, "more than 6 decimals")]
    [InlineData("0.1234567", null, "more than 6 decimals")]
    [InlineData("10000", 10_000_000_000L, null)]
    [InlineData("10000.0", 10_000_000_000L, null)]
    [InlineData("10000.0000001", null, "out of range")]
    [InlineData("-0.0", 0L, null)]
    [InlineData("-0", 0L, null)]
    [InlineData("-1", null, "out of range")]
    [InlineData("1e400", null, "out of range")]
    [InlineData("1E2", 100_000_000L, null)]
    [InlineData("2.5e0", 2_500_000L, null)]
    [InlineData("0.000001", 1L, null)]
    public void PriceLiteralsConvertExactly(string literal, long? micro, string? error)
    {
        var result = LoadWithPrice(literal);
        if (error is null)
        {
            Assert.Null(result.Error);
            Assert.Equal(micro, result.Table!.Models[0].Pricing!.Base.Prices.Input);
        }
        else
        {
            Assert.Contains(Errors(result), e => e.Contains("prices.input", StringComparison.Ordinal) && e.Contains(error, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ARepeatedKeyKeepsItsLastValueLikePython()
    {
        var accepted = LoadWithPrice("99999, \"input\": 5");
        Assert.Null(accepted.Error);
        Assert.Equal(5_000_000, accepted.Table!.Models[0].Pricing!.Base.Prices.Input);
        Assert.Contains(Errors(LoadWithPrice("5, \"input\": 99999")), e => e.Contains("out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateAliasesAndShadowingUnknownsAreRejected()
    {
        var table = BundledJson();
        var models = table["models"]!.AsArray();
        var firstId = (string)models[0]!["id"]!;
        var second = models[1]!.AsObject();
        var aliases = second["aliases"]?.AsArray() ?? [];
        aliases.Add(firstId);
        second["aliases"] = aliases.DeepClone();
        Assert.Contains(Errors(Load(table)), e => e.Contains("is used 2 times", StringComparison.Ordinal));

        foreach (var (match, value) in new[] { ("exact", firstId), ("prefix", firstId[..^2]) })
        {
            var shadowing = BundledJson();
            shadowing["unknown"]!.AsArray().Add(new JsonObject { ["match"] = match, ["value"] = value, ["reason"] = "test" });
            Assert.Contains(Errors(Load(shadowing)), e => e.Contains("shadows", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TiersMustAscendWithTheBaseFields()
    {
        var table = BundledJson();
        var tiers = TieredModel(table)["tiers"]!.AsArray();
        tiers.Add(tiers[0]!.DeepClone());
        Assert.Contains(Errors(Load(table)), e => e.Contains("greater than the previous threshold", StringComparison.Ordinal));

        var fields = BundledJson();
        TieredModel(fields)["tiers"]![0]!.AsObject().Remove("cachedInput");
        Assert.Contains(Errors(Load(fields)), e => e.Contains("must set exactly the price fields of the base prices", StringComparison.Ordinal));
    }

    [Fact]
    public void FailingOwnTestsRejectTheTable()
    {
        var table = BundledJson();
        table["tests"]!.AsArray().Add(new JsonObject { ["model"] = "claude-opus-5-5", ["expect"] = "gpt-5.5" });
        var errors = Errors(Load(table));
        Assert.Contains(errors, e => e.Contains("expected gpt-5.5", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingPeriodsAreRejected()
    {
        var table = BundledJson();
        var model = table["models"]!.AsArray().First(m => m!["periods"] is JsonArray { Count: > 0 })!;
        var periods = model["periods"]!.AsArray();
        periods.Add(periods[0]!.DeepClone());
        Assert.Contains(Errors(Load(table)), e => e.Contains("overlaps another period", StringComparison.Ordinal));
    }

    /// <summary>The apps take ASCII dates only; Python's <c>\d</c> also matches other Unicode digits and its <c>$</c> a final newline.</summary>
    [Fact]
    public void NonAsciiDigitsInDatesAndIdsAreRejected()
    {
        const string fullwidth2026 = "２０２６";
        var updated = BundledJson();
        updated["updated"] = fullwidth2026 + "-10-22";
        Assert.Contains(Errors(Load(updated)), e => e.Contains("updated must be YYYY-MM-DD", StringComparison.Ordinal));

        var id = BundledJson();
        id["models"]![0]!["id"] = "claude-opus-٥-٥";
        Assert.Contains(Errors(Load(id)), e => e.Contains("id must be lowercase", StringComparison.Ordinal));

        var period = BundledJson();
        period["models"]!.AsArray().First(m => m!["periods"] is JsonArray { Count: > 0 })!["periods"]![0]!["to"] = fullwidth2026 + "-08-21";
        Assert.Contains(Errors(Load(period)), e => e.Contains("needs from and/or to", StringComparison.Ordinal));

        var holiday = BundledJson();
        holiday["models"]!.AsArray().First(m => m!["offPeak"] is not null)!["offPeak"]!["offPeakDates"] = new JsonArray(fullwidth2026 + "-10-01");
        Assert.Contains(Errors(Load(holiday)), e => e.Contains("offPeakDates: YYYY-MM-DD dates", StringComparison.Ordinal));

        var trailing = BundledJson();
        trailing["updated"] = "2026-10-22\n";
        Assert.Contains(Errors(Load(trailing)), e => e.Contains("updated must be YYYY-MM-DD", StringComparison.Ordinal));
    }

    // Rules

    [Fact]
    public void OffPeakFollowsUtcWindowsWeekdaysAndHolidays()
    {
        var rule = new OffPeakRule(
            [(60, 240), (360, 600)],
            new HashSet<int> { 1, 2, 3, 4, 5 },
            new HashSet<string>(StringComparer.Ordinal) { "2026-10-01" },
            new TieredPrices(new PriceSet(1, 1, 1, 1, 1), []));
        var monday = UtcDay.InstantMs("2026-10-19T02:00Z")!.Value;
        Assert.False(rule.IsOffPeak(monday), "Monday 02:00 UTC is peak");
        Assert.False(rule.IsOffPeak(monday - 60 * 60_000), "01:00 is the inclusive start");
        Assert.True(rule.IsOffPeak(monday - 60 * 60_000 - 1), "00:59:59.999 is before the window");
        Assert.True(rule.IsOffPeak(monday + 2 * 3_600_000), "04:00 is the exclusive end");
        Assert.False(rule.IsOffPeak(monday + 4 * 3_600_000), "06:00 starts the second window");
        Assert.True(rule.IsOffPeak(UtcDay.InstantMs("2026-10-18T02:00Z")!.Value), "Sunday");
        Assert.True(rule.IsOffPeak(UtcDay.InstantMs("2026-10-01T02:00Z")!.Value), "a holiday");
        Assert.False(rule.IsOffPeak(UtcDay.InstantMs("2026-10-02T02:00Z")!.Value), "the day after the holiday");
    }

    [Fact]
    public void UtcCalendarArithmetic()
    {
        Assert.Equal(0, UtcDay.InstantMs("1970-01-01"));
        Assert.Equal(1_792_540_740_000, UtcDay.InstantMs("2026-10-20T23:59Z"));
        Assert.Null(UtcDay.InstantMs("2026-02-29"));
        Assert.Equal(1_709_164_800_000, UtcDay.InstantMs("2024-02-29"));
        Assert.Null(UtcDay.InstantMs("2100-02-29"));
        Assert.Equal(951_782_400_000, UtcDay.InstantMs("2000-02-29"));
        Assert.Null(UtcDay.InstantMs("2026-10-20T24:00Z"));
        Assert.Null(UtcDay.InstantMs("2026-10-20T12:60Z"));
        Assert.Null(UtcDay.InstantMs("2026-13-01"));
        Assert.Null(UtcDay.InstantMs("2026-10-20T12:00"));
        Assert.Null(UtcDay.InstantMs("2026-10-2١"));
        Assert.Null(UtcDay.InstantMs(null));
        Assert.Equal("2026-10-20", new UtcDay(1_792_540_799_999).Label);
        Assert.Equal("2026-10-21", new UtcDay(1_792_540_800_000).Label);
        Assert.Equal(4, new UtcDay(0).IsoWeekday);
        Assert.Equal("1969-12-31", new UtcDay(-1).Label);
        Assert.Equal((3, 1439), (new UtcDay(-1).IsoWeekday, new UtcDay(-1).MinuteOfDay));
        Assert.True(UtcDay.IsDateText("2026-10-20"));
        Assert.False(UtcDay.IsDateText("2026-10-2١"));
        Assert.False(UtcDay.IsDateText("2026/10/20"));
        Assert.False(UtcDay.IsDateText(null));

        for (var ms = UtcDay.InstantMs("1900-01-01")!.Value; ms < UtcDay.InstantMs("2101-01-01")!.Value; ms += 37 * UtcDay.MsPerDay + 3_599_999)
        {
            var day = new UtcDay(ms);
            var expected = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            Assert.Equal(expected.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), day.Label);
            Assert.Equal(expected.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)expected.DayOfWeek, day.IsoWeekday);
            Assert.Equal(expected.Hour * 60 + expected.Minute, day.MinuteOfDay);
            Assert.Equal(day.DaysSinceEpoch * UtcDay.MsPerDay, UtcDay.InstantMs(day.Label));
        }
    }

    [Fact]
    public void CostClassesAreClamped()
    {
        var tokens = new TokenCounts(Input: 500_000_000, Output: 500_000_000);
        var max = new PriceSet(10_000_000_000, 0, 0, 0, 10_000_000_000);
        Assert.Equal(2 * ResponseCost.MaxClassTokens * 10_000_000_000, ResponseCost.PicoUsd(tokens, new TieredPrices(max, [])).Pico);

        var classes = new TokenCounts(
            Input: 900_000_000, CachedInput: 300_000_000, CacheWriteInput: 300_000_000, Output: 0, CacheWrite1hInput: 150_000_000);
        Assert.Equal(10 * ResponseCost.MaxClassTokens, ResponseCost.PicoUsd(classes, new TieredPrices(new PriceSet(1, 2, 3, 4, 5), [])).Pico);

        var everything = new TokenCounts(
            Input: 5_000_000_000, CachedInput: 1_000_000_000, CacheWriteInput: 2_000_000_000, Output: 1_000_000_000,
            CacheWrite1hInput: 1_000_000_000);
        var top = new PriceSet(10_000_000_000, 10_000_000_000, 10_000_000_000, 10_000_000_000, 10_000_000_000);
        Assert.Equal(5 * ResponseCost.MaxClassTokens * 10_000_000_000, ResponseCost.PicoUsd(everything, new TieredPrices(top, [])).Pico);

        Assert.Equal(0, ResponseCost.MicroUsd(499_999));
        Assert.Equal(1, ResponseCost.MicroUsd(500_000));
        Assert.Equal(1, ResponseCost.MicroUsd(1_499_999));
    }

    [Fact]
    public void BindingsKeepTheirTableAndRepairMissingOnes()
    {
        var tables = FixtureTables.Value;
        var book = new PriceBook(tables.Values);
        var raised = 0;
        book.Bound += () => raised++;
        Assert.Equal(2, book.CurrentVersion);
        Assert.Equal(["2026-10-02"], book.Restore(new Dictionary<string, int> { ["2026-10-01"] = 1, ["2026-10-02"] = 9 }));
        Assert.Equal(1, book.VersionForDay("2026-10-01"));
        Assert.Equal(2, book.VersionForDay("2026-10-02"));
        Assert.Equal(0, raised);
        Assert.Equal(2, book.VersionForDay("2026-10-03"));
        Assert.Equal(1, raised);
        Assert.Equal([("2026-10-02", 2), ("2026-10-03", 2)], Pairs(book.TakeNewBindings()));
        Assert.Empty(book.TakeNewBindings());
        Assert.False(book.Add(tables["v1"]));
        book.ClearBindings();
        Assert.Null(book.BoundVersion("2026-10-01"));
    }

    [Fact]
    public void StoredTableCopiesAreAddedAndInvalidOnesRepaired()
    {
        var tables = FixtureTables.Value;
        var book = new PriceBook([tables["v2"]]);
        var repaired = book.Restore(
            new Dictionary<string, int> { ["2026-10-01"] = 1, ["2026-10-02"] = 3, ["2026-10-03"] = 5 },
            new Dictionary<int, byte[]> { [1] = tables["v1"].Data, [3] = "{\"schema\": 1}"u8.ToArray(), [5] = tables["v1"].Data });
        Assert.Equal(["2026-10-02", "2026-10-03"], repaired);
        Assert.NotNull(book.Table(1));
        Assert.Null(book.Table(3));
        Assert.Null(book.Table(5));
        Assert.Equal(2, book.CurrentVersion);
        Assert.Equal((1, 2, 2), (book.VersionForDay("2026-10-01"), book.VersionForDay("2026-10-02"), book.VersionForDay("2026-10-03")));
        Assert.Equal([("2026-10-02", 2), ("2026-10-03", 2)], Pairs(book.TakeNewBindings()));
    }

    private static TokenCounts Tokens(JsonNode tokens) => new(
        Input: (long?)tokens["input"] ?? 0,
        CachedInput: (long?)tokens["cachedInput"] ?? 0,
        CacheWriteInput: (long?)tokens["cacheWriteInput"] ?? 0,
        Output: (long?)tokens["output"] ?? 0,
        Reasoning: (long?)tokens["reasoning"] ?? 0,
        CacheWrite1hInput: (long?)tokens["cacheWrite1hInput"] ?? 0);

    private static List<(string Day, int Version)> Pairs(IReadOnlyDictionary<string, int> bindings) =>
        [.. bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => (b.Key, b.Value))];

    private static JsonObject BundledJson() => JsonNode.Parse(PriceBook.Bundled.Data)!.AsObject();

    private static JsonObject TieredModel(JsonObject table) =>
        table["models"]!.AsArray().First(m => m!["tiers"] is JsonArray { Count: > 0 })!.AsObject();

    private static PriceTableLoadResult Load(JsonNode table) => PriceTable.Load(Encoding.UTF8.GetBytes(table.ToJsonString()));

    /// <summary>The bundled table with the raw JSON <paramref name="literal"/> as the first model's input price.</summary>
    private static PriceTableLoadResult LoadWithPrice(string literal)
    {
        var table = BundledJson();
        table["models"]![0]!["prices"]!["input"] = "__price__";
        var text = table.ToJsonString();
        Assert.Contains("\"__price__\"", text, StringComparison.Ordinal);
        return PriceTable.Load(Encoding.UTF8.GetBytes(text.Replace("\"__price__\"", literal, StringComparison.Ordinal)));
    }

    private static IReadOnlyList<string> Errors(PriceTableLoadResult result) =>
        result.Error is { Kind: PriceTableErrorKind.Invalid } error ? error.Errors : [];

    /// <summary>A file of the repository's <c>spec/assets</c> (not copied to the test output; an artifacts path may lie outside the repository).</summary>
    private static string SpecAsset(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(SourceFile()) })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "spec", "assets", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("spec/assets/" + name + " not found above " + AppContext.BaseDirectory);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
