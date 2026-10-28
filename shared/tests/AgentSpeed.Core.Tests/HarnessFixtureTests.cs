using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.GrokBuild;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Kimi;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.ZCode;

namespace AgentSpeed.Core.Tests;

public sealed class HarnessFixtureTests
{
    private static JsonDocument Expected() => JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("harness-expected.json")));

    [Fact]
    public void ZCodeNativeDatabaseMatchesEverySharedRequestAndStaysReadOnly()
    {
        using var expected = Expected();
        var section = expected.RootElement.GetProperty("zcode");
        var path = FixtureLocator.PathOf(section.GetProperty("database").GetString()!);
        var before = File.ReadAllBytes(path);
        var folder = Path.GetDirectoryName(path)!;
        var names = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToArray();
        using (var database = ZCodeDatabase.Open(path))
        {
            var rows = database.Read();
            foreach (var session in section.GetProperty("sessions").EnumerateArray())
            {
                var id = session.GetProperty("id").GetString()!;
                var actual = rows.Where(row => row.SessionId == id).Select(row => row.Response(path)).OfType<ParsedResponse>().ToList();
                AssertResponses(session.GetProperty("responses"), actual);
                Assert.All(actual, response => Assert.Equal(path + "#" + id, response.AgentId));
            }
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(names, Directory.GetFiles(folder).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KimiNativeWireVariantsAndForkMirrorsShareOneRequestIdentity()
    {
        using var expected = Expected();
        var deduper = new Deduper();
        foreach (var file in expected.RootElement.GetProperty("kimi").GetProperty("files").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(file.GetProperty("path").GetString()!);
            var parser = new KimiParser(path, deduper);
            var sink = new RecordingSink();
            var batch = parser.NewBatch();
            new FileTailer(path).ReadAppended(batch);
            batch.ApplyTo(sink);
            AssertResponses(file.GetProperty("responses"), sink.Latest);
            Assert.Equal(file.GetProperty("project").GetString(), parser.Project);
            Assert.Equal(file.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
            var parent = file.GetProperty("parentFile").GetString();
            Assert.Equal(parent is null ? null : FixtureLocator.PathOf(parent), parser.ParentPath);
            Assert.Equal(sink.Latest.Count, sink.Emissions.Count(e => e.IsCompletion));
        }
    }


    [Fact]
    public void GrokNativeBatchesExcludeChildRollupsAndForkCopies()
    {
        using var expected = Expected();
        var deduper = new Deduper();
        foreach (var file in expected.RootElement.GetProperty("grok").GetProperty("files").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(file.GetProperty("path").GetString()!);
            var parser = new GrokParser(path, deduper);
            var sink = new RecordingSink();
            var batch = parser.NewBatch();
            new FileTailer(path).ReadAppended(batch);
            batch.ApplyTo(sink);
            AssertResponses(file.GetProperty("responses"), sink.Latest);
            Assert.Equal(file.GetProperty("project").GetString(), parser.Project);
            Assert.Equal(file.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
            var parent = file.GetProperty("parentFile").GetString();
            Assert.Equal(parent is null ? null : FixtureLocator.PathOf(parent), parser.ParentPath);
        }
    }

    [Fact]
    public void AllHarnessesReachTheLiveMonitorAndPersistentHistoryWithoutChangingEngineMath()
    {
        using var temp = new TempDirectory();
        using var expected = Expected();
        var zcode = expected.RootElement.GetProperty("zcode");
        var kimi = expected.RootElement.GetProperty("kimi");
        var grok = expected.RootElement.GetProperty("grok");
        var paths = new SourceLocations("", "", "", "", [],
            ZCodeRoot: FixtureLocator.PathOf(zcode.GetProperty("database").GetString()!),
            KimiRoot: FixtureLocator.PathOf(kimi.GetProperty("root").GetString()!),
            GrokBuildRoot: FixtureLocator.PathOf(grok.GetProperty("root").GetString()!));
        var enabled = new HashSet<SourceKind> { SourceKind.ZCode, SourceKind.Kimi, SourceKind.GrokBuild };
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        using (var core = new MonitorCore(new MonitorConfiguration(enabled), paths, start))
        {
            core.Seed(start + 30_000_000, start, CancellationToken.None);
            var (snapshot, _) = core.BuildSnapshot(start + 30_000_000, start, MonitorState.Running, observeLive: false);
            Assert.Equal(12, snapshot.Today.Requests);
            Assert.Equal(1475, snapshot.Today.Output);
            Assert.Equal(2, core.SessionCount(SourceKind.ZCode, start));
            Assert.Equal(2, core.SessionCount(SourceKind.Kimi, start));
            Assert.Contains(snapshot.Agents, a => a.Source == SourceKind.Kimi && a.IsSubagent && a.ParentId is not null);
            Assert.Contains(snapshot.Agents, a => a.Source == SourceKind.ZCode && a.IsSubagent && a.ParentId is not null);
        }

        var storePath = temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            var report = history.Run(paths, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal(0, report.FilesUnreadable);
            Assert.Equal(12, history.Days().Sum(day => day.Totals.Requests));
            Assert.Equal(1475, history.Days().Sum(day => day.Totals.Output));
            var repeated = history.Run(paths, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal(0, repeated.FilesPlanned);
        }

        using (var store = HistoryStore.Open(storePath))
        {
            Assert.Equal(1475, new HistoryAnalyzer(store).Days().Sum(day => day.Totals.Output));
        }
    }

    private static void AssertResponses(JsonElement expected, IReadOnlyList<ParsedResponse> responses)
    {
        var rows = expected.EnumerateArray().ToList();
        Assert.Equal(rows.Select(row => row.GetProperty("key").GetString()), responses.Select(r => r.Key));
        foreach (var (row, response) in rows.Zip(responses))
        {
            var start = row.GetProperty("startMs");
            Assert.Equal(start.ValueKind == JsonValueKind.Null ? (long?)null : start.GetInt64(), response.StartMs);
            Assert.Equal(row.GetProperty("endMs").GetInt64(), response.EndMs);
            Assert.Equal(row.GetProperty("input").GetInt64(), response.Tokens.Input);
            Assert.Equal(row.GetProperty("cachedInput").GetInt64(), response.Tokens.CachedInput);
            Assert.Equal(row.GetProperty("cacheWriteInput").GetInt64(), response.Tokens.CacheWriteInput);
            Assert.Equal(row.GetProperty("output").GetInt64(), response.Tokens.Output);
            Assert.Equal(row.GetProperty("reasoning").GetInt64(), response.Tokens.Reasoning);
            Assert.Equal(row.GetProperty("model").GetString(), response.Model);
            Assert.Equal(row.GetProperty("provider").GetString(), response.Provider);
            Assert.Equal(row.GetProperty("final").GetBoolean(), response.Final);
            Assert.Equal(row.GetProperty("requestCount").GetInt64(), response.Requests);
        }
    }
}
