using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using static AgentSpeed.Core.Tests.Quota.Estimate.EstimateFixture;

namespace AgentSpeed.Core.Tests.Quota.Estimate;

/// <summary>The unit cases of spec/fixtures/quota-estimate-cases.json (QUOTAS 10, REPORTING-EXTENSIONS 11).</summary>
public sealed class QuotaEstimateFixtureTests
{
    [Fact]
    public void ConstantsMatchTheReference()
    {
        var c = Root.GetProperty("constants");
        Assert.Equal(EstimateLimits.LagMs, c.GetProperty("lagMs").GetInt64());
        Assert.Equal(EstimateLimits.HiPenalty, c.GetProperty("hiPenalty").GetDouble());
        Assert.Equal(EstimateLimits.FullStep, c.GetProperty("fullStep").GetDouble());
        Assert.Equal(EstimateLimits.MinWeight, c.GetProperty("minWeight").GetDouble());
        Assert.Equal(EstimateLimits.IncrementStep, c.GetProperty("incrementStep").GetDouble());
        Assert.Equal(EstimateLimits.SegmentsPerInstance, c.GetProperty("segmentsPerInstance").GetInt32());
        Assert.Equal(EstimateLimits.InstancesPerClass, c.GetProperty("instancesPerClass").GetInt32());
        Assert.Equal(EstimateLimits.HighWidth, c.GetProperty("highWidth").GetDouble());
        Assert.Equal(EstimateLimits.PrecisionFloor, c.GetProperty("precisionFloor").GetDouble());
        Assert.Equal(EstimateLimits.SendEveryMs, c.GetProperty("sendEveryMs").GetInt64());
    }

    [Fact]
    public void Resolutions()
    {
        foreach (var c in Root.GetProperty("resolution").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expected").GetDouble(), QuotaObservation.Resolution(c.GetProperty("p").GetDouble()));
        }
    }

    [Fact]
    public void AccountKeysPlansAndChains()
    {
        var accounts = Root.GetProperty("accounts");
        var salt = Convert.FromHexString(accounts.GetProperty("salt").GetString()!);
        foreach (var k in accounts.GetProperty("keys").EnumerateArray())
        {
            Assert.Equal(k.GetProperty("expected").GetString(), AccountKeys.Key(salt, k.GetProperty("vendor").GetString()!, k.GetProperty("id").GetString()!));
        }

        foreach (var p in accounts.GetProperty("plans").EnumerateArray())
        {
            var value = p.GetProperty("value");
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            Assert.Equal(OptionalString(p, "expected"), AccountKeys.CleanPlan(text));
        }

        var chains = new CodexChains();
        var names = new Dictionary<string, string>();
        var roots = accounts.GetProperty("chains").GetProperty("lines").EnumerateArray()
            .Select(l => chains.Link([.. l.EnumerateArray().Select(t => t.GetString()!)])).ToList();
        var got = roots.Select(r => chains.Find(r)).Select(r => names.TryGetValue(r, out var n) ? n : names[r] = "chain" + names.Count).ToList();
        Assert.Equal(accounts.GetProperty("chains").GetProperty("expected").EnumerateArray().Select(e => e.GetString()), got);
    }

    [Fact]
    public void FingerprintsAndMerges()
    {
        var accounts = Root.GetProperty("accounts");
        static List<WindowPoint> Points(JsonElement list) =>
            [.. list.EnumerateArray().Select(x => new WindowPoint(x[0].GetInt64(), x[1].GetString()!, x[2].GetInt64(), x[3].GetInt32()))];
        foreach (var f in accounts.GetProperty("fingerprints").EnumerateArray())
        {
            var why = f.GetProperty("why").GetString();
            var expected = f.GetProperty("expected");
            bool? want = expected.ValueKind == JsonValueKind.Null ? null : expected.GetBoolean();
            Assert.True(want == AccountMerge.Fingerprint(Points(f.GetProperty("a")), Points(f.GetProperty("b")), f.GetProperty("at").GetInt64()), why);
        }

        foreach (var m in accounts.GetProperty("desktopMerge").EnumerateArray())
        {
            var fp = m.GetProperty("fingerprint");
            bool? fingerprint = fp.ValueKind == JsonValueKind.Null ? null : fp.GetBoolean();
            Assert.Equal(m.GetProperty("expected").GetBoolean(),
                AccountMerge.DesktopSharesCli(OptionalString(m, "desktopOrg"), OptionalString(m, "cliOrg"), OptionalString(m, "cliOrgType"), fingerprint));
        }

        foreach (var k in accounts.GetProperty("keysShare").EnumerateArray())
        {
            var fp = k.GetProperty("fingerprint");
            Assert.Equal(k.GetProperty("expected").GetBoolean(), AccountMerge.KeysShare(fp.ValueKind == JsonValueKind.Null ? null : fp.GetBoolean()));
        }
    }

    public static TheoryData<int> SegmentCases() => new(Enumerable.Range(0, Root.GetProperty("segments").GetArrayLength()));

    [Theory]
    [MemberData(nameof(SegmentCases))]
    public void Segments(int index)
    {
        var c = Root.GetProperty("segments")[index];
        var ledger = LedgerFrom(c.GetProperty("rows"));
        var observations = c.GetProperty("observations").EnumerateArray().Select(o => Observation(o)).ToList();
        var instance = WindowInstance.Group(observations)[0];
        var segments = InstanceSegments.Of(instance, ledger, "a", observations[0].Name);
        var bounds = InstanceSegments.Bounds(segments, instance.R);
        var actual = new JsonObject
        {
            ["S"] = instance.S,
            ["Shi"] = instance.SHi,
            ["segments"] = new JsonArray([.. segments.Select(s => Segment(s.Type == SegmentType.Cumulative ? "cumulative" : "increment", s.Lo, s.Hi, s.Dp))]),
            ["instance"] = bounds is null ? null : Segment("instance", bounds.Lo, bounds.Hi, bounds.Dp),
        };
        AssertSame(c.GetProperty("expected"), ToElement(actual), c.GetProperty("name").GetString()!);
    }

    private static JsonObject Segment(string type, double lo, double hi, double dp) => new()
    {
        ["type"] = type,
        ["lo"] = Math.Round(lo, 3),
        ["hi"] = Fnum(hi),
        ["dp"] = dp,
    };

    [Fact]
    public void Votes()
    {
        foreach (var c in Root.GetProperty("vote").EnumerateArray())
        {
            var items = c.GetProperty("items").EnumerateArray()
                .Select(i => new VoteItem(i[0].GetDouble(), Hi(i[1]), i[2].GetDouble(), i[3].GetInt64())).ToList();
            var r = CapVote.Vote(items)!;
            var above = items.Where(i => i.Lo > r.Low && i.W >= EstimateLimits.MinWeight).Sum(i => i.W);
            var actual = new JsonObject
            {
                ["low"] = r.Low,
                ["high"] = Fnum(r.High),
                ["inside"] = Math.Round(r.Inside, 6),
                ["insideWindows"] = r.Narrow,
                ["loss"] = Math.Round(r.Loss, 6),
                ["confidence"] = CapEstimate.Name(CapVote.Confidence(r.Low, r.High, r.Inside, r.Narrow, above)),
            };
            AssertSame(c.GetProperty("expected"), ToElement(actual), c.GetProperty("name").GetString()!);
        }
    }

    [Fact]
    public void Weights()
    {
        foreach (var c in Root.GetProperty("weights").EnumerateArray())
        {
            var age = (long)(c.GetProperty("ageDays").GetDouble() * Day);
            var w = CapVote.Weight(c.GetProperty("dp").GetDouble(), T0, T0 + age, CapVote.HalfLifeMs(c.GetProperty("minutes").GetInt32()));
            Assert.Equal(c.GetProperty("expected").GetDouble(), Math.Round(w, 6), 9);
            Assert.Equal(c.GetProperty("used").GetBoolean(), w >= EstimateLimits.MinWeight);
        }
    }

    [Fact]
    public void Estimates()
    {
        foreach (var c in Root.GetProperty("estimate").EnumerateArray())
        {
            var items = c.GetProperty("items").EnumerateArray().Select(i => new InstanceInterval(
                i.GetProperty("window").GetInt64(), i.GetProperty("lo").GetDouble(), Hi(i.GetProperty("hi")), i.GetProperty("dp").GetDouble(),
                T0 - (long)(i.GetProperty("ageDays").GetDouble() * Day))).ToList();
            var e = CapVote.Estimate(items, T0, 7 * Day);
            AssertSame(c.GetProperty("expected"), ToElement(EstimateJson(e, 6)), c.GetProperty("name").GetString()!);
        }
    }

    internal static JsonObject EstimateJson(CapEstimate e, int digits) => new()
    {
        ["confidence"] = CapEstimate.Name(e.Confidence),
        ["cap"] = e.Cap is { } cap ? Math.Round(cap, digits) : null,
        ["low"] = e.Low is { } low ? Math.Round(low, digits) : null,
        ["high"] = e.High is { } high ? Math.Round(high, digits) : null,
        ["windows"] = e.Windows,
    };

    [Fact]
    public void APlanChangeMovesTheEvidence()
    {
        var c = Root.GetProperty("plan");
        var ledger = LedgerFrom(c.GetProperty("rows"));
        var actual = new JsonObject();
        foreach (var group in new[] { "old", "new" })
        {
            var observations = c.GetProperty(group).EnumerateArray().Select(o => Observation(o)).ToList();
            var bounds = WindowInstance.Group(observations).Select(i => InstanceSegments.Bounds(InstanceSegments.Of(i, ledger, "a", null), i.R)!).ToList();
            var e = CapVote.Estimate(bounds, T0 + 100 * Hour, 7 * Day);
            actual[observations[0].Plan!] = new JsonObject
            {
                ["cap"] = EstimateMath.Micro(e.Cap),
                ["low"] = EstimateMath.Micro(e.Low),
                ["high"] = EstimateMath.Micro(e.High),
                ["confidence"] = CapEstimate.Name(e.Confidence),
            };
        }

        AssertSame(c.GetProperty("expected"), ToElement(actual), "plan");
    }

    [Fact]
    public void LivePercents()
    {
        var live = Root.GetProperty("live");
        var ledger = LedgerFrom(live.GetProperty("rows"));
        foreach (var c in live.GetProperty("cases").EnumerateArray())
        {
            var minutes = c.GetProperty("minutes").GetInt32();
            var r = c.GetProperty("R").GetInt64();
            var v = LivePercent.Of(c.GetProperty("p").GetDouble(), c.GetProperty("t").GetInt64(), r, r - minutes * Minute, minutes,
                EstimateNames.ParseKind(c.GetProperty("kind").GetString())!.Value, OptionalDouble(c, "cap"), ledger, "a", null,
                c.GetProperty("now").GetInt64(), OptionalLong(c, "firstAfterReset"));
            var actual = new JsonObject { ["live"] = Math.Round(v.Percent, 6), ["estimated"] = v.Estimated, ["resets"] = v.ResetsAtMs };
            AssertSame(c.GetProperty("expected"), ToElement(actual), c.GetProperty("name").GetString()!);
        }
    }

    [Fact]
    public void ReportEntries()
    {
        var report = Root.GetProperty("report");
        var ledger = LedgerFrom(report.GetProperty("rows"));
        var input = report.GetProperty("input");
        foreach (var c in report.GetProperty("entries").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var estimate = name == "no estimate" ? CapEstimate.None : EstimateOf(input.GetProperty("estimate"));
            var entry = QuotaEntryBuilder.Build(Basis(input, estimate), c.GetProperty("now").GetInt64(), ledger, 8);
            AssertSame(c.GetProperty("expected"), EntryJson(entry), name);
        }
    }

    [Fact]
    public void SendCadence()
    {
        var report = Root.GetProperty("report");
        var ledger = LedgerFrom(report.GetProperty("rows"));
        var input = report.GetProperty("input");
        var now = report.GetProperty("entries")[0].GetProperty("now").GetInt64();
        var a = QuotaEntryBuilder.Build(Basis(input, EstimateOf(input.GetProperty("estimate"))), now, ledger, 8);
        var b = a with { UsedMicroUsd = a.UsedMicroUsd + 5 };
        var c = a with { UsedPercent = 24.0 };
        foreach (var step in report.GetProperty("cadence").EnumerateArray())
        {
            var name = step.GetProperty("name").GetString()!;
            var ago = OptionalLong(step, "lastSentMsAgo");
            var got = name switch
            {
                "first report" => QuotaReportCadence.ShouldSend([a], null, 0, 0, false),
                "leaving" => QuotaReportCadence.ShouldSend([a], [a], 0, ago!.Value, true),
                _ => step.GetProperty("change").GetString() switch
                {
                    "spend" => QuotaReportCadence.ShouldSend([b], [a], 0, ago!.Value, false),
                    "usedPercent" => QuotaReportCadence.ShouldSend([c], [a], 0, ago!.Value, false),
                    "set" => QuotaReportCadence.ShouldSend([a, b with { Sources = [SourceKind.ClaudeCode] }], [a], 0, ago!.Value, false),
                    var other => throw new InvalidDataException(other),
                },
            };
            Assert.True(step.GetProperty("expected").GetBoolean() == got, name);
        }
    }

    private static CapEstimate EstimateOf(JsonElement e) => new(
        e.GetProperty("confidence").GetString() switch
        {
            "high" => EstimateConfidence.High,
            "medium" => EstimateConfidence.Medium,
            "low" => EstimateConfidence.Low,
            _ => EstimateConfidence.None,
        },
        OptionalDouble(e, "cap"), OptionalDouble(e, "low"), OptionalDouble(e, "high"), e.GetProperty("windows").GetInt32());

    private static EntryBasis Basis(JsonElement acc, CapEstimate estimate) => new(
        Source(acc.GetProperty("source").GetString()), [.. acc.GetProperty("sources").EnumerateArray().Select(s => Source(s.GetString()))],
        OptionalString(acc, "plan"), OptionalString(acc, "tier"), EstimateNames.ParseKind(acc.GetProperty("kind").GetString())!.Value,
        OptionalString(acc, "name"), acc.GetProperty("minutes").GetInt32(), acc.GetProperty("p").GetDouble(), acc.GetProperty("t").GetInt64(),
        acc.GetProperty("R").GetInt64(), acc.GetProperty("S").GetInt64(), acc.GetProperty("accounts")[0].GetString()!, estimate,
        OptionalLong(acc, "firstAfterReset"));
}
