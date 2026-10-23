using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentSpeed.Core.Tests;

public sealed class LiveMonitorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string CodexRoot => _temp.Combine("codex", "sessions");

    private string ClaudeRoot => _temp.Combine("claude", "projects");

    private string DshRoot => _temp.Combine("dsh", "sessions");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SeedsPublishesRunningAndRaisesLiveEvents()
    {
        Directory.CreateDirectory(CodexRoot);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var transcript = _temp.WriteFile("claude/projects/p/s.jsonl",
            ClaudeLines.UserText(now - 5000, "go") + "\n" + ClaudeLines.Assistant(now - 3000, "msg_seed", 60) + "\n");
        var probe = await HubProbe.CreateAsync();
        using (var monitor = new LiveMonitor(probe.Hub, Configuration()))
        {
            monitor.Start();
            var running = await probe.WaitForAsync(s => s.State.Kind == MonitorStateKind.Running && s.Today.Requests == 1);
            Assert.Equal(Enum.GetValues<SourceKind>().Length, running.Sources.Count);
            Assert.True(running.Sources.Single(s => s.Kind == SourceKind.Codex).Available);
            Assert.Equal(300, running.History.Count);
            Assert.Empty(probe.Events);

            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var writer = new FileStream(transcript, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            await using (var text = new StreamWriter(writer))
            {
                await text.WriteAsync(ClaudeLines.UserText(start, "more") + "\n" + ClaudeLines.Assistant(start + 800, "msg_live", 90) + "\n");
            }

            var completed = await probe.WaitForEventAsync<SpeedEvent.ResponseCompleted>();
            Assert.Equal(90, completed.Response.OutputTokens);
            await probe.WaitForAsync(s => s.Today.Requests == 2);
        }

        var count = probe.SnapshotCount;
        await Task.Delay(700);
        Assert.Equal(count, probe.SnapshotCount);
    }

    [Fact]
    public async Task FollowsDshFramesAppendedWhileRunning()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = ZstdFrames.Write(Path.Combine(DshRoot, "--w-quasar--", "session-a", "session.v4.jsonl.zstd"),
            ZstdFrames.Lines(DshLines.Session(now - 60_000, id: "a", cwd: "/w/quasar")));
        var probe = await HubProbe.CreateAsync();
        using (var monitor = new LiveMonitor(probe.Hub, Configuration()))
        {
            monitor.Start();
            await probe.WaitForAsync(s => s.State.Kind == MonitorStateKind.Running
                && s.Sources.Single(x => x.Kind == SourceKind.DeepSeek).Available);

            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ZstdFrames.Append(session, ZstdFrames.Lines(DshLines.StepStart(1, start)));
            ZstdFrames.Append(session, ZstdFrames.Lines(DshLines.Message(2, start + 900, "dsm-live", DshLines.Usage(40, 90))));

            var completed = await probe.WaitForEventAsync<SpeedEvent.ResponseCompleted>();
            Assert.Equal((SourceKind.DeepSeek, "quasar", 90L), (completed.Response.Source, completed.Response.Project, completed.Response.OutputTokens));
            await probe.WaitForAsync(s => s.Sources.Single(x => x.Kind == SourceKind.DeepSeek).Today.Output == 90);
        }
    }

    [Fact]
    public async Task ReportsNoSourcesWhenNoRootExists()
    {
        var probe = await HubProbe.CreateAsync();
        using var monitor = new LiveMonitor(probe.Hub, Configuration());

        monitor.Start();
        var snapshot = await probe.WaitForAsync(s => s.State.Kind == MonitorStateKind.NoSources);

        Assert.Equal(Enum.GetValues<SourceKind>(), snapshot.Sources.Select(s => s.Kind));
        Assert.All(snapshot.Sources, s => Assert.False(s.Available));
    }

    [Fact]
    public async Task UpdateReseedsWithTheNewConfiguration()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _temp.WriteFile("claude/projects/p/s.jsonl",
            ClaudeLines.UserText(now - 5000, "go") + "\n" + ClaudeLines.Assistant(now - 3000, "msg_a", 60) + "\n");
        var probe = await HubProbe.CreateAsync();
        using var monitor = new LiveMonitor(probe.Hub, Configuration());
        monitor.Start();
        await probe.WaitForAsync(s => s.State.Kind == MonitorStateKind.Running && s.Today.Output == 60);

        monitor.Update(Configuration(SourceKind.Codex));
        var updated = await probe.WaitForAsync(s => s.Sources.Single(x => x.Kind == SourceKind.ClaudeCode).Enabled == false);

        Assert.Equal(TokenTotals.Zero, updated.Today);
        Assert.Equal(MonitorStateKind.NoSources, (await probe.WaitForAsync(s => s.State.Kind != MonitorStateKind.Starting)).State.Kind);
        Assert.Equal(Configuration(SourceKind.Codex), monitor.Configuration);
    }

    [Fact]
    public void StopIsIdempotentAndDisposeStopsQuickly()
    {
        var monitor = new LiveMonitor(new SpeedHub(), Configuration());
        monitor.Stop();
        monitor.Start();
        monitor.Start();
        var watch = Stopwatch.StartNew();
        monitor.Dispose();
        monitor.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    private string OpenCodeRoot => _temp.Combine("opencode");

    private string GeminiRoot => _temp.Combine("gemini", "tmp");

    private MonitorConfiguration Configuration(params SourceKind[] enabled) => new(
        new HashSet<SourceKind>(enabled.Length == 0 ? Enum.GetValues<SourceKind>() : enabled),
        CodexRoot,
        ClaudeRoot,
        DshRoot,
        OpenCodeRoot,
        GeminiRoot,
        _temp.Combine("antigravity"),
        _temp.Combine("antigravity-cli"));

    private sealed class HubProbe
    {
        private readonly ConcurrentQueue<SpeedSnapshot> _snapshots = new();
        private readonly ConcurrentQueue<SpeedEvent> _events = new();
        private int _count;

        private HubProbe(SpeedHub hub)
        {
            Hub = hub;
            hub.SnapshotChanged += (_, s) =>
            {
                Interlocked.Increment(ref _count);
                _snapshots.Enqueue(s);
            };
            hub.EventRaised += (_, e) => _events.Enqueue(e);
        }

        public SpeedHub Hub { get; }

        public int SnapshotCount => Volatile.Read(ref _count);

        public IReadOnlyCollection<SpeedEvent> Events => _events;

        // Built on a pool thread so the hub has no synchronization context and delivers inline.
        public static Task<HubProbe> CreateAsync() => Task.Run(() => new HubProbe(new SpeedHub()));

        public async Task<SpeedSnapshot> WaitForAsync(Func<SpeedSnapshot, bool> predicate)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                while (_snapshots.TryDequeue(out var snapshot))
                {
                    if (predicate(snapshot))
                    {
                        return snapshot;
                    }
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("expected snapshot was not published");
        }

        public async Task<T> WaitForEventAsync<T>()
            where T : SpeedEvent
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (_events.OfType<T>().FirstOrDefault() is { } found)
                {
                    return found;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("expected event was not raised");
        }
    }
}
