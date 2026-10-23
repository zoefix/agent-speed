using System.Collections.Concurrent;
using System.Diagnostics;
using AgentSpeed.Core.History;

namespace AgentSpeed.Core.Tests;

public sealed class HistoryServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _t = ClaudeLines.T0;

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    [Fact]
    public async Task PublishesAnalyzingThenReadyWithTheDays()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var probe = await HistoryProbe.CreateAsync();
        using (var service = Service(probe.Hub))
        {
            service.Start();
            var ready = await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready);
            Assert.Equal(100, Assert.Single(ready.Days).Totals.Output);
            Assert.Contains(probe.States, s => s.Kind == HistoryStateKind.Analyzing);
            Assert.NotNull(probe.Hub.RefreshHandler);
            Assert.True(File.Exists(StorePath));
        }

        Assert.Null(probe.Hub.RefreshHandler);
        Assert.Null(probe.Hub.ClearHandler);
    }

    [Fact]
    public async Task RefreshPicksUpNewLogsAndClearForgetsDeletedOnes()
    {
        var a = WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var probe = await HistoryProbe.CreateAsync();
        using var service = Service(probe.Hub);
        service.Start();
        await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready);

        WriteClaude("p/b.jsonl", _t + 86_400_000, "msg_b", 50);
        probe.Hub.Refresh();
        await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Days.Count == 2);

        File.Delete(a);
        var mark = probe.Count;
        probe.Hub.Refresh();
        var kept = await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Count > mark);
        Assert.Equal(2, kept.Days.Count);

        mark = probe.Count;
        probe.Hub.Clear();
        var cleared = await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Count > mark);
        Assert.Equal(50, Assert.Single(cleared.Days).Totals.Output);
    }

    [Fact]
    public async Task UpdateRecomputesForTheEnabledSources()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var probe = await HistoryProbe.CreateAsync();
        using var service = Service(probe.Hub);
        service.Start();
        await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Days.Count == 1);

        var mark = probe.Count;
        service.Update(Configuration(SourceKind.Codex));
        await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Days.Count == 0 && s.Count > mark);
        mark = probe.Count;
        service.Update(Configuration());
        await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready && s.Days.Count == 1 && s.Count > mark);
    }

    [Fact]
    public async Task LivePeaksAreStoredForTheirDay()
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(_t);
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var probe = await HistoryProbe.CreateAsync();
        using (var service = Service(probe.Hub))
        {
            service.ObserveLive(SpeedSnapshot.Empty with { Timestamp = day, State = MonitorState.Running, PeakToday = new Rate(88, 900) });
            service.ObserveLive(SpeedSnapshot.Empty with { Timestamp = day, State = MonitorState.Running, PeakToday = new Rate(40, 400) });
            service.ObserveLive(SpeedSnapshot.Empty with { Timestamp = day, State = MonitorState.Starting(0.5), PeakToday = new Rate(500, 900) });
            service.Start();
            var ready = await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Ready);
            Assert.Equal(88, Assert.Single(ready.Days).Peak);
        }

        using var store = HistoryStore.Open(StorePath);
        Assert.Equal(88, Assert.Single(new HistoryAnalyzer(store).Days()).Peak);
    }

    [Fact]
    public async Task AnUnusableStoreIsReportedAsFailed()
    {
        Directory.CreateDirectory(StorePath);
        var probe = await HistoryProbe.CreateAsync();
        using var service = Service(probe.Hub);
        service.Start();
        var failed = await probe.WaitForAsync(s => s.Kind == HistoryStateKind.Failed);
        Assert.False(string.IsNullOrEmpty(failed.Error));
    }

    [Fact]
    public void StopAndDisposeAreIdempotent()
    {
        var service = new HistoryService(new HistoryHub(), Configuration(), StorePath);
        service.Stop();
        service.Start();
        service.Start();
        var watch = Stopwatch.StartNew();
        service.Dispose();
        service.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(service.Start);
        Assert.Equal(Path.Combine("AgentSpeed", "history.sqlite"),
            Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HistoryService.DefaultStorePath));
    }

    private HistoryService Service(HistoryHub hub) =>
        new(hub, Configuration(), StorePath, TimeZoneInfo.Utc, new HistoryOptions { Workers = 2 });

    private MonitorConfiguration Configuration(params SourceKind[] enabled) => new(
        new HashSet<SourceKind>(enabled.Length == 0 ? Enum.GetValues<SourceKind>() : enabled),
        _temp.Combine("codex", "sessions"),
        _temp.Combine("claude", "projects"),
        _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"),
        _temp.Combine("gemini", "tmp"),
        _temp.Combine("antigravity"),
        _temp.Combine("antigravity-cli"));

    private string WriteClaude(string relative, long ms, string id, long output) =>
        _temp.WriteFile("claude/projects/" + relative,
            ClaudeLines.UserText(ms, "go") + "\n" + ClaudeLines.Assistant(ms + 2000, id, output) + "\n");

    /// <summary>Every publication, in order; the hub is built off the test's synchronization context so it delivers inline.</summary>
    private sealed class HistoryProbe
    {
        private readonly ConcurrentQueue<Published> _published = new();
        private int _count;

        private HistoryProbe(HistoryHub hub)
        {
            Hub = hub;
            hub.Changed += (_, _) =>
            {
                var count = Interlocked.Increment(ref _count);
                _published.Enqueue(new Published(hub.State.Kind, hub.Days, hub.State.Error, count));
            };
        }

        public HistoryHub Hub { get; }

        public IEnumerable<Published> States => _published;

        public int Count => Volatile.Read(ref _count);

        public static Task<HistoryProbe> CreateAsync() => Task.Run(() => new HistoryProbe(new HistoryHub()));

        public async Task<Published> WaitForAsync(Func<Published, bool> predicate)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (_published.LastOrDefault(predicate) is { } found)
                {
                    return found;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("expected history state was not published");
        }
    }

    private sealed record Published(HistoryStateKind Kind, IReadOnlyList<DailyUsage> Days, string? Error, int Count);
}
