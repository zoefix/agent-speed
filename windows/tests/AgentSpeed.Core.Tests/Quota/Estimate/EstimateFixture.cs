using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Quota.Estimate;

/// <summary>Loads spec/fixtures/quota-estimate-cases.json and compares outputs with the reference's JSON.</summary>
internal static class EstimateFixture
{
    public const long Minute = EstimateLimits.Minute;
    public const long Hour = EstimateLimits.Hour;
    public const long Day = EstimateLimits.Day;
    public const long T0 = 1_794_000_000_000 / Day * Day;

    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-estimate-cases.json"))).RootElement.Clone());

    public static JsonElement Root => Cases.Value;

    public static long? OptionalLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

    public static double? OptionalDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static SourceKind Source(string? name) => EstimateNames.ParseSource(name) ?? throw new InvalidDataException(name);

    /// <summary>The reference's <c>ledger_from</c>: account <c>a</c>, source codex, model gpt-5.5 unless named, no tokens.</summary>
    public static SpendLedger LedgerFrom(JsonElement rows)
    {
        var ledger = new SpendLedger();
        foreach (var r in rows.EnumerateArray())
        {
            ledger.Add(OptionalString(r, "account") ?? "a", EstimateNames.ParseSource(OptionalString(r, "source")) ?? SourceKind.Codex,
                OptionalString(r, "model") ?? "gpt-5.5", r.GetProperty("at").GetInt64(), OptionalLong(r, "cost"), OptionalLong(r, "requests") ?? 1);
        }

        return ledger;
    }

    public static QuotaObservation Observation(JsonElement o, string account = "a") => new(
        account, Source(OptionalString(o, "source")), EstimateNames.ParseKind(o.GetProperty("kind").GetString())!.Value,
        OptionalString(o, "name"), o.GetProperty("minutes").GetInt32(), o.GetProperty("p").GetDouble(), o.GetProperty("t").GetInt64(),
        o.GetProperty("tLo").GetInt64(), o.GetProperty("R").GetInt64(), OptionalString(o, "plan"), OptionalString(o, "tier"));

    public static QuotaWindow Window(JsonElement w) => new(
        EstimateNames.ParseKind(w.GetProperty("kind").GetString())!.Value, OptionalString(w, "name"),
        w.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : null,
        w.GetProperty("usedPercent").GetDouble(), OptionalLong(w, "resetsAtMs"));

    public static double Hi(JsonElement e) => e.ValueKind == JsonValueKind.Null ? double.PositiveInfinity : e.GetDouble();

    /// <summary>The reference's <c>fnum</c>: null for ∞, three decimals for a float.</summary>
    public static JsonNode? Fnum(double? x) => x is not { } v || !double.IsFinite(v) ? null : JsonValue.Create(Math.Round(v, 3));

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement ToElement(JsonNode? node) => Parse(node?.ToJsonString() ?? "null");

    public static JsonElement EntryJson(QuotaReportEntry entry) => Parse(ReportQuotas.Entry(entry).ToCompactString());

    public static JsonElement EntriesJson(IEnumerable<QuotaReportEntry> entries) => Parse(ReportQuotas.Json([.. entries]).ToCompactString());

    /// <summary>Deep equality: integers exactly, other numbers within 1e-3 (the reference rounds to three decimals).</summary>
    public static void AssertSame(JsonElement expected, JsonElement actual, string path)
    {
        var problems = new List<string>();
        Compare(expected, actual, path, problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> problems)
    {
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
        {
            var e = expected.GetDouble();
            var a = actual.GetDouble();
            var integral = !expected.GetRawText().Contains('.') && !expected.GetRawText().Contains('e') && !expected.GetRawText().Contains('E');
            var ok = integral && Math.Abs(e) < 1e15 ? a == e : Math.Abs(a - e) <= 1e-3 + 1e-9 * Math.Abs(e);
            if (!ok)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}"));
            }

            return;
        }

        if (expected.ValueKind != actual.ValueKind && !(IsBool(expected) && IsBool(actual)))
        {
            problems.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var names = expected.EnumerateObject().Select(p => p.Name).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToList();
                foreach (var missing in names.Except(actualNames))
                {
                    problems.Add($"{path}.{missing}: missing");
                }

                foreach (var extra in actualNames.Except(names))
                {
                    problems.Add($"{path}.{extra}: unexpected");
                }

                foreach (var name in names.Intersect(actualNames))
                {
                    Compare(expected.GetProperty(name), actual.GetProperty(name), path + "." + name, problems);
                }

                break;
            case JsonValueKind.Array:
                var e = expected.EnumerateArray().ToList();
                var a = actual.EnumerateArray().ToList();
                if (e.Count != a.Count)
                {
                    problems.Add($"{path}: expected {e.Count} items, got {a.Count}: {actual.GetRawText()[..Math.Min(400, actual.GetRawText().Length)]}");
                    return;
                }

                for (var i = 0; i < e.Count; i++)
                {
                    Compare(e[i], a[i], path + "[" + i + "]", problems);
                }

                break;
            case JsonValueKind.String:
                if (expected.GetString() != actual.GetString())
                {
                    problems.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
                }

                break;
            case JsonValueKind.True or JsonValueKind.False:
                if (expected.GetBoolean() != actual.GetBoolean())
                {
                    problems.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
                }

                break;
        }
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>The JSON text of a store, as the app writes it.</summary>
    public static JsonElement StoreJson(EstimateStoreData data) => Parse(Encoding.UTF8.GetString(EstimateStore.Serialize(data)));
}
