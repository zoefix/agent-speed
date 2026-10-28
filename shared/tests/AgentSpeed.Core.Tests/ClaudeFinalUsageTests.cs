using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.ClaudeLines;

namespace AgentSpeed.Core.Tests;

public sealed class ClaudeFinalUsageTests
{
    private const string Agent = "/home/me/.claude/projects/p/s.jsonl";

    [Fact]
    public void SnapshotLinesAreUnfinalizedUntilAStopReasonArrives()
    {
        var parser = new ClaudeParser(Agent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UserText(T0, "go"), Snapshot(T0 + 2000, "msg_S", "thinking"));
        Assert.False(Assert.Single(sink.Latest).Final);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(T0, parser.InFlightStartMs);

        Feed.Lines(parser, sink, Snapshot(T0 + 3000, "msg_S", "tool_use"));
        Assert.False(Assert.Single(sink.Latest).Final);
        Assert.Equal(AgentPhase.Tools, parser.Phase);

        Feed.Lines(parser, sink, Assistant(T0 + 4000, "msg_S", 300, "\"tool_use\""));
        var response = Assert.Single(sink.Latest);
        Assert.True(response.Final);
        Assert.Equal(300, response.Tokens.Output);
        Assert.Equal(T0 + 4000, response.EndMs);
    }

    [Fact]
    public void FinalNeverReturnsToFalse()
    {
        var parser = new ClaudeParser(Agent, new Deduper());
        var sink = new RecordingSink();

        Feed.Lines(parser, sink, UserText(T0, "go"), Assistant(T0 + 1000, "msg_F", 50, "\"end_turn\""), Snapshot(T0 + 1500, "msg_F", "text"));

        Assert.True(Assert.Single(sink.Latest).Final);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
    }

    [Fact]
    public void UnfinalizedResponsesCountTodayAndRaiseAWarning()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var today = now - 3_600_000;
        temp.WriteFile("claude/projects/p/s.jsonl", string.Join('\n',
            UserText(now - 9000, "a"), Assistant(now - 7000, "msg_ok", 100),
            UserText(now - 6000, "b"), Snapshot(now - 4000, "msg_snap", "tool_use")) + "\n");
        var core = new MonitorCore(
            MonitorConfiguration.Default, temp.Combine("codex"), temp.Combine("claude", "projects"), temp.Combine("dsh"), today);

        core.Seed(now, today, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(now, today, MonitorState.Running, observeLive: false);

        Assert.Equal(2, snapshot.Today.Requests);
        Assert.Equal(108, snapshot.Today.Output);
        Assert.Equal(50.0, snapshot.AverageToday!.Value, 6);
        Assert.Equal(1, Assert.Single(snapshot.Warnings.OfType<MonitorWarning.UnfinalizedUsage>()).Count);
        Assert.Equal(AgentPhase.Tools, Assert.Single(snapshot.Agents).Phase);
    }

    private static string Snapshot(long ms, string id, string blockType) =>
        $$"""{"type":"assistant","timestamp":"{{Ts.Iso(ms)}}","requestId":"req_{{id}}","message":{"id":"{{id}}","model":"claude-opus-5-5","content":[{"type":"{{blockType}}"}],"stop_reason":null,"usage":{"input_tokens":2,"cache_read_input_tokens":600,"output_tokens":8} } }""";
}
