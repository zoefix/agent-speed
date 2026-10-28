using AgentSpeed.Core.IO;
using AgentSpeed.Core.Omp;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>parser-expected.json <c>omp</c>: traces, tail reads, discovery and the automatic roots, like the reference.</summary>
public sealed class OmpFixtureTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string, int, string, long?> Traces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in ParserFixtureTests.Expected.GetProperty("omp").GetProperty("traces").EnumerateArray())
        {
            data.Add(
                trace.GetProperty("file").GetString()!,
                trace.GetProperty("afterLine").GetInt32(),
                trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    public static TheoryData<int> TailReads()
    {
        var data = new TheoryData<int>();
        var count = ParserFixtureTests.Expected.GetProperty("omp").GetProperty("tailReads").GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Fact]
    public void EveryTraceAndTailReadIsListed() => Assert.Equal((78, 1), (Traces().Count, TailReads().Count));

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = ReadFilesBefore(file);
        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);

        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(parser.Phase), parser.InFlightStartMs));
    }

    /// <summary>An older file that grows is read from a byte offset: its header comes from the first two lines (METRICS 4.7).</summary>
    [Theory]
    [MemberData(nameof(TailReads))]
    public void ReadingFromAnOffsetTakesTheHeaderFirst(int index)
    {
        var expected = ParserFixtureTests.Expected.GetProperty("omp").GetProperty("tailReads")[index];
        var file = expected.GetProperty("file").GetString()!;
        var path = FixtureLocator.PathOf(file);
        var deduper = ReadFilesBefore(file);
        var parser = new OmpParser(path, deduper, OmpFixture.RootOf(path));
        var tailer = new FileTailer(path);
        var sink = new RecordingSink();

        tailer.StartAt(expected.GetProperty("offset").GetInt64(), StartAlignment.SkipPartialLine);
        var batch = parser.NewBatch();
        parser.PrepareBatch(batch, tailer.Offset);
        tailer.ReadAppended(batch);
        batch.ApplyTo(sink);

        Assert.Equal(expected.GetProperty("project").GetString(), parser.Project);
        Assert.Equal(expected.GetProperty("phase").GetString(), ParserFixtureTests.PhaseName(parser.Phase));
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("inFlightStartMs")), parser.InFlightStartMs);
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("lastLineMs")), parser.LastLineMs);
        Assert.Equal(
            expected.GetProperty("responseKeys").EnumerateArray().Select(k => k.GetString()!).ToList(),
            sink.Latest.Select(r => r.Key).ToList());
    }

    [Fact]
    public void DiscoveryFindsSessionsAndSubAgentsButNoTempsBackupsArchivesOrArtifacts()
    {
        var expected = ParserFixtureTests.Expected.GetProperty("omp").GetProperty("discover").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        var found = OmpFixture.Roots
            .SelectMany(root => TranscriptDiscovery.Enumerate(root, TranscriptNaming.OmpSession))
            .Select(f => Path.GetRelativePath(FixtureLocator.FixturesDirectory, f.Path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, found);
    }

    [Fact]
    public void WatcherPathsFollowTheDiscoveryRules()
    {
        var root = OmpFixture.Roots[0];
        string P(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(TranscriptDiscovery.IsAcceptablePath(root,
            P("-projects-comet/2026-10-20T10-48-00-000Z_01a158ec-db00-75e2-83a6-00013c6ef362.jsonl"), TranscriptNaming.OmpSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P("stray.jsonl"), TranscriptNaming.OmpSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root,
            P("-projects-pulsar/.omp-gc-compress-4242/2026-10-20T10-50-00-000Z_01a158ee-afc0-7bc4-874c-000278dde6c4.jsonl"),
            TranscriptNaming.OmpSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root,
            P("-projects-pulsar/.2026-10-20T10-50-00-000Z_01a158ee-afc0-7bc4-874c-000278dde6c4.jsonl.7f3e.tmp"), TranscriptNaming.OmpSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root,
            P("-projects-pulsar/2026-10-20T10-50-00-000Z_01a158ee-afc0-7bc4-874c-000278dde6c4.jsonl.7f3e.bak"), TranscriptNaming.OmpSession));
    }

    /// <summary>The cases of the reference's <c>check_omp_roots</c>.</summary>
    [Fact]
    public void AutomaticRootsFollowTheEnvironmentProfilesAndPlatform()
    {
        var home = _temp.Combine("home");
        var xdg = _temp.Combine("xdg");
        foreach (var folder in (string[])[".omp/profiles/work", ".omp/profiles/play", ".pi-alt/profiles/solo"])
        {
            Directory.CreateDirectory(Path.Combine(home, folder));
        }

        foreach (var folder in (string[])["omp/profiles/work", "omp/profiles/lab"])
        {
            Directory.CreateDirectory(Path.Combine(xdg, folder));
        }

        var h = Path.Combine(home, ".omp");
        var x = Path.Combine(xdg, "omp");
        var agent = Path.Combine(_temp.Path, "agent");
        string J(params string[] parts) => Path.Combine(parts);
        IReadOnlyList<string> Roots(bool macOS, params (string Name, string Value)[] env) =>
            OmpPaths.Roots(name => env.FirstOrDefault(e => e.Name == name).Value, macOS, home);

        Assert.Equal(
            [J(h, "agent", "sessions"), J(h, "profiles", "play", "agent", "sessions"), J(h, "profiles", "work", "agent", "sessions")],
            Roots(false));
        Assert.Equal(
            [J(agent, "sessions"), J(h, "profiles", "play", "agent", "sessions"), J(h, "profiles", "work", "agent", "sessions")],
            Roots(false, ("PI_CODING_AGENT_DIR", agent)));
        Assert.Equal(
            [J(home, ".pi-alt", "agent", "sessions"), J(home, ".pi-alt", "profiles", "solo", "agent", "sessions")],
            Roots(false, ("PI_CONFIG_DIR", ".pi-alt")));
        Assert.Equal(
            [J(x, "sessions"), J(x, "profiles", "lab", "sessions"), J(h, "profiles", "play", "agent", "sessions"), J(x, "profiles", "work", "sessions")],
            Roots(true, ("XDG_DATA_HOME", xdg)));
        Assert.Equal(
            [J(h, "agent", "sessions"), J(h, "profiles", "play", "agent", "sessions"), J(h, "profiles", "work", "agent", "sessions")],
            Roots(false, ("XDG_DATA_HOME", xdg)));
    }

    [Fact]
    public void FixtureRootsAreTheDefaultRootAndTheWorkProfile()
    {
        Assert.Equal(
            [FixtureLocator.PathOf("omp/agent/sessions"), FixtureLocator.PathOf("omp/profiles/work/agent/sessions")],
            OmpFixture.Roots);
    }

    private static Deduper ReadFilesBefore(string file)
    {
        var deduper = new Deduper();
        foreach (var element in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray())
        {
            var relative = element.GetString()!;
            if (relative == file)
            {
                break;
            }

            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        return deduper;
    }
}
