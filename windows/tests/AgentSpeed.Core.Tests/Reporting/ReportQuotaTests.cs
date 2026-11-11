using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// The <c>quotas</c> of reports (REPORTING-EXTENSIONS 11): the extension in the document, the member's place and the size
/// rule, the per-run cadence on both transports, and the reference's schema on bodies that carry them.
/// </summary>
public sealed class ReportQuotaTests
{
    private static readonly ReportServiceUrl Service = ReportServiceUrl.Parse("https://example.com")!;

    private static readonly ReportEnvelope Envelope = new(
        new ReportClientInfo("1.3.0", "Windows", 11), "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", "Tester",
        "c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", 7, 1_794_121_212_345, 5000, ReportActivity.Active, 0);

    private static readonly ReportOptions Shared = new(ReportSections.All.ToHashSet()) { Extensions = new(QuotaEstimate: true) };

    private static readonly ReportSample Sample = ReportSample.Empty;

    private static readonly ReportWindowContent Content = new ReportWindow(Sample).Content(Sample);

    internal static QuotaReportEntry Entry(QuotaWindowKind kind = QuotaWindowKind.SevenDay, double? used = 23.46,
        EstimateConfidence confidence = EstimateConfidence.Medium, SourceKind source = SourceKind.Codex, string? name = null)
    {
        var estimate = confidence == EstimateConfidence.None
            ? new QuotaEntryEstimate(null, null, null, EstimateConfidence.None, null, null, 0)
            : new QuotaEntryEstimate(512_345_678, 480_000_001, 546_000_000, confidence, 0.514, 126_348_902, 2);
        var minutes = kind == QuotaWindowKind.FiveHour ? 300 : 10_080;
        IReadOnlyList<SourceKind> sources = source == SourceKind.ClaudeDesktop ? [SourceKind.ClaudeCode, SourceKind.ClaudeDesktop] : [source];
        var account = AccountKeys.ReportTag(Service.Origin, AccountKeys.Key(AccountKeys.Salt, "test", ReportBody.SourceId(source)));
        return new QuotaReportEntry(account, source, sources, source == SourceKind.Codex ? "promax" : "claude_max", null, kind, name, minutes, used,
            used is null ? null : 18_012_345, 406_787_655, used ?? 0, true, 67_902_725, used is null ? null : 61_729_625, 1, estimate,
            [new("gpt-5.5", 0.673), new("gpt-5.4-mini", 0.327)], 8);
    }

    private static IReadOnlyList<QuotaReportEntry> Entries(double used = 23.46) =>
        [Entry(used: used), Entry(QuotaWindowKind.FiveHour, 61.2, EstimateConfidence.High), Entry(QuotaWindowKind.SevenDay, null, EstimateConfidence.None, SourceKind.ClaudeDesktop)];

    private static JsonElement Parse(byte[] data) => JsonDocument.Parse(data).RootElement.Clone();

    private static List<string> Members(byte[] data) => [.. Parse(data).EnumerateObject().Select(p => p.Name)];

    private static int? Count(byte[] data) => Parse(data).TryGetProperty("quotas", out var quotas) ? quotas.GetArrayLength() : null;

    // Discovery

    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("\"1\"", false)]
    [InlineData("2", false)]
    [InlineData("0", false)]
    [InlineData("{}", false)]
    public void TheExtensionIsOnOnlyForTheInteger1AndIsKeptWithTheStoredDocument(string? value, bool expected)
    {
        var raw = JsonNode.Parse(ReportSpec.Example("discovery.json"))!.AsObject();
        raw["extensions"] = JsonNode.Parse(value is null ? """{"perfScore":1}""" : $$"""{"perfScore":1,"quotaEstimate":{{value}}}""");
        var discovery = ReportDiscovery.Parse(System.Text.Encoding.UTF8.GetBytes(raw.ToJsonString()), Service, out var problem);
        Assert.Null(problem);
        Assert.Equal(expected, discovery!.Extensions.QuotaEstimate);
        Assert.True(discovery.Extensions.PerfScore);

        var stored = discovery.ToJson();
        Assert.Equal(expected, stored["extensions"]!["quotaEstimate"] is not null);
        var roundtrip = ReportDiscovery.Parse(stored.ToCompactBytes(), Service, out _);
        Assert.Equal(discovery.Extensions, roundtrip!.Extensions);
    }

    [Fact]
    public void ThePreviewAssumesTheExtensionBeforeADocumentIsKnown()
    {
        var assumed = ReportDiscovery.Assumed(Service, "Example")!;
        Assert.True(assumed.Extensions.QuotaEstimate);
        Assert.True(assumed.Extensions.PerfScore);
    }

    // Body

    [Fact]
    public void QuotasFollowThePerformanceScoreAndRealtimeStaysLast()
    {
        var options = Shared with { Extensions = new(PerfScore: true, QuotaEstimate: true), Quotas = Entries() };
        var http = Members(ReportBody.Data(Envelope, Content, Sample, options));
        Assert.Equal(["perfScore", "quotas"], http.TakeLast(2));

        var stream = Members(ReportBody.Data(Envelope, Content, Sample, options with { Stream = new ReportStreamBody(120, 65_536) }));
        Assert.Equal(["perfScore", "quotas", "realtime"], stream.TakeLast(3));

        var quotas = Parse(ReportBody.Data(Envelope, Content, Sample, options)).GetProperty("quotas");
        Assert.Equal(ReportQuotas.Json(Entries()).ToCompactString(), quotas.GetRawText());
    }

    [Fact]
    public void EachEntryLeadsWithItsAccountsTagAndNeverItsKey()
    {
        var key = AccountKeys.Key(AccountKeys.Salt, "test", "codex");
        var entry = Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries() })).GetProperty("quotas")[0];
        var first = entry.EnumerateObject().First();
        Assert.Equal(("account", AccountKeys.ReportTag(Service.Origin, key)), (first.Name, first.Value.GetString()));
        Assert.True(AccountKeys.IsKey(first.Value.GetString()));
        Assert.DoesNotContain(key, entry.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMemberNeedsTheExtensionAndNullLeavesItOut()
    {
        Assert.Null(Count(ReportBody.Data(Envelope, Content, Sample, Shared with { Extensions = new(), Quotas = Entries() })));
        Assert.Null(Count(ReportBody.Data(Envelope, Content, Sample, Shared)));
        Assert.Equal(0, Count(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = [] })));
    }

    [Fact]
    public void AtMostSixteenEntriesAreSent()
    {
        var many = Enumerable.Range(0, 20).Select(i => Entry(QuotaWindowKind.ModelWeek, name: "Model" + i)).ToList();
        Assert.Equal(EstimateLimits.MaxEntries, Count(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = many })));
    }

    [Fact]
    public void ABodyTooLargeDropsQuotasBeforeAnythingElse()
    {
        var without = ReportBody.Data(Envelope, Content, Sample, Shared).Length;
        var with = ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries() }).Length;
        Assert.True(with > without);

        var fitted = Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries(), BodyLimit = without }));
        Assert.False(fitted.TryGetProperty("quotas", out _));
        Assert.True(fitted.TryGetProperty("models", out _));
        Assert.True(fitted.TryGetProperty("sources", out _));

        var streamWithout = ReportBody.Data(Envelope, Content, Sample, Shared with { Stream = new ReportStreamBody(null, 65_536) });
        var limit = ReportWebSocketProtocol.MessageBytes(streamWithout);
        var stream = Parse(ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries(), Stream = new ReportStreamBody(null, limit) }));
        Assert.False(stream.TryGetProperty("quotas", out _));
        Assert.True(stream.TryGetProperty("models", out _));
    }

    // Cadence per run

    private sealed class Run
    {
        private readonly ReportQuotaRun _quotas = new();

        public IReadOnlyList<QuotaReportEntry> Now { get; set; } = Entries();

        public bool SwitchOn { get; set; } = true;

        public int Asked { get; private set; }

        public ReportOptions Options { get; set; } = Shared;

        public int? Report(long at, ReportActivity activity = ReportActivity.Active, int? limit = null)
        {
            var options = limit is { } bytes ? Options with { BodyLimit = bytes } : Options;
            var (json, data) = _quotas.Fitted(Envelope with { Activity = activity }, Content, Sample, options, at, SwitchOn, () =>
            {
                Asked++;
                return Now;
            });
            Assert.Equal(json.ToCompactString(), System.Text.Encoding.UTF8.GetString(data));
            return Count(data);
        }

        public void NewRun() => _quotas.NewRun();
    }

    public static TheoryData<bool> Transports => new() { false, true };

    private static Run On(bool stream) =>
        new() { Options = stream ? Shared with { Stream = new ReportStreamBody(null, 65_536) } : Shared };

    [Theory]
    [MemberData(nameof(Transports))]
    public void ARunCarriesQuotasWhenTheyChangeEveryMinuteAndWhenLeaving(bool stream)
    {
        var run = On(stream);
        Assert.Equal(3, run.Report(0));
        Assert.Null(run.Report(10_000));
        run.Now = Entries(used: 24.0);
        Assert.Equal(3, run.Report(15_000));
        Assert.Null(run.Report(30_000));
        Assert.Equal(3, run.Report(75_000));
        run.Now = [.. run.Now.Take(2)];
        Assert.Equal(2, run.Report(76_000));
        Assert.Equal(2, run.Report(77_000, ReportActivity.Leaving));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void SwitchingOffSendsAnEmptyListOnceWithoutAskingForEntries(bool stream)
    {
        var run = On(stream);
        Assert.Equal(3, run.Report(0));
        var asked = run.Asked;
        run.SwitchOn = false;
        Assert.Equal(0, run.Report(5_000));
        Assert.Null(run.Report(10_000));
        Assert.Null(run.Report(200_000, ReportActivity.Leaving));
        Assert.Equal(asked, run.Asked);
        run.SwitchOn = true;
        Assert.Equal(3, run.Report(201_000));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void AReportTooLargeForQuotasCountsAsUnsent(bool stream)
    {
        var run = On(stream);
        var small = ReportBody.Data(Envelope, Content, Sample, run.Options).Length;
        Assert.Null(run.Report(0, limit: small));
        Assert.Equal(3, run.Report(1_000));
        Assert.Null(run.Report(2_000));
        run.SwitchOn = false;
        Assert.Null(run.Report(3_000, limit: small));
        Assert.Equal(0, run.Report(4_000));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void ANewRunStartsEmpty(bool stream)
    {
        var run = On(stream);
        Assert.Equal(3, run.Report(0));
        run.NewRun();
        Assert.Equal(3, run.Report(1_000));
        run.SwitchOn = false;
        Assert.Equal(0, run.Report(2_000));
        run.SwitchOn = true;
        Assert.Equal(3, run.Report(3_000));
        run.SwitchOn = false;
        run.NewRun();
        Assert.Null(run.Report(4_000));
    }

    [Fact]
    public void AServiceWithoutTheExtensionGetsNoneAndIsNotAsked()
    {
        var run = new Run { Options = Shared with { Extensions = new(PerfScore: true) } };
        Assert.Null(run.Report(0));
        run.SwitchOn = false;
        Assert.Null(run.Report(1_000));
        Assert.Equal(0, run.Asked);
        run.SwitchOn = true;
        run.Options = Shared;
        Assert.Equal(3, run.Report(2_000));
        run.Options = Shared with { Extensions = new() };
        Assert.Null(run.Report(3_000));
        run.Options = Shared;
        Assert.Equal(3, run.Report(4_000));
    }

    [Fact]
    public void NoEntriesAreAnEmptyListOnTheCadence()
    {
        var run = new Run { Now = [] };
        Assert.Equal(0, run.Report(0));
        Assert.Null(run.Report(30_000));
        Assert.Equal(0, run.Report(60_000));
    }

    // The reference

    [Fact]
    public void ReferenceAcceptsBodiesWithQuotas()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        var named = Entry(QuotaWindowKind.ModelWeek, 12.5, EstimateConfidence.Low, SourceKind.ClaudeDesktop, "Fable");
        var bodies = new List<byte[]>
        {
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries() }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = [.. Entries(), named] }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = [] }),
            ReportBody.Data(Envelope with { Activity = ReportActivity.Leaving }, Content, Sample, Shared with { Quotas = Entries(), Sections = new HashSet<ReportSection>() }),
            ReportBody.Data(Envelope, Content, Sample, Shared with { Quotas = Entries(), Extensions = new(PerfScore: true, QuotaEstimate: true) }),
        };
        var failures = ReferencePython.StrictErrors(bodies);
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.All(bodies, b => Assert.NotNull(Count(b)));
    }
}
