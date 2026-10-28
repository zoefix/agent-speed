using System.Text;
using AgentSpeed.Core.GrokBuild;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Kimi;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.ZCode;

namespace AgentSpeed.Core.Tests;

public sealed class HarnessEdgeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly long Start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void KimiIgnoresPartialMessagesAndKeepsTheActualRequestStartIncludingFirstTokenWait()
    {
        var parser = new KimiParser(_temp.Combine("w", "s", "agents", "main", "wire.jsonl"), new Deduper());
        var sink = new RecordingSink();
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"llm.request","time":1000,"agentId":"main"}"""), sink);
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_loop_event","time":1200,"agentId":"main","event":{"type":"step.begin","uuid":"step"}}"""), sink);
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_message","time":2000,"agentId":"main","message":{"role":"assistant","id":"m","partial":true,"usage":{"output":10}}}"""), sink);
        Assert.Empty(sink.Latest);
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_message","time":3000,"agentId":"main","message":{"role":"assistant","id":"m","usage":{"output":20},"llmTiming":{"llmFirstTokenLatencyMs":200,"llmStreamDurationMs":300}}}"""), sink);
        Assert.Equal((long?)1000, Assert.Single(sink.Latest).StartMs);
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_loop_event","time":3400,"agentId":"main","event":{"type":"step.end","messageId":"m","usage":{"output":20}}}"""), sink);
        Assert.Equal(3000, Assert.Single(sink.Latest).EndMs);
    }

    [Fact]
    public void KimiWithoutAProviderOrStepIdUsesItsNativeSessionAgentAndTimestampIdentity()
    {
        var parser = new KimiParser(_temp.Combine("w", "s", "agents", "main", "wire.jsonl"), new Deduper());
        var sink = new RecordingSink();
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_message","time":2000,"agentId":"main","message":{"role":"assistant","usage":{"output":20}}}"""), sink);
        Assert.Equal("kimi:s:main:2000", Assert.Single(sink.Latest).Key);
    }

    [Fact]
    public void OutOfRangeNativeTimestampsNeverReachTheEngineOrUserInterface()
    {
        var parser = new KimiParser(_temp.Combine("w", "s", "agents", "main", "wire.jsonl"), new Deduper());
        var sink = new RecordingSink();
        parser.Feed(Encoding.UTF8.GetBytes("""{"type":"context.append_message","time":9223372036854775000,"agentId":"main","message":{"role":"assistant","id":"m","usage":{"output":20}}}"""), sink);
        Assert.Empty(sink.Latest);
        Assert.Null(parser.LastLineMs);
        _temp.WriteFile("g/s/summary.json", "{}");
        var grok = new GrokParser(_temp.Combine("g", "s", "updates.jsonl"), new Deduper());
        grok.Feed(Encoding.UTF8.GetBytes("""{"timestamp":9223372036854,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"p","usage":{"outputTokens":20}}}}"""), sink);
        Assert.Empty(sink.Latest);
        Assert.Null(grok.LastLineMs);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void ADelayedGrokSummaryFlushesOrSuppressesUsageWithoutAnyNewUpdateLines(bool subagent, long requests)
    {
        var path = _temp.WriteFile("sessions/work/child/updates.jsonl", """{"timestamp":1792497665,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"p","usage":{"outputTokens":20,"modelCalls":1,"apiDurationMs":1000}}}}""" + "\n");
        var root = _temp.Combine("sessions");
        var locations = new SourceLocations("", "", "", "", [], GrokBuildRoot: root);
        var enabled = new HashSet<SourceKind> { SourceKind.GrokBuild };
        using var core = new MonitorCore(new MonitorConfiguration(enabled), locations, Start);
        core.Seed(Start + 43_300_000, Start, CancellationToken.None);
        Assert.Equal(0, core.BuildSnapshot(Start + 43_300_000, Start, MonitorState.Running, false).Item1.Today.Requests);
        var summary = _temp.WriteFile("sessions/work/child/summary.json", subagent
            ? """{"info":{"id":"child","cwd":"/projects/comet"},"parent_session_id":"main","session_kind":"subagent","current_model_id":"grok-4.5"}"""
            : """{"info":{"id":"child","cwd":"/projects/comet"},"session_kind":"headless","current_model_id":"grok-4.5"}""");
        Assert.True(core.ProcessPaths([summary], Start + 43_300_000, Start));
        Assert.Equal(requests, core.BuildSnapshot(Start + 43_300_000, Start, MonitorState.Running, false).Item1.Today.Requests);
        Assert.False(core.ProcessPaths([summary], Start + 43_300_000, Start));
        Assert.Equal(path, Assert.Single(core.BuildSnapshot(Start + 43_300_000, Start, MonitorState.Running, false).Item1.Agents).Id);
    }

    [Fact]
    public void FlushingAnOldGrokBatchNeverChangesTheNewTurnPhase()
    {
        var path = _temp.WriteFile("g/s/updates.jsonl",
            """{"timestamp":1000,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"old","usage":{"outputTokens":20,"apiDurationMs":1000}}}}""" + "\n" +
            """{"timestamp":1001,"method":"session/update","params":{"update":{"sessionUpdate":"user_message_chunk"}}}""" + "\n");
        var parser = new GrokParser(path, new Deduper());
        var tailer = new FileTailer(path);
        var sink = new RecordingSink();
        var batch = parser.NewBatch();
        tailer.ReadAppended(batch);
        batch.ApplyTo(sink);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Empty(sink.Latest);
        _temp.WriteFile("g/s/summary.json", "{}");
        batch = parser.NewBatch();
        tailer.ReadAppended(batch);
        batch.ApplyTo(sink);
        Assert.Single(sink.Latest);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal((long?)1001000, parser.InFlightStartMs);
        Assert.Equal((long?)1001000, parser.LastLineMs);
    }

    [Fact]
    public void GrokPendingCapacityCountsModelBucketsAndSignalsOverflow()
    {
        var parser = new GrokParser(_temp.Combine("g", "s", "updates.jsonl"), new Deduper());
        var line = new GrokLine
        {
            Status = LineStatus.Valid, Type = "turn_completed", TimeMs = 1000, PromptId = "p",
            Usage = Enumerable.Range(0, 300).Select(i => new GrokUsage("m" + i, new TokenCounts(Output: 1), 500, 1)).ToList(),
        };
        parser.Apply(line, new RecordingSink());
        Assert.Equal(256, parser.PendingUsageCount);
        Assert.True(parser.PendingOverflow);
    }

    [Fact]
    public void GrokUsesNativeEventIdThenSessionTimestampWhenPromptIdIsMissing()
    {
        _temp.WriteFile("g/s/summary.json", "{}");
        var parser = new GrokParser(_temp.Combine("g", "s", "updates.jsonl"), new Deduper());
        var sink = new RecordingSink();
        parser.Feed(Encoding.UTF8.GetBytes("""{"timestamp":1000,"method":"_x.ai/session/update","params":{"sessionId":"s","_meta":{"eventId":"evt"},"update":{"sessionUpdate":"turn_completed","model_id":"grok-4.5","usage":{"outputTokens":20}}}}"""), sink);
        parser.Feed(Encoding.UTF8.GetBytes("""{"timestamp":1001,"method":"_x.ai/session/update","params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","model_id":"grok-4.5","usage":{"outputTokens":20}}}}"""), sink);
        Assert.Equal(["grok:evt:grok-4.5", "grok:s:1001000:grok-4.5"], sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void AnOrphanGrokChildWithoutAParentIsCountedAsAnIndependentSession()
    {
        _temp.WriteFile("g/s/summary.json", """{"info":{"id":"s"},"session_kind":"subagent"}""");
        var parser = new GrokParser(_temp.Combine("g", "s", "updates.jsonl"), new Deduper());
        var sink = new RecordingSink();
        parser.Feed(Encoding.UTF8.GetBytes("""{"timestamp":1000,"method":"_x.ai/session/update","params":{"update":{"sessionUpdate":"turn_completed","prompt_id":"p","usage":{"outputTokens":20}}}}"""), sink);
        Assert.False(parser.IsSubagent);
        Assert.Single(sink.Latest);
    }

    [Fact]
    public void GrokParentsResolveAcrossWorkspaceGroupsAndPreferACopyInTheirOwnGroup()
    {
        AgentLink Main(string group) => new($"/grok/{group}/main/updates.jsonl", LinkKind.GrokBuildSession, "main", null, 1);
        var child = new AgentLink("/grok/worktree/child/updates.jsonl", LinkKind.GrokBuildSession, "child", "main", 2);
        var parents = ParentLinks.Resolve([Main("original"), child], StringComparer.Ordinal);
        Assert.Equal(Main("original").Id, parents[child.Id]);
        parents = ParentLinks.Resolve([Main("original"), Main("worktree"), child], StringComparer.Ordinal);
        Assert.Equal(Main("worktree").Id, parents[child.Id]);
    }

    [Fact]
    public void ZCodeFindsADatabaseCreatedAfterStartupAndRemovesAgentsWhenItDisappears()
    {
        var data = _temp.Combine(".zcode");
        var source = new ZCodeSource(data, new Deduper());
        Assert.Empty(source.Seed(Start).Agents);
        var path = _temp.Combine(".zcode", "cli", "db", "db.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), path);
        Assert.Equal(3, source.Refresh(force: true).Agents.Count);
        Assert.Equal(path, source.DatabasePath);
        File.Move(path, path + ".retained");
        Assert.Equal(3, source.Refresh().Removed.Count);
        Assert.Empty(source.Agents);
    }

    [Fact]
    public void MetadataReadsUseTheOpenedFileLengthAndRejectOversizedDocuments()
    {
        var path = _temp.WriteFile("metadata.json", "{\"cwd\":\"/project\"}");
        using (var read = Harnesses.HarnessJson.ReadMetadata(path, 1024))
        {
            Assert.Equal("/project", read!.RootElement.GetProperty("cwd").GetString());
        }

        File.WriteAllText(path, "{\"padding\":\"" + new string('x', 1024) + "\"}");
        Assert.Null(Harnesses.HarnessJson.ReadMetadata(path, 1024));
    }

    [Fact]
    public void ZCodeStreamActivityUsesSessionTimeUpdatedByNativePartWrites()
    {
        var path = _temp.Combine("db.sqlite");
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), path);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            using var message = writer.Prepare("INSERT INTO message(id,session_id,time_created,time_updated,data) VALUES(?,?,?,?,?)");
            message.Bind(1, "stream").Bind(2, "sess-z-main").Bind(3, Start - 1_000_000).Bind(4, Start - 1_000_000)
                .Bind(5, "{\"role\":\"assistant\",\"time\":{\"created\":" + (Start - 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}").Run();
            using var update = writer.Prepare("UPDATE session SET time_updated=? WHERE id='sess-z-main'");
            update.Bind(1, Start + 50_000_000).Run();
        }

        using var database = ZCodeDatabase.Open(path);
        var agent = Assert.Single(database.ReadAgents(Start), a => a.SessionId == "sess-z-main");
        Assert.Equal(AgentPhase.Generating, agent.Phase);
        Assert.Equal(Start + 50_000_000, agent.LastMs);
    }

    [Fact]
    public void ZCodeIncludesCrossDayCompletionsAndPreservesExplicitZeroTokenRequests()
    {
        var path = _temp.Combine("db.sqlite");
        File.Copy(FixtureLocator.PathOf("zcode/cli/db/db.sqlite"), path);
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            using var update = writer.Prepare("UPDATE model_usage SET started_at=?,completed_at=?,input_tokens=0,output_tokens=0 WHERE id='z-r1'");
            update.Bind(1, Start - 3_600_000).Bind(2, Start + 1000).Run();
        }

        using var database = ZCodeDatabase.Open(path);
        var row = Assert.Single(database.Read(Start), row => row.Id == "z-r1");
        Assert.Equal(0, row.Response(path)!.Tokens.Total);
        Assert.Equal(1, row.Response(path)!.Requests);
    }
}
