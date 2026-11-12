using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The facts a living character reacts to (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>), on each agent of the list:
/// whether output came since its in-flight request started, the newest end of its responses, and a live failure right
/// after a failure.
/// </summary>
public sealed class CharacterFactsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public CharacterFactsTests()
    {
        _todayStart = _now - 3_600_000;
        Directory.CreateDirectory(ClaudeRoot);
    }

    private string ClaudeRoot => _temp.Combine("claude", "projects");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LiveFailuresInARowAndTheWaitForOutputReachTheAgent()
    {
        var path = Path.Combine(ClaudeRoot, "p", "s-loop.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Lines([ClaudeLines.UserText(_now - 91_000, "go"), .. Runs(0, 2, _now - 90_000)]));
        var core = new MonitorCore(MonitorConfiguration.Default, _temp.Combine("codex"), ClaudeRoot, _temp.Combine("dsh"), _todayStart);
        core.Seed(_now, _todayStart, CancellationToken.None);
        var seeded = Assert.Single(Agents(core));
        Assert.Null(seeded.RepeatFailureAt);

        File.AppendAllText(path, Lines(Runs(2, 2, _now - 20_000)));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var failing = Assert.Single(Agents(core));
        var newestResult = DateTimeOffset.FromUnixTimeMilliseconds(_now - 20_000 + 3 * 3000 + 1000);
        Assert.Equal(newestResult, failing.RepeatFailureAt);
        // The failed result asks the model again: it waits for output since then.
        Assert.Equal(AgentPhase.Generating, failing.Phase);
        Assert.Equal(newestResult, failing.InFlightSince);
        Assert.False(failing.OutputSinceInFlight);
        Assert.True(failing.LastResponseEnd < newestResult);

        File.AppendAllText(path, Lines([ClaudeLines.Assistant(_now - 3000, "m9", 4, "null")]));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));
        var answering = Assert.Single(Agents(core));
        Assert.True(answering.OutputSinceInFlight);
        Assert.True(answering.LastResponseEnd >= newestResult);
        Assert.Equal(newestResult, answering.InFlightSince);
    }

    [Fact]
    public void AnIdleAgentHasNoInFlightStart()
    {
        var path = Path.Combine(ClaudeRoot, "p", "s-done.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Lines([ClaudeLines.UserText(_now - 5000, "hi"), ClaudeLines.Assistant(_now - 2000, "m1", 40)]));
        var core = new MonitorCore(MonitorConfiguration.Default, _temp.Combine("codex"), ClaudeRoot, _temp.Combine("dsh"), _todayStart);
        core.Seed(_now, _todayStart, CancellationToken.None);

        var agent = Assert.Single(Agents(core));

        Assert.Equal(AgentPhase.Idle, agent.Phase);
        Assert.Null(agent.InFlightSince);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(_now - 2000), agent.LastResponseEnd);
        Assert.Null(agent.RepeatFailureAt);
    }

    [Fact]
    public void ASuccessBetweenFailuresIsNoTrouble()
    {
        static WorkAction Result(long result, bool failed, bool seeded = false) =>
            new(result - 100, "Bash", 0, failed) { ResultMs = result, Seeded = seeded };

        Assert.Equal(2000L, SpeedEngine.RepeatFailure([Result(1000, true), Result(2000, true)]));
        Assert.Null(SpeedEngine.RepeatFailure([Result(1000, true), Result(1500, false), Result(2000, true)]));
        Assert.Null(SpeedEngine.RepeatFailure([Result(1000, true), Result(2000, true, seeded: true)]));
        // Results count in the order they were read, not in call order.
        Assert.Equal(3000L, SpeedEngine.RepeatFailure([Result(3000, true), Result(1000, false), Result(2000, true)]));
        Assert.Null(SpeedEngine.RepeatFailure([Result(1000, true)]));
        Assert.Null(SpeedEngine.RepeatFailure([Result(1000, true), new WorkAction(1500, "Bash", 0), Result(2000, true) with { Failed = null }]));
        Assert.Null(SpeedEngine.RepeatFailure([]));
    }

    /// <summary>Failing <c>Bash</c> runs: each a call, then its failed result a second later, three seconds apart.</summary>
    private static IEnumerable<string> Runs(int first, int count, long startMs)
    {
        for (var i = first; i < first + count; i++)
        {
            var at = startMs + i * 3000L;
            yield return ClaudeLines.Assistant(at, "m" + i, 5, "\"tool_use\"").Replace(
                """[{"type":"text","text":"x"}]""",
                $$$"""[{"type":"tool_use","id":"t{{{i}}}","name":"Bash","input":{"command":"swift test"}}]""", StringComparison.Ordinal);
            yield return ClaudeLines.User(at + 1000, $$"""[{"type":"tool_result","tool_use_id":"t{{i}}","is_error":true,"content":"failed"}]""");
        }
    }

    private static string Lines(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    private IReadOnlyList<AgentStatus> Agents(MonitorCore core) =>
        core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: true).Snapshot.Agents;
}
