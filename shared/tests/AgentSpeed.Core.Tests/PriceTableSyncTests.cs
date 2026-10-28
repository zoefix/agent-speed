using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Core.Tests;

public sealed class PriceTableSyncTests
{
    private static readonly Uri Raw = PriceTableSync.Urls[0];
    private static readonly Uri Mirror = PriceTableSync.Urls[1];

    /// <summary>2026-10-22T12:00Z, the reference clock for synchronization and future-date checks.</summary>
    private static readonly DateTimeOffset Noon = DateTimeOffset.FromUnixTimeSeconds(1_792_670_400);

    private static readonly Lazy<string> Bundled = new(() => Encoding.UTF8.GetString(RepoSpec.Embedded("AgentSpeed.Prices.json")));

    [Fact]
    public void UrlsAndHostsAreTheSpecs()
    {
        Assert.Equal(
            [
                "https://raw.githubusercontent.com/zoefix/agent-speed/main/spec/assets/prices.json",
                "https://cdn.jsdelivr.net/gh/zoefix/agent-speed@main/spec/assets/prices.json",
            ],
            PriceTableSync.Urls.Select(u => u.AbsoluteUri));
        Assert.True(PriceTableSync.AllowedHosts.SetEquals(["raw.githubusercontent.com", "cdn.jsdelivr.net"]));
        Assert.Equal("https://github.com/zoefix/agent-speed/blob/main/spec/assets/prices.json", PriceTableSync.SourcePage.AbsoluteUri);
    }

    [Fact]
    public void TheEmbeddedPriceTableIsTheSpecAsset() =>
        Assert.Equal(File.ReadAllBytes(RepoSpec.PathOf("assets/prices.json")), RepoSpec.Embedded("AgentSpeed.Prices.json"));

    [Fact]
    public async Task ANewerTableIsStoredAndDelivered()
    {
        using var harness = new Harness();
        harness.Transport.Reply(Raw, body: Table(version: 2), etag: "\"v2\"");
        var result = await harness.Sync.RunAsync(currentVersion: 1);
        Assert.Equal(new PriceSyncOutcome.Updated(2), result.Outcome);
        Assert.Equal(2, result.Table?.Version);
        Assert.Equal([Raw.AbsoluteUri], harness.Transport.RequestedUrls);
        var request = harness.Transport.Requests[0];
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("AgentSpeed/9.9.9 (Windows)", request.UserAgent);
        Assert.Null(request.IfNoneMatch);
        Assert.Equal(PriceTable.MaxBytes, request.MaxBytes);
        Assert.True(request.AllowedHosts.SetEquals(["raw.githubusercontent.com", "cdn.jsdelivr.net"]));
        Assert.Equal(2, harness.Store.LoadTable()?.Version);
        var state = harness.Store.LoadState();
        Assert.Equal(2, state.Version);
        Assert.Equal("\"v2\"", state.ETag);
        Assert.Equal(Raw.AbsoluteUri, state.Url);
        Assert.Equal(1_792_670_400_000, state.LastSuccessMs);
        Assert.Equal(0, state.Failures);
        Assert.Equal(state, result.State);
        Assert.Empty(Directory.GetFiles(harness.Store.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task TheFixtureTablesSyncInOrder()
    {
        using var harness = new Harness(Noon.AddDays(-7));
        harness.Transport.Reply(Raw, body: File.ReadAllBytes(FixtureLocator.PathOf("prices/v2.json")));
        Assert.Equal(new PriceSyncOutcome.Updated(2), (await harness.Sync.RunAsync(currentVersion: 1)).Outcome);
        Assert.Equal("2026-10-15", harness.Store.LoadTable()?.Updated);
        harness.Transport.Reply(Raw, body: File.ReadAllBytes(FixtureLocator.PathOf("prices/v1.json")));
        var older = await harness.Sync.RunAsync(currentVersion: 2);
        Assert.Equal(new PriceSyncOutcome.UpToDate(), older.Outcome);
        Assert.Null(older.Table);
        Assert.Equal(2, harness.Store.LoadTable()?.Version);
    }

    [Fact]
    public async Task TheMirrorIsTriedWhenGitHubFails()
    {
        var failures = new Action<ScriptedTransport>[]
        {
            t => t.Fail(Raw, new HttpFetchException(HttpFetchFailure.Transport, "ConnectionError")),
            t => t.Reply(Raw, status: 404),
            t => t.Reply(Raw, body: "not json"u8.ToArray()),
        };
        foreach (var fail in failures)
        {
            using var harness = new Harness();
            fail(harness.Transport);
            harness.Transport.Reply(Mirror, body: Table(version: 3));
            var result = await harness.Sync.RunAsync(currentVersion: 1);
            Assert.Equal(new PriceSyncOutcome.Updated(3), result.Outcome);
            Assert.Equal([Raw.AbsoluteUri, Mirror.AbsoluteUri], harness.Transport.RequestedUrls);
            Assert.Equal(Mirror.AbsoluteUri, harness.Store.LoadState().Url);
        }
    }

    [Fact]
    public async Task AStoredETagTurnsTheNextDayIntoA304()
    {
        using var harness = new Harness();
        harness.Transport.Reply(Raw, body: Table(version: 2), etag: "\"v2\"");
        await harness.Sync.RunAsync(currentVersion: 1);
        harness.Clock.Advance(PriceTableSync.Interval);
        harness.Transport.Reply(Raw, status: 304);
        var result = await harness.Sync.RunAsync(currentVersion: 2);
        Assert.Equal(new PriceSyncOutcome.UpToDate(), result.Outcome);
        Assert.Equal("\"v2\"", harness.Transport.Requests[^1].IfNoneMatch);
        Assert.Equal(1_792_756_800_000, harness.Store.LoadState().LastSuccessMs);
        Assert.Equal(2, harness.Store.LoadTable()?.Version);
    }

    [Fact]
    public async Task AnETagIsNotSentWhenItsTableIsNewerThanOurs()
    {
        using var harness = new Harness();
        harness.Store.SaveState(new PriceSyncState { Version = 5, Url = Raw.AbsoluteUri, ETag = "\"v5\"" });
        harness.Transport.Reply(Raw, body: Table(version: 5));
        var result = await harness.Sync.RunAsync(currentVersion: 1);
        Assert.Null(harness.Transport.Requests[0].IfNoneMatch);
        Assert.Equal(new PriceSyncOutcome.Updated(5), result.Outcome);
    }

    [Fact]
    public async Task EqualOrLowerVersionsChangeNothing()
    {
        foreach (var version in new[] { 1, 2 })
        {
            using var harness = new Harness();
            harness.Transport.Reply(Raw, body: Table(version: version));
            var result = await harness.Sync.RunAsync(currentVersion: 2);
            Assert.Equal(new PriceSyncOutcome.UpToDate(), result.Outcome);
            Assert.Null(result.Table);
            Assert.Null(harness.Store.LoadTable());
            Assert.Equal([Raw.AbsoluteUri], harness.Transport.RequestedUrls);
            Assert.Equal(version, harness.Store.LoadState().Version);
        }
    }

    [Fact]
    public async Task ANewerSchemaIsNotAnError()
    {
        using var harness = new Harness();
        harness.Transport.Reply(Raw, body: Table(version: 9, schema: 2));
        harness.Transport.Reply(Mirror, body: Table(version: 9, schema: 2));
        var result = await harness.Sync.RunAsync(currentVersion: 1);
        Assert.Equal(new PriceSyncOutcome.NewerSchema(), result.Outcome);
        Assert.Equal(0, harness.Store.LoadState().Failures);
        Assert.Null(harness.Store.LoadTable());
    }

    [Fact]
    public async Task ALaggingMirrorStillServesTheCurrentSchema()
    {
        using var harness = new Harness();
        harness.Transport.Reply(Raw, body: Table(version: 9, schema: 2));
        harness.Transport.Reply(Mirror, body: Table(version: 4));
        Assert.Equal(new PriceSyncOutcome.Updated(4), (await harness.Sync.RunAsync(currentVersion: 1)).Outcome);
    }

    [Fact]
    public async Task TablesDatedMoreThanTwoDaysAheadAreRejected()
    {
        using (var harness = new Harness())
        {
            harness.Transport.Reply(Raw, body: Table(version: 2, updated: "2026-10-25"));
            harness.Transport.Reply(Mirror, body: Table(version: 2, updated: "2026-10-24"));
            var result = await harness.Sync.RunAsync(currentVersion: 1);
            Assert.Equal(new PriceSyncOutcome.Updated(2), result.Outcome);
            Assert.Equal(2, harness.Transport.Requests.Count);
        }

        using var late = new Harness();
        late.Transport.Reply(Raw, body: Table(version: 2, updated: "2026-10-25"));
        late.Transport.Reply(Mirror, body: Table(version: 2, updated: "2026-10-25"));
        var failed = Assert.IsType<PriceSyncOutcome.Failed>((await late.Sync.RunAsync(currentVersion: 1)).Outcome);
        Assert.Equal("table dated in the future", failed.Reason);
    }

    [Fact]
    public void TheLocalDateDecidesHowFarAheadIsTooFar()
    {
        var store = new PriceTableStore(Path.GetTempPath());
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        var honolulu = TimeZoneInfo.CreateCustomTimeZone("UTC-10", TimeSpan.FromHours(-10), "UTC-10", "UTC-10");
        var lateEvening = DateTimeOffset.Parse("2026-10-22T20:00:00Z", CultureInfo.InvariantCulture);
        var east = new PriceTableSync(store, new ScriptedTransport(), "ua", tokyo);
        var west = new PriceTableSync(store, new ScriptedTransport(), "ua", honolulu);
        Assert.True(east.IsAcceptableUpdated("2026-10-25", lateEvening), "already 10-23 in Tokyo");
        Assert.False(west.IsAcceptableUpdated("2026-10-25", lateEvening));
        Assert.True(west.IsAcceptableUpdated("2026-10-24", lateEvening));
    }

    [Fact]
    public async Task OversizedAndInvalidDownloadsFailTheRound()
    {
        using var harness = new Harness();
        harness.Transport.Reply(Raw, body: new byte[PriceTable.MaxBytes + 1]);
        var broken = JsonNode.Parse(Table(version: 2))!.AsObject();
        var model = broken["models"]!.AsArray().First(m => m!["prices"] is JsonObject)!;
        model["prices"]!["input"] = 4.0000001;
        harness.Transport.Reply(Mirror, body: Encoding.UTF8.GetBytes(broken.ToJsonString()));
        var result = await harness.Sync.RunAsync(currentVersion: 1);
        var failed = Assert.IsType<PriceSyncOutcome.Failed>(result.Outcome);
        Assert.Equal("invalid table", failed.Reason);
        var state = harness.Store.LoadState();
        Assert.Equal(1, state.Failures);
        Assert.Equal("invalid table", state.LastError);
        Assert.Null(state.LastSuccessMs);
        Assert.Equal(1_792_670_400_000, state.LastAttemptMs);
        Assert.Null(harness.Store.LoadTable());
    }

    [Fact]
    public void TheStoredCopyIsValidatedAgain()
    {
        using var directory = new TempDirectory();
        var log = new LogLines();
        var store = new PriceTableStore(directory.Path, log.Add);
        Assert.Null(store.LoadTable());
        store.SaveTable("{\"schema\": 1}"u8);
        Assert.Null(store.LoadTable());
        Assert.False(File.Exists(store.TablePath), "an invalid copy is deleted");
        Assert.Single(log.Lines);
        store.SaveTable(Table(version: 7));
        Assert.Equal(7, store.LoadTable()?.Version);
        Assert.Equal(Path.Combine(directory.Path, "prices", "prices.json"), store.TablePath);
        Assert.Equal(Path.Combine(directory.Path, "prices", "sync.json"), store.StatePath);
    }

    [Fact]
    public void ScheduleFollowsLaunchDailyAndRetryRules()
    {
        var launch = Noon;
        var now = launch.AddSeconds(10);
        static long Ms(DateTimeOffset date) => date.ToUnixTimeMilliseconds();
        Assert.Equal(launch.AddSeconds(60), PriceTableSync.NextAttempt(launch, new PriceSyncState(), now));
        var synced = new PriceSyncState { LastSuccessMs = Ms(launch.AddHours(-1)), LastAttemptMs = Ms(launch.AddHours(-1)) };
        Assert.Equal(launch.AddHours(23), PriceTableSync.NextAttempt(launch, synced, now));
        var old = new PriceSyncState { LastSuccessMs = Ms(launch.AddDays(-3)) };
        Assert.Equal(launch.AddSeconds(60), PriceTableSync.NextAttempt(launch, old, now));
        var failedOnce = new PriceSyncState { LastAttemptMs = Ms(now), Failures = 1 };
        Assert.Equal(now.AddHours(1), PriceTableSync.NextAttempt(launch, failedOnce, now));
        var failedTwice = new PriceSyncState { LastAttemptMs = Ms(now), Failures = 2 };
        Assert.Equal(now.AddDays(1), PriceTableSync.NextAttempt(launch, failedTwice, now));
        var future = new PriceSyncState { LastSuccessMs = Ms(now.AddDays(10)) };
        Assert.Equal(now.AddDays(1), PriceTableSync.NextAttempt(launch, future, now));
        var corrupt = new PriceSyncState { LastSuccessMs = long.MinValue };
        Assert.Equal(launch.AddSeconds(60), PriceTableSync.NextAttempt(launch, corrupt, now));
    }

    [Fact]
    public async Task AFailedRoundThenASuccessResetsTheRetryCount()
    {
        using var harness = new Harness();
        var first = await harness.Sync.RunAsync(currentVersion: 1);
        Assert.Equal(new PriceSyncOutcome.Failed("offline"), first.Outcome);
        Assert.Equal(1, harness.Store.LoadState().Failures);
        await harness.Sync.RunAsync(currentVersion: 1);
        Assert.Equal(2, harness.Store.LoadState().Failures);
        harness.Transport.Reply(Raw, body: Table(version: 1));
        Assert.Equal(new PriceSyncOutcome.UpToDate(), (await harness.Sync.RunAsync(currentVersion: 1)).Outcome);
        Assert.Equal(0, harness.Store.LoadState().Failures);
        Assert.Null(harness.Store.LoadState().LastError);
    }

    [Fact]
    public void SyncStateDecodesWithoutTheRetryCount()
    {
        var json = """{"version":3,"updated":"2026-10-21","url":"u","etag":"e","lastSuccessMs":1,"lastAttemptMs":2,"lastError":null}"""u8;
        Assert.Equal(
            new PriceSyncState { Version = 3, Updated = "2026-10-21", Url = "u", ETag = "e", LastSuccessMs = 1, LastAttemptMs = 2 },
            PriceSyncState.FromJson(json));
        Assert.Null(PriceSyncState.FromJson("""{"version":"3"}"""u8));
        Assert.Null(PriceSyncState.FromJson("[]"u8));
    }

    [Fact]
    public void SyncStateIsWrittenWithSortedKeysAndWithoutNulls()
    {
        using var directory = new TempDirectory();
        var store = new PriceTableStore(directory.Path);
        var state = new PriceSyncState { Version = 2, ETag = "\"v2\"", LastAttemptMs = 5, Failures = 1, LastError = "HTTP 404" };
        store.SaveState(state);
        using var document = JsonDocument.Parse(File.ReadAllBytes(store.StatePath));
        Assert.Equal(["etag", "failures", "lastAttemptMs", "lastError", "version"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(state, store.LoadState());
        File.WriteAllText(store.StatePath, "{");
        Assert.Equal(new PriceSyncState(), store.LoadState());
    }

    /// <summary>The bundled contents with explicit test metadata, independent of future bundled-table updates.</summary>
    private static byte[] Table(int version, int schema = 1, string? updated = null)
    {
        var table = JsonNode.Parse(Bundled.Value)!.AsObject();
        var date = updated ?? Noon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        table["schema"] = schema;
        table["version"] = version;
        table["updated"] = date;
        foreach (var model in table["models"]!.AsArray().Select(m => m!.AsObject()))
        {
            if (string.CompareOrdinal((string?)model["checked"], date) > 0)
            {
                model["checked"] = date;
            }
        }

        return Encoding.UTF8.GetBytes(table.ToJsonString());
    }

    private sealed class Harness(DateTimeOffset? start = null) : IDisposable
    {
        public TempDirectory Directory { get; } = new();

        public ScriptedTransport Transport { get; } = new();

        public TestClock Clock { get; } = new(start ?? Noon);

        public PriceTableStore Store => new(Directory.Path);

        public PriceTableSync Sync => new(Store, Transport, "AgentSpeed/9.9.9 (Windows)", TimeZoneInfo.Utc, Clock.Function);

        public void Dispose() => Directory.Dispose();
    }
}
