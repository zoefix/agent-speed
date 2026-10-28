using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Monitor;

namespace AgentSpeed.Core.Tests;

/// <summary>The traffic store (METRICS 10.9) and the sampling core's cadence, states and days (10.4, 10.10).</summary>
public sealed class TrafficMonitorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("history.sqlite");

    [Fact]
    public void TrafficRowsUpsertAndKeepTheFirstDay()
    {
        using (var store = HistoryStore.Open(StorePath))
        {
            store.SaveTraffic([new TrafficDay("2026-10-21", TrafficDay.MachineKey, 10, 1, 0, 0, 1000), new TrafficDay("2026-10-21", "codex", 5, 1, 2, 0, 900)]);
            store.SaveTraffic([new TrafficDay("2026-10-21", "codex", 7, 2, 3, 1, 1900)]);
            store.SaveTraffic([new TrafficDay("2026-10-20", TrafficDay.MachineKey, 1, 1, 0, 0, 10)]);
        }

        var (days, since) = HistoryStore.StoredTraffic(StorePath);
        Assert.Equal("2026-10-20", since);
        Assert.Equal(3, days.Count);
        Assert.Equal(new TrafficDay("2026-10-21", "codex", 7, 2, 3, 1, 1900), days.Single(d => d.Day == "2026-10-21" && d.Key == "codex"));

        // The table does not change the schema version, so older builds still open the store.
        using var connection = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(HistoryStore.SchemaVersion, connection.ScalarInt64("PRAGMA user_version"));
    }

    [Fact]
    public void AStoreWithoutTrafficReadsEmpty()
    {
        Assert.Empty(HistoryStore.StoredTraffic(_temp.Combine("missing.sqlite")).Days);
        using (HistoryStore.Open(StorePath))
        {
        }

        var (days, since) = HistoryStore.StoredTraffic(StorePath);
        Assert.Empty(days);
        Assert.Null(since);
    }

    [Fact]
    public void WithoutAgentProcessesTheSourceStaysIdleAndValuesAreRealZeros()
    {
        var harness = new Harness(StorePath);
        harness.Core.StartInline(true);
        harness.Tick();
        harness.Tick();

        Assert.Equal(ProcessTrafficKind.Standby, harness.Last.ProcessState.Kind);
        Assert.All(harness.Source.Polls, p => Assert.False(p.Active));
        Assert.NotNull(harness.Last.MachineLive);
    }

    [Fact]
    public void AnAgentProcessStartsSamplingEverySecondAndQuietAgentsEveryFive()
    {
        var harness = new Harness(StorePath);
        harness.Lister.Entries.Add(new ProcessEntry(40, 1, 1_000_000));
        harness.Lister.Paths[40] = @"C:\Users\dev\.local\bin\claude.exe";
        harness.Core.StartInline(true);
        harness.Source.Rows.Add(new CounterRow(new ProcessIdentity(40, 1_000_000), new TrafficCounter(100, 10)));
        harness.Tick();
        Assert.True(harness.Source.Polls[^1].Active);
        Assert.True(harness.Source.Polls[^1].Fetch);
        Assert.Equal(ProcessTrafficKind.Running, harness.Last.ProcessState.Kind);

        // No agent has been active (the start counts as activity): after 60 s the source is fetched every 5 s.
        for (var i = 0; i < 80; i++)
        {
            harness.Tick();
        }

        var fetches = harness.Source.Polls.TakeLast(10).Count(p => p.Fetch);
        Assert.InRange(fetches, 2, 3);
        Assert.Equal(5, harness.Last.ProcessState.Interval);

        // Something on screen shows per-source traffic: every second again.
        harness.Core.SetDetailDemand(true);
        for (var i = 0; i < 5; i++)
        {
            harness.Tick();
        }

        Assert.All(harness.Source.Polls.TakeLast(5), p => Assert.True(p.Fetch));
    }

    [Fact]
    public void WithNoAgentActiveTheTableIsListedEveryFiveSecondsOrWhenAnUnknownProcessSendsBytes()
    {
        var harness = new Harness(StorePath);
        harness.Lister.Entries.Add(new ProcessEntry(40, 1, 1_000_000));
        harness.Lister.Paths[40] = @"C:\Users\dev\.local\bin\claude.exe";
        harness.Core.StartInline(true);
        harness.Core.SetDetailDemand(true);
        harness.Source.Rows.Add(new CounterRow(new ProcessIdentity(40, 1_000_000), new TrafficCounter(100, 10)));
        // The start counts as activity: every tick for the first minute.
        for (var i = 0; i < 70; i++)
        {
            harness.Tick();
        }

        var before = harness.Lister.Listings;
        for (var i = 0; i < 10; i++)
        {
            harness.Tick();
        }

        Assert.InRange(harness.Lister.Listings - before, 2, 3);

        // A tool starts sending: it is listed in the tick its first bytes arrive, and attributed.
        before = harness.Lister.Listings;
        harness.Lister.Entries.Add(new ProcessEntry(41, 40, 2_000_000));
        harness.Lister.Paths[41] = @"C:\Windows\System32\curl.exe";
        harness.Source.Rows.Add(new CounterRow(new ProcessIdentity(41, 2_000_000), new TrafficCounter(0, 0)));
        harness.Tick();
        Assert.Equal(before + 1, harness.Lister.Listings);
        harness.Source.Rows[1] = new CounterRow(new ProcessIdentity(41, 2_000_000), new TrafficCounter(4_000, 100));
        harness.Tick();
        Assert.Equal(new TrafficBytes(4_000, 100), harness.Last.Sources[SourceKind.ClaudeCode].Today.Tools);

        // A row of a process that had already exited asks once, not every tick.
        before = harness.Lister.Listings;
        harness.Source.Rows.Add(new CounterRow(new ProcessIdentity(77, 3_000_000), new TrafficCounter(10, 0)));
        harness.Tick();
        harness.Tick();
        Assert.Equal(before + 1, harness.Lister.Listings);
    }

    [Fact]
    public void TheFirstSampleOfAConnectionIsABaselineAndLaterBytesCount()
    {
        var harness = new Harness(StorePath);
        harness.Lister.Entries.Add(new ProcessEntry(40, 1, 1_000_000));
        harness.Lister.Paths[40] = @"C:\Users\dev\.local\bin\claude.exe";
        harness.Core.StartInline(true);
        harness.Core.SetDetailDemand(true);
        var identity = new ProcessIdentity(40, 1_000_000);
        harness.Source.Rows.Add(new CounterRow(identity, new TrafficCounter(5_000, 500)));
        harness.Tick();
        Assert.Equal(TrafficBytes.Zero, harness.Last.Sources.GetValueOrDefault(SourceKind.ClaudeCode)?.Today.Self ?? TrafficBytes.Zero);

        harness.Source.Rows[0] = new CounterRow(identity, new TrafficCounter(8_000, 700));
        harness.Tick();
        Assert.Equal(new TrafficBytes(3_000, 200), harness.Last.Sources[SourceKind.ClaudeCode].Today.Self);

        // A reconnection starts a new stream: its first sample is a baseline again, nothing is counted twice.
        harness.Source.Stream++;
        harness.Source.Rows[0] = new CounterRow(identity, new TrafficCounter(100, 10));
        harness.Tick();
        Assert.Equal(new TrafficBytes(3_000, 200), harness.Last.Sources[SourceKind.ClaudeCode].Today.Self);
    }

    [Fact]
    public void AMissingHelperIsUnavailableEvenWithoutAgents()
    {
        var harness = new Harness(StorePath);
        harness.Source.Status = ProcessCounterStatus.HelperNotInstalled;
        harness.Core.StartInline(true);
        harness.Tick();
        harness.Tick();
        Assert.Equal(TrafficUnavailableReason.HelperMissing, harness.Last.ProcessState.UnavailableReason);
        Assert.Empty(harness.Last.Sources);
        Assert.NotNull(harness.Last.MachineLive);
        // Nothing can be attributed without the helper: the process listing (most of the cost) is skipped.
        Assert.Equal(0, harness.Lister.Listings);

        harness.Source.Status = ProcessCounterStatus.Idle;
        harness.Tick();
        Assert.Equal(1, harness.Lister.Listings);
    }

    [Fact]
    public void TodayIsWrittenEveryMinuteAndRestoredAtLaunch()
    {
        var harness = new Harness(StorePath);
        harness.Core.StartInline(true);
        for (var i = 0; i < 62; i++)
        {
            harness.Machine.Add(1000, 100);
            harness.Tick();
        }

        var stored = HistoryStore.StoredTraffic(StorePath).Days.Single(d => d.IsMachine);
        Assert.InRange(stored.Rx, 59_000, 62_000);

        harness.Core.SetEnabledInline(false);
        Assert.False(harness.Last.Enabled);
        var final = HistoryStore.StoredTraffic(StorePath).Days.Single(d => d.IsMachine);
        Assert.Equal(61_000, final.Rx);

        var again = new Harness(StorePath, harness.NowMs);
        again.Core.StartInline(true);
        again.Tick();
        Assert.Equal(61_000, again.Last.MachineToday.Rx);
    }

    [Fact]
    public void TheDayChangeWritesTheFinishedDayAndStartsAtZero()
    {
        var midnight = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var harness = new Harness(StorePath, midnight - 3000);
        harness.Core.StartInline(true);
        for (var i = 0; i < 6; i++)
        {
            harness.Machine.Add(1000, 0);
            harness.Tick();
        }

        var days = HistoryStore.StoredTraffic(StorePath).Days.Where(d => d.IsMachine).ToDictionary(d => d.Day);
        Assert.True(days.ContainsKey("2026-10-21"));
        // The sample at 00:00:00 belongs to the new day (METRICS 10.4.6).
        Assert.Equal(1000, days["2026-10-21"].Rx);
        Assert.Equal(4000, harness.Last.MachineToday.Rx);
    }

    [Fact]
    public void ALongSamplingGapDoesNotBecomeALiveRateOrHistoryBucket()
    {
        var harness = new Harness(StorePath);
        harness.Core.StartInline(true);
        harness.Tick(); // Establish the first counter baseline.
        harness.Machine.Add(1000, 0);
        harness.Tick();
        Assert.Equal(1000d, harness.Last.MachineLive?.Rx);

        // Simulate suspend with the Linux CLOCK_BOOTTIME elapsed time, without suspending the test machine.
        harness.Machine.Add(500_000, 0);
        harness.AdvanceAndTick(60_000);

        Assert.Null(harness.Last.MachineLive);
        Assert.Equal(TrafficRate.Zero, harness.Last.MachineHistory[^1].Rate);
        Assert.Equal(501_000, harness.Last.MachineToday.Rx);
    }

    /// <summary>A core on the test thread with fake samplers and a clock that advances one second per tick.</summary>
    private sealed class Harness
    {
        public Harness(string storePath, long? wallStartMs = null)
        {
            NowMs = wallStartMs ?? new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var platform = new TrafficPlatform(TrafficPlatformKind.Windows, @"C:\Users\dev",
                new Dictionary<string, string> { ["LOCALAPPDATA"] = @"C:\Users\dev\AppData\Local", ["APPDATA"] = @"C:\Users\dev\AppData\Roaming" });
            Core = new TrafficCore(storePath, platform, Lister, Machine, Source, s => Last = s, (_, _) => { }, zone: TimeZoneInfo.Utc,
                monotonicMs: () => NowMs, wallMs: () => NowMs);
        }

        public long NowMs { get; private set; }

        public TrafficCore Core { get; }

        public FakeLister Lister { get; } = new();

        public FakeMachine Machine { get; } = new();

        public FakeSource Source { get; } = new();

        public TrafficSnapshot Last { get; private set; } = TrafficSnapshot.Off;

        public void Tick()
        {
            AdvanceAndTick(1000);
        }

        public void AdvanceAndTick(long elapsedMs)
        {
            NowMs += elapsedMs;
            Core.TickInline();
        }
    }

    private sealed class FakeLister : IProcessLister
    {
        public List<ProcessEntry> Entries { get; } = new();

        public Dictionary<int, string> Paths { get; } = new();

        public int Listings { get; private set; }

        public bool List(List<ProcessEntry> entries)
        {
            Listings++;
            entries.Clear();
            entries.AddRange(Entries);
            return true;
        }

        public string? ExecutablePath(in ProcessEntry entry) => Paths.GetValueOrDefault(entry.Pid);

        public IReadOnlyList<string>? Arguments(in ProcessEntry entry) => null;

        public string? WorkingDirectory(in ProcessEntry entry) => null;

        public string? BundleId(in ProcessEntry entry) => null;

        public IReadOnlyCollection<string> OpenFiles(in ProcessEntry entry, long nowMs) => [];
    }

    private sealed class FakeMachine : IMachineCounterReader
    {
        private long _rx;
        private long _tx;

        public IReadOnlyList<string> CountedNames => ["Ethernet"];

        public void Add(long rx, long tx)
        {
            _rx += rx;
            _tx += tx;
        }

        public bool Read(Dictionary<ulong, TrafficCounter> counters)
        {
            counters.Clear();
            counters[1] = new TrafficCounter(_rx, _tx);
            return true;
        }
    }

    private sealed class FakeSource : IProcessCounterSource
    {
        public ProcessCounterStatus Status { get; set; } = ProcessCounterStatus.Idle;

        public int Stream { get; set; } = 1;

        public string? LastError => null;

        public List<CounterRow> Rows { get; } = new();

        public List<(bool Active, bool Fetch)> Polls { get; } = new();

        public IReadOnlyList<CounterRow>? Poll(bool active, bool fetch, long nowMs)
        {
            Polls.Add((active, fetch));
            if (Status == ProcessCounterStatus.HelperNotInstalled)
            {
                return null;
            }

            Status = active ? ProcessCounterStatus.Sampling : ProcessCounterStatus.Idle;
            return active && fetch ? Rows.ToArray() : null;
        }

        public void Retry()
        {
        }

        public void Dispose()
        {
        }
    }
}
