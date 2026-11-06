using System.Text.Json;
using AgentSpeed.Core.Devin;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The shared Devin CLI fixtures (<c>spec/fixtures/devin</c>, <c>devin-expected.json</c>, METRICS 4.17) through the live source
/// and the daily history, like <c>devin_reference.py</c>.
/// </summary>
public sealed class DevinFixtureTests : IDisposable
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "devin-expected.json"))));

    private readonly TempDirectory _temp = new();

    public DevinFixtureTests() => CopyDirectory(FixtureLocator.PathOf("devin"), _temp.Combine("devin"));

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Scenarios() => ["one", "two"];

    private static JsonElement Expected => Document.Value.RootElement;

    private static JsonElement Scenario(string name) => Expected.GetProperty("scenarios").GetProperty(name);

    private string PathOf(string relative) => _temp.Combine(relative.Split('/'));

    private string Relative(string path) => Path.GetRelativePath(_temp.Path, path).Replace('\\', '/');

    private string AgentRelative(string agentId)
    {
        var hash = agentId.LastIndexOf('#');
        return Relative(agentId[..hash]) + agentId[hash..];
    }

    private List<string> Locations(JsonElement scenario) =>
        [.. scenario.GetProperty("locations").EnumerateArray().Select(l => PathOf(l.GetString()!))];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheLiveSourceReadsEveryScenario(string name)
    {
        var scenario = Scenario(name);
        var source = new DevinSource([.. Locations(scenario).Select(DevinRoot.Location)], new Deduper());
        // A start of day before every fixture row: the startup window covers them all.
        var (updates, _, _, _) = source.Seed(0);
        Assert.True(source.Available);
        var latest = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var update in updates)
        {
            Assert.True(update.Seeded || update.Responses.Count == 0, "startup responses raise no live event");
            foreach (var (response, _) in update.Responses)
            {
                if (!latest.ContainsKey(response.Key))
                {
                    order.Add(response.Key);
                }

                latest[response.Key] = response;
            }
        }

        var byAgent = order.Select(key => latest[key]).GroupBy(r => AgentRelative(r.AgentId)).ToDictionary(g => g.Key, g => g.ToList());
        var agents = scenario.GetProperty("agents");
        Assert.Equal(agents.EnumerateObject().Select(a => a.Name).Order(StringComparer.Ordinal), byAgent.Keys.Order(StringComparer.Ordinal));
        var states = source.Agents.ToDictionary(a => AgentRelative(a.Id));
        foreach (var agent in agents.EnumerateObject())
        {
            var wanted = agent.Value;
            var responses = wanted.GetProperty("responses").EnumerateArray().ToList();
            var records = byAgent[agent.Name];
            Assert.Equal(responses.Select(r => r.GetProperty("key").GetString()), records.Select(r => r.Key));
            foreach (var (record, want) in records.Zip(responses))
            {
                Compare(record, want);
            }

            var state = states[agent.Name];
            Assert.Equal(wanted.GetProperty("project").GetString(), state.Project);
            Assert.Equal(Text(wanted, "cwdPath"), state.CwdPath);
            Assert.Equal(Text(wanted, "model"), state.Model);
            Assert.Equal(wanted.GetProperty("phase").GetString(), ParserFixtureTests.PhaseName(state.Phase));
            Assert.Equal(Number(wanted, "inFlightStartMs"), state.InFlightStartMs);
            Assert.Equal(Number(wanted, "lastLineMs"), state.LastLineMs);
            Assert.Equal(wanted.GetProperty("malformedLines").GetInt32(), state.MalformedRows);
            Assert.Contains(updates, u => u.Agents.ContainsKey(state.Id));
        }

        Assert.Empty(CreatedFiles());
    }

    [Fact]
    public void LocationsResolveToTheCliDatabaseOnly()
    {
        foreach (var location in Expected.GetProperty("resolve").EnumerateObject())
        {
            var database = DevinRoot.Location(PathOf(location.Name)).DatabaseNow;
            Assert.Equal(Text(Expected.GetProperty("resolve"), location.Name), database is null ? null : Relative(database));
        }

        foreach (var name in new[] { "one", "two" })
        {
            var scenario = Scenario(name);
            Assert.Equal(scenario.GetProperty("databases").EnumerateArray().Select(d => d.GetString()),
                DevinPaths.Databases(Locations(scenario).Select(DevinRoot.Location)).Select(Relative));
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheHistoryMatchesTheScenarioDays(string name)
    {
        var scenario = Scenario(name);
        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var first = Run(analyzer, Locations(scenario));
        AssertDays(analyzer, scenario);
        Assert.True(first.SessionsAnalyzed > 0);
        var second = Run(analyzer, Locations(scenario));
        Assert.Equal((0, 0), (second.FilesAnalyzed, second.SessionsAnalyzed));
        Assert.Empty(CreatedFiles());
    }

    /// <summary>The second database added to a stored history: its copy of a session adds nothing, its own session counts.</summary>
    [Fact]
    public void AddingASecondDatabaseAddsOnlyItsOwnSessions()
    {
        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        Run(analyzer, Locations(Scenario("one")));
        AssertDays(analyzer, Scenario("one"));
        var added = Run(analyzer, Locations(Scenario("two")));
        Assert.Equal(1, added.SessionsAnalyzed);
        AssertDays(analyzer, Scenario("two"));
    }

    private static HistoryReport Run(HistoryAnalyzer analyzer, IEnumerable<string> locations)
    {
        var enabled = new HashSet<SourceKind> { SourceKind.Devin };
        var configuration = new MonitorConfiguration(enabled)
        {
            Locations = LocationLists.Automatic.With(LocationFolder.Devin, [.. locations.Select(DataLocation.At)]),
        };
        return analyzer.Run(SourceLocations.Resolve(configuration), enabled, TimeZoneInfo.Utc, CancellationToken.None);
    }

    private static void AssertDays(HistoryAnalyzer analyzer, JsonElement scenario)
    {
        var days = analyzer.Days(new HashSet<SourceKind> { SourceKind.Devin });
        var wanted = scenario.GetProperty("history");
        Assert.Equal(wanted.EnumerateObject().Select(d => d.Name).Order(StringComparer.Ordinal), days.Select(d => d.Day));
        foreach (var day in days)
        {
            var want = wanted.GetProperty(day.Day);
            Assert.Equal(want.GetProperty("requests").GetInt64(), day.Totals.Requests);
            Assert.Equal(want.GetProperty("input").GetInt64(), day.Totals.Input);
            Assert.Equal(want.GetProperty("cachedInput").GetInt64(), day.Totals.CachedInput);
            Assert.Equal(want.GetProperty("cacheWriteInput").GetInt64(), day.Totals.CacheWriteInput);
            Assert.Equal(want.GetProperty("output").GetInt64(), day.Totals.Output);
            Assert.Equal(want.GetProperty("reasoning").GetInt64(), day.Totals.Reasoning);
            Assert.Equal(want.GetProperty("measuredOutput").GetInt64(), day.MeasuredOutput);
            Assert.Equal(want.GetProperty("measuredMs").GetInt64(), day.MeasuredMs);
            Assert.Equal(want.GetProperty("agents").GetInt32(), day.Agents);
            var models = want.GetProperty("models").EnumerateObject()
                .ToDictionary(m => m.Name, m => (m.Value.GetProperty("requests").GetInt64(), m.Value.GetProperty("output").GetInt64()));
            Assert.Equal(models.OrderBy(m => m.Key, StringComparer.Ordinal),
                day.Models.ToDictionary(m => m.Model, m => (m.Requests, m.Output)).OrderBy(m => m.Key, StringComparer.Ordinal));
        }
    }

    private void Compare(ParsedResponse record, JsonElement want)
    {
        var label = record.Key;
        Assert.True(want.GetProperty("agentId").GetString() == AgentRelative(record.AgentId), label + " agent");
        Assert.True(Number(want, "startMs") == record.StartMs, label + " start");
        Assert.True(want.GetProperty("endMs").GetInt64() == record.EndMs, label + " end");
        Assert.True(Number(want, "firstMs") == record.FirstMs, label + " first");
        Assert.Equal(
            (want.GetProperty("input").GetInt64(), want.GetProperty("cachedInput").GetInt64(), want.GetProperty("cacheWriteInput").GetInt64(),
                want.GetProperty("cacheWrite1hInput").GetInt64(), want.GetProperty("output").GetInt64(), want.GetProperty("reasoning").GetInt64()),
            (record.Tokens.Input, record.Tokens.CachedInput, record.Tokens.CacheWriteInput, record.Tokens.CacheWrite1hInput, record.Tokens.Output,
                record.Tokens.Reasoning));
        Assert.Equal(Text(want, "model"), record.Model);
        Assert.Equal(Text(want, "provider"), record.Provider);
        Assert.Equal((want.GetProperty("final").GetBoolean(), want.GetProperty("fast").GetBoolean()), (record.Final, record.Fast));
        Assert.Equal((1L, SourceKind.Devin), (record.Requests, record.Source));
    }

    private static string? Text(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static long? Number(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetInt64();

    /// <summary>Reading never creates a <c>-wal</c>, <c>-shm</c> or journal next to a fixture database.</summary>
    private List<string> CreatedFiles() =>
        [.. Directory.EnumerateFiles(_temp.Combine("devin"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith("-wal", StringComparison.Ordinal) || f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("-journal", StringComparison.Ordinal))];

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }
}
