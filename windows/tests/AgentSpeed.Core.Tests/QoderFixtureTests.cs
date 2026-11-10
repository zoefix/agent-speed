using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Qoder;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The shared Qoder fixtures (<c>spec/fixtures/qoder</c>, <c>qoder-expected.json</c>, METRICS 4.18 and 4.19) through the live
/// source and the daily history, like <c>qoder_reference.py</c>; the Qoder CLI files' discovery and traces of
/// parser-expected.json <c>qoderCli</c>.
/// </summary>
public sealed class QoderFixtureTests : IDisposable
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "qoder-expected.json"))));

    private static readonly Func<string, string?> NoVariables = _ => null;

    private readonly TempDirectory _temp = new();

    public QoderFixtureTests() => CopyDirectory(FixtureLocator.PathOf("qoder"), _temp.Combine("qoder"));

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Scenarios() => ["one", "two", "startup"];

    public static TheoryData<string> HistoryScenarios() => ["one", "two"];

    public static TheoryData<string, int, string, long?> CliTraces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in QoderCliFixture.Expected.GetProperty("traces").EnumerateArray())
        {
            data.Add(trace.GetProperty("file").GetString()!, trace.GetProperty("afterLine").GetInt32(), trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    private static JsonElement Expected => Document.Value.RootElement;

    private static JsonElement Scenario(string name) => Expected.GetProperty("scenarios").GetProperty(name);

    private string PathOf(string relative) => _temp.Combine(relative.Split('/'));

    private string Relative(string path) => Path.GetRelativePath(_temp.Path, path).Replace('\\', '/');

    private string? AgentRelative(string? agentId)
    {
        if (agentId is null)
        {
            return null;
        }

        var hash = agentId.LastIndexOf('#');
        return Relative(agentId[..hash]) + agentId[hash..];
    }

    /// <summary>A scenario's roots: <c>@auto</c> is the automatic entry with its <c>home</c> as the macOS home folder.</summary>
    private List<QoderRoot> Roots(JsonElement scenario)
    {
        var home = scenario.TryGetProperty("home", out var value) ? PathOf(value.GetString()!) : _temp.Combine("no-home");
        var roots = new List<QoderRoot>();
        foreach (var location in scenario.GetProperty("locations").EnumerateArray().Select(l => l.GetString()!))
        {
            roots.AddRange(location == "@auto"
                ? QoderPaths.AutomaticDatabases(NoVariables, windows: false, macOS: true, home).Select(QoderRoot.Database)
                : [QoderRoot.Location(PathOf(location))]);
        }

        return roots;
    }

    [Fact]
    public void TheAutomaticEntryNamesOneDatabasePerEdition()
    {
        foreach (var testCase in Expected.GetProperty("automatic").EnumerateArray())
        {
            var platform = testCase.GetProperty("platform").GetString();
            var env = testCase.GetProperty("env").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString());
            var databases = QoderPaths.AutomaticDatabases(key => env.GetValueOrDefault(key), platform == "windows", platform == "macos",
                testCase.GetProperty("home").GetString()!);
            Assert.Equal(testCase.GetProperty("databases").EnumerateArray().Select(d => d.GetString()), databases);
        }
    }

    [Fact]
    public void LocationsResolveToTheChatDatabaseOnly()
    {
        foreach (var location in Expected.GetProperty("resolve").EnumerateObject())
        {
            var database = QoderRoot.Location(PathOf(location.Name)).DatabaseNow;
            Assert.True(Text(Expected.GetProperty("resolve"), location.Name) == (database is null ? null : Relative(database)), location.Name);
        }

        foreach (var name in new[] { "one", "two", "startup" })
        {
            var scenario = Scenario(name);
            Assert.Equal(scenario.GetProperty("databases").EnumerateArray().Select(d => d.GetString()),
                QoderPaths.Databases(Roots(scenario)).Select(Relative));
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheLiveSourceReadsEveryScenario(string name)
    {
        var scenario = Scenario(name);
        var source = new QoderSource(Roots(scenario), new Deduper());
        // Without a start of day the startup window covers every fixture row, as the history reads them.
        var todayStartMs = scenario.TryGetProperty("todayStartMs", out var start) ? start.GetInt64() : 0;
        var (updates, _, _, _) = source.Seed(todayStartMs);
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

        var unsupported = source.Readers.Where(r => r.Status == QoderStatus.Unsupported).Select(r => Relative(r.Path));
        Assert.Equal(scenario.TryGetProperty("unsupported", out var listed) ? listed.EnumerateArray().Select(u => u.GetString()!) : [], unsupported);
        Assert.Equal(scenario.TryGetProperty("unsupported", out _), source.Unsupported);

        var byAgent = order.Select(key => latest[key]).GroupBy(r => AgentRelative(r.AgentId)!).ToDictionary(g => g.Key, g => g.ToList());
        var states = source.Agents.ToDictionary(a => AgentRelative(a.Id)!);
        var parents = ParentLinks.Resolve(
            [.. source.Agents.Select(a => new AgentLink(a.Id, LinkKind.QoderSession, a.Id, a.ParentId, null))], StringComparer.Ordinal);
        var agents = scenario.GetProperty("agents");
        Assert.Equal(agents.EnumerateObject().Select(a => a.Name).Order(StringComparer.Ordinal), states.Keys.Order(StringComparer.Ordinal));
        foreach (var agent in agents.EnumerateObject())
        {
            var wanted = agent.Value;
            var responses = wanted.GetProperty("responses").EnumerateArray().ToList();
            var records = byAgent.GetValueOrDefault(agent.Name) ?? [];
            Assert.Equal(responses.Select(r => r.GetProperty("key").GetString()), records.Select(r => r.Key));
            foreach (var (record, want) in records.Zip(responses))
            {
                Compare(record, want);
            }

            var state = states[agent.Name];
            var label = name + " " + agent.Name;
            Assert.True(wanted.GetProperty("project").GetString() == state.Project, label + " project");
            Assert.True(Text(wanted, "cwdPath") == state.CwdPath, label + " cwd");
            Assert.True(Text(wanted, "model") == state.Model, label + " model");
            Assert.True(wanted.GetProperty("isSubagent").GetBoolean() == state.IsSubagent, label + " sub-agent");
            Assert.True(Text(wanted, "parentId") == AgentRelative(parents.GetValueOrDefault(state.Id)), label + " parent");
            Assert.True(wanted.GetProperty("phase").GetString() == ParserFixtureTests.PhaseName(state.Phase), label + " phase");
            Assert.True(Number(wanted, "inFlightStartMs") == state.InFlightStartMs, label + " in flight");
            Assert.True(Number(wanted, "lastLineMs") == state.LastLineMs, label + " last line");
            Assert.True(wanted.GetProperty("malformedLines").GetInt32() == state.MalformedRows, label + " malformed");
            Assert.Contains(updates, u => u.Agents.ContainsKey(state.Id));
        }

        Assert.Empty(CreatedFiles());
    }

    [Theory]
    [MemberData(nameof(HistoryScenarios))]
    public void TheHistoryMatchesTheScenarioDays(string name)
    {
        var scenario = Scenario(name);
        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var first = Run(analyzer, Roots(scenario));
        AssertDays(analyzer, scenario);
        Assert.True(first.SessionsAnalyzed > 0);
        foreach (var session in scenario.GetProperty("sessions").EnumerateObject())
        {
            var entry = analyzer.Entries.Single(e => AgentRelative(e.Key) == session.Name).Value;
            Assert.Equal((session.Value.GetProperty("rows").GetInt64(), session.Value.GetProperty("highestRowid").GetInt64()), (entry.Size, entry.Modified));
        }

        Assert.Equal(scenario.GetProperty("sessions").EnumerateObject().Count(), analyzer.Entries.Count);
        var second = Run(analyzer, Roots(scenario));
        Assert.Equal((0, 0), (second.FilesAnalyzed, second.SessionsAnalyzed));
        Assert.Empty(CreatedFiles());
    }

    /// <summary>The second database added to a stored history: its copy of a session adds nothing, its own session counts.</summary>
    [Fact]
    public void AddingTheOtherDatabasesAddsOnlyTheirOwnSessions()
    {
        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        Run(analyzer, Roots(Scenario("one")));
        AssertDays(analyzer, Scenario("one"));
        var added = Run(analyzer, Roots(Scenario("two")));
        Assert.Equal(2, added.SessionsAnalyzed);
        AssertDays(analyzer, Scenario("two"));
    }

    [Fact]
    public void TheCliRootsFollowBothEditions()
    {
        var cli = Expected.GetProperty("cli");
        foreach (var testCase in cli.GetProperty("automatic").EnumerateArray())
        {
            var env = testCase.GetProperty("env").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString());
            var roots = QoderCliPaths.AutomaticRoots(key => env.GetValueOrDefault(key), testCase.GetProperty("platform").GetString() == "windows",
                testCase.GetProperty("home").GetString()!);
            Assert.Equal(testCase.GetProperty("roots").EnumerateArray().Select(r => r.GetString()), roots);
        }

        foreach (var location in cli.GetProperty("locations").EnumerateObject())
        {
            Assert.Equal(location.Value.GetString(), QoderCliPaths.RootOf(location.Name, windows: false));
        }
    }

    [Fact]
    public void DiscoveryFindsTheCliTranscriptsAndNotTheEventLogs()
    {
        var found = TranscriptDiscovery.Enumerate(QoderCliFixture.Root, Families.Naming(RootFamily.QoderCli))
            .Select(f => Path.GetRelativePath(FixtureLocator.FixturesDirectory, f.Path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(QoderCliFixture.Expected.GetProperty("discover").EnumerateArray().Select(d => d.GetString()!), found);
    }

    [Theory]
    [MemberData(nameof(CliTraces))]
    public void TheCliPhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        foreach (var relative in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).TakeWhile(r => r != file))
        {
            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);
        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(parser.Phase), parser.InFlightStartMs));
    }

    /// <summary><c>cli.history</c>: the UTC day totals of the Qoder CLI fixture files, read as the history reads them.</summary>
    [Fact]
    public void TheCliHistoryMatchesItsDays()
    {
        var copy = FixtureCopy.WithModificationTimes(_temp.Combine("fixtures"));
        var enabled = new HashSet<SourceKind> { SourceKind.QoderCli };
        var locations = new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, []) { QoderCliRoots = [QoderCliFixture.RootIn(copy)] };
        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var report = analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(5, report.FilesAnalyzed);

        var wanted = Expected.GetProperty("cli").GetProperty("history");
        var days = analyzer.Days(enabled);
        Assert.Equal(wanted.EnumerateObject().Select(d => d.Name), days.Select(d => d.Day));
        foreach (var day in days)
        {
            var want = wanted.GetProperty(day.Day);
            AssertTotals(want, day);
            var models = want.GetProperty("models").EnumerateObject().ToDictionary(m => m.Name, m => (m.Value.GetProperty("requests").GetInt64(),
                m.Value.GetProperty("output").GetInt64(), m.Value.GetProperty("measuredOutput").GetInt64(), m.Value.GetProperty("measuredMs").GetInt64()));
            Assert.Equal(models.OrderBy(m => m.Key, StringComparer.Ordinal),
                day.Models.ToDictionary(m => m.Model, m => (m.Requests, m.Output, m.MeasuredOutput, m.MeasuredMs)).OrderBy(m => m.Key, StringComparer.Ordinal));
        }
    }

    private static HistoryReport Run(HistoryAnalyzer analyzer, IReadOnlyList<QoderRoot> roots)
    {
        var enabled = new HashSet<SourceKind> { SourceKind.Qoder };
        var locations = new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, []) { QoderRoots = roots };
        return analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
    }

    private static void AssertDays(HistoryAnalyzer analyzer, JsonElement scenario)
    {
        var days = analyzer.Days(new HashSet<SourceKind> { SourceKind.Qoder });
        var wanted = scenario.GetProperty("history");
        Assert.Equal(wanted.EnumerateObject().Select(d => d.Name).Order(StringComparer.Ordinal), days.Select(d => d.Day));
        foreach (var day in days)
        {
            var want = wanted.GetProperty(day.Day);
            AssertTotals(want, day);
            var models = want.GetProperty("models").EnumerateObject()
                .ToDictionary(m => m.Name, m => (m.Value.GetProperty("requests").GetInt64(), m.Value.GetProperty("output").GetInt64()));
            Assert.Equal(models.OrderBy(m => m.Key, StringComparer.Ordinal),
                day.Models.ToDictionary(m => m.Model, m => (m.Requests, m.Output)).OrderBy(m => m.Key, StringComparer.Ordinal));
        }
    }

    private static void AssertTotals(JsonElement want, DailyUsage day)
    {
        Assert.Equal(want.GetProperty("requests").GetInt64(), day.Totals.Requests);
        Assert.Equal(want.GetProperty("input").GetInt64(), day.Totals.Input);
        Assert.Equal(want.GetProperty("cachedInput").GetInt64(), day.Totals.CachedInput);
        Assert.Equal(want.GetProperty("cacheWriteInput").GetInt64(), day.Totals.CacheWriteInput);
        Assert.Equal(want.GetProperty("output").GetInt64(), day.Totals.Output);
        Assert.Equal(want.GetProperty("reasoning").GetInt64(), day.Totals.Reasoning);
        Assert.Equal(want.GetProperty("measuredOutput").GetInt64(), day.MeasuredOutput);
        Assert.Equal(want.GetProperty("measuredMs").GetInt64(), day.MeasuredMs);
        Assert.Equal(want.GetProperty("agents").GetInt32(), day.Agents);
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
        Assert.Equal((1L, SourceKind.Qoder), (record.Requests, record.Source));
    }

    private static string? Text(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static long? Number(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetInt64();

    /// <summary>Reading never creates a <c>-wal</c>, <c>-shm</c> or journal next to a fixture database.</summary>
    private List<string> CreatedFiles() =>
        [.. Directory.EnumerateFiles(_temp.Combine("qoder"), "*", SearchOption.AllDirectories)
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
