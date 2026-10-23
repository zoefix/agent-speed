using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Tests;

/// <summary>Whole-file reads while running happen off the live loop (METRICS 5.3); vanished files leave the agents list (6.7).</summary>
public sealed class BackgroundReadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose() => _temp.Dispose();

    private string CodexRoot => _temp.Combine("codex", "sessions");

    private string ClaudeRoot => _temp.Combine("claude", "projects");

    [Fact]
    public void ASlowWholeFileReadNeverHoldsTheLoopAndIsAppliedInOneStep()
    {
        Directory.CreateDirectory(CodexRoot);
        Directory.CreateDirectory(ClaudeRoot);
        var today = _now - 3_600_000;
        using var gate = new ManualResetEventSlim(false);
        using var core = new MonitorCore(MonitorConfiguration.Default,
            new SourceLocations(CodexRoot, ClaudeRoot, string.Empty, string.Empty, []), today)
        {
            ReadWholeFilesInBackground = true,
            BeforeBackgroundRead = _ => gate.Wait(TimeSpan.FromSeconds(20)),
        };
        core.Seed(_now, today, CancellationToken.None);

        var zst = WriteZst("rollout-b.jsonl.zst", _now - 20_000, "resp-zst", 150);
        var watch = Stopwatch.StartNew();
        Assert.False(core.ProcessPaths([zst], _now, today));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, core.PendingReadCount);

        var plain = _temp.WriteFile("claude/projects/p/s.jsonl",
            ClaudeLines.UserText(_now - 9000, "go") + "\n" + ClaudeLines.Assistant(_now - 8000, "msg_live", 40) + "\n");
        Assert.True(core.ProcessPaths([plain], _now, today));
        Assert.False(core.ApplyCompletedReads());
        Assert.Equal(40, core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: true).Snapshot.Today.Output);

        gate.Set();
        SpinWait.SpinUntil(() => core.ApplyCompletedReads(), TimeSpan.FromSeconds(10));
        var (snapshot, events) = core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: true);
        Assert.Equal((0, 190L), (core.PendingReadCount, snapshot.Today.Output));
        Assert.Equal(150, Assert.Single(events.OfType<SpeedEvent.ResponseCompleted>()).Response.OutputTokens);
    }

    [Fact]
    public async Task LiveMonitorKeepsPublishingWhileAWholeFileIsRead()
    {
        Directory.CreateDirectory(CodexRoot);
        Directory.CreateDirectory(ClaudeRoot);
        using var gate = new ManualResetEventSlim(false);
        var snapshots = new ConcurrentQueue<SpeedSnapshot>();
        var hub = await Task.Run(() => new SpeedHub());
        hub.SnapshotChanged += (_, s) => snapshots.Enqueue(s);
        using var monitor = new LiveMonitor(hub, new MonitorConfiguration(
            new HashSet<SourceKind>(Enum.GetValues<SourceKind>()), CodexRoot, ClaudeRoot, _temp.Combine("dsh"),
            _temp.Combine("opencode"), _temp.Combine("gemini", "tmp"), _temp.Combine("antigravity"), _temp.Combine("antigravity-cli")))
        {
            BeforeBackgroundRead = _ => gate.Wait(TimeSpan.FromSeconds(20)),
        };
        monitor.Start();
        await WaitAsync(() => snapshots.Any(s => s.State.Kind == MonitorStateKind.Running));

        WriteZst("rollout-live.jsonl.zst", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000, "resp-live", 77);
        await Task.Delay(1500);
        var whileBlocked = snapshots.Count;
        await Task.Delay(1500);
        Assert.True(snapshots.Count - whileBlocked >= 2, "snapshots kept coming while the read was blocked");
        Assert.All(snapshots, s => Assert.Equal(0, s.Today.Output));

        gate.Set();
        await WaitAsync(() => snapshots.Any(s => s.Today.Output == 77));
    }

    [Fact]
    public void AnAgentWhoseFileIsGoneLeavesTheListButItsResponsesStay()
    {
        Directory.CreateDirectory(CodexRoot);
        var today = _now - 3_600_000;
        var a = _temp.WriteFile("claude/projects/p/a.jsonl",
            ClaudeLines.UserText(_now - 9000, "go") + "\n" + ClaudeLines.Assistant(_now - 8000, "msg_a", 40) + "\n");
        var b = _temp.WriteFile("claude/projects/p/b.jsonl",
            ClaudeLines.UserText(_now - 7000, "go") + "\n" + ClaudeLines.Assistant(_now - 6000, "msg_b", 60) + "\n");
        using var core = new MonitorCore(MonitorConfiguration.Default, CodexRoot, ClaudeRoot, _temp.Combine("dsh"), today);
        core.Seed(_now, today, CancellationToken.None);
        Assert.Equal(2, core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: false).Snapshot.Agents.Count);

        File.Delete(a);
        Assert.True(core.ProcessPaths([a], _now, today));
        var afterWatcher = core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: false).Snapshot;
        Assert.Equal(b, Assert.Single(afterWatcher.Agents).Id);
        Assert.Equal(100, afterWatcher.Today.Output);

        File.Delete(b);
        Assert.True(core.Rediscover(_now, today, CancellationToken.None));
        var afterRediscovery = core.BuildSnapshot(_now, today, MonitorState.Running, observeLive: false).Snapshot;
        Assert.Empty(afterRediscovery.Agents);
        Assert.Equal((2L, 100L), (afterRediscovery.Today.Requests, afterRediscovery.Today.Output));
    }

    private string WriteZst(string name, long t, string responseId, long output)
    {
        var lines = string.Concat(new[]
        {
            CodexLines.SessionMeta(t, "thread-" + responseId, "/home/me/comet"),
            CodexLines.TurnContext(t + 10),
            CodexLines.TaskStarted(t + 1000),
            CodexLines.UsageRecord(t + 4000, responseId, CodexLines.Usage(600, 500, output, 0), "thread-" + responseId),
            CodexLines.TaskComplete(t + 4100),
        }.Select(l => l + "\n"));
        using var compressor = new ZstdSharp.Compressor();
        var path = Path.Combine(CodexRoot, "2026", "10", "21", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, compressor.Wrap(Encoding.UTF8.GetBytes(lines)).ToArray());
        return path;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(25);
        }
    }
}
