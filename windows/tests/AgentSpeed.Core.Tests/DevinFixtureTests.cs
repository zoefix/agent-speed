using System.Text.Json;
using AgentSpeed.Core.Devin;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class DevinFixtureTests
{
    private const long DayStart = 1_792_454_400_000;
    private const long Now = 1_792_497_700_000;

    [Fact]
    public void SharedFixtureCombinesCLIAndDesktopWithoutDoubleCounting()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("devin-expected.json")));
        var source = new DevinSource(DevinPaths.Resolve(FixtureLocator.PathOf("devin")), new Deduper());
        var update = source.Seed(DayStart);
        var rows = expected.RootElement.GetProperty("responses").EnumerateArray().ToList();
        Assert.Equal(rows.Select(row => row.GetProperty("key").GetString()), update.Responses.Select(item => item.Response.Key));
        foreach (var (row, item) in rows.Zip(update.Responses)) AssertResponse(row, item.Response);
        Assert.Equal(expected.RootElement.GetProperty("agents").EnumerateObject().Count(), update.Agents.Count);
        Assert.Equal(2, source.MalformedLines);
        Assert.DoesNotContain(update.Responses, item => item.Response.AgentId.EndsWith("desktop-mirror.ndjson", StringComparison.Ordinal));
        Assert.False(source.RefreshIfChanged().Changed);
        Assert.Equal(0, source.ActiveSessionCount(Now + 100_000_000_000));
    }

    [Fact]
    public void LiveAndPersistentHistoryShareTotals()
    {
        var locations = new SourceLocations("", "", "", "", [], DevinRoot: FixtureLocator.PathOf("devin"));
        var enabled = new HashSet<SourceKind> { SourceKind.Devin };
        using (var core = new MonitorCore(new MonitorConfiguration(enabled), locations, DayStart))
        {
            core.Seed(Now, DayStart, CancellationToken.None);
            var (snapshot, _) = core.BuildSnapshot(Now, DayStart, MonitorState.Running, observeLive: false);
            Assert.Equal(7, snapshot.Today.Requests);
            Assert.Equal(424, snapshot.Today.Input);
            Assert.Equal(98, snapshot.Today.Output);
            Assert.Equal(81.0 / 6.5, snapshot.AverageToday);
            Assert.Equal(6, core.SessionCount(SourceKind.Devin, DayStart));
        }

        using var temp = new TempDirectory();
        var path = temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(path))
        {
            var history = new HistoryAnalyzer(store);
            var report = history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal(0, report.FilesUnreadable);
            var day = Assert.Single(history.Days());
            Assert.Equal(SourceKind.Devin, day.Source);
            Assert.Equal(7, day.Totals.Requests);
            Assert.Equal(424, day.Totals.Input);
            Assert.Equal(98, day.Totals.Output);
            Assert.Equal(81, day.Totals.MeasuredOutput);
            Assert.Equal(6500, day.Totals.MeasuredMs);
            Assert.Equal(0, history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None).FilesPlanned);
        }
    }

    private static void AssertResponse(JsonElement row, ParsedResponse response)
    {
        var start = row.GetProperty("startMs");
        Assert.Equal(start.ValueKind == JsonValueKind.Null ? null : start.GetInt64(), response.StartMs);
        Assert.Equal(row.GetProperty("endMs").GetInt64(), response.EndMs);
        Assert.Equal(row.GetProperty("input").GetInt64(), response.Tokens.Input);
        Assert.Equal(row.GetProperty("cachedInput").GetInt64(), response.Tokens.CachedInput);
        Assert.Equal(row.GetProperty("cacheWriteInput").GetInt64(), response.Tokens.CacheWriteInput);
        Assert.Equal(row.GetProperty("output").GetInt64(), response.Tokens.Output);
        Assert.Equal(0, response.Tokens.Reasoning);
        Assert.Equal(row.GetProperty("model").GetString(), response.Model);
        Assert.Equal(FixtureLocator.PathOf(row.GetProperty("agentId").GetString()!), response.AgentId);
        Assert.Equal(SourceKind.Devin, response.Source);
    }
}
