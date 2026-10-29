using System.Collections.Concurrent;
using System.Diagnostics;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Tests;

public sealed class TraeBackgroundReadTests
{
    private static readonly long Day = Ts.Ms("2026-10-20T00:00:00Z");

    [Fact]
    public void ASlowTraeReadCoalescesNotificationsWithoutHoldingOtherSources()
    {
        using var temp = new TempDirectory();
        var path = Copy(temp, "trae/database.db");
        Directory.CreateDirectory(temp.Combine("claude/projects"));
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var reads = 0;
        var now = Day + 50_000_000;
        using var core = new MonitorCore(MonitorConfiguration.Default,
            new SourceLocations("", temp.Combine("claude/projects"), "", "", [], TraeStores: [path]), Day)
        {
            ReadWholeFilesInBackground = true,
            Clock = () => now,
            BeforeBackgroundRead = _ =>
            {
                Interlocked.Increment(ref reads);
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(20));
            },
        };
        core.Seed(now, Day, CancellationToken.None);
        CorrectUsage(path);
        var watch = Stopwatch.StartNew();
        Assert.False(core.ProcessPaths([path], now, Day));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            for (var i = 0; i < 100; i++) core.ProcessPaths([path], now, Day);
            Assert.Equal(1, core.PendingReadCount);
            Assert.Equal(1, Volatile.Read(ref reads));
            var plain = temp.WriteFile("claude/projects/p/s.jsonl",
                ClaudeLines.UserText(now - 9000, "go") + "\n" + ClaudeLines.Assistant(now - 8000, "msg_live", 40) + "\n");
            Assert.True(core.ProcessPaths([plain], now, Day));
            Assert.False(core.ApplyCompletedReads());
            Assert.Equal(380, core.BuildSnapshot(now, Day, MonitorState.Running, true).Snapshot.Today.Output);
        }
        finally { gate.Set(); }

        Assert.True(SpinWait.SpinUntil(() => { core.ApplyCompletedReads(); return core.PendingReadCount == 0; }, TimeSpan.FromSeconds(10)));
        var snapshot = core.BuildSnapshot(now, Day, MonitorState.Running, true).Snapshot;
        Assert.Equal((4, 430), (snapshot.Today.Requests, snapshot.Today.Output));
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.False(core.ApplyCompletedReads());
    }

    [Fact]
    public async Task LiveMonitorKeepsPublishingWhileTraeIsRead()
    {
        using var temp = new TempDirectory();
        var path = Copy(temp, "trae/database.db");
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var snapshots = new ConcurrentQueue<SpeedSnapshot>();
        var hub = await Task.Run(() => new SpeedHub());
        hub.SnapshotChanged += (_, snapshot) => snapshots.Enqueue(snapshot);
        using var monitor = new LiveMonitor(hub, new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Trae }, TraeRoot: path))
        {
            BeforeBackgroundRead = _ => { started.Set(); gate.Wait(TimeSpan.FromSeconds(20)); },
        };
        monitor.Start();
        try
        {
            await WaitAsync(() => snapshots.Any(snapshot => snapshot.State.Kind == MonitorStateKind.Running));
            CorrectUsage(path);
            await WaitAsync(() => started.IsSet);
            var count = snapshots.Count;
            await Task.Delay(1600);
            Assert.True(snapshots.Count - count >= 2, "Trae parsing must not stop the live heartbeat");
        }
        finally { gate.Set(); }
    }

    [Fact]
    public void PreparedReadIsDiscardedWhenThePreferredDatabaseChanges()
    {
        using var temp = new TempDirectory();
        Copy(temp, "trae/ModularData/ai-chat/database.db");
        var deduper = new Deduper();
        var source = new TraeSource(temp.Combine("trae"), deduper);
        Assert.False(source.BeginRefresh(true, out var request).Changed);
        var prepared = source.Read(Assert.IsType<TraeSource.ReadRequest>(request), CancellationToken.None);
        var preferred = Copy(temp, "trae/ModularData/ai-agent/database.db");
        Assert.False(source.Commit(prepared).Changed);
        Assert.Equal(0, deduper.Count);
        var accepted = source.Refresh(true);
        Assert.Equal(3, accepted.Responses.Count);
        Assert.All(accepted.Responses, item => Assert.StartsWith(preferred + "#", item.Response.AgentId));
    }

    private static string Copy(TempDirectory temp, string relative)
    {
        var path = temp.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixtureLocator.PathOf("trae/plain/database.db"), path);
        return path;
    }

    private static void CorrectUsage(string path)
    {
        using var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        writer.Execute("UPDATE chat_turn SET context=json_set(context,'$.token_usage.completion_tokens',150,'$.token_usage.total_tokens',950) WHERE turn_id='t-two'");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("condition not reached");
            await Task.Delay(25);
        }
    }
}
