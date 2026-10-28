using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Core.Tests;

/// <summary>METRICS 10.5, DESIGN 10: a disabled source is still measured and stored, but no view shows its traffic.</summary>
public sealed class TrafficVisibilityTests
{
    private static readonly TrafficPoint Point = new(DateTimeOffset.UnixEpoch, new TrafficRate(10, 1));

    private static readonly TrafficSnapshot Snapshot = new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(100),
        Enabled = true,
        MachineLive = new TrafficRate(9_000, 900),
        MachineToday = new TrafficBytes(90_000, 9_000),
        SourceHistory = new Dictionary<SourceKind, IReadOnlyList<TrafficPoint>> { [SourceKind.Codex] = [Point], [SourceKind.ClaudeCode] = [Point] },
        ProcessState = ProcessTrafficState.Running(1),
        Sources = new Dictionary<SourceKind, SourceTraffic>
        {
            [SourceKind.Codex] = new(SourceKind.Codex, new TrafficSplit(new TrafficBytes(1_000, 100), TrafficBytes.Zero), TrafficBytes.Zero,
                new TrafficRate(100, 10), new TrafficRate(100, 10)),
            [SourceKind.ClaudeCode] = new(SourceKind.ClaudeCode, new TrafficSplit(new TrafficBytes(2_000, 200), TrafficBytes.Zero), TrafficBytes.Zero,
                new TrafficRate(200, 20), new TrafficRate(200, 20)),
        },
        Agents = new Dictionary<string, AgentTraffic> { ["a"] = new("a", new TrafficSplit(new TrafficBytes(5, 5), TrafficBytes.Zero), null, null) },
    };

    [Fact]
    public void DisabledSourcesLeaveEverySumButTheMachineIsUnchanged()
    {
        var visible = TrafficVisibility.Snapshot(Snapshot, SourceSet.Of(SourceKind.Codex));

        Assert.Equal([SourceKind.Codex], visible.Sources.Keys);
        Assert.Equal([SourceKind.Codex], visible.SourceHistory.Keys);
        Assert.Equal(new TrafficRate(100, 10), visible.Live(null, includeTools: true));
        Assert.Equal(new TrafficBytes(1_000, 100), visible.Today(null, includeTools: true));
        Assert.Equal(Snapshot.MachineLive, visible.MachineLive);
        Assert.Equal(Snapshot.MachineToday, visible.MachineToday);
        Assert.Equal(Snapshot.ProcessState, visible.ProcessState);
        Assert.Same(Snapshot.Agents, visible.Agents);
    }

    [Fact]
    public void EverySourceEnabledKeepsTheSnapshot() =>
        Assert.Same(Snapshot, TrafficVisibility.Snapshot(Snapshot, SourceSet.Everything));

    [Fact]
    public void OnlyDisabledSourcesRunningShowsAMeasuredZero()
    {
        var visible = TrafficVisibility.Snapshot(Snapshot, SourceSet.Of(SourceKind.Gemini));
        Assert.Empty(visible.Sources);
        // Per-process data is available, so the agents' rate is a real 0 rather than unknown.
        Assert.Equal(TrafficRate.Zero, visible.Live(null, includeTools: true));
    }

    [Fact]
    public void StoredDaysKeepTheMachineRowsAndEnabledSources()
    {
        TrafficDay[] days =
        [
            new("2026-10-21", TrafficDay.MachineKey, 9, 1, 0, 0, 1_000),
            new("2026-10-21", "codex", 5, 1, 0, 0, 1_000),
            new("2026-10-21", "claudeCode", 3, 1, 0, 0, 1_000),
        ];

        Assert.Equal([TrafficDay.MachineKey, "claudeCode"], TrafficVisibility.Days(days, SourceSet.Of(SourceKind.ClaudeCode)).Select(d => d.Key));
        Assert.Same(days, TrafficVisibility.Days(days, SourceSet.Everything));
    }
}
