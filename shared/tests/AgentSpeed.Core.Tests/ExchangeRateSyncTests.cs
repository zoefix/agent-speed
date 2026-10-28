using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Tests;

public sealed class ExchangeRateSyncTests
{
    private static readonly IReadOnlyList<ExchangeRateProvider> Providers = ExchangeRateSync.Providers;
    private static readonly Uri ErApi = Providers[0].Url;
    private static readonly Uri JsDelivr = Providers[1].Url;
    private static readonly Uri Pages = Providers[2].Url;
    private static readonly Uri Frankfurter = Providers[3].Url;

    /// <summary>2026-10-22T12:00Z.</summary>
    private static readonly DateTimeOffset Noon = DateTimeOffset.FromUnixTimeSeconds(1_792_670_400);

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public void ProvidersAreTheFourOfTheSpec()
    {
        Assert.Equal(
            [
                "https://open.er-api.com/v6/latest/USD",
                "https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json",
                "https://latest.currency-api.pages.dev/v1/currencies/usd.min.json",
                "https://api.frankfurter.dev/v1/latest?base=USD",
            ],
            Providers.Select(p => p.Url.AbsoluteUri));
        Assert.Equal(
            [ExchangeRates.Shape.ErApi, ExchangeRates.Shape.Fawaz, ExchangeRates.Shape.Fawaz, ExchangeRates.Shape.Frankfurter],
            Providers.Select(p => p.Shape));
        Assert.Equal(256 * 1024, ExchangeRateSync.MaxBytes);
        Assert.Equal("https://www.exchangerate-api.com/", ExchangeRateSync.AttributionUrl.AbsoluteUri);
    }

    [Fact]
    public async Task TheFirstProviderWinsAndIsStored()
    {
        using var harness = new Harness();
        harness.Transport.Reply(ErApi, body: ErApiBody(), etag: "W/\"1\"");
        var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("TWD")).Set;
        Assert.Equal("open.er-api.com", set.Provider);
        Assert.True(set.NeedsAttribution);
        Assert.Equal(32.41m, set.Rates["TWD"]);
        Assert.Equal(1_792_627_351_000, set.DateMs);
        Assert.Equal([ErApi.AbsoluteUri], harness.Transport.RequestedUrls);
        var request = harness.Transport.Requests[0];
        Assert.DoesNotContain("twd", request.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(256 << 10, request.MaxBytes);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("AgentSpeed/9.9.9 (Windows)", request.UserAgent);
        Assert.Null(request.IfNoneMatch);
        Assert.True(request.AllowedHosts.SetEquals(["open.er-api.com", "cdn.jsdelivr.net", "latest.currency-api.pages.dev", "api.frankfurter.dev"]));
        var stored = harness.Store.Load();
        Assert.NotNull(stored);
        Assert.Equal("open.er-api.com", stored.Provider);
        Assert.Equal(new Dictionary<string, string> { [ErApi.AbsoluteUri] = "W/\"1\"" }, stored.ETags);
        Assert.Equal(Noon.ToUnixTimeMilliseconds(), stored.FetchedMs);
        Assert.Equal(set, stored.ToRateSet());
        Assert.All(harness.Log.Lines, line => Assert.DoesNotContain("TWD", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStoreFileHasTheSpecShape()
    {
        using var harness = new Harness();
        harness.Transport.Reply(ErApi, body: ErApiBody(), etag: "\"e\"");
        await harness.Sync.RunAsync("TWD");
        Assert.Equal(Path.Combine(harness.Directory.Path, "rates.json"), harness.Store.FilePath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(harness.Store.FilePath));
        Assert.Equal(["date", "etags", "fetchedMs", "provider", "rates", "url"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("32.41", document.RootElement.GetProperty("rates").GetProperty("TWD").GetString());
        Assert.Equal(1_792_627_351_000, document.RootElement.GetProperty("date").GetInt64());
        Assert.Empty(Directory.GetFiles(harness.Directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ProvidersFallBackInOrderAndThePagesMirrorOnlyWhenJsDelivrFails()
    {
        using (var harness = new Harness())
        {
            harness.Transport.Reply(ErApi, status: 429);
            harness.Transport.Reply(JsDelivr, body: CurrencyApiBody());
            var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("TWD")).Set;
            Assert.Equal("cdn.jsdelivr.net", set.Provider);
            Assert.False(set.NeedsAttribution);
            Assert.Equal([ErApi.AbsoluteUri, JsDelivr.AbsoluteUri], harness.Transport.RequestedUrls);
            Assert.False(set.Rates.ContainsKey("BTC"));
        }

        using var mirror = new Harness();
        mirror.Transport.Reply(JsDelivr, status: 503);
        mirror.Transport.Reply(Pages, body: CurrencyApiBody());
        var fromPages = Assert.IsType<ExchangeRateOutcome.Updated>(await mirror.Sync.RunAsync("TWD")).Set;
        Assert.Equal("latest.currency-api.pages.dev", fromPages.Provider);
        Assert.Equal([ErApi.AbsoluteUri, JsDelivr.AbsoluteUri, Pages.AbsoluteUri], mirror.Transport.RequestedUrls);
    }

    [Fact]
    public async Task ARateOutsideTheBoundsRejectsTheProvider()
    {
        using var harness = new Harness();
        harness.Transport.Reply(ErApi, body: ErApiBody(twd: "97.2"));
        harness.Transport.Reply(JsDelivr, body: CurrencyApiBody());
        var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("TWD")).Set;
        Assert.Equal("cdn.jsdelivr.net", set.Provider);
    }

    [Fact]
    public async Task StaleRatesNeedTwoProvidersAndKeepTheFirstOnesRates()
    {
        using var harness = new Harness();
        var monthAgo = Noon - 40 * Day;
        harness.StoreRates(Rates(("TWD", 20m)), Providers[1], monthAgo.ToUnixTimeMilliseconds(), monthAgo.ToUnixTimeMilliseconds());
        harness.Transport.Reply(ErApi, body: ErApiBody(twd: "32.41"));
        harness.Transport.Reply(JsDelivr, body: CurrencyApiBody(twd: "32.55"));
        var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("TWD")).Set;
        Assert.Equal("open.er-api.com", set.Provider);
        Assert.Equal(32.41m, set.Rates["TWD"]);
        Assert.Equal([ErApi.AbsoluteUri, JsDelivr.AbsoluteUri], harness.Transport.RequestedUrls);
    }

    [Fact]
    public async Task StaleRatesWithDisagreeingProvidersFail()
    {
        using var harness = new Harness();
        var monthAgo = Noon - 40 * Day;
        harness.StoreRates(Rates(("TWD", 32m)), Providers[1], monthAgo.ToUnixTimeMilliseconds(), monthAgo.ToUnixTimeMilliseconds());
        harness.Transport.Reply(ErApi, body: ErApiBody(twd: "45"));
        harness.Transport.Reply(JsDelivr, body: CurrencyApiBody(twd: "32.55"));
        harness.Transport.Reply(Frankfurter, body: FrankfurterBody("\"JPY\":157.9"));
        Assert.IsType<ExchangeRateOutcome.Failed>(await harness.Sync.RunAsync("TWD"));
        Assert.DoesNotContain(Pages.AbsoluteUri, harness.Transport.RequestedUrls);
        Assert.Equal(32m, harness.Store.Load()!.ToRateSet().Rates["TWD"]);
    }

    [Fact]
    public async Task AStaleRoundWithOneAnsweringProviderNeedsASecond()
    {
        using var harness = new Harness();
        var monthAgo = Noon - 40 * Day;
        harness.StoreRates(Rates(("TWD", 32m)), Providers[1], monthAgo.ToUnixTimeMilliseconds(), monthAgo.ToUnixTimeMilliseconds());
        harness.Transport.Reply(ErApi, body: ErApiBody(twd: "45"));
        var failed = Assert.IsType<ExchangeRateOutcome.Failed>(await harness.Sync.RunAsync("TWD"));
        Assert.Equal("needs a second provider", failed.Reason);
    }

    [Fact]
    public async Task FrankfurterNeedsTheSelectedCurrencyAndKeepsTheOthers()
    {
        using (var harness = new Harness())
        {
            var yesterday = Noon - Day;
            harness.StoreRates(Rates(("TWD", 32.4m), ("JPY", 158m)), Providers[0],
                yesterday.ToUnixTimeMilliseconds() - (long)Day.TotalMilliseconds, yesterday.ToUnixTimeMilliseconds());
            harness.Transport.Reply(Frankfurter, body: FrankfurterBody("\"JPY\":157.88,\"CNY\":6.6761"));
            var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("JPY")).Set;
            Assert.Equal("api.frankfurter.dev", set.Provider);
            Assert.Equal(157.88m, set.Rates["JPY"]);
            Assert.Equal(32.4m, set.Rates["TWD"]);
            Assert.Equal(1m, set.Rates["USD"]);
            Assert.Equal(6.6761m, set.Rates["CNY"]);
        }

        using (var bundled = new Harness())
        {
            bundled.Transport.Reply(Frankfurter, body: FrankfurterBody("\"JPY\":157.88"));
            var set = Assert.IsType<ExchangeRateOutcome.Updated>(await bundled.Sync.RunAsync("JPY")).Set;
            Assert.Equal(CurrencyTable.Bundled.FallbackRates.Rates["TWD"], set.Rates["TWD"]);
            Assert.Equal(38, set.Rates.Count);
        }

        using var missing = new Harness();
        missing.Transport.Reply(Frankfurter, body: FrankfurterBody("\"JPY\":157.88"));
        var failed = Assert.IsType<ExchangeRateOutcome.Failed>(await missing.Sync.RunAsync("TWD"));
        Assert.Equal("selected currency missing", failed.Reason);
    }

    [Fact]
    public async Task A304ConfirmsTheStoredRates()
    {
        using var harness = new Harness();
        var yesterday = Noon - Day;
        harness.StoreRates(Rates(("TWD", 32m)), Providers[0], yesterday.ToUnixTimeMilliseconds(), yesterday.ToUnixTimeMilliseconds(), etag: "\"e1\"");
        harness.Transport.Reply(ErApi, status: 304);
        var set = Assert.IsType<ExchangeRateOutcome.Unchanged>(await harness.Sync.RunAsync("TWD")).Set;
        Assert.Equal("\"e1\"", harness.Transport.Requests[0].IfNoneMatch);
        Assert.Equal(Noon.ToUnixTimeMilliseconds(), set.FetchedMs);
        Assert.Equal(Noon.ToUnixTimeMilliseconds(), harness.Store.Load()!.FetchedMs);
        Assert.Equal(yesterday.ToUnixTimeMilliseconds(), set.DateMs);
        Assert.Equal("\"e1\"", harness.Store.Load()!.ETags[ErApi.AbsoluteUri]);
    }

    [Fact]
    public async Task AnETagIsSentOnlyToTheUrlTheRatesCameFrom()
    {
        using var harness = new Harness();
        var yesterday = Noon - Day;
        harness.StoreRates(Rates(("TWD", 32m)), Providers[1], yesterday.ToUnixTimeMilliseconds(), yesterday.ToUnixTimeMilliseconds(), etag: "\"j\"");
        harness.Transport.Reply(ErApi, status: 304);
        harness.Transport.Reply(JsDelivr, body: CurrencyApiBody());
        var set = Assert.IsType<ExchangeRateOutcome.Updated>(await harness.Sync.RunAsync("TWD")).Set;
        Assert.Equal("cdn.jsdelivr.net", set.Provider);
        Assert.Null(harness.Transport.Requests[0].IfNoneMatch);
        Assert.Equal("\"j\"", harness.Transport.Requests[1].IfNoneMatch);
        Assert.False(harness.Store.Load()!.ETags.ContainsKey(JsDelivr.AbsoluteUri), "the new response had no ETag");
    }

    [Fact]
    public async Task RatesNeverGoBackInTime()
    {
        using var harness = new Harness();
        harness.StoreRates(Rates(("TWD", 32m)), Providers[1], Noon.ToUnixTimeMilliseconds(), Noon.ToUnixTimeMilliseconds());
        harness.Transport.Reply(ErApi, body: ErApiBody());
        harness.Transport.Reply(JsDelivr, body: CurrencyApiBody(date: "2026-10-21"));
        harness.Transport.Reply(Frankfurter, body: FrankfurterBody("\"TWD\":32.5", date: "2026-10-21"));
        var failed = Assert.IsType<ExchangeRateOutcome.Failed>(await harness.Sync.RunAsync("TWD"));
        Assert.Equal("older than the stored rates", failed.Reason);
    }

    [Fact]
    public async Task InvalidAndOversizedResponsesFallThrough()
    {
        using var harness = new Harness();
        harness.Transport.Reply(ErApi, body: "{\"result\":\"error\"}"u8.ToArray());
        harness.Transport.Reply(JsDelivr, body: new byte[ExchangeRateSync.MaxBytes + 1]);
        harness.Transport.Reply(Pages, body: "not json"u8.ToArray());
        harness.Transport.Fail(Frankfurter, new HttpFetchException(HttpFetchFailure.Transport, "timeout"));
        var failed = Assert.IsType<ExchangeRateOutcome.Failed>(await harness.Sync.RunAsync("TWD"));
        Assert.Equal("timeout", failed.Reason);
        Assert.Equal(4, harness.Transport.Requests.Count);
        Assert.Null(harness.Store.Load());
    }

    [Fact]
    public void ScheduleRules()
    {
        var launch = Noon;
        var now = launch.AddSeconds(5);
        Assert.Equal(launch.AddSeconds(30), ExchangeRateSync.NextAttempt(launch, null, null, now));
        var fresh = launch.AddHours(-1);
        Assert.Equal(fresh + Day, ExchangeRateSync.NextAttempt(launch, fresh, null, now));
        Assert.Equal(now.AddHours(1), ExchangeRateSync.NextAttempt(launch, null, now, now));
        Assert.Equal(now + Day, ExchangeRateSync.NextAttempt(launch, now + 30 * Day, null, now));
        Assert.True(ExchangeRateSync.FetchesOnSelection(null, null, now));
        Assert.False(ExchangeRateSync.FetchesOnSelection(fresh, null, now));
        Assert.True(ExchangeRateSync.FetchesOnSelection(now - Day, null, now));
        Assert.False(ExchangeRateSync.FetchesOnSelection(null, now.AddSeconds(-60), now));
        Assert.True(ExchangeRateSync.FetchesOnSelection(null, now.AddHours(-2), now));
    }

    [Fact]
    public void AnUnreadableStoreIsDiscarded()
    {
        using var directory = new TempDirectory();
        var log = new LogLines();
        var store = new ExchangeRateStore(directory.Path, log.Add);
        Assert.Null(store.Load());
        directory.WriteFile("rates.json", "{");
        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath));
        Assert.Single(log.Lines);
        directory.WriteFile("rates.json", """{"provider":"p","url":"u","date":1,"fetchedMs":2,"etags":{},"rates":{"TWD":"32"}}""");
        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath), "rates without USD are discarded");
    }

    private static byte[] ErApiBody(string twd = "32.41", long unix = 1_792_627_351) => Encoding.UTF8.GetBytes(
        "{\"base_code\":\"USD\",\"rates\":{\"CNY\":6.689327,\"JPY\":158.1,\"TWD\":" + twd + ",\"USD\":1},"
        + "\"result\":\"success\",\"time_last_update_unix\":" + unix.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    private static byte[] CurrencyApiBody(string twd = "32.55", string date = "2026-10-22") => Encoding.UTF8.GetBytes(
        "{\"date\":\"" + date + "\",\"usd\":{\"btc\":0.0000091,\"cny\":6.67476008,\"jpy\":157.7,\"twd\":" + twd + ",\"usd\":1}}");

    private static byte[] FrankfurterBody(string rates, string date = "2026-10-21") => Encoding.UTF8.GetBytes(
        "{\"amount\":1.0,\"base\":\"USD\",\"date\":\"" + date + "\",\"rates\":{" + rates + "}}");

    private static Dictionary<string, decimal> Rates(params (string Code, decimal Rate)[] pairs) => ExchangeRateTests.Rates(pairs);

    private sealed class Harness : IDisposable
    {
        public TempDirectory Directory { get; } = new();

        public ScriptedTransport Transport { get; } = new();

        public TestClock Clock { get; } = new(Noon);

        public LogLines Log { get; } = new();

        public ExchangeRateStore Store => new(Directory.Path);

        public ExchangeRateSync Sync => new(Store, Transport, "AgentSpeed/9.9.9 (Windows)", now: Clock.Function, log: Log.Add);

        public void StoreRates(Dictionary<string, decimal> rates, ExchangeRateProvider provider, long dateMs, long fetchedMs, string? etag = null)
        {
            rates.TryAdd("USD", 1m);
            var set = new ExchangeRateSet(provider.Host, provider.Url.AbsoluteUri, dateMs, fetchedMs, rates);
            var etags = etag is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [provider.Url.AbsoluteUri] = etag };
            Store.Save(StoredRates.From(set, etags));
        }

        public void Dispose() => Directory.Dispose();
    }
}
