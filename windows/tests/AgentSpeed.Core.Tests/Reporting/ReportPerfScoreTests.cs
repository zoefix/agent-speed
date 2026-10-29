using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// The <c>perfScore</c> extension (METRICS 11.10) against <c>report-vectors.json</c> → <c>perfScore</c>: which extension a
/// report carries, the live values and the model list (the legacy thinking-only <c>taskScore</c> included), and the
/// statistics ledger (watch, counting, the online union rule, builds and breaks), each through the app's own code.
/// </summary>
public sealed class ReportPerfScoreTests
{
    private static JsonElement Section => ReportSpec.Vector("perfScore");

    public static TheoryData<string> Names(string group)
    {
        var data = new TheoryData<string>();
        foreach (var item in Section.GetProperty(group).EnumerateArray())
        {
            data.Add(item.Text("name"));
        }

        return data;
    }

    public static TheoryData<string> CapabilityNames() => Names("capability");

    public static TheoryData<string> AssemblyNames() => Names("assembly");

    public static TheoryData<string> LedgerNames() => Names("ledger");

    [Fact]
    public void EveryClientCaseIsRun()
    {
        Assert.Equal((11, 4, 4), (Section.GetProperty("capability").GetArrayLength(), Section.GetProperty("assembly").GetArrayLength(),
            Section.GetProperty("ledger").GetArrayLength()));
    }

    [Fact]
    public void TheLatencyEdgesAreTheSpecs()
    {
        Assert.Equal(Section.GetProperty("edges").EnumerateArray().Select(e => e.GetInt64()), LatencyBins.Edges);
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public void Capability(string name)
    {
        var item = Case("capability", name);
        var raw = new JsonObject
        {
            ["protocol"] = "agent-speed-report",
            ["versions"] = new JsonArray(1),
            ["service"] = new JsonObject { ["name"] = "x", ["terms"] = "https://example.com/t", ["privacy"] = "https://example.com/p" },
            ["api"] = "https://api.example.com",
        };
        foreach (var (key, value) in JsonNode.Parse(item.GetProperty("document").GetRawText())!.AsObject())
        {
            raw[key] = value?.DeepClone();
        }

        var document = ReportDiscovery.Parse(ReportSpec.Utf8(raw.ToJsonString()), ReportServiceUrl.Parse("https://example.com")!, out var problem);
        Assert.Null(problem);
        var switches = ReportSections.All.Where(s => s != ReportSection.Models || item.GetProperty("modelsSwitch").GetBoolean());
        var options = new ReportOptions(switches.Where(document!.Sections.Contains).ToHashSet(), document.Limits.MaxModels)
        {
            Extensions = document.Extensions,
        };
        var expect = item.GetProperty("expect");
        var want = expect.ValueKind == JsonValueKind.Null ? ReportScoreExtension.None
            : expect.GetString() == "perfScore" ? ReportScoreExtension.Perf
            : ReportScoreExtension.LegacyTask;
        Assert.Equal(want, options.ScoreExtension);
        Assert.Equal(document.Extensions, ReportDiscovery.Parse(document.ToJson().ToCompactBytes(), ReportServiceUrl.Parse("https://example.com")!, out _)!.Extensions);
    }

    [Theory]
    [MemberData(nameof(AssemblyNames))]
    public void Assembly(string name)
    {
        var item = Case("assembly", name);
        var input = item.GetProperty("input");
        var legacy = input.TryGetProperty("extension", out var extension) && extension.GetString() == "taskScore";
        var counters = new ReportCounters();
        var generating = new Dictionary<ReportModelKey, int>();
        foreach (var model in input.GetProperty("models").EnumerateArray())
        {
            var key = new ReportModelKey(model.Text("id"), Source(model.GetProperty("sources")[0].GetString()!));
            counters.Add(new ReportUsage(model.GetProperty("requests").GetInt64(), model.GetProperty("output").GetInt64(),
                MeasuredOutput: model.GetProperty("measuredOutput").GetInt64(), MeasuredMs: model.GetProperty("measuredMs").GetInt64()), key);
            if (model.GetProperty("generating").GetInt32() is var count and > 0)
            {
                generating[key] = count;
            }
        }

        var agents = input.GetProperty("agents").EnumerateArray()
            .Where(a => a.Text("form") != "none")
            .Select(a => new ReportPerfAgent(Source(a.Text("source")), a.Text("model"), Form(a.Text("form")), a.GetProperty("score").GetDouble()))
            .ToList();
        var thinking = input.GetProperty("thinking");
        var modelThinking = thinking.GetProperty("models").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
        var rows = new Dictionary<ReportModelKey, PerfSums>();
        if (!legacy && input.TryGetProperty("ledger", out var ledger))
        {
            foreach (var model in ledger.EnumerateObject())
            {
                foreach (var row in model.Value.EnumerateArray())
                {
                    rows[new ReportModelKey(model.Name, Source(row.Text("source")))] = ParseRow(row);
                }
            }
        }

        var facts = new ReportLiveFacts(new Dictionary<SourceKind, ReportAgentCounts>(), generating, counters.Copy(), 1)
        {
            Perf = new ReportPerfFacts(agents, thinking.GetProperty("total").GetDouble(), modelThinking),
        };
        var sample = ReportSample.Empty with { Facts = facts };
        var content = new ReportWindow(ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Epoch = 1 } }).Content(sample) with { Perf = rows };
        var options = new ReportOptions(new HashSet<ReportSection> { ReportSection.Models }, input.GetProperty("maxModels").GetInt32())
        {
            Extensions = legacy ? new ReportExtensions(TaskScore: true) : new ReportExtensions(PerfScore: true),
        };
        var body = JsonDocument.Parse(ReportBody.Json(Envelope, content, sample, options).ToCompactBytes()).RootElement;
        var expect = item.GetProperty("expect");
        var top = legacy ? "taskScore" : "perfScore";
        Assert.Equal(Canonical(expect.GetProperty(top)), Canonical(body.GetProperty(top)));
        Assert.Equal(Canonical(expect.GetProperty("models")), Canonical(body.GetProperty("models")));
        Assert.False(body.TryGetProperty(legacy ? "perfScore" : "taskScore", out _));
    }

    [Theory]
    [MemberData(nameof(LedgerNames))]
    public void Ledger(string name)
    {
        var item = Case("ledger", name);
        var driver = new LedgerDriver();
        foreach (var e in item.GetProperty("events").EnumerateArray())
        {
            driver.Apply(e);
        }

        var expect = item.GetProperty("expect");
        Assert.Equal(expect.GetProperty("builds").GetArrayLength(), driver.Builds.Count);
        var index = 0;
        foreach (var build in expect.GetProperty("builds").EnumerateArray())
        {
            var (seq, stats) = driver.Builds[index++];
            Assert.Equal(build.GetProperty("seq").GetInt32(), seq);
            var want = build.GetProperty("perfStats");
            if (want.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(stats);
            }
            else
            {
                Assert.NotNull(stats);
                Assert.Equal(ByModel(want), ByModel(stats));
            }
        }

        Assert.Equal(ByModel(expect.GetProperty("pending")), ByModel(driver.Pending()));
    }

    /// <summary>A ledger's rows keyed by model id, in id order (the vectors' map has no order of its own).</summary>
    private static string ByModel(JsonElement map) =>
        string.Join('\n', map.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + Canonical(p.Value)));

    private static string ByModel(JsonObject map) => ByModel(JsonDocument.Parse(map.ToJsonString()).RootElement);

    /// <summary>
    /// The statistics ledger as the app runs it: the engine's counting (<see cref="ReportPerfLedger"/> on the report ledger's
    /// counters) following the watch, and the report side's <see cref="ReportPerfWindow"/>.
    /// </summary>
    private sealed class LedgerDriver
    {
        private ReportLedger _engine = new();
        private ReportPerfLedger _perf;
        private ReportPerfWindow _window = new();
        private long _epoch = 1;
        private int _keys;

        public LedgerDriver()
        {
            _perf = new ReportPerfLedger(_engine.Counters) { Epoch = _epoch };
        }

        public List<(int Seq, JsonObject? Stats)> Builds { get; } = [];

        public void Apply(JsonElement e)
        {
            var at = e.GetProperty("at").GetInt64();
            if (e.TryGetProperty("watch", out var watch))
            {
                _window.Update(watch.GetBoolean(), "https://service.example");
                _perf.Follow(_window.Watch);
            }
            else if (e.TryGetProperty("response", out var r))
            {
                _perf.Follow(_window.Watch);
                var counted = !e.TryGetProperty("counted", out var c) || c.GetBoolean();
                var source = Source(r.Text("source"));
                var response = new ParsedResponse("k" + ++_keys, source, r.Text("agent"), r.Text("model"), r.GetProperty("startMs").GetInt64(),
                    r.GetProperty("endMs").GetInt64(), new TokenCounts(r.GetProperty("input").GetInt64(), r.GetProperty("cachedInput").GetInt64(), 0,
                        r.GetProperty("output").GetInt64(), r.GetProperty("reasoning").GetInt64()))
                {
                    FirstMs = r.GetProperty("firstMs").ValueKind == JsonValueKind.Number ? r.GetProperty("firstMs").GetInt64() : null,
                };
                _perf.Response(response, r.Text("model"), counted);
            }
            else if (e.TryGetProperty("call", out var call))
            {
                _perf.Follow(_window.Watch);
                if (e.TryGetProperty("seeding", out var seeding) && seeding.GetBoolean())
                {
                    // A result read while seeding never reaches the engine's counting (MonitorCore passes only live ones).
                    return;
                }

                var action = new WorkAction(call.GetProperty("callMs").GetInt64(), "tool", 0, call.GetProperty("failed").GetBoolean())
                {
                    ResultMs = call.GetProperty("resultMs").GetInt64(),
                    Model = call.Text("model"),
                };
                _perf.Call(call.Text("agent"), Source(call.Text("source")), action, call.Text("model"), at);
            }
            else if (e.TryGetProperty("build", out var build))
            {
                var rows = _window.Take(Sample());
                _perf.Follow(_window.Watch);
                var models = !e.TryGetProperty("models", out var m) || m.GetBoolean();
                Builds.Add((build.GetInt32(), models ? Group(rows) : null));
            }
            else if (e.TryGetProperty("relaunch", out _))
            {
                _epoch++;
                _engine = new ReportLedger();
                _perf = new ReportPerfLedger(_engine.Counters) { Epoch = _epoch };
                _window = new ReportPerfWindow();
            }

            // "discard", "newRun" and a lost build leave the statistics ledger alone, and so does the driver.
        }

        public JsonObject Pending() => Group(_window.Take(Sample()));

        private ReportSample Sample()
        {
            _perf.Follow(_window.Watch);
            return ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Counters = _engine.Counters.Copy(), Epoch = _epoch } };
        }

        private static JsonObject Group(IReadOnlyDictionary<ReportModelKey, PerfSums> rows)
        {
            var output = new JsonObject();
            foreach (var model in rows.GroupBy(p => p.Key.ModelId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var list = new JsonArray();
                foreach (var (key, row) in model.OrderBy(p => p.Key.Source))
                {
                    list.Add(JsonNode.Parse(ReportPerfRow.Json(key.Source, row).ToCompactBytes()));
                }

                output[model.Key] = list;
            }

            return output;
        }
    }

    [Fact]
    public void AModelsDropForSizeKeepsTheTopLevelObject()
    {
        var counters = new ReportCounters();
        for (var i = 0; i < 16; i++)
        {
            counters.Add(new ReportUsage(1, 100 + i), new ReportModelKey($"model-{i:D2}-{new string('x', 50)}", SourceKind.Codex));
        }

        var facts = new ReportLiveFacts(new Dictionary<SourceKind, ReportAgentCounts>(), new Dictionary<ReportModelKey, int>(), counters.Copy(), 1)
        {
            Perf = new ReportPerfFacts([new ReportPerfAgent(SourceKind.Codex, "model-00", PerfForm.Live, 1285.5)], 12.34,
                new Dictionary<string, double>(StringComparer.Ordinal)),
        };
        var sample = ReportSample.Empty with { Facts = facts };
        var content = new ReportWindow(ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Epoch = 1 } }).Content(sample);
        var options = new ReportOptions(new HashSet<ReportSection> { ReportSection.Models, ReportSection.Sources }, 16, BodyLimit: 1200)
        {
            Extensions = new ReportExtensions(ModelHarnessRequests: true, ExecutionModes: true, PerfScore: true),
        };
        var (json, data) = ReportBody.Fitted(Envelope, content, sample, options);
        var body = JsonDocument.Parse(data).RootElement;
        Assert.False(body.TryGetProperty("models", out _));
        Assert.Equal("""{"score":1286,"form":"live","thinkingTokS":12.3}""", body.GetProperty("perfScore").GetRawText());
        Assert.NotNull(json);

        var legacy = options with { Extensions = new ReportExtensions(TaskScore: true) };
        var legacyBody = JsonDocument.Parse(ReportBody.Fitted(Envelope, content, sample, legacy).Data).RootElement;
        Assert.Equal("""{"score":null,"form":"none","thinkingTokS":12.3}""", legacyBody.GetProperty("taskScore").GetRawText());
        Assert.False(legacyBody.TryGetProperty("perfScore", out _));

        var inactive = options with { Extensions = new ReportExtensions(), BodyLimit = 16_384 };
        Assert.False(JsonDocument.Parse(ReportBody.Data(Envelope, content, sample, inactive)).RootElement.TryGetProperty("perfScore", out _));
        var switchedOff = options with { Sections = new HashSet<ReportSection> { ReportSection.Sources }, BodyLimit = 16_384 };
        Assert.False(JsonDocument.Parse(ReportBody.Data(Envelope, content, sample, switchedOff)).RootElement.TryGetProperty("perfScore", out _));
    }

    [Fact]
    public void ABreakDropsPendingRowsAndARunChangeDoesNot()
    {
        var engine = new ReportLedger();
        var perf = new ReportPerfLedger(engine.Counters) { Epoch = 7 };
        var window = new ReportPerfWindow();
        Assert.True(window.Update(true, "a"));
        ReportSample Sample() => ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Counters = engine.Counters.Copy(), Epoch = 7 } };
        var call = new WorkAction(1000, "Bash", 0, false) { ResultMs = 2000, Model = "gpt-5.5" };

        perf.Follow(window.Watch);
        Assert.True(perf.Call("a1", SourceKind.Codex, call, "gpt-5.5", 3000));
        Assert.Single(window.Content(Sample()));
        Assert.False(window.Update(true, "a"));
        Assert.Single(window.Take(Sample()));
        Assert.Empty(window.Take(Sample()));
        Assert.Equal(1, window.Watch.TakenCalls);

        perf.Follow(window.Watch);
        Assert.True(perf.Call("a1", SourceKind.Codex, call, "gpt-5.5", 3000));
        Assert.True(window.Update(true, "b"));
        perf.Follow(window.Watch);
        Assert.Empty(window.Content(Sample()));
        Assert.Empty(window.Take(Sample()));
        Assert.True(window.Update(false, null));
        perf.Follow(window.Watch);
        Assert.False(perf.Call("a1", SourceKind.Codex, call, "gpt-5.5", 3000));
        Assert.Empty(window.Content(Sample()));
    }

    [Fact]
    public void ACompletionIsAChangeButNoActivity()
    {
        var engine = new ReportLedger();
        var perf = new ReportPerfLedger(engine.Counters) { Epoch = 3 };
        var window = new ReportPerfWindow();
        window.Update(true, "a");
        perf.Follow(window.Watch);
        ReportSample Sample() => ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Counters = engine.Counters.Copy(), Epoch = 3 } };
        var report = new ReportWindow(Sample());
        perf.Call("a1", SourceKind.Codex, new WorkAction(1000, "Bash", 0) { ResultMs = 1200, Model = "m" }, "m", 2000);
        var sample = Sample();
        Assert.True(report.HasIncrement(sample, traffic: false));
        Assert.False(report.HasTokenIncrement(sample));
        report.Observe(sample);
        Assert.False(report.HasIncrement(sample, traffic: false));
    }

    [Fact]
    public void StreamValuesChangeWithARoundedScore()
    {
        ReportSample With(double score, double thinking) => ReportSample.Empty with
        {
            Facts = ReportLiveFacts.Empty with
            {
                Perf = new ReportPerfFacts([new ReportPerfAgent(SourceKind.ClaudeCode, "claude-opus-5-5", PerfForm.Live, score)], thinking,
                    new Dictionary<string, double>(StringComparer.Ordinal) { ["claude-opus-5-5"] = thinking }),
            },
        };

        var perf = ReportScoreExtension.Perf;
        var legacy = ReportScoreExtension.LegacyTask;
        Assert.Equal(ReportStreamValues.Of(With(1270.2, 1.01), perf), ReportStreamValues.Of(With(1269.6, 1.04), perf));
        Assert.NotEqual(ReportStreamValues.Of(With(1270.2, 1.01), perf), ReportStreamValues.Of(With(1270.6, 1.01), perf));
        Assert.NotEqual(ReportStreamValues.Of(With(1270.2, 1.01), perf), ReportStreamValues.Of(With(1270.2, 1.06), perf));
        Assert.Equal(ReportStreamValues.Of(With(1270.2, 1.01), legacy), ReportStreamValues.Of(With(9000, 1.01), legacy));
        Assert.NotEqual(ReportStreamValues.Of(With(1270.2, 1.01), legacy), ReportStreamValues.Of(With(1270.2, 1.06), legacy));
        Assert.Equal(ReportStreamValues.Of(With(1270.2, 1.01)), ReportStreamValues.Of(With(90, 9)));
    }

    [Fact]
    public void AScoreAboveTenMillionIsSentAsTenMillion()
    {
        Assert.Equal(10_000_000, new ReportPerfValue(PerfForm.Live, 2e9, 0).Shown);
        Assert.Equal("""{"score":null,"form":"none","thinkingTokS":0}""",
            System.Text.Encoding.UTF8.GetString(new ReportPerfValue(PerfForm.Resting, null, 0).Json.ToCompactBytes()));
    }

    private static readonly ReportEnvelope Envelope = new(new ReportClientInfo("1.3.0", "Windows", 11), "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b",
        "tester", "c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", 1, 1_792_656_930_500, 5000, ReportActivity.Active, 0);

    private static JsonElement Case(string group, string name) =>
        Section.GetProperty(group).EnumerateArray().Single(c => c.Text("name") == name);

    internal static SourceKind Source(string id) => Enum.GetValues<SourceKind>().Single(k => ReportBody.SourceId(k) == id);

    internal static PerfForm Form(string form) => form switch
    {
        "live" => PerfForm.Live,
        "resting" => PerfForm.Resting,
        _ => PerfForm.None,
    };

    /// <summary>A row as sent (sparse latency buckets) in the ledger's form.</summary>
    internal static PerfSums ParseRow(JsonElement row)
    {
        var sums = new PerfSums
        {
            N = row.GetProperty("n").GetInt64(),
            OutputSum = row.GetProperty("outputSum").GetInt64(),
            DurationMsSum = row.GetProperty("durationMsSum").GetInt64(),
            ReasonN = row.GetProperty("reasonN").GetInt64(),
            ReasonOutput = row.GetProperty("reasonOutput").GetInt64(),
            ReasonDurationMsSum = row.GetProperty("reasonDurationMsSum").GetInt64(),
            CacheN = row.GetProperty("cacheN").GetInt64(),
            CacheInput = row.GetProperty("cacheInput").GetInt64(),
            CacheCached = row.GetProperty("cacheCached").GetInt64(),
            ToolN = row.GetProperty("toolN").GetInt64(),
            ToolLnSum = row.GetProperty("toolLnSum").GetDouble(),
            ToolFailed = row.GetProperty("toolFailed").GetInt64(),
            ActiveMs = row.GetProperty("activeMs").GetInt64(),
        };
        foreach (var bin in row.GetProperty("latencyBins").EnumerateArray())
        {
            sums.LatencyBins[bin[0].GetInt32()] += bin[1].GetInt64();
        }

        return sums;
    }

    /// <summary>Compact JSON with numbers by value (<c>0.0</c> and <c>0</c> agree), members in their order.</summary>
    private static string Canonical(JsonElement element) => Normalize(JsonNode.Parse(element.GetRawText()))?.ToJsonString() ?? "null";

    private static string Canonical(JsonNode node) => Canonical(JsonDocument.Parse(node.ToJsonString()).RootElement);

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(v.GetValue<double>()),
        _ => node?.DeepClone(),
    };
}
