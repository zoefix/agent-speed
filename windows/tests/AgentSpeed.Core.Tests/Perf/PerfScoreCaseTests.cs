using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Tests.Perf;

/// <summary>
/// The performance score (METRICS 6.9) and its history (6.11) against <c>spec/fixtures/perf-score-cases.json</c>, through
/// the app's own evaluator, set values, recorder, pooled score, baseline and History window rows.
/// </summary>
public sealed class PerfScoreCaseTests
{
    private static readonly Lazy<JsonElement> Root = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("perf-score-cases.json"))).RootElement);

    public static TheoryData<string> AgentNames() => Names("agents");

    public static TheoryData<string> SetNames() => Names("sets");

    public static TheoryData<string> DayNames() => Names("days");

    public static TheoryData<string> PooledNames() => Names("pooled");

    public static TheoryData<string> BaselineNames() => Names("baseline");

    public static TheoryData<string> RangeNames() => Names("range");

    [Fact]
    public void EveryCaseIsRun()
    {
        var root = Root.Value;
        Assert.Equal((16, 7, 3, 1, 4, 3, 2), (root.GetProperty("latencyBins").GetArrayLength(), root.GetProperty("agents").GetArrayLength(),
            root.GetProperty("sets").GetArrayLength(), root.GetProperty("days").GetArrayLength(), root.GetProperty("pooled").GetArrayLength(),
            root.GetProperty("baseline").GetArrayLength(), root.GetProperty("range").GetArrayLength()));
    }

    [Fact]
    public void EdgesAndBuckets()
    {
        Assert.Equal(Root.Value.GetProperty("edges").EnumerateArray().Select(e => e.GetInt64()), LatencyBins.Edges);
        foreach (var item in Root.Value.GetProperty("latencyBins").EnumerateArray())
        {
            var ms = item.GetProperty("ms").GetInt64();
            Assert.True(item.GetProperty("expect").GetInt32() == LatencyBins.Of(ms), $"{ms} ms");
        }
    }

    [Theory]
    [MemberData(nameof(AgentNames))]
    public void AgentsMatch(string name)
    {
        var item = Case("agents", name);
        var responses = item.GetProperty("responses").EnumerateArray().Select(PerfJson.Response).ToList();
        var baselines = item.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array
            && item.TryGetProperty("today", out var today) && today.ValueKind == JsonValueKind.String
                ? new PerfHistory([.. history.EnumerateArray().Select(PerfJson.HistoryRecord)], today.GetString()!)
                : null;
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            foreach (var agent in item.GetProperty("agents").EnumerateArray())
            {
                var id = agent.GetProperty("id").GetString()!;
                var source = EngineCaseTests.ParseSource(agent.GetProperty("source").GetString()!);
                var phase = EngineCaseTests.ParsePhase(agent.GetProperty("phase").GetString()!);
                long? lastLine = agent.GetProperty("lastLineMs").ValueKind == JsonValueKind.Number ? agent.GetProperty("lastLineMs").GetInt64() : null;
                var actions = item.TryGetProperty("actions", out var all) && all.TryGetProperty(id, out var own)
                    ? own.EnumerateArray().Select(PerfJson.Action).ToList()
                    : [];
                var sample = PerfEvaluator.Sample(responses.Where(r => r.AgentId == id), nowMs);
                var idle = PerfSetView.EffectivePhase(phase, lastLine, nowMs) == AgentPhase.Idle;
                var evaluation = PerfEvaluator.Evaluate(source, idle, sample, actions, nowMs, baselines);
                PerfJson.AssertSame(query.GetProperty("expect").GetProperty(id), PerfJson.Evaluation(evaluation), $"{name} @ {nowMs} {id}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(SetNames))]
    public void SetsMatch(string name)
    {
        var item = Case("sets", name);
        var values = item.GetProperty("values");
        var agents = item.GetProperty("agents").EnumerateArray().Select(a =>
        {
            var id = a.GetProperty("id").GetString()!;
            var value = values.GetProperty(id);
            return new PerfCandidate(id, a.GetProperty("lastLineMs").GetInt64(), Form(value.GetProperty("form").GetString()!),
                value.GetProperty("score").ValueKind == JsonValueKind.Number ? value.GetProperty("score").GetDouble() : null);
        }).ToDictionary(c => c.AgentId, StringComparer.Ordinal);
        var expect = item.GetProperty("expect");
        PerfJson.AssertSame(expect.GetProperty("all"), PerfJson.Value(PerfSetValue.Highest(agents.Values)), name + " all");

        // The merge of parts gives the same value as the whole (an orb or a group adds its sources' or members' values).
        var merged = agents.Values.Aggregate(PerfSetValue.None,
            (total, a) => PerfSetValue.Merge(total, a.Score is null ? PerfSetValue.None : new PerfSetValue(a.Form, a.Score, a.AgentId) { LastLineMs = a.LastLineMs }));
        PerfJson.AssertSame(expect.GetProperty("all"), PerfJson.Value(merged), name + " merged");

        var links = item.GetProperty("agents").EnumerateArray()
            .Select(a => (a.GetProperty("id").GetString()!, a.GetProperty("parentId").ValueKind == JsonValueKind.String ? a.GetProperty("parentId").GetString() : null))
            .ToList();
        var groups = new JsonObject();
        foreach (var (root, members) in PerfSetView.BoardGroups(links))
        {
            var value = PerfJson.Value(PerfSetValue.Highest(members.Select(m => agents[m])));
            value["members"] = new JsonArray([.. members.Select(m => (JsonNode?)JsonValue.Create(m))]);
            groups[root] = value;
        }

        PerfJson.AssertSame(expect.GetProperty("groups"), groups, name + " groups");
    }

    [Theory]
    [MemberData(nameof(DayNames))]
    public void DaysMatch(string name)
    {
        var item = Case("days", name);
        var recorder = new PerfRecorder(UtcDay);
        foreach (var agent in item.GetProperty("agents").EnumerateArray())
        {
            var id = agent.GetProperty("id").GetString()!;
            var source = EngineCaseTests.ParseSource(agent.GetProperty("source").GetString()!);
            foreach (var e in agent.GetProperty("events").EnumerateArray())
            {
                recorder.Add(id, source, e.TryGetProperty("response", out var r)
                    ? PerfCompletion.Of(PerfJson.Response(r))
                    : PerfCompletion.Of(PerfJson.Action(e.GetProperty("action"))));
            }
        }

        PerfJson.AssertSame(item.GetProperty("expect"), new JsonArray([.. recorder.Records().Select(r => (JsonNode?)PerfJson.Record(r))]), name);
    }

    [Theory]
    [MemberData(nameof(PooledNames))]
    public void PooledMatches(string name)
    {
        var item = Case("pooled", name);
        var source = EngineCaseTests.ParseSource(item.GetProperty("source").GetString()!);
        var records = item.GetProperty("records").EnumerateArray().Select(r => PerfJson.HistoryRecord(r).Sums);
        PerfJson.AssertSame(item.GetProperty("expect"), PerfJson.Pooled(PerfPooled.Of(records, source)), name);
    }

    [Theory]
    [MemberData(nameof(BaselineNames))]
    public void BaselineMatches(string name)
    {
        var item = Case("baseline", name);
        var records = item.GetProperty("records").EnumerateArray().Select(PerfJson.HistoryRecord).ToList();
        var source = EngineCaseTests.ParseSource(item.GetProperty("source").GetString()!);
        var today = item.GetProperty("today").GetString()!;
        var value = PerfHistory.BaselineOf(records, source, item.GetProperty("model").GetString()!, today);
        PerfJson.AssertSame(item.GetProperty("expect"), value is { } v ? JsonValue.Create(v) : null, name);
        Assert.Equal(value, new PerfHistory(records, today).Baseline(source, item.GetProperty("model").GetString()!));
    }

    [Theory]
    [MemberData(nameof(RangeNames))]
    public void RangeMatches(string name)
    {
        var item = Case("range", name);
        var records = item.GetProperty("records").EnumerateArray().Select(PerfJson.HistoryRecord).ToList();
        SourceKind? source = item.TryGetProperty("source", out var only) && only.ValueKind == JsonValueKind.String
            ? EngineCaseTests.ParseSource(only.GetString()!)
            : null;
        var rows = PerfHistory.RangeOf(records, item.GetProperty("first").GetString()!, item.GetProperty("last").GetString()!, source);
        var actual = new JsonArray();
        foreach (var row in rows)
        {
            var json = new JsonObject { ["source"] = AgentSpeed.Core.History.SourceNames.Of(row.Source), ["model"] = row.Model };
            foreach (var (key, value) in PerfJson.Pooled(row.Pooled))
            {
                json[key] = value?.DeepClone();
            }

            json["trend"] = new JsonArray([.. row.Trend.Select(p => (JsonNode?)new JsonObject { ["day"] = p.Day, ["score"] = p.Score })]);
            actual.Add(json);
        }

        PerfJson.AssertSame(item.GetProperty("expect"), actual, name);
    }

    [Fact]
    public void ARoundingHalfGoesAwayFromZero()
    {
        Assert.Equal((1, 2, 1286, -2), (PerfFormula.Round(0.5), PerfFormula.Round(1.5), PerfFormula.Round(1285.5), PerfFormula.Round(-1.5)));
    }

    private static string UtcDay(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static PerfForm Form(string form) => form switch
    {
        "live" => PerfForm.Live,
        "resting" => PerfForm.Resting,
        _ => PerfForm.None,
    };

    private static JsonElement Case(string group, string name) =>
        Root.Value.GetProperty(group).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static TheoryData<string> Names(string group)
    {
        var data = new TheoryData<string>();
        foreach (var item in Root.Value.GetProperty(group).EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }
}
