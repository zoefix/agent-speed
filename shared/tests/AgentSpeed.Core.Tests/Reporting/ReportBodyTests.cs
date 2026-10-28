using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// REPORTING 6 / METRICS 11.2, 11.7: bodies built from windows and samples, checked against the examples, the schema and the
/// strict rules of spec/tools/report_reference.py (when python3 is available).
/// </summary>
public sealed class ReportBodyTests
{
    private static readonly ReportEnvelope Envelope = new(
        new ReportClientInfo("1.3.0", "Windows", 11), "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", "小明的 Mac",
        "c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", 42, 1_792_656_930_500, 5000, ReportActivity.Active, 8);

    private static readonly ReportOptions Everything = new(ReportSections.All.ToHashSet(), Metric: SpeedMetric.Output, Redline: 2000);

    private static ReportOptions With(params ReportSection[] sections) => Everything with { Sections = sections.ToHashSet() };

    private static ReportTrafficSample Traffic(ReportTrafficStatus status, bool rate = true) => new(
        status, true, rate ? new TrafficRate(24_310.4, 1288.2) : null,
        new Dictionary<SourceKind, TrafficRate> { [SourceKind.Codex] = new(4200, 288), [SourceKind.ClaudeCode] = new(20_110.4, 1000.2) },
        new TrafficReportTotals(7, new Dictionary<SourceKind, TrafficSplit>
        {
            [SourceKind.Codex] = new(new TrafficBytes(21_000, 1440), TrafficBytes.Zero),
            [SourceKind.ClaudeCode] = new(new TrafficBytes(90_000, 4000), new TrafficBytes(10_550, 1000)),
        }),
        SourceSet.Everything);

    private static (ReportWindowContent Content, ReportSample Sample) BusySample(int models = 3, ReportTrafficStatus traffic = ReportTrafficStatus.Ok)
    {
        var counters = new ReportCounters();
        var ids = new[] { "claude-opus-5-5", "gpt-5.5", "unknown", "local" }.Concat(Enumerable.Range(0, Math.Max(0, models - 4)).Select(i => $"model-{i}"))
            .Take(Math.Max(models, 1)).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            var source = i % 2 == 0 ? SourceKind.ClaudeCode : SourceKind.Codex;
            var unknown = ids[i] == "unknown";
            counters.Add(new ReportUsage(2, 900 - i * 10, 40_000, 30_000, 1000, 20, 800 - i * 10, 2100, unknown ? 0 : 1500, unknown ? 2 : 0),
                new ReportModelKey(ids[i], source));
        }

        var facts = new ReportLiveFacts(
            new Dictionary<SourceKind, ReportAgentCounts>
            {
                [SourceKind.ClaudeCode] = new(2, 1, 3),
                [SourceKind.Codex] = new(1, 0, 2),
                [SourceKind.Gemini] = new(0, 0, 1),
            },
            new Dictionary<ReportModelKey, int>
            {
                [new("claude-opus-5-5", SourceKind.ClaudeCode)] = 2,
                [new("gpt-5.5", SourceKind.Codex)] = 1,
            },
            counters,
            1);
        var sources = new Dictionary<SourceKind, ReportSourceSample>
        {
            [SourceKind.ClaudeCode] = new(new Rate(300.44, 150_220.16), new Rate(36.05, 0), new LiveCost(3600.4, 300.44)),
            [SourceKind.Codex] = new(new Rate(112.21, 32_120.09), new Rate(52.7, 0), new LiveCost(612.6, 100)),
            [SourceKind.Gemini] = new(new Rate(0.01, 0.02), Rate.Zero, LiveCost.Zero),
        };
        var sample = new ReportSample(new Rate(412.66, 182_340.27), 0.2154, new LiveCost(4213, 400.44), sources, facts, Traffic(traffic));
        var baseline = ReportSample.Empty with
        {
            Facts = ReportLiveFacts.Empty with { Epoch = 1 },
            Traffic = Traffic(traffic) with { Totals = new TrafficReportTotals(7, new Dictionary<SourceKind, TrafficSplit>()) },
        };
        var content = new ReportWindow(baseline).Content(sample) with
        {
            Peak = new Rate(655, 240_118.66),
            PeakCost = 6120.2,
            PeakRate = new TrafficRate(61_200, 3410),
        };
        return (content, sample);
    }

    private static JsonElement Parse(byte[] data) => JsonDocument.Parse(data).RootElement;

    private static JsonElement Body(ReportWindowContent content, ReportSample sample, ReportOptions options, ReportEnvelope? envelope = null) =>
        Parse(ReportBody.Json(envelope ?? Envelope, content, sample, options).ToCompactBytes());

    private static List<string> Ids(JsonElement array, string key) => array.EnumerateArray().Select(e => e.GetProperty(key).GetString()!).ToList();

    [Fact]
    public void DisabledSourcesReportNoTraffic()
    {
        var baseline = new TrafficReportTotals(3, new Dictionary<SourceKind, TrafficSplit>
        {
            [SourceKind.Codex] = new(new TrafficBytes(1000, 100), TrafficBytes.Zero),
            [SourceKind.Gemini] = new(new TrafficBytes(2000, 200), TrafficBytes.Zero),
        });
        var totals = new TrafficReportTotals(3, new Dictionary<SourceKind, TrafficSplit>
        {
            [SourceKind.Codex] = new(new TrafficBytes(5000, 500), new TrafficBytes(10, 1)),
            [SourceKind.Gemini] = new(new TrafficBytes(9000, 900), TrafficBytes.Zero),
        });
        var sample = ReportTrafficSample.Off with { Status = ReportTrafficStatus.Ok, Totals = totals, Enabled = SourceSet.Of(SourceKind.Codex) };
        var bytes = sample.AgentBytes(baseline);
        Assert.Equal(new TrafficBytes(4010, 401), Assert.Single(bytes).Value);
        Assert.Equal(new TrafficBytes(4000, 400), (sample with { IncludesTools = false }).AgentBytes(baseline)[SourceKind.Codex]);
        var restarted = sample with { Totals = totals with { Epoch = 4 } };
        Assert.Equal(new TrafficBytes(5010, 501), restarted.AgentBytes(baseline)[SourceKind.Codex]);
    }

    [Fact]
    public void ActiveReportHasEverySectionAndExactSums()
    {
        var (content, sample) = BusySample();
        var json = ReportBody.Json(Envelope, content, sample, Everything);
        var text = json.ToCompactString();
        Assert.StartsWith("{\"schema\":1,\"client\":{\"app\":\"AgentSpeed\",\"version\":\"1.3.0\",\"platform\":\"Windows\",\"osMajor\":11}", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text);
        var body = Parse(json.ToCompactBytes());
        var live = body.GetProperty("live");
        Assert.Equal(412.7, live.GetProperty("outputTokS").GetDouble());
        Assert.Equal(0.215, live.GetProperty("estimatedShare").GetDouble());
        Assert.Equal(3, live.GetProperty("agents").GetProperty("generating").GetInt32());
        Assert.Equal(1, live.GetProperty("agents").GetProperty("tools").GetInt32());
        Assert.Equal(6, live.GetProperty("agents").GetProperty("active").GetInt32());
        Assert.Equal(["codex", "claudeCode"], Ids(body.GetProperty("sources"), "source"));
        Assert.Equal(["claude-opus-5-5", "gpt-5.5", "unknown"], Ids(body.GetProperty("models"), "id"));
        Assert.Equal(0.388, body.GetProperty("title").GetProperty("heat").GetDouble());
        Assert.Equal(2000, body.GetProperty("title").GetProperty("redline").GetDouble());
        Assert.Equal("2026-10-22T08:15:30.500Z", body.GetProperty("sentAt").GetString());
        Assert.Equal(655, body.GetProperty("peak").GetProperty("outputTokS").GetDouble());
        var cost = body.GetProperty("cost");
        Assert.Equal(6120, cost.GetProperty("peakMicroUsdPerSecond").GetInt64());
        Assert.Equal(2, cost.GetProperty("unpricedRequests").GetInt64());
        Assert.Equal(3000, cost.GetProperty("deltaMicroUsd").GetInt64());
        var traffic = body.GetProperty("traffic");
        Assert.Equal(24_310, traffic.GetProperty("rxBytesPerSecond").GetInt64());
        Assert.Equal(121_550, traffic.GetProperty("rxBytes").GetInt64());
        Assert.Equal(6440, traffic.GetProperty("txBytes").GetInt64());
    }

    [Fact]
    public void ModelsBeyondTheLimitMergeIntoOther()
    {
        var (content, sample) = BusySample(models: 9);
        var body = Body(content, sample, Everything with { MaxModels = 4 });
        var models = body.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(4, models.Count);
        Assert.Equal("other", models[^1].GetProperty("id").GetString());
        Assert.Equal(18, models.Sum(m => m.GetProperty("requests").GetInt32()));
        Assert.Equal(["codex", "claudeCode"], models[^1].GetProperty("sources").EnumerateArray().Select(s => s.GetString()!).ToList());
        Assert.False(Body(content, sample, Everything with { MaxModels = 0 }).TryGetProperty("models", out _));
    }

    [Fact]
    public void OversizeDropsModelsThenSources()
    {
        var (content, sample) = BusySample(models: 16);
        var dropped = Parse(ReportBody.Data(Envelope, content, sample, Everything with { BodyLimit = 2600 }));
        Assert.False(dropped.TryGetProperty("models", out _));
        Assert.True(dropped.TryGetProperty("sources", out _));
        var smaller = ReportBody.Data(Envelope, content, sample, Everything with { BodyLimit = 1200 });
        var parsed = Parse(smaller);
        Assert.False(parsed.TryGetProperty("models", out _));
        Assert.False(parsed.TryGetProperty("sources", out _));
        Assert.True(parsed.TryGetProperty("cost", out _));
        Assert.True(ReportBody.Data(Envelope, content, sample, Everything).Length <= 16_384);
    }

    [Fact]
    public void UnavailableAndOffTrafficAreAStatusNeverZero()
    {
        foreach (var (status, name) in new[] { (ReportTrafficStatus.Unavailable, "unavailable"), (ReportTrafficStatus.Off, "off") })
        {
            var (content, sample) = BusySample(traffic: status);
            var body = Body(content, sample, Everything);
            var traffic = body.GetProperty("traffic");
            Assert.Equal(name, traffic.GetProperty("status").GetString());
            Assert.Single(traffic.EnumerateObject());
            Assert.All(body.GetProperty("sources").EnumerateArray(), s => Assert.False(s.TryGetProperty("traffic", out _)));
        }

        var (c, s) = BusySample();
        var early = Body(c, s with { Traffic = s.Traffic with { Rate = null, SourceRates = new Dictionary<SourceKind, TrafficRate>() } }, Everything);
        Assert.Equal(JsonValueKind.Null, early.GetProperty("traffic").GetProperty("rxBytesPerSecond").ValueKind);
        Assert.All(early.GetProperty("sources").EnumerateArray(),
            entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("traffic").GetProperty("rxBytesPerSecond").ValueKind));
    }

    /// <summary>A response of an earlier window priced only now: listed with the cost section so every cost sum adds up.</summary>
    private static (ReportWindowContent Content, ReportSample Sample) LatePriced()
    {
        var (content, sample) = BusySample();
        var usage = content.Usage.Copy();
        usage.Add(new ReportUsage(CostMicroUsd: 2500), new ReportModelKey("gemini-3.1-pro", SourceKind.Gemini));
        var sources = new Dictionary<SourceKind, ReportSourceSample>(sample.Sources) { [SourceKind.Gemini] = default };
        return (content with { Usage = usage }, sample with { Sources = sources });
    }

    [Fact]
    public void LatePricedSourcesAreListedWithTheCostSection()
    {
        var (content, sample) = LatePriced();
        var body = Body(content, sample, Everything);
        var sources = body.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["codex", "claudeCode", "gemini"], sources.Select(e => e.GetProperty("source").GetString()));
        var models = body.GetProperty("models").EnumerateArray().ToList();
        Assert.Contains(models, m => m.GetProperty("id").GetString() == "gemini-3.1-pro" && m.GetProperty("output").GetInt32() == 0);
        var total = body.GetProperty("cost").GetProperty("deltaMicroUsd").GetInt64();
        Assert.Equal(total, models.Sum(m => m.GetProperty("deltaMicroUsd").GetInt64()));
        Assert.Equal(total, sources.Sum(e => e.GetProperty("cost").GetProperty("deltaMicroUsd").GetInt64()));
        var withoutCost = Body(content, sample, With(ReportSection.Sources, ReportSection.Models, ReportSection.Traffic));
        Assert.Equal(["codex", "claudeCode"], Ids(withoutCost.GetProperty("sources"), "source"));
    }

    [Fact]
    public void NumbersAreRoundedHalfAwayFromZero()
    {
        Assert.Equal("0.3", ReportJson.Number(0.25, 1));
        Assert.Equal("412.7", ReportJson.Number(412.65, 1));
        Assert.Equal("0", ReportJson.Number(0.0004, 3));
        Assert.Equal("0", ReportJson.Number(-0.0001, 1));
        Assert.Equal("2000", ReportJson.Number(2000, 1));
        Assert.Equal("0.215", ReportJson.Number(0.215, 3));
        Assert.Equal("0", ReportJson.Number(double.NaN, 1));
        Assert.Equal("\"a\\\"b\\\\c\\n\\u0001é\"", ReportJson.Of("a\"b\\c\n\u0001é").ToCompactString());
        Assert.Equal("{\n  \"a\": [\n    1,\n    null\n  ],\n  \"b\": {}\n}",
            ReportJson.Object(("a", ReportJson.Array([ReportJson.Of(1), ReportJson.Null])), ("b", ReportJson.Object())).ToPrettyString());
        var tokens = ReportJson.Object(("k", ReportJson.Of("v")), ("n", ReportJson.Decimal(1.25, 1))).ToPrettyTokens();
        Assert.Equal(ReportJson.Object(("k", ReportJson.Of("v")), ("n", ReportJson.Decimal(1.25, 1))).ToPrettyString(), string.Concat(tokens.Select(t => t.Text)));
        Assert.Contains(tokens, t => t is { Kind: ReportJsonTokenKind.Key, Text: "\"k\"" });
        Assert.Contains(tokens, t => t is { Kind: ReportJsonTokenKind.Number, Text: "1.3" });
    }

    [Fact]
    public void ClientInfoAndDocuments()
    {
        Assert.Equal("0.0.0", new ReportClientInfo("1.3", "Windows", 11).Version);
        Assert.Equal("1.3.0-beta.1", new ReportClientInfo("1.3.0-beta.1", "Windows", 11).Version);
        Assert.Equal(10, new ReportClientInfo("1.3.0", "Windows", 7).OsMajor);
        Assert.Equal("AgentSpeed/1.3.0 (Windows)", new ReportClientInfo("1.3.0", "Windows", 11).UserAgent);
        var windows = ReportClientInfo.Windows("1.3.0");
        Assert.Equal("Windows", windows.Platform);
        Assert.InRange(windows.OsMajor, 10, 11);

        using var key = ReportSpec.Key("device");
        var registration = ReportDocuments.Registration("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", "zoe-mbp", key.PublicKey,
            new ReportClientInfo("1.3.0", "macOS", 27));
        var example = JsonSerializer.Serialize(JsonDocument.Parse(ReportSpec.Example("registration.json")).RootElement);
        Assert.Equal(example, Encoding.UTF8.GetString(registration));
        Assert.Contains("\"transfer\":true", Encoding.UTF8.GetString(ReportDocuments.Registration("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b",
            "zoe-mbp", key.PublicKey, new ReportClientInfo("1.3.0", "Windows", 11), transfer: true)), StringComparison.Ordinal);
        Assert.Equal("{\"username\":\"zoe-studio\"}", Encoding.UTF8.GetString(ReportDocuments.Rename("zoe-studio")));
        var rotate = Parse(ReportDocuments.Rotation(key.PublicKey));
        Assert.Equal(key.PublicKey, ReportPublicKey.ParseJwk(rotate.GetProperty("publicKey")));
    }

    [Fact]
    public void ResponseExamplesParse()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_792_656_931);
        foreach (var item in JsonDocument.Parse(ReportSpec.Example("responses.json")).RootElement.GetProperty("responses").EnumerateArray())
        {
            var status = item.GetProperty("status").GetInt32();
            var body = item.GetProperty("body");
            var reply = ReportReply.Parse(status, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body.GetRawText()), now);
            if (body.TryGetProperty("error", out var error))
            {
                Assert.Equal(error.Text("code"), reply.Code);
                Assert.Null(reply.Result);
            }
            else
            {
                Assert.Equal(body.Text("status"), reply.Result);
                Assert.True(reply.IsSuccess);
                if (body.TryGetProperty("profileUrl", out var profile))
                {
                    Assert.Equal(profile.GetString(), reply.ProfileUrl);
                }
            }
        }
    }

    /// <summary>Each report example rebuilt from its own numbers by the C# body builder gives the same document.</summary>
    [Theory]
    [InlineData("report-active.json")]
    [InlineData("report-idle.json")]
    [InlineData("report-leaving.json")]
    [InlineData("report-minimal.json")]
    public void ExamplesAreRebuiltExactly(string name)
    {
        var example = JsonDocument.Parse(ReportSpec.Example(name)).RootElement;
        var (envelope, content, sample, options) = FromExample(example);
        var built = Body(content, sample, options, envelope);
        var difference = Difference(example, built, "$");
        Assert.True(difference is null, $"{name}: {difference}");
    }

    private static (ReportEnvelope, ReportWindowContent, ReportSample, ReportOptions) FromExample(JsonElement report)
    {
        var client = report.GetProperty("client");
        var run = report.GetProperty("run");
        var title = report.GetProperty("title");
        var activity = report.Text("activity") switch
        {
            "active" => ReportActivity.Active,
            "idle" => ReportActivity.Idle,
            _ => ReportActivity.Leaving,
        };
        var envelope = new ReportEnvelope(
            new ReportClientInfo(client.Text("version"), client.Text("platform"), client.GetProperty("osMajor").GetInt32()),
            report.Text("deviceId"), report.Text("username"), run.Text("id"), run.GetProperty("seq").GetInt32(),
            DateTimeOffset.Parse(report.Text("sentAt"), System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            report.GetProperty("windowMs").GetInt64(), activity, title.GetProperty("level").GetInt32());

        var sections = ReportSections.All.Where(s => report.TryGetProperty(ReportSections.Name(s), out _)).ToHashSet();
        var hasCost = report.TryGetProperty("cost", out var cost);
        var options = new ReportOptions(sections, 16, 16_384, title.Text("metric") == "total" ? SpeedMetric.Total : SpeedMetric.Output,
            title.GetProperty("redline").GetDouble(), hasCost ? cost.GetProperty("priceVersion").GetInt32() : 1);

        static SourceKind Source(string id) => Enum.GetValues<SourceKind>().Single(k => ReportBody.SourceId(k) == id);
        var live = report.GetProperty("live");
        var output = live.GetProperty("outputTokS").GetDouble();
        var agents = new Dictionary<SourceKind, ReportAgentCounts>();
        var sources = new Dictionary<SourceKind, ReportSourceSample>();
        var counters = new ReportCounters();
        var sourceRates = new Dictionary<SourceKind, TrafficRate>();
        var totals = new Dictionary<SourceKind, TrafficSplit>();
        var models = report.TryGetProperty("models", out var modelList) ? modelList.EnumerateArray().ToList() : [];
        var generating = new Dictionary<ReportModelKey, int>();
        foreach (var model in models)
        {
            var count = model.GetProperty("generating").GetInt32();
            if (count > 0)
            {
                generating[new ReportModelKey(model.Text("id"), Source(model.GetProperty("sources")[0].GetString()!))] = count;
            }
        }

        foreach (var entry in report.TryGetProperty("sources", out var sourceList) ? sourceList.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var kind = Source(entry.Text("source"));
            var entryLive = entry.GetProperty("live");
            var sourceOutput = entryLive.GetProperty("outputTokS").GetDouble();
            var entryCost = entry.TryGetProperty("cost", out var c) ? c : default;
            sources[kind] = new ReportSourceSample(
                new Rate(sourceOutput, entryLive.GetProperty("totalTokS").GetDouble()),
                new Rate(sourceOutput * entryLive.GetProperty("estimatedShare").GetDouble(), 0),
                entryCost.ValueKind == JsonValueKind.Object
                    ? new LiveCost(entryCost.GetProperty("liveMicroUsdPerSecond").GetDouble(), sourceOutput * entryCost.GetProperty("knownShare").GetDouble())
                    : LiveCost.Zero);
            var a = entry.GetProperty("agents");
            agents[kind] = new ReportAgentCounts(a.GetProperty("generating").GetInt32(), a.GetProperty("tools").GetInt32(), a.GetProperty("active").GetInt32());
            var delta = entry.GetProperty("delta");
            var own = models.Where(m => m.GetProperty("sources").EnumerateArray().Any(s => s.GetString() == entry.Text("source"))).ToList();
            var usage = new ReportUsage(
                delta.GetProperty("requests").GetInt64(), delta.GetProperty("output").GetInt64(), delta.GetProperty("input").GetInt64(),
                delta.GetProperty("cachedInput").GetInt64(), delta.GetProperty("cacheWriteInput").GetInt64(), delta.GetProperty("reasoning").GetInt64(),
                delta.GetProperty("measuredOutput").GetInt64(), delta.GetProperty("measuredMs").GetInt64(),
                entryCost.ValueKind == JsonValueKind.Object ? entryCost.GetProperty("deltaMicroUsd").GetInt64() : 0,
                entryCost.ValueKind == JsonValueKind.Object ? entryCost.GetProperty("unpricedRequests").GetInt64() : 0);
            Assert.True(own.Count <= 1, "the examples give each source at most one model");
            counters.Add(usage, new ReportModelKey(own.Count == 1 ? own[0].Text("id") : "unknown", kind));
            if (entry.TryGetProperty("traffic", out var t))
            {
                if (t.GetProperty("rxBytesPerSecond").ValueKind == JsonValueKind.Number)
                {
                    sourceRates[kind] = new TrafficRate(t.GetProperty("rxBytesPerSecond").GetDouble(), t.GetProperty("txBytesPerSecond").GetDouble());
                }

                totals[kind] = new TrafficSplit(new TrafficBytes(t.GetProperty("rxBytes").GetInt64(), t.GetProperty("txBytes").GetInt64()), TrafficBytes.Zero);
            }
        }

        var liveAgents = live.GetProperty("agents");
        var listed = agents.Values.Aggregate(default(ReportAgentCounts), (x, y) => x + y);
        var unlisted = liveAgents.GetProperty("active").GetInt32() - listed.Active;
        if (unlisted > 0)
        {
            agents[SourceKind.Gemini] = new ReportAgentCounts(0, 0, unlisted);
        }

        ReportTrafficSample trafficSample;
        TrafficRate? peakRate = null;
        if (report.TryGetProperty("traffic", out var traffic))
        {
            var status = traffic.Text("status") switch
            {
                "ok" => ReportTrafficStatus.Ok,
                "off" => ReportTrafficStatus.Off,
                _ => ReportTrafficStatus.Unavailable,
            };
            TrafficRate? rate = status == ReportTrafficStatus.Ok && traffic.GetProperty("rxBytesPerSecond").ValueKind == JsonValueKind.Number
                ? new TrafficRate(traffic.GetProperty("rxBytesPerSecond").GetDouble(), traffic.GetProperty("txBytesPerSecond").GetDouble())
                : null;
            if (status == ReportTrafficStatus.Ok && traffic.GetProperty("peakRxBytesPerSecond").ValueKind == JsonValueKind.Number)
            {
                peakRate = new TrafficRate(traffic.GetProperty("peakRxBytesPerSecond").GetDouble(), traffic.GetProperty("peakTxBytesPerSecond").GetDouble());
            }

            trafficSample = new ReportTrafficSample(status, status != ReportTrafficStatus.Ok || traffic.GetProperty("includesTools").GetBoolean(),
                rate, sourceRates, new TrafficReportTotals(9, totals), SourceSet.Everything);
        }
        else
        {
            trafficSample = ReportTrafficSample.Off;
        }

        var sample = new ReportSample(
            new Rate(output, live.GetProperty("totalTokS").GetDouble()), live.GetProperty("estimatedShare").GetDouble(),
            hasCost ? new LiveCost(cost.GetProperty("liveMicroUsdPerSecond").GetDouble(), output * cost.GetProperty("knownShare").GetDouble()) : LiveCost.Zero,
            sources, new ReportLiveFacts(agents, generating, counters, 2), trafficSample);
        if (!report.TryGetProperty("sources", out _))
        {
            var total = report.GetProperty("delta");
            counters.Add(new ReportUsage(
                total.GetProperty("requests").GetInt64(), total.GetProperty("output").GetInt64(), total.GetProperty("input").GetInt64(),
                total.GetProperty("cachedInput").GetInt64(), total.GetProperty("cacheWriteInput").GetInt64(), total.GetProperty("reasoning").GetInt64(),
                total.GetProperty("measuredOutput").GetInt64(), total.GetProperty("measuredMs").GetInt64(),
                hasCost ? cost.GetProperty("deltaMicroUsd").GetInt64() : 0, hasCost ? cost.GetProperty("unpricedRequests").GetInt64() : 0),
                new ReportModelKey("unknown", SourceKind.Codex));
            agents[SourceKind.Codex] = new ReportAgentCounts(liveAgents.GetProperty("generating").GetInt32(), liveAgents.GetProperty("tools").GetInt32(),
                liveAgents.GetProperty("active").GetInt32());
            agents.Remove(SourceKind.Gemini);
        }

        var baseline = ReportSample.Empty with
        {
            Facts = ReportLiveFacts.Empty with { Epoch = 2 },
            Traffic = trafficSample with { Totals = new TrafficReportTotals(9, new Dictionary<SourceKind, TrafficSplit>()) },
        };
        var peak = report.GetProperty("peak");
        var content = new ReportWindow(baseline).Content(sample) with
        {
            Peak = new Rate(peak.GetProperty("outputTokS").GetDouble(), peak.GetProperty("totalTokS").GetDouble()),
            PeakCost = hasCost ? cost.GetProperty("peakMicroUsdPerSecond").GetDouble() : 0,
            PeakRate = peakRate,
        };
        return (envelope, content, sample, options);
    }

    /// <summary>The first place two documents differ (numbers by value, member order ignored), or null.</summary>
    private static string? Difference(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
        {
            return expected.GetDecimal() == actual.GetDecimal() ? null : $"{path}: {expected.GetRawText()} != {actual.GetRawText()}";
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            return $"{path}: {expected.ValueKind} != {actual.ValueKind}";
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var names = expected.EnumerateObject().Select(p => p.Name).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToList();
                if (!names.Order(StringComparer.Ordinal).SequenceEqual(actualNames.Order(StringComparer.Ordinal)))
                {
                    return $"{path}: members [{string.Join(',', names)}] != [{string.Join(',', actualNames)}]";
                }

                foreach (var name in names)
                {
                    if (Difference(expected.GetProperty(name), actual.GetProperty(name), path + "." + name) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var left = expected.EnumerateArray().ToList();
                var right = actual.EnumerateArray().ToList();
                if (left.Count != right.Count)
                {
                    return $"{path}: {left.Count} items != {right.Count}";
                }

                for (var i = 0; i < left.Count; i++)
                {
                    if (Difference(left[i], right[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return expected.GetRawText() == actual.GetRawText() ? null : $"{path}: {expected.GetRawText()} != {actual.GetRawText()}";
        }
    }

    [Fact]
    public void ExamplesPassTheReference()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        var script = """
            import json, sys
            sys.path.insert(0, sys.argv[1])
            import report_reference as r
            print(json.dumps(r.check_examples(False)))
            """;
        var failures = JsonSerializer.Deserialize<string[]>(ReferencePython.Run(script, ReportSpec.Tools));
        Assert.Empty(failures!);
    }

    /// <summary>Every body the builder makes passes the reference's schema and strict conformance rules.</summary>
    [Fact]
    public void ReferenceAcceptsTheBodies()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        var bodies = new List<byte[]>();
        foreach (var models in new[] { 0, 3, 9, 16 })
        {
            foreach (var traffic in new[] { ReportTrafficStatus.Ok, ReportTrafficStatus.Off, ReportTrafficStatus.Unavailable })
            {
                var (content, sample) = BusySample(models, traffic);
                foreach (var sections in new[]
                         {
                             ReportSections.All.ToArray(), [], [ReportSection.Sources, ReportSection.Cost], [ReportSection.Models, ReportSection.Traffic],
                         })
                {
                    bodies.Add(ReportBody.Data(Envelope, content, sample, Everything with { Sections = sections.ToHashSet(), MaxModels = models == 9 ? 4 : 16 }));
                }
            }
        }

        var idle = Envelope with { Activity = ReportActivity.Idle, TitleLevel = 0 };
        var empty = ReportSample.Empty with { Traffic = ReportTrafficSample.Off with { Status = ReportTrafficStatus.Ok, IncludesTools = false } };
        bodies.Add(ReportBody.Data(idle, new ReportWindow(empty).Content(empty), empty, Everything));
        var leaving = Envelope with { Activity = ReportActivity.Leaving, TitleLevel = 0, WindowMs = 1_200_000 };
        bodies.Add(ReportBody.Data(leaving, new ReportWindow(empty).Content(empty), empty, Everything));
        var (late, lateSample) = LatePriced();
        foreach (var sections in new[] { ReportSections.All.ToArray(), [ReportSection.Sources, ReportSection.Models] })
        {
            bodies.Add(ReportBody.Data(Envelope, late, lateSample, Everything with { Sections = sections.ToHashSet() }));
        }

        var huge = ReportSample.Empty with { Live = new Rate(5e9, double.PositiveInfinity), EstimatedShare = 7 };
        bodies.Add(ReportBody.Data(Envelope with { TitleLevel = 99 }, new ReportWindow(huge).Content(huge), huge, Everything with { Sections = new HashSet<ReportSection>() }));
        var failures = ReferencePython.StrictErrors(bodies);
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.NotEmpty(ReferencePython.StrictErrors([Encoding.UTF8.GetBytes("{\"schema\":1}")]));
    }
}
