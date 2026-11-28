using System.Runtime.Versioning;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Tests.Quota.Cookies;
using AgentSpeed.Core.Tests.Quota.Local;
using AgentSpeed.Core.Tests.Quota.Network;
using AgentSpeed.Core.Tests.Quota.Vendors;
using static AgentSpeed.Core.Tests.Quota.Routes.QuotaVendorHarness;

namespace AgentSpeed.Core.Tests.Quota.Routes;

/// <summary>
/// QUOTAS 9.4: the Antigravity route falls back to agy's usage report (the stand-in agy) when no server gives windows,
/// under the route's cadence and only while a user's agy runs or the dashboard is open.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(FakeAgy.Collection)]
public sealed class AntigravityReportRouteTests : IDisposable
{
    private const long Minute = 60_000;

    private static readonly IReadOnlySet<QuotaRoute> Route = On(QuotaRoute.Antigravity);

    private readonly FakeAgy _agy = new();
    private readonly QuotaVendorHarness _h;

    public AntigravityReportRouteTests()
    {
        _h = new QuotaVendorHarness(report: _agy.Report);
        _h.Scripted.Answer = _ => QuotaHttpResponse.Of(401);
    }

    public void Dispose()
    {
        _h.Dispose();
        _agy.Dispose();
    }

    private int Reports => _agy.Arguments().Count(a => a is ["-p", ..]);

    [WindowsFact]
    public async Task ServersThatAnswerRunNoReport()
    {
        _h.Scripted.Answer = _ => ScriptedQuotaTransport.Json(Body("antigravity"));
        _h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("http", 42100, "csrf-1")]));
        _h.Locator.AgyRuns = true;
        var readings = await _h.RoundAsync(Route, dashboard: true);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
        QuotaVendorFixtureTests.AssertWindows(Vendor("antigravity").GetProperty("expected"), readings.Snapshot(SourceKind.Antigravity)!.Windows);
        Assert.Empty(_agy.Calls());
    }

    [WindowsFact]
    public async Task ATokenlessRefusalFallsBackToTheReport()
    {
        _h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("https", 42100, null)]));
        _h.Locator.AgyRuns = true;
        var readings = await _h.RoundAsync(Route);
        var request = Assert.Single(_h.Scripted.Requests);
        Assert.Null(request.Header("X-Codeium-Csrf-Token"));
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.True(readings.SignedIn(SourceKind.Antigravity));
        Assert.True(readings.AntigravityRuns);
        var snapshot = readings.Snapshot(SourceKind.Antigravity)!;
        Assert.Equal(_h.Now, snapshot.ObservedMs);
        QuotaVendorFixtureTests.AssertWindows(FakeAgy.ObservedWindows, snapshot.Windows);
        Assert.Equal(1, Reports);

        // Within 5 minutes nothing runs; then the refusing server is asked again and the report runs again.
        _h.Now += 4 * Minute;
        await _h.RoundAsync(Route);
        Assert.Single(_h.Scripted.Requests);
        Assert.Equal(1, Reports);
        _h.Now += Minute;
        readings = await _h.RoundAsync(Route);
        Assert.Equal(2, _h.Scripted.Requests.Count);
        Assert.Equal(2, Reports);
        Assert.Equal(1, _agy.Report.VersionRuns);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
    }

    [WindowsFact]
    public async Task AFailedReportWaitsFifteenMinutes()
    {
        _agy.Configure(report: "Please sign in to Antigravity: https://accounts.google.com/...\n");
        _h.Locator.AgyRuns = true;
        var readings = await _h.RoundAsync(Route);
        Assert.Equal(QuotaStatusKind.Failed, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.False(readings.SignedIn(SourceKind.Antigravity));
        Assert.Null(readings.Snapshot(SourceKind.Antigravity));
        Assert.Equal(1, Reports);

        _h.Now += 14 * Minute;
        await _h.RoundAsync(Route);
        Assert.Equal(1, Reports);

        _agy.Configure(report: FakeAgy.Observed);
        _h.Now += Minute;
        readings = await _h.RoundAsync(Route);
        Assert.Equal(2, Reports);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.True(readings.SignedIn(SourceKind.Antigravity));
    }

    [WindowsFact]
    public async Task AnUnsupportedAgyFailsTheRoundWithoutAReport()
    {
        _agy.Configure(version: "1.1.10", report: FakeAgy.Observed);
        _h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("http", 42100, null)]));
        _h.Locator.AgyRuns = true;
        var readings = await _h.RoundAsync(Route);
        Assert.Equal(QuotaStatusKind.Failed, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Equal(0, Reports);
        Assert.Equal(1, _agy.Report.VersionRuns);
    }

    [WindowsFact]
    public async Task WithoutAnAgyBinaryTheRouteIsAsBefore()
    {
        foreach (var file in Directory.GetFiles(_agy.Bin))
        {
            File.Delete(file);
        }

        _h.Locator.AgyRuns = true;
        var readings = await _h.RoundAsync(Route, dashboard: true);
        Assert.Equal(QuotaStatusKind.NotRunning, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Empty(_h.Scripted.Requests);

        _h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("http", 42100, null)]));
        readings = await _h.RoundAsync(Route, dashboard: true);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Single(_h.Scripted.Requests);
        Assert.Empty(_agy.Calls());
    }

    [WindowsFact]
    public async Task TheReportRunsOnlyWhileAUserAgyRunsOrTheDashboardIsOpen()
    {
        // Antigravity runs (its desktop app, say) but no server is found and no agy runs.
        var readings = await _h.RoundAsync(Route);
        Assert.Equal(QuotaStatusKind.NotRunning, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Empty(_agy.Calls());

        // A refusing server without agy: the refusal stands, as before.
        _h.Locator.Found.Add(new AntigravityServer(4242, [new AntigravityEndpoint("http", 42100, null)]));
        readings = await _h.RoundAsync(Route);
        Assert.Equal(QuotaStatusKind.Denied, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Empty(_agy.Calls());

        // The dashboard opens: the report runs although the server refused.
        _h.Now += 15 * Minute;
        readings = await _h.RoundAsync(Route, dashboard: true);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.Equal(1, Reports);

        // The network switch and the route's own switch stop it.
        _h.Now += 15 * Minute;
        _h.Locator.AgyRuns = true;
        readings = await _h.RoundAsync(Route, network: false, dashboard: true);
        Assert.Equal(QuotaStatusKind.NetworkOff, readings.Status(QuotaRoute.Antigravity)?.Kind);
        await _h.RoundAsync(On(QuotaRoute.Kimi), dashboard: true);
        Assert.Equal(1, Reports);

        // Neither Antigravity nor the dashboard: nothing is looked for.
        await _h.RoundAsync(Route, running: new HashSet<SourceKind>());
        Assert.Equal(1, Reports);

        // A user's agy runs.
        readings = await _h.RoundAsync(Route);
        Assert.Equal(2, Reports);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);

        // The quota block shows Antigravity (issue #81): the report runs as with the dashboard open, though nothing runs.
        _h.Now += 15 * Minute;
        _h.Locator.AgyRuns = false;
        readings = await _h.RoundAsync(Route, running: new HashSet<SourceKind>(), shown: new HashSet<SourceKind> { SourceKind.Antigravity });
        Assert.Equal(3, Reports);
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
    }
}
