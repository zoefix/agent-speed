using System.IO.Compression;
using System.Text;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Hermes;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.OpenClaw;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>EXECUTION-MODES, Current values and the stores behind them: Brotli rows, config files, session stores and the ledger rule.</summary>
public sealed class CurrentValueTests : IDisposable
{
    private const long Now = 1_800_000_000_000;
    private const long Today = Now - 3_600_000;
    private const string Trace = """{"type":"trace.metadata","data":{"model":{"thinkLevel":"high","fastMode":true,"reasoningLevel":"on"},"systemPromptReport":{"text":"secret"}}}""";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Brotli(byte[] input)
    {
        var output = new byte[input.Length + 1024];
        Assert.True(BrotliEncoder.TryCompress(input, output, out var written));
        return output[..written];
    }

    [Fact]
    public void ABrotliRowKeepsItsTwoFieldsOnly()
    {
        var bytes = Encoding.UTF8.GetBytes(Trace);
        var trace = OpenClawTraces.ReadCompressed(1000, "br", Brotli(bytes), bytes.Length);
        Assert.NotNull(trace);
        Assert.Equal(1000, trace.Value.CreatedMs);
        Assert.Equal(("high", "recorded"), Pair(trace.Value.Modes, ExecutionModeKind.Effort));
        Assert.Equal(("fast", "selected"), Pair(trace.Value.Modes, ExecutionModeKind.ServiceTier));
        Assert.Equal(2, trace.Value.Modes.Facts.Count);
    }

    [Fact]
    public void DamagedTruncatedOversizedAndWrongSizedBrotliStreamsReturnNothing()
    {
        var bytes = Encoding.UTF8.GetBytes(Trace);
        var stream = Brotli(bytes);

        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", stream[..^3], bytes.Length));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", [], bytes.Length));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", [.. stream, 0, 1, 2], bytes.Length));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", stream, bytes.Length - 1));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", stream, bytes.Length + 1));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "zstd", stream, bytes.Length));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", stream, 0));
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", stream, OpenClawTraces.MaxBytes + 1));

        var damaged = stream.ToArray();
        var rejected = 0;
        for (var i = 0; i < damaged.Length; i++)
        {
            damaged[i] ^= 0x5A;
            rejected += OpenClawTraces.ReadCompressed(1, "br", damaged, bytes.Length) is null ? 1 : 0;
            damaged[i] ^= 0x5A;
        }

        Assert.True(rejected > damaged.Length / 2, $"{rejected} of {damaged.Length} flips rejected");
    }

    [Fact]
    public void ABombStopsAtTheDeclaredSize()
    {
        var bomb = Brotli(new byte[64 * 1024 * 1024]);
        Assert.True(bomb.Length < 4096, $"{bomb.Length}");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(OpenClawTraces.ReadCompressed(1, "br", bomb, OpenClawTraces.MaxBytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2 * 1024 * 1024);
    }

    [Theory]
    [InlineData("""{"type":"prompt.submitted","data":{"model":{"thinkLevel":"high"}}}""")]
    [InlineData("""[{"type":"trace.metadata"}]""")]
    [InlineData("""{"type":"trace.metadata","data":{"model":{"thinkLevel":"high"}}} trailing""")]
    [InlineData("not json")]
    [InlineData("")]
    public void TextThatIsNoTraceIsIgnored(string text)
    {
        Assert.Null(OpenClawTraces.Read(1, Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void InvalidUtf8IsIgnoredAndARowWithoutModelIsKeptWithoutFacts()
    {
        Assert.Null(OpenClawTraces.Read(1, [0x7B, 0xFF, 0x7D]));
        var trimmed = OpenClawTraces.Read(5, """{"type":"trace.metadata","data":{"truncated":true}}"""u8);
        Assert.NotNull(trimmed);
        Assert.True(trimmed.Value.Modes.IsEmpty);
    }

    [Fact]
    public void ALateTraceWithoutModelEndsThePreviousRowsReach()
    {
        var parser = new OpenClawParser("/db", new OpenClawWindow("w", 0, 0, null, string.Empty, false), new Deduper());
        var sink = new RecordingSink();
        parser.ApplyTraces([new OpenClawTrace(100, OpenClawTraces.Read(100, """{"type":"trace.metadata","data":{"model":{"thinkLevel":"low"}}}"""u8)!.Value.Modes)], sink);
        Assert.Equal("low", parser.Modes.Attribute(ExecutionModeKind.Effort).Mode);
        parser.ApplyTraces([new OpenClawTrace(200, ExecutionModes.Empty)], sink);
        Assert.True(parser.Modes.IsEmpty);
    }

    [Fact]
    public void AConfigFileIsAtMostOneMegabyteOfUtf8WithOneByteOrderMarkRemoved()
    {
        var path = _temp.WriteFile("c.json", "﻿{\"reasoningEffort\":\"high\"}");
        Assert.Equal("high", Effort(ConfiguredValues.WorkBuddy(path).Modes));
        File.WriteAllText(path, "﻿﻿{\"reasoningEffort\":\"high\"}");
        Assert.True(ConfiguredValues.WorkBuddy(path).Modes.IsEmpty);
        File.WriteAllText(path, "[1]");
        Assert.True(ConfiguredValues.WorkBuddy(path).Modes.IsEmpty);
        File.WriteAllText(path, "{\"reasoningEffort\":\"high\"," + new string(' ', ConfigFile.MaxBytes) + "}");
        Assert.True(ConfiguredValues.WorkBuddy(path).Modes.IsEmpty);
        Assert.True(ConfiguredValues.WorkBuddy(_temp.Combine("missing.json")).Modes.IsEmpty);
    }

    [Fact]
    public void AConfigLinkIsNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = _temp.WriteFile("target.json", """{"reasoningEffort":"high"}""");
        File.CreateSymbolicLink(_temp.Combine("link.json"), target);
        Assert.Null(ConfigFile.Text(_temp.Combine("link.json")));
    }

    [Fact]
    public void ValuesAreReadOnceAndAgainOnlyWhenTheFileChanged()
    {
        var data = _temp.Combine(".workbuddy");
        var path = _temp.WriteFile(".workbuddy/settings.json", """{"reasoningEffort":"low"}""");
        var values = new ConfiguredValues();
        var root = Path.Combine(data, "projects");
        Assert.Equal("low", Effort(values.Of(SourceKind.WorkBuddy, root, "a", null)));
        Assert.False(values.Refresh());
        File.WriteAllText(path, """{"reasoningEffort":"max","other":true}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(values.Refresh());
        Assert.Equal("max", Effort(values.Of(SourceKind.WorkBuddy, root, "a", null)));
        File.Delete(path);
        Assert.True(values.Refresh());
        Assert.Equal("unknown", Effort(values.Of(SourceKind.WorkBuddy, root, "a", null)));
    }

    [Fact]
    public void GeminiTakesTheLastEntryForExactlyThatModelThatNamesALevel()
    {
        var path = _temp.WriteFile(".gemini/settings.json", """
            {"modelConfigs":{"overrides":[
              {"match":{"model":"a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"LOW"}}}},
              {"match":{"model":"a","overrideScope":"x"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}},
              {"match":{"model":"b"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":0}}}},
              {"match":{"model":"c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"MEDIUM"}}}}],
             "customOverrides":[
              {"match":{"model":"a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":8192}}}},
              {"match":{"model":"c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"THINKING_LEVEL_UNSPECIFIED"}}}}]}}
            """);
        var value = ConfiguredValues.Gemini(path);
        Assert.Equal("low", Effort(value.ByModel["a"]));
        Assert.Equal("off", Effort(value.ByModel["b"]));
        Assert.True(value.ByModel["c"].IsEmpty);
        Assert.False(value.ByModel.ContainsKey("d"));
        Assert.Equal(Path.Combine(_temp.Path, ".gemini", "settings.json"), ConfiguredValues.GeminiSettingsPath(_temp.Combine(".gemini", "tmp")));
        Assert.Equal(Path.Combine(_temp.Path, ".gemini", "settings.json"), ConfiguredValues.GeminiSettingsPath(_temp.Combine(".cache", ".gemini", "tmp") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ASessionStoreReadsDueSessionsOnlyAndCreatesNoSidecars()
    {
        var path = _temp.Combine("workbuddy.db");
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("PRAGMA journal_mode=WAL");
            writer.Execute("CREATE TABLE sessions (id TEXT PRIMARY KEY, title TEXT, thought_level TEXT); INSERT INTO sessions VALUES ('s1', 'private', 'disabled'), ('s2', 'x', NULL)");
        }

        CloseWal(path);
        var store = WorkBuddyPaths.Sessions(_temp.Path);
        Assert.True(store.Refresh(["s1", "s2", "gone"]));
        Assert.Equal(("off", "selected"), Pair(store.Of("s1"), ExecutionModeKind.Effort));
        Assert.True(store.Of("s2").IsEmpty);
        Assert.True(store.Of("gone").IsEmpty);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*-*"));
        Assert.False(store.Refresh(["s1", "s2"]));

        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("UPDATE sessions SET thought_level='high' WHERE id='s2'");
            Assert.True(store.Refresh(["s1", "s2"]));
            Assert.Equal("high", Effort(store.Of("s2")));
        }
    }

    [Theory]
    [InlineData("CREATE TABLE other (id TEXT)")]
    [InlineData("CREATE TABLE sessions (id TEXT)")]
    public void AMissingTableOrColumnGivesNothing(string schema)
    {
        var path = _temp.Combine("workbuddy.db");
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute(schema);
        }

        var store = WorkBuddyPaths.Sessions(_temp.Path);
        Assert.False(store.Refresh(["s1"]));
        Assert.True(store.Of("s1").IsEmpty);
        Assert.False(store.Refresh(["s1"]));
    }

    [Fact]
    public void ALinkedOrMissingStoreGivesNothing()
    {
        var store = WorkBuddyPaths.Sessions(_temp.Path);
        Assert.False(store.Refresh(["s1"]));
        if (OperatingSystem.IsWindows()) return;
        var real = _temp.Combine("real.db");
        using (var writer = SqliteConnection.Open(real, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("CREATE TABLE sessions (id TEXT, thought_level TEXT); INSERT INTO sessions VALUES ('s1', 'high')");
        }

        File.CreateSymbolicLink(_temp.Combine("workbuddy.db"), real);
        Assert.False(store.Refresh(["s1"]));
        Assert.True(store.Of("s1").IsEmpty);
    }

    [Theory]
    [InlineData("""{"reasoning_config":{"enabled":false,"effort":"high"}}""", "off")]
    [InlineData("""{"reasoning_config":{"enabled":true,"effort":"high"}}""", "high")]
    [InlineData("""{"reasoning_config":{"effort":"max"},"service_tier":"priority"}""", "max")]
    [InlineData("""{"reasoning_config":"high"}""", "unknown")]
    [InlineData("""{"other":1}""", "unknown")]
    [InlineData("""[1]""", "unknown")]
    [InlineData("""not json""", "unknown")]
    public void HermesTakesOnlyTheReasoningConfig(string json, string expected) => Assert.Equal(expected, Effort(HermesLogSet.StateEffort(json)));

    [Fact]
    public void TheLedgerFreezesASelectedEffortAndNeverAConfiguredOne()
    {
        var engine = new SpeedEngine(Today, new PriceBook());
        var session = ExecutionModes.Of(ExecutionModeNative.Effort("medium", ExecutionModeEvidence.Selected, ExecutionModeScope.Session));
        var configured = ExecutionModes.Of(ExecutionModeNative.Effort("max", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration));
        engine.UpdateAgent(new AgentInfo("a", SourceKind.GrokBuild, null, "m", false, AgentPhase.Generating, null, Now) { SessionModes = session, ConfiguredModes = configured });
        engine.UpdateAgent(new AgentInfo("b", SourceKind.GrokBuild, null, "m", false, AgentPhase.Generating, null, Now) { ConfiguredModes = configured });
        engine.Upsert(Response("a", "r1", 100), isCompletion: true, seeded: false, liveNowMs: Now);
        engine.Upsert(Response("b", "r2", 50), isCompletion: true, seeded: false, liveNowMs: Now);

        // A later selection does not move what was frozen, and the response itself stays unknown.
        engine.UpdateAgent(new AgentInfo("a", SourceKind.GrokBuild, null, "m", false, AgentPhase.Generating, null, Now)
            { SessionModes = ExecutionModes.Of(ExecutionModeNative.Effort("low", ExecutionModeEvidence.Selected, ExecutionModeScope.Session)) });
        engine.Upsert(Response("a", "r1", 140), isCompletion: false, seeded: false, liveNowMs: Now);
        var frame = engine.Compute(Now, Today, observeLive: true);
        var rows = frame.Report.Counters.ByMode.Where(p => p.Key.Kind == ExecutionModeKind.Effort).ToDictionary(p => (p.Key.Mode, p.Key.Evidence), p => p.Value.Output);
        Assert.Equal(new Dictionary<(string, string), long> { [("medium", "selected")] = 140, [("unknown", "unknown")] = 50 }, rows);
        Assert.All(frame.TodayModes.Where(m => m.Kind == ExecutionModeKind.Effort), m => Assert.Equal("unknown", m.Mode));
    }

    [Fact]
    public void WorkBuddyAgentsShowTheirSessionEffortThenTheConfiguredOne()
    {
        var data = _temp.Combine(".workbuddy");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var today = now - 3_600_000;
        _temp.WriteFile(".workbuddy/projects/p/s1.jsonl", WorkBuddyLines.User(now - 60_000, "u1") + "\n");
        _temp.WriteFile(".workbuddy/projects/p/s2.jsonl", WorkBuddyLines.User(now - 60_000, "u2") + "\n");
        _temp.WriteFile(".workbuddy/settings.json", """{"reasoningEffort":"low"}""");
        using (var writer = SqliteConnection.Open(Path.Combine(data, "workbuddy.db"), SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("CREATE TABLE sessions (id TEXT PRIMARY KEY, thought_level TEXT); INSERT INTO sessions VALUES ('s1', 'xhigh')");
        }

        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.WorkBuddy }),
            new SourceLocations("", "", "", "", [], WorkBuddyRoot: SourceLocations.WorkBuddyRootOf(data)), today);
        core.Seed(now, today, CancellationToken.None);
        var agents = core.BuildSnapshot(now, today, MonitorState.Running, observeLive: false).Snapshot.Agents.ToDictionary(a => Path.GetFileNameWithoutExtension(a.Id));
        Assert.Equal(("xhigh", "selected"), Pair(agents["s1"], ExecutionModeKind.Effort));
        Assert.Equal(("low", "configured"), Pair(agents["s2"], ExecutionModeKind.Effort));

        using (var writer = SqliteConnection.Open(Path.Combine(data, "workbuddy.db"), SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("INSERT INTO sessions VALUES ('s2', 'max')");
        }

        Assert.True(core.Rediscover(now, today, CancellationToken.None));
        agents = core.BuildSnapshot(now, today, MonitorState.Running, observeLive: false).Snapshot.Agents.ToDictionary(a => Path.GetFileNameWithoutExtension(a.Id));
        Assert.Equal(("max", "selected"), Pair(agents["s2"], ExecutionModeKind.Effort));
    }

    [Fact]
    public void HermesAgentsFollowStateDbAndConfigAtRediscoveryAndCreateNoSidecars()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000 * 1000;
        var today = now - 3_600_000;
        var home = _temp.Combine("hermes");
        const string Session = "20261028_101500_a1b2c3";
        _temp.WriteFile("hermes/config.yaml", "agent:\n  reasoning_effort: low\n  service_tier: priority\n");
        HermesLines.Append(Path.Combine(home, "logs", "agent.log"),
            HermesLines.Turn(now - 60_000, TimeZoneInfo.Utc, Session), HermesLines.Call(now - 50_000, TimeZoneInfo.Utc, Session, 1, 100, 20));
        var state = Path.Combine(home, "state.db");
        using (var writer = SqliteConnection.Open(state, SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("PRAGMA journal_mode=WAL");
            writer.Execute("""CREATE TABLE sessions (id TEXT PRIMARY KEY, model_config TEXT, system_prompt TEXT)""");
        }

        CloseWal(state);
        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Hermes }),
            new SourceLocations("", "", "", "", [], HermesRoot: home, HermesChosen: true), today, zone: TimeZoneInfo.Utc);
        core.Seed(now, today, CancellationToken.None);
        var agent = Assert.Single(core.BuildSnapshot(now, today, MonitorState.Running, observeLive: false).Snapshot.Agents);
        Assert.Equal(("low", "configured"), Pair(agent, ExecutionModeKind.Effort));
        Assert.Equal(("fast", "configured"), Pair(agent, ExecutionModeKind.Speed));
        Assert.DoesNotContain(Directory.GetFiles(home), f => f.EndsWith("-wal", StringComparison.Ordinal) || f.EndsWith("-shm", StringComparison.Ordinal));

        using (var writer = SqliteConnection.Open(state, SqliteOpenMode.ReadWriteCreate))
        {
            using var insert = writer.Prepare("INSERT INTO sessions VALUES (?1, ?2, 'never read')");
            insert.Bind(1, Session).Bind(2, """{"reasoning_config":{"enabled":true,"effort":"xhigh"}}""").Run();
        }

        Assert.True(core.Rediscover(now, today, CancellationToken.None));
        agent = Assert.Single(core.BuildSnapshot(now, today, MonitorState.Running, observeLive: false).Snapshot.Agents);
        Assert.Equal(("xhigh", "selected"), Pair(agent, ExecutionModeKind.Effort));
        Assert.Equal(("fast", "configured"), Pair(agent, ExecutionModeKind.Speed));
    }

    [Fact]
    public void TheHistoryCountsARowsEffortAndKeepsSessionAndConfiguredValuesOut()
    {
        var enabled = new HashSet<SourceKind> { SourceKind.WorkBuddy, SourceKind.WorkBuddyAi, SourceKind.GrokBuild, SourceKind.Hermes, SourceKind.OpenClaw };
        var locations = new SourceLocations("", "", "", "", [],
            WorkBuddyRoot: WorkBuddyFixture.RootOf(SourceKind.WorkBuddy), WorkBuddyAiRoot: WorkBuddyFixture.RootOf(SourceKind.WorkBuddyAi),
            GrokBuildRoot: FixtureLocator.PathOf("grok/sessions"), HermesRoot: FixtureLocator.PathOf("hermes/home"), OpenClawRoots: [FixtureLocator.PathOf("openclaw/.openclaw")]);
        using var store = HistoryStore.Open(_temp.Combine("history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        analyzer.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        var rows = analyzer.Days().SelectMany(d => d.Modes).Where(m => m.Kind == ExecutionModeKind.Effort).ToList();
        Assert.All(rows.Where(m => m.Source != SourceKind.OpenClaw), m => Assert.Equal(("unknown", "unknown"), (m.Mode, m.Evidence)));
        Assert.Contains(rows, m => m.Source == SourceKind.OpenClaw && m.Evidence == "recorded");
        Assert.DoesNotContain(rows, m => m.Evidence == "configured");
    }

    [Fact]
    public void AnAntigravityExecutorReadLaterUpdatesItsResponse()
    {
        var parser = new AntigravityParser("/store/conversations/x.db", new Deduper());
        var sink = new RecordingSink();
        var usage = new AntigravityUsage(10, 20, 0, 0, 20, "r1", "priority");
        var step = new AntigravityStep(1000, null, 2000, null, null, null, usage);
        var batch = (AntigravityBatch)parser.NewBatch();
        batch.Generations.Add(new AntigravityGeneration(1, new HashSet<ulong> { 5 }, "gemini-3.7-flash", "gemini-3.7-flash-exp-b", null));
        batch.Steps.Add(new AntigravityStepRow(5, StepCodes.PlannerResponse, StepCodes.Done, step));
        batch.ApplyTo(sink);
        Assert.Equal("unknown", Effort(sink.Latest[0].Modes));
        Assert.Equal(("fast", "served"), Pair(sink.Latest[0].Modes, ExecutionModeKind.Speed));

        var late = (AntigravityBatch)parser.NewBatch();
        late.Executors.Add(new AntigravityExecutor(2, 0, 9, "gemini-3.7-flash-high"));
        late.ApplyTo(sink);
        Assert.Equal(("high", "recorded"), Pair(sink.Latest[0].Modes, ExecutionModeKind.Effort));
        Assert.Equal(("high", "recorded"), Pair(parser.Modes, ExecutionModeKind.Effort));
        Assert.Equal([true, false], sink.Emissions.Select(e => e.IsCompletion));
    }

    [Fact]
    public void AnAntigravityExecutorOfAnotherModelSaysNothing()
    {
        var parser = new AntigravityParser("/store/conversations/x.db", new Deduper());
        var sink = new RecordingSink();
        var step = new AntigravityStep(1000, null, 2000, null, null, null, new AntigravityUsage(10, 20, 0, 0, 20, "r1"));
        var batch = (AntigravityBatch)parser.NewBatch();
        batch.Generations.Add(new AntigravityGeneration(1, new HashSet<ulong> { 5 }, "claude-opus-5-5", "claude-opus-5-5", null));
        batch.Executors.Add(new AntigravityExecutor(2, 0, 9, "gemini-3.7-flash-high"));
        batch.Steps.Add(new AntigravityStepRow(5, StepCodes.PlannerResponse, StepCodes.Done, step));
        batch.ApplyTo(sink);
        Assert.True(sink.Latest[0].Modes.IsEmpty);
    }

    // macOS's SQLite keeps both files of a closed WAL database; the other platforms remove them.
    private static void CloseWal(string path)
    {
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    private static ParsedResponse Response(string agent, string key, long output) =>
        new(key, SourceKind.GrokBuild, agent, "m", Now - 3000, Now - 1000, new TokenCounts(10, 0, 0, output, 0));

    private static string Effort(ExecutionModes modes) => modes.Attribute(ExecutionModeKind.Effort) is { IsKnown: true } own ? own.Mode
        : ExecutionModes.Current(ExecutionModeKind.Effort, ExecutionModes.Empty, modes, modes).Mode;

    private static (string, string) Pair(ExecutionModes modes, ExecutionModeKind kind) => modes.Attribute(kind) is var value ? (value.Mode, value.Evidence) : default;

    private static (string, string) Pair(AgentStatus agent, ExecutionModeKind kind) => agent.Current(kind) is var value ? (value.Mode, value.Evidence) : default;
}
