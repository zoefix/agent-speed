using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Tests.Perf;

/// <summary>
/// The performance score in the live monitor (METRICS 6.9, 6.10): seeded actions count for the score but not the signals,
/// so a relaunch scores the same; only a call's first result counts; the baseline comes from the stored records.
/// </summary>
public sealed class PerfMonitorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public PerfMonitorTests()
    {
        _todayStart = _now - 3_600_000;
        Directory.CreateDirectory(_temp.Combine("codex", "sessions"));
        Directory.CreateDirectory(_temp.Combine("claude", "projects"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ASeededAgentScoresLikeOneReadLiveAndItsSeededActionsLeaveTheSignalsAlone()
    {
        var path = Write("p/a.jsonl", Turns(_now - 300_000, 5));

        using var seeded = Core();
        seeded.Seed(_now, _todayStart, CancellationToken.None);
        var agent = Agent(seeded);
        Assert.Equal(5, agent.Perf!.N);
        Assert.Equal(5, agent.Perf.Tools);
        Assert.NotNull(agent.Intelligence.Shown);
        Assert.True(agent.Intelligence.Provisional);
        Assert.Equal(WorkSignals.None, agent.Work);

        using var live = Core();
        File.WriteAllText(path, string.Empty);
        live.Seed(_now, _todayStart, CancellationToken.None);
        File.WriteAllText(path, Turns(_now - 300_000, 5));
        Assert.True(live.ProcessPaths([path], _now, _todayStart));
        Assert.Equal(agent.Perf, Agent(live).Perf);
        Assert.Equal(agent.Intelligence.Value, Agent(live).Intelligence.Value);
    }

    [Fact]
    public void TheSourceAndTheSetTakeTheHighestScore()
    {
        Write("p/fast.jsonl", Turns(_now - 300_000, 3, output: 600));
        Write("p/slow.jsonl", Turns(_now - 300_000, 3, output: 60, ids: "s"));
        using var core = Core();
        core.Seed(_now, _todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false);
        var best = snapshot.Agents.MaxBy(a => a.Intelligence.Value)!;
        var source = snapshot.Sources.Single(s => s.Kind == SourceKind.ClaudeCode).Intelligence;
        Assert.Equal(best.Intelligence.Value, source.Index);
        Assert.Equal(best.Id, source.Score.AgentId);
        Assert.Equal(best.Intelligence.Value, OrbSubset.Reading(snapshot, null).Intelligence.Index);
    }

    [Fact]
    public void TheBaselineIsTheStoredRecordsOfTheAgentsSourceAndModel()
    {
        Write("p/a.jsonl", Turns(_now - 300_000, 3));
        using var core = Core();
        var today = DateTimeOffset.FromUnixTimeMilliseconds(_now).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var yesterday = DateTimeOffset.FromUnixTimeMilliseconds(_now).ToLocalTime().AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var sums = new PerfSums { N = 30, OutputSum = 30_000, DurationMsSum = 600_000 };
        core.PerfBaselines = new PerfHistory([new PerfRecord(yesterday, SourceKind.ClaudeCode, "claude-opus-5-5", sums)], today);
        core.Seed(_now, _todayStart, CancellationToken.None);
        var perf = Agent(core).Perf!;
        Assert.Equal(1000, perf.Baseline!.Value, 9);
        Assert.Equal(PerfFormula.Round(100 * (perf.Score!.Value / 1000 - 1)), perf.VsUsualPercent);
    }

    [Fact]
    public void OnlyTheFirstResultOfACallCounts()
    {
        var completed = new List<WorkAction>();
        var log = new WorkLog { Completed = completed.Add };
        log.Call(new ToolCall("c1", "Bash", 1), 1000, "m", seeded: true);
        Assert.True(log.Outcome(new ToolOutcome("c1", null), 1500));
        Assert.False(log.Outcome(new ToolOutcome("c1", true), 1800));
        Assert.False(log.Outcome(new ToolOutcome("other", true), 1900));
        var action = Assert.Single(log.Actions);
        Assert.Equal((1500L, (bool?)null, "m", true), (action.ResultMs!.Value, action.Failed, action.Model, action.Seeded));
        Assert.Single(completed);
    }

    [Fact]
    public void AWholeFileReadKeepsEveryActionAndTheLiveLogTheLast64()
    {
        var capped = new WorkLog();
        var whole = new WorkLog(capacity: null);
        for (var i = 0; i < 70; i++)
        {
            capped.Call(new ToolCall("c" + i, "Read", (ulong)i), i);
            whole.Call(new ToolCall("c" + i, "Read", (ulong)i), i);
        }

        Assert.Equal((64, 70), (capped.Actions.Count, whole.Actions.Count));
        Assert.False(capped.Outcome(new ToolOutcome("c0", false), 100));
        Assert.True(whole.Outcome(new ToolOutcome("c0", false), 100));
    }

    private MonitorCore Core() =>
        new(MonitorConfiguration.Default, _temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh"), _todayStart)
        {
            Clock = () => _now,
        };

    private static AgentStatus Agent(MonitorCore core) =>
        Assert.Single(core.BuildSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, MonitorState.Running, observeLive: false).Snapshot.Agents);

    private string Write(string relative, string content)
    {
        var path = Path.Combine([_temp.Combine("claude", "projects"), .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Requests that each call a tool and get its result.</summary>
    private static string Turns(long start, int count, long output = 240, string ids = "m")
    {
        var lines = new List<string>();
        var t = start;
        for (var i = 0; i < count; i++)
        {
            var call = ids + "call" + i;
            lines.Add(ClaudeLines.ApiRequest(t += 500));
            lines.Add(ClaudeLines.Assistant(t += 2000, ids + i, output, "\"tool_use\"", input: 3000, cacheRead: 2800).Replace(
                """[{"type":"text","text":"x"}]""",
                $$"""[{"type":"tool_use","id":"{{call}}","name":"Bash","input":{"command":"ls {{i}}"} }]""", StringComparison.Ordinal));
            lines.Add(ClaudeLines.User(t += 700, $$"""[{"type":"tool_result","tool_use_id":"{{call}}","is_error":false,"content":"ok"}]"""));
        }

        return string.Concat(lines.Select(l => l + "\n"));
    }
}
