using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Tests.Perf;

/// <summary>
/// The app's performance results in the shapes of <c>perf-score-cases.json</c> and <c>history-expected.json</c>, the
/// fixtures' inputs read back into the app's types, and the reference's comparison (numbers within 1e-9, relative above 1).
/// </summary>
internal static class PerfJson
{
    public static JsonObject Evaluation(PerfEvaluation e) => new()
    {
        ["form"] = Form(e.Form),
        ["score"] = e.Score,
        ["shown"] = e.Shown,
        ["provisional"] = e.Provisional,
        ["n"] = e.N,
        ["tools"] = e.Tools,
        ["activeMs"] = e.ActiveMs,
        ["spanStartMs"] = e.SpanStartMs,
        ["model"] = e.Model,
        ["components"] = Components(e.Components),
        ["baseline"] = e.Baseline,
        ["vsUsual"] = e.VsUsual,
        ["vsUsualPercent"] = e.VsUsualPercent,
    };

    public static JsonObject Pooled(PerfPooled p) => new()
    {
        ["n"] = p.N,
        ["tools"] = p.Tools,
        ["activeMs"] = p.ActiveMs,
        ["score"] = p.Score,
        ["shown"] = p.Shown,
        ["components"] = Components(p.Components),
    };

    public static JsonObject Value(PerfSetValue v) => new()
    {
        ["form"] = Form(v.Form),
        ["score"] = v.Score,
        ["shown"] = v.Shown,
        ["agent"] = v.AgentId,
    };

    public static JsonObject Record(PerfRecord r)
    {
        var s = r.Sums;
        return new JsonObject
        {
            ["day"] = r.Day,
            ["source"] = AgentSpeed.Core.History.SourceNames.Of(r.Source),
            ["model"] = r.Model,
            ["n"] = s.N,
            ["outputSum"] = s.OutputSum,
            ["durationMsSum"] = s.DurationMsSum,
            ["latencyBins"] = new JsonArray([.. s.Bins.Select(b => (JsonNode?)JsonValue.Create(b))]),
            ["reasonN"] = s.ReasonN,
            ["reasonOutput"] = s.ReasonOutput,
            ["reasonDurationMsSum"] = s.ReasonDurationMsSum,
            ["cacheN"] = s.CacheN,
            ["cacheInput"] = s.CacheInput,
            ["cacheCached"] = s.CacheCached,
            ["toolN"] = s.ToolN,
            ["toolLnSum"] = s.ToolLnSum,
            ["toolFailed"] = s.ToolFailed,
            ["activeMs"] = s.ActiveMs,
        };
    }

    public static JsonArray Components(IReadOnlyList<PerfComponent> components) =>
    [
        .. components.Select(c => (JsonNode?)new JsonObject
        {
            ["id"] = c.Id.ToString(),
            ["present"] = c.Present,
            ["reason"] = c.Reason switch
            {
                PerfAbsence.NotRecorded => "notRecorded",
                PerfAbsence.FewSamples => "fewSamples",
                _ => null,
            },
            ["samples"] = c.Samples,
            ["measure"] = c.Measure,
            ["ratio"] = c.Ratio,
            ["subScore"] = c.SubScore,
        }),
    ];

    public static string Form(PerfForm form) => form switch
    {
        PerfForm.Live => "live",
        PerfForm.Resting => "resting",
        _ => "none",
    };

    /// <summary>A response in the fixtures' engine form.</summary>
    public static ParsedResponse Response(JsonElement r) => new(
        r.GetProperty("key").GetString()!,
        EngineCaseTests.ParseSource(r.GetProperty("source").GetString()!),
        r.GetProperty("agentId").GetString()!,
        Text(r, "model"),
        Number(r, "startMs"),
        r.GetProperty("endMs").GetInt64(),
        new TokenCounts(Input: Long(r, "input"), CachedInput: Long(r, "cachedInput"), Output: Long(r, "output"), Reasoning: Long(r, "reasoning")),
        !r.TryGetProperty("final", out var final) || final.GetBoolean())
    {
        FirstMs = Number(r, "firstMs"),
    };

    /// <summary>An action in the fixtures' form (METRICS 6.10).</summary>
    public static WorkAction Action(JsonElement a) => new(
        a.GetProperty("callMs").GetInt64(),
        a.GetProperty("tool").GetString()!,
        ulong.Parse(a.GetProperty("digest").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        a.TryGetProperty("failed", out var failed) && failed.ValueKind != JsonValueKind.Null ? failed.GetBoolean() : null)
    {
        ResultMs = Number(a, "resultMs"),
        Model = Text(a, "model"),
    };

    /// <summary>A history record in the fixtures' form (dense latency bins).</summary>
    public static PerfRecord HistoryRecord(JsonElement r)
    {
        var sums = new PerfSums
        {
            N = Long(r, "n"),
            OutputSum = Long(r, "outputSum"),
            DurationMsSum = Long(r, "durationMsSum"),
            ReasonN = Long(r, "reasonN"),
            ReasonOutput = Long(r, "reasonOutput"),
            ReasonDurationMsSum = Long(r, "reasonDurationMsSum"),
            CacheN = Long(r, "cacheN"),
            CacheInput = Long(r, "cacheInput"),
            CacheCached = Long(r, "cacheCached"),
            ToolN = Long(r, "toolN"),
            ToolLnSum = r.GetProperty("toolLnSum").GetDouble(),
            ToolFailed = Long(r, "toolFailed"),
            ActiveMs = Long(r, "activeMs"),
        };
        var k = 0;
        foreach (var bin in r.GetProperty("latencyBins").EnumerateArray())
        {
            sums.LatencyBins[k++] = bin.GetInt64();
        }

        return new PerfRecord(
            r.TryGetProperty("day", out var day) ? day.GetString()! : string.Empty,
            r.TryGetProperty("source", out var source) ? EngineCaseTests.ParseSource(source.GetString()!) : SourceKind.ClaudeCode,
            r.TryGetProperty("model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
            sums);
    }

    /// <summary>The first place <paramref name="actual"/> differs from <paramref name="expected"/>, or null.</summary>
    public static string? Difference(JsonElement expected, JsonNode? actual, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                return actual is null ? null : $"{path}: expected null, got {actual.ToJsonString()}";
            case JsonValueKind.Number:
                if (actual is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
                {
                    return $"{path}: expected {expected.GetRawText()}, got {actual?.ToJsonString() ?? "null"}";
                }

                var a = expected.GetDouble();
                var b = JsonDocument.Parse(value.ToJsonString()).RootElement.GetDouble();
                return Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b))) ? null : $"{path}: expected {a:R}, got {b:R}";
            case JsonValueKind.True or JsonValueKind.False or JsonValueKind.String:
                return actual is JsonValue v && JsonElementEquals(expected, v) ? null : $"{path}: expected {expected.GetRawText()}, got {actual?.ToJsonString() ?? "null"}";
            case JsonValueKind.Array:
                if (actual is not JsonArray array || array.Count != expected.GetArrayLength())
                {
                    return $"{path}: expected {expected.GetArrayLength()} items, got {actual?.ToJsonString() ?? "null"}";
                }

                var i = 0;
                foreach (var item in expected.EnumerateArray())
                {
                    if (Difference(item, array[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }

                    i++;
                }

                return null;
            case JsonValueKind.Object:
                if (actual is not JsonObject obj)
                {
                    return $"{path}: expected an object, got {actual?.ToJsonString() ?? "null"}";
                }

                var keys = expected.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                if (obj.Select(p => p.Key).FirstOrDefault(k => !keys.Contains(k)) is { } extra)
                {
                    return $"{path}.{extra}: not in the fixture";
                }

                foreach (var property in expected.EnumerateObject())
                {
                    if (!obj.ContainsKey(property.Name))
                    {
                        return $"{path}.{property.Name}: missing";
                    }

                    if (Difference(property.Value, obj[property.Name], $"{path}.{property.Name}") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return $"{path}: unexpected {expected.ValueKind}";
        }
    }

    public static void AssertSame(JsonElement expected, JsonNode? actual, string label)
    {
        var difference = Difference(expected, actual, label);
        Assert.True(difference is null, difference);
    }

    private static bool JsonElementEquals(JsonElement expected, JsonValue actual) => expected.ValueKind switch
    {
        JsonValueKind.String => actual.GetValueKind() == JsonValueKind.String && actual.GetValue<string>() == expected.GetString(),
        _ => actual.GetValueKind() == expected.ValueKind,
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
}
