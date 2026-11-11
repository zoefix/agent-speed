using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using static AgentSpeed.Core.Tests.Quota.Estimate.EstimateFixture;

namespace AgentSpeed.Core.Tests.Quota.Estimate;

/// <summary>The <c>pipeline</c> cases of spec/fixtures/quota-estimate-cases.json: raw inputs to the outputs a port must match.</summary>
public sealed class QuotaEstimatePipelineTests
{
    public static TheoryData<string, int> PipelineCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var group in Root.GetProperty("pipeline").EnumerateObject())
        {
            if (group.Value.ValueKind == JsonValueKind.Array)
            {
                for (var i = 0; i < group.Value.GetArrayLength(); i++)
                {
                    data.Add(group.Name, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PipelineCases))]
    public void PipelineCaseMatchesTheReference(string group, int index)
    {
        var c = Root.GetProperty("pipeline").GetProperty(group)[index];
        var label = group + "/" + c.GetProperty("name").GetString();
        var input = c.GetProperty("input");
        var expected = c.GetProperty("expected");
        var actual = Run(input);
        foreach (var output in expected.EnumerateObject())
        {
            Assert.True(actual.ContainsKey(output.Name), label + ": no " + output.Name);
            AssertSame(output.Value, ToElement(actual[output.Name]), label + "." + output.Name);
        }
    }

    [Fact]
    public void ReporterCadence()
    {
        var c = Root.GetProperty("pipeline").GetProperty("cadence");
        var e = c.GetProperty("entry");
        var w = e.GetProperty("window");
        var entry = new QuotaReportEntry(e.GetProperty("account").GetString()!, SourceKind.Codex,
            [.. e.GetProperty("sources").EnumerateArray().Select(s => Source(s.GetString()))],
            null, null, EstimateNames.ParseKind(w.GetProperty("kind").GetString())!.Value, OptionalString(w, "name"), w.GetProperty("minutes").GetInt32(),
            OptionalDouble(e, "usedPercent"), null, OptionalLong(e, "resetsInMs"), 0, false, e.GetProperty("usedMicroUsd").GetInt64(), null, 0,
            new QuotaEntryEstimate(null, null, null, EstimateConfidence.Medium, null, null, 0), [], 8);
        var reporter = new QuotaReportCadence();
        List<QuotaReportEntry> current = [entry];
        var got = new List<string>();
        foreach (var step in c.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("newRun", out var newRun) && newRun.GetBoolean())
            {
                reporter.NewRun();
            }

            var change = step.GetProperty("change").GetString();
            if (change == "set")
            {
                current = [.. current, current[0] with { Account = "fedcba9876543210" }];
            }
            else if (change != "none")
            {
                current = [Variant(change!, current[0]), .. current.Skip(1)];
            }

            var quotas = reporter.Step(step.GetProperty("at").GetInt64(), current, !step.TryGetProperty("switch", out var s) || s.GetBoolean(),
                step.TryGetProperty("leaving", out var l) && l.GetBoolean(), !step.TryGetProperty("fits", out var f) || f.GetBoolean());
            got.Add(quotas is null ? "absent" : quotas.Count == 0 ? "[]" : "entries");
        }

        Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(x => x.GetString()), got);
    }

    private static QuotaReportEntry Variant(string change, QuotaReportEntry x) => change switch
    {
        "spend" => x with { UsedMicroUsd = x.UsedMicroUsd + 1 },
        "percent" => x with { UsedPercent = x.UsedPercent + 1 },
        "confidence" => x with
        {
            Estimate = x.Estimate with { Confidence = x.Estimate.Confidence != EstimateConfidence.High ? EstimateConfidence.High : EstimateConfidence.Medium },
        },
        "reset" => x with { ResetsInMs = x.ResetsInMs is not null ? null : 1_000_000, UsedPercent = null },
        _ => throw new InvalidDataException(change),
    };

    /// <summary>
    /// Seen 2026-11-11: a service rejected two Codex accounts of one window, which a report could not tell apart. They are two
    /// entries with the same sources and window, told apart by their tags, two identities for the cadence, and ordered by
    /// their tags, so the order follows the service.
    /// </summary>
    [Fact]
    public void TwoCodexAccountsAreTwoTaggedEntriesOrderedByTheirTags()
    {
        var c = Root.GetProperty("pipeline").GetProperty("report").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "two Codex accounts");
        var input = c.GetProperty("input");
        var (model, evaluation) = Evaluate(input);
        var origin = input.GetProperty("origin").GetString()!;
        var now = input.GetProperty("now").GetInt64();
        var entries = evaluation.Entries(now, origin);
        AssertSame(c.GetProperty("expected").GetProperty("entries"), EntriesJson(entries), "entries");
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal([SourceKind.Codex], e.Sources));
        Assert.Single(entries.Select(e => e.WindowId).Distinct());
        Assert.Equal(2, entries.Select(e => e.Account).Distinct().Count());
        var keys = model.ResponseAccounts.OfType<string>().ToHashSet();
        Assert.Equal(2, keys.Count);
        Assert.All(entries, e =>
        {
            Assert.True(AccountKeys.IsKey(e.Account), e.Account);
            Assert.DoesNotContain(e.Account, keys);
        });
        Assert.Equal(keys.Select(k => AccountKeys.ReportTag(origin, k)).Order(StringComparer.Ordinal), entries.Select(e => e.Account));

        // Another service sees other tags for the same accounts, and there the second account's tag sorts first.
        var elsewhere = evaluation.Entries(now, "https://other.example");
        Assert.Empty(elsewhere.Select(e => e.Account).Intersect(entries.Select(e => e.Account)));
        Assert.Equal(elsewhere.Select(e => e.Account).Order(StringComparer.Ordinal), elsewhere.Select(e => e.Account));
        Assert.Equal([40.0, 10.0], entries.Select(e => e.UsedPercent!.Value));
        Assert.Equal([10.0, 40.0], elsewhere.Select(e => e.UsedPercent!.Value));

        Assert.False(QuotaReportCadence.ShouldSend(entries, entries, 0, 1_000, false));
        Assert.False(QuotaReportCadence.ShouldSend([entries[1], entries[0]], entries, 0, 1_000, false));
        Assert.True(QuotaReportCadence.ShouldSend([entries[0]], entries, 0, 1_000, false), "an account leaving the set is a changed set");
    }

    /// <summary>The reference's <c>run</c>: builds the model from the events and writes the requested outputs.</summary>
    internal static Dictionary<string, JsonNode?> Run(JsonElement inp)
    {
        var (model, evaluation) = Evaluate(inp);
        var now = inp.GetProperty("now").GetInt64();
        var want = inp.TryGetProperty("outputs", out var outputs)
            ? outputs.EnumerateArray().Select(o => o.GetString()!).ToHashSet()
            : ["responses", "instances", "estimates", "entries"];
        return Outputs(inp, model, evaluation, now, want);
    }

    /// <summary>The model of a case's events and its evaluation at the case's <c>now</c>.</summary>
    private static (EstimateModel Model, EstimateEvaluation Evaluation) Evaluate(JsonElement inp)
    {
        var salt = Convert.FromHexString(inp.GetProperty("salt").GetString()!);
        var input = new EstimateInput();
        foreach (var e in inp.GetProperty("events").EnumerateArray())
        {
            Add(input, salt, e);
        }

        if (inp.TryGetProperty("store", out var store))
        {
            input.Stored = EstimateStore.Parse(store);
        }

        if (inp.TryGetProperty("generatedStore", out var generated))
        {
            input.Stored = EstimateStore.Parse(Encoding.UTF8.GetBytes(GeneratedStore(generated).ToJsonString()));
        }

        var model = EstimateModel.Build(input, salt, inp.GetProperty("install").GetString()!, 8);
        return (model, EstimateEvaluation.Of(model, inp.GetProperty("now").GetInt64()));
    }

    private static Dictionary<string, JsonNode?> Outputs(JsonElement inp, EstimateModel model, EstimateEvaluation evaluation, long now, HashSet<string> want)
    {
        var result = new Dictionary<string, JsonNode?>();
        if (want.Contains("responses"))
        {
            result["responses"] = new JsonArray([.. model.ResponseAccounts.Select(a => (JsonNode?)a)]);
        }

        if (want.Contains("sums"))
        {
            var sums = new JsonArray();
            foreach (var q in inp.GetProperty("sums").EnumerateArray())
            {
                var account = model.ResponseAccounts[q.GetProperty("accountsOf")[0].GetInt32()]!;
                var name = OptionalString(q, "name");
                var a = q.GetProperty("a").GetInt64();
                var b = q.GetProperty("b").GetInt64();
                var outer = model.Ledger.Outer(account, name, a, b);
                sums.Add(new JsonObject
                {
                    ["inner"] = model.Ledger.Inner(account, name, a, b).Cost,
                    ["outer"] = new JsonArray(outer.Cost, outer.Unpriced),
                    ["outerHigh"] = Fnum(model.Ledger.OuterHigh(account, name, a, b)),
                });
            }

            result["sums"] = sums;
        }

        if (want.Contains("instances"))
        {
            var rows = evaluation.Instances
                .OrderBy(x => x.Account, StringComparer.Ordinal).ThenBy(x => x.Window, StringComparer.Ordinal).ThenBy(x => x.Instance.R)
                .ThenBy(x => x.Plan ?? "None", StringComparer.Ordinal)
                .Select(x => (JsonNode)new JsonObject
                {
                    ["account"] = x.Account,
                    ["window"] = x.Window,
                    ["plan"] = x.Plan,
                    ["tier"] = x.Tier,
                    ["class"] = new JsonArray([.. x.Class.Parts.Select(p => (JsonNode?)p)]),
                    ["R"] = x.Instance.R,
                    ["S"] = x.Instance.S,
                    ["Shi"] = x.Instance.SHi,
                    ["observations"] = x.Instance.Observations.Count,
                    ["bounds"] = x.Bounds is { } bounds
                        ? new JsonObject { ["lo"] = Fnum(bounds.Lo), ["hi"] = Fnum(bounds.Hi), ["offset"] = bounds.Offset }
                        : null,
                });
            result["instances"] = new JsonArray([.. rows]);
        }

        if (want.Contains("estimates"))
        {
            var estimates = new JsonObject();
            foreach (var (cls, e) in evaluation.Estimates)
            {
                estimates[cls.Key] = new JsonObject
                {
                    ["cap"] = Fnum(e.Cap),
                    ["low"] = Fnum(e.Low),
                    ["high"] = Fnum(e.High),
                    ["confidence"] = CapEstimate.Name(e.Confidence),
                    ["windows"] = e.Windows,
                };
            }

            result["estimates"] = estimates;
        }

        if (want.Contains("entries"))
        {
            result["entries"] = JsonNode.Parse(EntriesJson(evaluation.Entries(now, inp.GetProperty("origin").GetString()!)).GetRawText());
        }

        if (want.Contains("store"))
        {
            result["store"] = JsonNode.Parse(StoreJson(evaluation.Store()).GetRawText());
        }

        if (want.Contains("storeCounts"))
        {
            var st = evaluation.Store();
            result["storeCounts"] = new JsonObject { ["observations"] = st.Observations.Count, ["instances"] = st.Instances.Count, ["keys"] = st.Keys.Count };
        }

        return result;
    }

    private static void Add(EstimateInput input, byte[] salt, JsonElement e)
    {
        switch (e.GetProperty("type").GetString())
        {
            case "response":
                input.Responses.Add(new EstimateResponse(
                    Source(e.GetProperty("source").GetString()), OptionalString(e, "file"), OptionalLong(e, "startMs"), e.GetProperty("endMs").GetInt64(),
                    e.GetProperty("model").GetString()!, OptionalLong(e, "costMicroUsd"), OptionalLong(e, "tokens") ?? 0, OptionalString(e, "provider"),
                    OptionalString(e, "cloudRoute") is not null));
                break;
            case "tokenCount":
                var ts = e.GetProperty("ts").GetInt64();
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(e.GetProperty("rateLimits").GetRawText()));
                reader.Read();
                var limits = CodexRateLimits.Read(ref reader)!;
                input.CodexLines.Add(new CodexTokenLine(e.GetProperty("file").GetString()!, ts, limits.Windows(ts), limits.PlanType));
                break;
            case "codexAuth":
                input.CodexKeys.Add((e.GetProperty("at").GetInt64(), AccountKeys.Key(salt, AccountKeys.CodexVendor, e.GetProperty("accountId").GetString()!)));
                break;
            case "claudeProfile":
                var org = e.GetProperty("org").GetString()!;
                input.ClaudeProfiles.Add(new ClaudeProfile(e.GetProperty("at").GetInt64(),
                    AccountKeys.Key(salt, AccountKeys.ClaudeVendor, org + ":" + e.GetProperty("account").GetString()),
                    AccountKeys.Key(salt, AccountKeys.DesktopVendor, org), AccountKeys.CleanPlan(OptionalString(e, "orgType")),
                    AccountKeys.CleanPlan(OptionalString(e, "tier"))));
                break;
            case "snapshot":
                var desktopOrg = OptionalString(e, "org");
                input.Snapshots.Add(new EstimateSnapshot(Source(e.GetProperty("source").GetString()), e.GetProperty("observedMs").GetInt64(),
                    [.. e.GetProperty("windows").EnumerateArray().Select(Window)],
                    desktopOrg is null ? null : AccountKeys.Key(salt, AccountKeys.DesktopVendor, desktopOrg)));
                break;
            case var other:
                throw new InvalidDataException(other);
        }
    }

    /// <summary>The reference's <c>generated_store</c>.</summary>
    private static JsonObject GeneratedStore(JsonElement g)
    {
        var t0 = g.GetProperty("t0").GetInt64();
        var account = g.GetProperty("account").GetString();
        var observations = new JsonArray();
        for (var i = 1; i <= g.GetProperty("observations").GetInt32(); i++)
        {
            observations.Add(new JsonObject
            {
                ["account"] = account, ["source"] = "claudeCode", ["kind"] = "sevenDay", ["name"] = null, ["minutes"] = 10080,
                ["t"] = t0 + i * Minute, ["tLo"] = t0 + i * Minute, ["p"] = Math.Round(i / 10.0, 1), ["R"] = t0 + 6 * Day,
                ["plan"] = "claude_max", ["tier"] = null,
            });
        }

        var instances = new JsonArray();
        for (var i = 1; i <= g.GetProperty("instances").GetInt32(); i++)
        {
            instances.Add(new JsonObject
            {
                ["class"] = new JsonArray("plan", "anthropic/claude_max", null, "fiveHour::300"), ["account"] = account,
                ["R"] = t0 - i * 5 * Hour, ["lo"] = 1e8, ["hi"] = 2e8, ["pMax"] = 40.0, ["tLast"] = t0 - i * 5 * Hour - Hour,
            });
        }

        return new JsonObject
        {
            ["version"] = g.GetProperty("version").GetInt32(),
            ["observations"] = observations,
            ["instances"] = instances,
            ["keys"] = new JsonArray(new JsonObject { ["source"] = "claudeCode", ["at"] = t0, ["key"] = account, ["plan"] = "claude_max", ["tier"] = null }),
        };
    }

    [Fact]
    public void TheComparisonCatchesADifference()
    {
        var c = Root.GetProperty("pipeline").GetProperty("report")[0];
        var actual = Run(c.GetProperty("input"))["entries"]!;
        actual[0]!["usedMicroUsd"] = actual[0]!["usedMicroUsd"]!.GetValue<long>() + 1;
        Assert.ThrowsAny<Exception>(() => AssertSame(c.GetProperty("expected").GetProperty("entries"), ToElement(actual), "entries"));
        actual = Run(c.GetProperty("input"))["entries"]!;
        actual[1]!["livePercent"] = actual[1]!["livePercent"]!.GetValue<double>() + 0.1;
        Assert.ThrowsAny<Exception>(() => AssertSame(c.GetProperty("expected").GetProperty("entries"), ToElement(actual), "entries"));
        Assert.Equal(c.GetProperty("expected").GetProperty("entries").GetArrayLength(), Run(c.GetProperty("input"))["entries"]!.AsArray().Count);
    }

    [Fact]
    public void AStoreRoundTripsThroughItsFile()
    {
        var c = Root.GetProperty("pipeline").GetProperty("store")[0];
        var store = EstimateStore.Parse(c.GetProperty("expected").GetProperty("store"))!;
        var again = EstimateStore.Parse(EstimateStore.Serialize(store))!;
        AssertSame(c.GetProperty("expected").GetProperty("store"), StoreJson(again), "round trip");
        Assert.Null(EstimateStore.Parse("{not json"u8));
    }
}
