using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Reporting;
using AgentSpeed.Services;
using AgentSpeed.Settings;
using AgentSpeed.Traffic;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// The 額度 switch in Settings → API and the preview's <c>quotas</c> (REPORTING-EXTENSIONS 11, DESIGN 11 item 5), with the
/// real controller and pane model over a stored identity; nothing is requested.
/// </summary>
public class ReportQuotaPaneTests
{
    private const string ServiceText = "https://board.example.com";
    private const string DeviceId = "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b";

    private static readonly Localizer Loc = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private const string Key = "0123456789abcdef";

    private static readonly QuotaReportEntry Entry = new(string.Empty, SourceKind.Codex, [SourceKind.Codex], "promax", null, QuotaWindowKind.SevenDay, null, 10_080,
        23.46, 18_012_345, 406_787_655, 24.7, true, 67_902_725, 61_729_625, 1,
        new QuotaEntryEstimate(512_345_678, 480_000_001, 546_000_000, EstimateConfidence.Medium, 0.514, 126_348_902, 2),
        [new("gpt-5.5", 0.673), new("gpt-5.4-mini", 0.327)], 8);

    private static ReportDiscovery Document(string extensions)
    {
        var json = $$"""
            {"protocol":"agent-speed-report","versions":[1],
             "service":{"name":"Live board","terms":"https://example.com/terms","privacy":"https://example.com/privacy"},
             "api":"https://api.example.com/agent-speed","extensions":{{extensions}}}
            """;
        return ReportDiscovery.Parse(Encoding.UTF8.GetBytes(json), ReportServiceUrl.Parse(ServiceText)!, out var problem)
            ?? throw new InvalidOperationException(problem?.ToString());
    }

    /// <summary>A controller and pane over an identity with <paramref name="document"/> stored, or with no identity at all.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TrafficService _traffic;

        public Harness(ReportDiscovery? document, bool shareQuota = true)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Settings = new SettingsService(dispatcher);
            Settings.MakeEphemeral();
            var service = ReportServiceUrl.Parse(ServiceText)!;
            ReportIdentity[] identities = document is null
                ? []
                : [new ReportIdentity { Service = service, DeviceId = DeviceId, Discovery = document, KeyCreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow }];
            Settings.Update(s => s with
            {
                Report = new ReportPreferences { Preset = ReportPreset.Custom, CustomUrl = ServiceText, Identities = [.. identities], ShareQuota = shareQuota },
            });
            _traffic = new TrafficService(dispatcher, Settings);
            var keys = new ReportKeyStore(Path.Combine(Path.GetTempPath(), "agent-speed-quota-pane-" + Guid.NewGuid().ToString("N")));
            Report = new ReportController(dispatcher, Settings, new SpeedHub(), _traffic, new PriceBook(), "1.3.0", new NoTransport(), keys);
            Pane = new ReportPaneViewModel(Report, Settings, Loc, dispatcher, (_, _) => false, () => { });
        }

        public SettingsService Settings { get; }

        public ReportController Report { get; }

        public ReportPaneViewModel Pane { get; }

        public void Dispose()
        {
            Pane.Dispose();
            Report.Dispose();
            _traffic.Dispose();
            Settings.Dispose();
        }
    }

    private sealed class NoTransport : IReportTransport
    {
        public Task<ReportHttpResponse> GetAsync(Uri url, string userAgent, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no request is expected");

        public Task<ReportHttpResponse> SendAsync(ReportRequest request, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no request is expected");
    }

    [Fact]
    public void TheSwitchIsOnByDefaultBeforeADocumentIsKnownAndAlwaysShowsItsHint() => OnStaThread(() =>
    {
        using var harness = new Harness(null);
        var row = harness.Pane.Quota;
        Assert.Same(row, harness.Pane.Sections[^1]);
        Assert.Null(row.Section);
        Assert.Equal(Loc[ReportSectionCaption.Quota], row.Title);
        Assert.True(row.IsOn);
        Assert.True(row.IsEnabled);
        Assert.Null(row.Caption);
        Assert.Equal(Loc[ReportSectionCaption.QuotaHint], row.Hint);
        Assert.Equal(row.Hint, row.HelpText);
        Assert.All(harness.Pane.Sections.Take(4), r => Assert.Null(r.Hint));
    });

    [Fact]
    public void AServiceWithoutTheExtensionShowsTheSwitchOffAndDisabled() => OnStaThread(() =>
    {
        using var harness = new Harness(Document("""{"perfScore":1}"""));
        var row = harness.Pane.Quota;
        Assert.True(harness.Settings.Current.Report.ShareQuota);
        Assert.False(row.IsOn);
        Assert.False(row.IsEnabled);
        Assert.Equal(Loc[ReportSectionCaption.NotAccepted], row.Caption);
        Assert.Equal(Loc[ReportSectionCaption.QuotaHint], row.Hint);
        Assert.Equal(row.Caption + "\n" + row.Hint, row.HelpText);
    });

    [Fact]
    public void TheSwitchStoresItsOwnPreference() => OnStaThread(() =>
    {
        using var harness = new Harness(Document("""{"quotaEstimate":1}"""), shareQuota: false);
        var row = harness.Pane.Quota;
        Assert.True(row.IsEnabled);
        Assert.False(row.IsOn);
        row.IsOn = true;
        Assert.True(harness.Settings.Current.Report.ShareQuota);
        Assert.Equal(ReportPreferences.DefaultSections.ToArray(), harness.Settings.Current.Report.Sections.ToArray());
        harness.Report.SetShareQuota(false);
        Assert.False(row.IsOn);
    });

    [Fact]
    public void ThePreviewShowsTheEntriesWhileTheyAreShared() => OnStaThread(() =>
    {
        using (var harness = new Harness(Document("""{"perfScore":1,"quotaEstimate":1}""")))
        {
            long? askedAt = null;
            string? askedFor = null;
            harness.Report.QuotaEntries = (now, origin) =>
            {
                (askedAt, askedFor) = (now, origin);
                return [Tagged(origin)];
            };
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var body = JsonDocument.Parse(harness.Report.Preview()!.Body.ToCompactString());
            var members = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(["perfScore", "quotas"], members.TakeLast(2));
            var entry = Assert.Single(body.RootElement.GetProperty("quotas").EnumerateArray());
            Assert.Equal("promax", entry.GetProperty("plan").GetString());
            Assert.InRange(askedAt!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Tagged for the origin of the document's api, where reports go, and never by the key.
            Assert.Equal("https://api.example.com", askedFor);
            Assert.Equal(AccountKeys.ReportTag("https://api.example.com", Key), entry.GetProperty("account").GetString());
            Assert.DoesNotContain(Key, body.RootElement.GetRawText(), StringComparison.Ordinal);

            harness.Report.SetShareQuota(false);
            askedAt = null;
            Assert.Null(harness.Report.Preview()!.Body["quotas"]);
            Assert.Null(askedAt);

            // Without an estimate the member is an empty list: no quotas at the moment.
            harness.Report.SetShareQuota(true);
            harness.Report.QuotaEntries = null;
            Assert.Equal("[]", harness.Report.Preview()!.Body["quotas"]!.ToCompactString());
        }

        using (var refused = new Harness(Document("""{"perfScore":1}""")))
        {
            var asked = false;
            refused.Report.QuotaEntries = (_, _) =>
            {
                asked = true;
                return [Entry];
            };
            Assert.Null(refused.Report.Preview()!.Body["quotas"]);
            Assert.False(asked);
        }

        // Before the document is known the preview assumes the extension, and tags for the service URL as typed.
        using var assumed = new Harness(null);
        assumed.Report.QuotaEntries = (_, origin) => [Tagged(origin)];
        var preview = assumed.Report.Preview()!;
        Assert.True(preview.Assumed);
        using var quotas = JsonDocument.Parse(preview.Body["quotas"]!.ToCompactString());
        Assert.Equal(AccountKeys.ReportTag(ServiceText, Key), quotas.RootElement[0].GetProperty("account").GetString());
    });

    private static QuotaReportEntry Tagged(string origin) => Entry with { Account = AccountKeys.ReportTag(origin, Key) };

    private static void OnStaThread(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("The pane test failed.", error);
        }
    }
}
