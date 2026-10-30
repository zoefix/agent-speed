using System.Text.Json;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.OpenClaw;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// parser-expected.json <c>openclaw</c>: every window of every agent database (after the files of <c>order</c>, the OpenCode
/// and the Antigravity databases, one dedupe set), traces, discovery, the Codex roots next to the databases and the state
/// folders, like the reference's checks.
/// </summary>
public sealed class OpenClawFixtureTests : IDisposable
{
    private static readonly Lazy<Dictionary<string, Result>> Results = new(ReadAll);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static JsonElement Expected => ParserFixtureTests.Expected.GetProperty("openclaw");

    private static List<string> DatabaseOrder => [.. Expected.GetProperty("dbOrder").EnumerateArray().Select(e => e.GetString()!)];

    public static TheoryData<string> WindowIds()
    {
        var data = new TheoryData<string>();
        foreach (var database in Expected.GetProperty("databases").EnumerateObject())
        {
            if (!database.Value.TryGetProperty("unsupported", out _))
            {
                foreach (var window in database.Value.EnumerateObject())
                {
                    data.Add(window.Name);
                }
            }
        }

        return data;
    }

    public static TheoryData<string, string, long, string, long?> Traces()
    {
        var data = new TheoryData<string, string, long, string, long?>();
        foreach (var trace in Expected.GetProperty("traces").EnumerateArray())
        {
            data.Add(
                trace.GetProperty("database").GetString()!,
                trace.GetProperty("window").GetString()!,
                trace.GetProperty("afterSeq").GetInt64(),
                trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    [Fact]
    public void TheFixturesCoverEveryKindOfWindow()
    {
        Assert.Equal(9, WindowIds().Count);
        Assert.Equal(18, Traces().Count);
        Assert.Equal(3, DatabaseOrder.Count);
    }

    [Fact]
    public void EveryDatabaseHasExactlyTheExpectedWindows()
    {
        foreach (var relative in DatabaseOrder)
        {
            var expected = Expected.GetProperty("databases").GetProperty(relative);
            var (parsers, _) = Results.Value[relative];
            if (expected.TryGetProperty("unsupported", out _))
            {
                Assert.Null(parsers);
                continue;
            }

            Assert.NotNull(parsers);
            Assert.Equal(
                expected.EnumerateObject().Select(w => w.Name).Order(StringComparer.Ordinal),
                parsers.Select(p => Relative(p.AgentId)).Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(WindowIds))]
    public void WindowMatchesExpectation(string agentId)
    {
        var database = agentId[..agentId.IndexOf('#')];
        var expected = Expected.GetProperty("databases").GetProperty(database).GetProperty(agentId);
        var (parsers, sinks) = Results.Value[database];
        var parser = parsers!.Single(p => Relative(p.AgentId) == agentId);
        var sink = sinks[parser.AgentId];

        Assert.Equal(expected.GetProperty("source").GetString(), ParserFixtureTests.SourceName(parser.Source));
        Assert.Equal(expected.GetProperty("project").GetString(), parser.Project);
        Assert.Equal(expected.GetProperty("model").GetString(), parser.Model);
        Assert.Equal(expected.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
        Assert.Equal(expected.GetProperty("parentId").GetString(), parser.ParentId is { } parent ? Relative(parent) : null);
        Assert.Equal(expected.GetProperty("phase").GetString(), ParserFixtureTests.PhaseName(parser.Phase));
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("inFlightStartMs")), parser.InFlightStartMs);
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("lastLineMs")), parser.LastLineMs);
        Assert.Equal(expected.GetProperty("malformedLines").GetInt64(), parser.MalformedLines);

        var expectedResponses = expected.GetProperty("responses").EnumerateArray().ToList();
        var responses = sink.Latest;
        Assert.Equal(expectedResponses.Select(r => r.GetProperty("key").GetString()!), responses.Select(r => r.Key));
        for (var i = 0; i < responses.Count; i++)
        {
            var e = expectedResponses[i];
            var r = responses[i];
            Assert.Equal(
                (ParserFixtureTests.NullableLong(e.GetProperty("startMs")), e.GetProperty("endMs").GetInt64(), e.GetProperty("model").GetString(),
                    e.GetProperty("provider").GetString(), e.GetProperty("final").GetBoolean(), e.GetProperty("fast").GetBoolean()),
                (r.StartMs, r.EndMs, r.Model, r.Provider, r.Final, r.Fast));
            Assert.Equal(
                new TokenCounts(
                    e.GetProperty("input").GetInt64(),
                    e.GetProperty("cachedInput").GetInt64(),
                    e.GetProperty("cacheWriteInput").GetInt64(),
                    e.GetProperty("output").GetInt64(),
                    e.GetProperty("reasoning").GetInt64(),
                    e.GetProperty("cacheWrite1hInput").GetInt64()),
                r.Tokens);
            Assert.Equal((SourceKind.OpenClaw, parser.AgentId), (r.Source, r.AgentId));
        }

        ParserFixtureTests.AssertModes(expected, agentId, parser.Modes, responses);
    }

    [Fact]
    public void TheTraceQuerySelectsExactlyTheRowsOfTheFixture()
    {
        foreach (var database in Expected.GetProperty("traceSeqs").EnumerateObject())
        {
            using var open = OpenClawDatabase.Open(FixtureLocator.PathOf(database.Name));
            foreach (var window in database.Value.EnumerateObject())
            {
                var seqs = new List<long>();
                open.Traces(window.Name, -1, (seq, _) => seqs.Add(seq));
                Assert.Equal(window.Value.EnumerateArray().Select(s => s.GetInt64()), seqs);
            }
        }
    }

    [Fact]
    public void ParentsResolveThroughTheSharedLinkRules()
    {
        var parsers = Results.Value.Values.SelectMany(r => r.Parsers ?? []).ToList();
        var parents = ParentLinks.Resolve([.. parsers.Select(AgentLink.Of)], StringComparer.Ordinal);
        foreach (var parser in parsers)
        {
            Assert.Equal(parser.ParentId, parents.GetValueOrDefault(parser.AgentId));
        }

        Assert.Single(parents);
    }

    [Fact]
    public void FirstEmissionOfEachKeyIsTheOnlyCompletion()
    {
        foreach (var sink in Results.Value.Values.SelectMany(r => r.Sinks.Values))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (response, isCompletion) in sink.Emissions)
            {
                Assert.Equal(seen.Add(response.Key), isCompletion);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterRowMatchesTrace(string database, string window, long afterSeq, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        OpenClawParser? traced = null;
        foreach (var relative in DatabaseOrder)
        {
            var path = FixtureLocator.PathOf(relative);
            if (relative != database)
            {
                OpenClawSource.ReadAll(path, deduper, _ => new RecordingSink());
                continue;
            }

            traced = OpenClawSource.ReadAll(path, deduper, _ => new RecordingSink(), stopAfter: (window, afterSeq))!.Last();
            break;
        }

        Assert.NotNull(traced);
        Assert.Equal(window, traced.SessionId);
        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(traced.Phase), traced.InFlightStartMs));
    }

    [Fact]
    public void DiscoveryFindsAgentDatabasesOfEveryStateFolderAndNothingElse()
    {
        var expected = Expected.GetProperty("discover").EnumerateArray().Select(e => e.GetString()!).ToList();
        var folders = OpenClawFixture.StateFoldersIn(FixtureLocator.FixturesDirectory);

        Assert.Equal([Path.Combine(OpenClawFixture.Home, ".openclaw"), Path.Combine(OpenClawFixture.Home, ".openclaw-work")], folders);
        Assert.Equal(expected, OpenClawPaths.Databases(folders).Select(Relative).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheCodexHomesNextToTheDatabasesAreCodexRoots()
    {
        var expected = Expected.GetProperty("codexDiscover").EnumerateArray().Select(e => e.GetString()!).ToList();
        var databases = OpenClawPaths.Databases(OpenClawFixture.StateFoldersIn(FixtureLocator.FixturesDirectory));

        var found = OpenClawPaths.CodexRoots(databases)
            .SelectMany(root => TranscriptDiscovery.Enumerate(root, TranscriptNaming.CodexRollout))
            .Select(f => Relative(f.Path))
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, found);
        var locations = new SourceLocations(_temp.Combine("codex", "sessions"), string.Empty, string.Empty, string.Empty, [],
            OpenClawRoots: OpenClawFixture.StateFoldersIn(FixtureLocator.FixturesDirectory));
        Assert.Equal(
            [_temp.Combine("codex", "sessions"), .. OpenClawPaths.CodexRoots(databases)],
            locations.FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => r.Path));
        Assert.Equal(
            [_temp.Combine("codex", "sessions")],
            (locations with { CodexAutomatic = false }).FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => r.Path));
    }

    [Fact]
    public void ASettingNamesAStateFolderAnAgentFolderOrTheDatabase()
    {
        var main = FixtureLocator.PathOf(DatabaseOrder[0]);
        Assert.Equal([main], OpenClawPaths.Databases(Path.GetDirectoryName(main)!));
        Assert.Equal([main], OpenClawPaths.Databases(main));
        Assert.Empty(OpenClawPaths.Databases(Path.Combine(Path.GetDirectoryName(main)!, "incognito-openclaw-agent.sqlite")));
        Assert.Equal(2, OpenClawPaths.Databases(Path.Combine(OpenClawFixture.Home, ".openclaw")).Count);

        var locations = SourceLocations.Resolve(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.OpenClaw }, OpenClawRoot: main));
        Assert.Equal([main], locations.OpenClawRoots);
        Assert.False(locations.OpenClawAutomatic);
    }

    [Fact]
    public void StateFoldersFollowTheOverridesAndTheProfileNames()
    {
        var home = _temp.Path;
        foreach (var name in new[] { ".openclaw", ".openclaw-work", ".openclaw-Default", ".openclaw-x.y", ".openclaw-_z", ".openclaw-2nd", "openclaw-play" })
        {
            Directory.CreateDirectory(Path.Combine(home, name));
        }

        File.WriteAllText(Path.Combine(home, ".openclaw-file"), string.Empty);
        IReadOnlyList<string> Roots(params (string Name, string Value)[] env) =>
            OpenClawPaths.StateFolders(name => env.FirstOrDefault(e => e.Name == name).Value, home);

        var automatic = new[] { Path.Combine(home, ".openclaw"), Path.Combine(home, ".openclaw-2nd"), Path.Combine(home, ".openclaw-work") };
        Assert.Equal(automatic, Roots());
        Assert.Equal([Path.Combine(home, "state")], Roots(("OPENCLAW_STATE_DIR", "~/state")));
        Assert.Equal(automatic, Roots(("OPENCLAW_STATE_DIR", "  "), ("OPENCLAW_PROFILE", "work")));
        var other = Path.GetFullPath(Path.Combine(home, "..", "srv-oc"));
        Assert.Equal([Path.Combine(other, ".openclaw")], Roots(("OPENCLAW_HOME", other)));
        Assert.Equal([Path.Combine(home, ".openclaw-work", ".openclaw")], Roots(("OPENCLAW_HOME", "~/.openclaw-work")));
        Assert.Equal([Path.Combine(home, ".openclaw-work", ".openclaw")], Roots(("OPENCLAW_HOME", "~\\.openclaw-work")));
    }

    [Fact]
    public void ReadingTheDatabasesCreatesNoFile()
    {
        var before = Files(OpenClawFixture.Home);
        foreach (var relative in DatabaseOrder)
        {
            OpenClawSource.ReadAll(FixtureLocator.PathOf(relative), new Deduper(), _ => new RecordingSink());
        }

        using var core = new MonitorCore(
            new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.OpenClaw }),
            new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [],
                OpenClawRoots: OpenClawFixture.StateFoldersIn(FixtureLocator.FixturesDirectory)),
            0);
        core.Seed(0, 0, CancellationToken.None);
        Assert.Equal(before, Files(OpenClawFixture.Home));
    }

    private static List<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private static string Relative(string path) =>
        Path.GetRelativePath(FixtureLocator.FixturesDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Like the reference: the files of <c>order</c>, then the OpenCode database and the Antigravity databases, then the
    /// agent databases of <c>dbOrder</c>, all with one dedupe set.
    /// </summary>
    private static Dictionary<string, Result> ReadAll()
    {
        var deduper = new Deduper();
        foreach (var element in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray())
        {
            ParserFixtureTests.ParseFixture(element.GetString()!, deduper, new RecordingSink());
        }

        var results = new Dictionary<string, Result>(StringComparer.Ordinal);
        foreach (var relative in DatabaseOrder)
        {
            var sinks = new Dictionary<string, RecordingSink>(StringComparer.Ordinal);
            var parsers = OpenClawSource.ReadAll(FixtureLocator.PathOf(relative), deduper, p => sinks[p.AgentId] = new RecordingSink());
            results[relative] = new Result(parsers, sinks);
        }

        return results;
    }

    private sealed record Result(List<OpenClawParser>? Parsers, Dictionary<string, RecordingSink> Sinks);
}
