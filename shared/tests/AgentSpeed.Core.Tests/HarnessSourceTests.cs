using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Kimi;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.ZCode;

namespace AgentSpeed.Core.Tests;

public sealed class HarnessSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly long Start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ZCodeUpdatesAnExistingRequestWithoutCountingItTwice()
    {
        var path = _temp.Combine("db.sqlite");
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), path);
        var source = new ZCodeSource(path, new Deduper());
        var seeded = source.Seed(Start);
        Assert.Equal(4, seeded.Responses.Count);
        Assert.All(seeded.Responses, row => Assert.True(row.Completion));
        Assert.False(source.Refresh().Changed);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("UPDATE model_usage SET output_tokens=210 WHERE id='z-r1'");
        }

        var updated = source.Refresh(force: true);
        var row = Assert.Single(updated.Responses);
        Assert.Equal(("zcode:z-r1", 210, false), (row.Response.Key, row.Response.Tokens.Output, row.Completion));
        Assert.Empty(source.Refresh(force: true).Responses);
    }

    [Fact]
    public void ZCodeUsesNativeMessagesForInFlightStateAndNeverCountsTheirDuplicateUsage()
    {
        var path = _temp.Combine("db.sqlite");
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), path);
        var source = new ZCodeSource(path, new Deduper());
        source.Seed(Start);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            using var statement = writer.Prepare("INSERT INTO message(id,session_id,time_created,time_updated,data) VALUES(?,?,?,?,?)");
            statement.Bind(1, "new-assistant").Bind(2, "sess-z-main").Bind(3, Start + 50_000_000).Bind(4, Start + 50_000_000)
                .Bind(5, """{"role":"assistant","time":{"created":1792504400000},"modelId":"glm-5.3","tokens":{"output":900000}}""").Run();
        }

        var update = source.Refresh(force: true);
        Assert.Empty(update.Responses);
        var agent = Assert.Single(update.Agents);
        Assert.Equal(AgentPhase.Generating, agent.Phase);
        Assert.NotNull(agent.StartMs);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            using var statement = writer.Prepare("UPDATE message SET time_updated=?,data=? WHERE id='new-assistant'");
            statement.Bind(1, Start + 50_002_000).Bind(2,
                """{"role":"assistant","time":{"created":1792504400000,"completed":1792504402000},"modelId":"glm-5.3"}""").Run();
        }

        Assert.Equal(AgentPhase.Idle, Assert.Single(source.Refresh(force: true).Agents).Phase);
    }

    [Fact]
    public void ZCodeRejectsUnsupportedSchemasWithoutCreatingSidecars()
    {
        var path = _temp.Combine("unsupported.sqlite");
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("CREATE TABLE model_usage(id TEXT PRIMARY KEY)");
        }

        var before = Directory.GetFiles(_temp.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.Throws<InvalidDataException>(() => ZCodeDatabase.Open(path));
        Assert.Equal(before, Directory.GetFiles(_temp.Path).Order(StringComparer.Ordinal));
        var source = new ZCodeSource(path, new Deduper());
        Assert.Empty(source.Seed(Start).Responses);
        Assert.True(source.Unsupported);
    }

    [Fact]
    public void DisabledHarnessesAreNeverReadAndMissingDatabaseCanAppearLater()
    {
        var root = _temp.Combine(".zcode", "cli", "db");
        var locations = new SourceLocations("", "", "", "", [], ZCodeRoot: root);
        using (var disabled = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind>()), locations, Start))
        {
            disabled.Seed(Start + 500_000, Start, CancellationToken.None);
            Assert.DoesNotContain(disabled.WatchRoots, root => root.Enabled);
            Assert.False(disabled.AnyEnabledRootAvailable);
        }

        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.ZCode }), locations, Start);
        core.Seed(Start + 500_000, Start, CancellationToken.None);
        Directory.CreateDirectory(root);
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), Path.Combine(root, "db.sqlite"));
        Assert.True(core.Rediscover(Start + 500_000, Start, CancellationToken.None));
        var (snapshot, _) = core.BuildSnapshot(Start + 500_000, Start, MonitorState.Running, observeLive: false);
        Assert.Equal(4, snapshot.Today.Requests);
    }

    [Fact]
    public void KimiDoesNotCountUsageSummariesOrDifferentAgentEvents()
    {
        var parser = new KimiParser(_temp.Combine("work", "session", "agents", "main", "wire.jsonl"), new Deduper());
        var sink = new RecordingSink();
        Feed(parser, sink, """{"type":"llm.request","time":1000,"agentId":"main","model":"kimi-k2.5","provider":"moonshot"}""");
        Feed(parser, sink, """{"type":"usage.record","time":2000,"agentId":"main","usage":{"output":500}}""");
        Feed(parser, sink, """{"type":"subagent.completed","time":2001,"agentId":"main","usage":{"output":500}}""");
        Feed(parser, sink, """{"type":"context.append_message","time":2001,"agentId":"agent-0","message":{"role":"assistant","id":"foreign","usage":{"output":500}}}""");
        Assert.Empty(sink.Latest);
        Feed(parser, sink, """{"type":"context.append_loop_event","time":3000,"agentId":"main","event":{"type":"step.end","messageId":"m1","usage":{"inputOther":100,"inputCacheRead":20,"inputCacheCreation":10,"output":60}}}""");
        Assert.Equal(("kimi:m1", (long?)1000, 130, 60),
            (Assert.Single(sink.Latest).Key, sink.Latest[0].StartMs, sink.Latest[0].Tokens.Input, sink.Latest[0].Tokens.Output));
    }

    [Fact]
    public void KimiWithoutRequestTimingUsesTheExistingNullStartFallback()
    {
        var parser = new KimiParser(_temp.Combine("work", "session", "agents", "main", "wire.jsonl"), new Deduper());
        var sink = new RecordingSink();
        Feed(parser, sink, """{"type":"context.append_message","time":2000,"agentId":"main","message":{"role":"assistant","id":"m","usage":{"output":50}}}""");
        Assert.Null(Assert.Single(sink.Latest).StartMs);
        Feed(parser, sink, "not-json");
        Assert.Equal(1, parser.MalformedLines);
    }

    [Fact]
    public void DiscoveryAcceptsOnlyTheNativeLayoutAndRejectsArtifactAndSymlinkMirrors()
    {
        var root = _temp.Combine("sessions");
        var native = _temp.WriteFile("sessions/work/session/agents/main/wire.jsonl", "{}\n");
        _temp.WriteFile("sessions/work/session/tools/wire.jsonl", "{}\n");
        _temp.WriteFile("sessions/work/session/agents/main/other.jsonl", "{}\n");
        _temp.WriteFile("sessions/.hidden/session/agents/main/wire.jsonl", "{}\n");
        Assert.Equal([native], HarnessDiscovery.Enumerate(root, kimi: true, int.MaxValue, CancellationToken.None).Select(f => f.Path));
        Assert.False(HarnessDiscovery.IsSessionPath(root, _temp.Combine("outside", "wire.jsonl"), kimi: true));
        if (!OperatingSystem.IsWindows())
        {
            var external = _temp.Combine("external");
            _temp.WriteFile("external/main/wire.jsonl", "{}\n");
            Directory.CreateDirectory(_temp.Combine("sessions", "work", "linked"));
            Directory.CreateSymbolicLink(_temp.Combine("sessions", "work", "linked", "agents"), external);
            Assert.Equal([native], HarnessDiscovery.Enumerate(root, kimi: true, int.MaxValue, CancellationToken.None).Select(f => f.Path));
        }
    }

    [Fact]
    public void CustomDataFoldersAndNativeSubfoldersResolveToTheSameLocations()
    {
        var z = _temp.Combine(".zcode");
        var k = _temp.Combine(".kimi-code");
        _temp.WriteFile(".zcode/cli/db/db.sqlite", string.Empty);
        var config = new MonitorConfiguration(new HashSet<SourceKind>(), ZCodeRoot: z, KimiRoot: k);
        var locations = SourceLocations.Resolve(config);
        Assert.Equal(Path.Combine(z, "cli", "db", "db.sqlite"), ZCodeDatabase.DatabasePath(locations.ZCodeRoot));
        Assert.Equal(Path.Combine(k, "sessions"), locations.KimiRoot);
        Assert.Equal(locations.KimiRoot, SourceLocations.Resolve(config with { KimiRoot = Path.Combine(k, "sessions") }).KimiRoot);
    }

    private static void Feed(KimiParser parser, RecordingSink sink, string line) => parser.Feed(Encoding.UTF8.GetBytes(line), sink);
}
