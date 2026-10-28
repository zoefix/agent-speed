using System.Text.Json;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>parser-expected.json <c>antigravity</c>: every conversation database read completely, like the reference.</summary>
public sealed class AntigravityFixtureTests
{
    private static readonly Lazy<Dictionary<string, Result>> Results = new(ReadAll);

    private static JsonElement Expected => ParserFixtureTests.Expected.GetProperty("antigravity");

    private static string Tree => FixtureLocator.PathOf("antigravity");

    public static TheoryData<string> DatabaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expected.GetProperty("order").EnumerateArray())
        {
            data.Add(name.GetString()!);
        }

        return data;
    }

    [Fact]
    public void EveryDatabaseIsListedInOrder()
    {
        var listed = Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(Expected.GetProperty("databases").EnumerateObject().Select(p => p.Name).ToHashSet(), listed);
        Assert.Equal(11, listed.Count);
    }

    [Theory]
    [MemberData(nameof(DatabaseNames))]
    public void DatabaseMatchesExpectation(string relative)
    {
        var expected = Expected.GetProperty("databases").GetProperty(relative);
        var (parser, sink, _) = Results.Value[relative];
        if (expected.TryGetProperty("unsupported", out _))
        {
            Assert.True(parser.Unsupported);
            Assert.Null(parser.LastLineMs);
            Assert.Empty(sink.Emissions);
            return;
        }

        Assert.False(parser.Unsupported);
        Assert.Equal(expected.GetProperty("source").GetString(), ParserFixtureTests.SourceName(parser.Source));
        Assert.Equal(expected.GetProperty("project").GetString(), parser.Project);
        Assert.Equal(expected.GetProperty("model").GetString(), parser.Model);
        Assert.Equal(expected.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
        Assert.Equal(expected.GetProperty("phase").GetString(), ParserFixtureTests.PhaseName(parser.Phase));
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("inFlightStartMs")), parser.InFlightStartMs);
        Assert.Equal(ParserFixtureTests.NullableLong(expected.GetProperty("lastLineMs")), parser.LastLineMs);
        Assert.Equal(expected.GetProperty("malformedLines").GetInt64(), parser.MalformedLines);

        var expectedResponses = expected.GetProperty("responses").EnumerateArray().ToList();
        var responses = sink.Latest;
        Assert.Equal(expectedResponses.Select(r => r.GetProperty("key").GetString()!).ToList(), responses.Select(r => r.Key).ToList());
        for (var i = 0; i < responses.Count; i++)
        {
            var e = expectedResponses[i];
            var r = responses[i];
            Assert.Equal(
                (ParserFixtureTests.NullableLong(e.GetProperty("startMs")), e.GetProperty("endMs").GetInt64(), e.GetProperty("model").GetString()),
                (r.StartMs, r.EndMs, r.Model));
            Assert.Equal(
                new TokenCounts(
                    e.GetProperty("input").GetInt64(),
                    e.GetProperty("cachedInput").GetInt64(),
                    e.GetProperty("cacheWriteInput").GetInt64(),
                    e.GetProperty("output").GetInt64(),
                    e.GetProperty("reasoning").GetInt64()),
                r.Tokens);
            Assert.Equal((e.GetProperty("final").GetBoolean(), SourceKind.Antigravity, parser.AgentId), (r.Final, r.Source, r.AgentId));
        }
    }

    [Theory]
    [MemberData(nameof(DatabaseNames))]
    public void ParentMatchesExpectation(string relative)
    {
        var expected = Expected.GetProperty("databases").GetProperty(relative);
        if (expected.TryGetProperty("unsupported", out _))
        {
            return;
        }

        var supported = Results.Value.Values.Where(r => !r.Parser.Unsupported).Select(r => AgentLink.Of(r.Parser)).ToList();
        var parents = ParentLinks.Resolve(supported, StringComparer.Ordinal);
        var parent = parents.GetValueOrDefault(Results.Value[relative].Parser.AgentId);
        var actual = parent is null ? null : Relative(parent);
        Assert.Equal(expected.GetProperty("parentFile").GetString(), actual);
    }

    [Fact]
    public void FirstEmissionOfEachKeyIsTheOnlyCompletion()
    {
        foreach (var (_, sink, _) in Results.Value.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (response, isCompletion) in sink.Emissions)
            {
                Assert.Equal(seen.Add(response.Key), isCompletion);
            }
        }
    }

    [Fact]
    public void ClosedWalDatabasesAreReadImmutablyAndOthersReadOnly()
    {
        foreach (var (relative, result) in Results.Value)
        {
            var path = FixtureLocator.PathOf(relative);
            Assert.Equal(SqliteHeader.IsWal(path) && !File.Exists(path + "-wal"), result.Immutable);
        }

        Assert.Contains(Results.Value, r => !r.Value.Immutable);
    }

    [Fact]
    public void DiscoveryFindsUuidDatabasesInEveryStoreAndNothingElse()
    {
        var expected = Expected.GetProperty("discover").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(expected, Discover(AntigravityPaths.Stores(Tree, null)));
        Assert.Equal(expected, Discover(SourceLocations.NormalizeAll(AntigravityPaths.Stores(Tree, null))));
    }

    [Fact]
    public void ASettingReplacesTheStoresAndABackupStoreIsNeverRead()
    {
        var backup = Path.Combine(Tree, "antigravity-backup");
        Assert.Empty(AntigravityPaths.Stores(Tree, backup));
        Assert.Empty(AntigravityPaths.Stores(Tree, Path.Combine(backup, "conversations")));
        Assert.Empty(AntigravityPaths.Stores(Tree, backup + Path.DirectorySeparatorChar));

        foreach (var store in AntigravityPaths.Stores(Tree, null))
        {
            var viaFolder = Discover(AntigravityPaths.Stores(Tree, Path.Combine(store, "conversations")));
            Assert.Equal(Discover([store]), viaFolder);
            Assert.Equal([store], AntigravityPaths.Stores(Tree, store));
        }
    }

    [Theory]
    [InlineData("3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7b.db", true)]
    [InlineData("E4D3C2B1-A0F9-4E8D-B7C6-A5F4E3D2C1B0.db", true)]
    [InlineData("3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7b.db-wal", false)]
    [InlineData("3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7b.pb", false)]
    [InlineData("3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7b.trajectory.json", false)]
    [InlineData("3f6c2a108b4d-4e5f-9a1b-2c3d4e5f6a7b0.db", false)]
    [InlineData("3f6c2a10-8b4d-4e5f-9a1b-2c3d4e5f6a7g.db", false)]
    [InlineData("conversation_summaries.db", false)]
    [InlineData("notes.db", false)]
    public void DatabaseNamesAreUuids(string name, bool expected) => Assert.Equal(expected, AntigravityPaths.IsDatabaseName(name));

    /// <summary>Like the reference: the fixture tree is compared before and after reading every database.</summary>
    [Fact]
    public void ReadingCreatesNoFiles()
    {
        var before = Snapshot();
        var deduper = new Deduper();
        foreach (var name in Expected.GetProperty("order").EnumerateArray())
        {
            Read(name.GetString()!, deduper);
        }

        Assert.Equal(before, Snapshot());
    }

    internal static List<string> Snapshot() =>
        [.. Directory.EnumerateFiles(Tree, "*", SearchOption.AllDirectories).Select(Relative).Order(StringComparer.Ordinal)];

    private static List<string> Discover(IEnumerable<string> stores) =>
        [.. stores.SelectMany(s => AntigravityDiscovery.Enumerate(AntigravityPaths.ConversationsOf(s)))
            .Select(f => Relative(f.Path)).Order(StringComparer.Ordinal)];

    private static string Relative(string path) =>
        Path.GetRelativePath(FixtureLocator.FixturesDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static Result Read(string relative, IDeduper deduper)
    {
        var path = FixtureLocator.PathOf(relative);
        var parser = new AntigravityParser(path, deduper);
        var sink = new RecordingSink();
        using var tailer = new AntigravityTailer(path);
        var batch = parser.NewBatch();
        tailer.ReadAppended(batch);
        var immutable = tailer.IsImmutable;
        batch.ApplyTo(sink);
        return new Result(parser, sink, immutable);
    }

    private static Dictionary<string, Result> ReadAll()
    {
        var deduper = new Deduper();
        var results = new Dictionary<string, Result>(StringComparer.Ordinal);
        foreach (var name in Expected.GetProperty("order").EnumerateArray())
        {
            results[name.GetString()!] = Read(name.GetString()!, deduper);
        }

        return results;
    }

    private sealed record Result(AntigravityParser Parser, RecordingSink Sink, bool Immutable);
}

internal static class SqliteHeader
{
    /// <summary>Header bytes 18 and 19 are 2: the database is in WAL mode.</summary>
    public static bool IsWal(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[20];
        return stream.Read(header, 0, 20) == 20 && header[18] == 2 && header[19] == 2;
    }
}
