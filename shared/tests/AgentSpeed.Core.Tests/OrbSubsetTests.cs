namespace AgentSpeed.Core.Tests;

/// <summary>Per-orb readings of a snapshot (METRICS 6.8, DESIGN 2b).</summary>
public sealed class OrbSubsetTests
{
    private static readonly SourceSet Partial = [SourceKind.ClaudeCode, SourceKind.ClaudeDesktop, SourceKind.Gemini];

    private static SpeedSnapshot Snapshot => SpeedSnapshot.Empty with
    {
        State = MonitorState.Running,
        Live = new Rate(90, 180),
        LiveEstimatedShare = 0.25,
        PeakToday = new Rate(400, 900),
        Today = new TokenTotals(9, 300, 0, 0, 300),
        Sources = Enum.GetValues<SourceKind>().Select(kind => kind switch
        {
            SourceKind.Codex => Source(kind, live: 50, estimated: 20, requests: 3, output: 100),
            SourceKind.ClaudeCode => Source(kind, live: 30, estimated: 3, requests: 2, output: 50),
            SourceKind.ClaudeDesktop => Source(kind, enabled: false),
            SourceKind.Gemini => Source(kind, available: false),
            SourceKind.DeepSeek => Source(kind, available: false, requests: 1, output: 9),
            _ => Source(kind),
        }).ToList(),
        Agents =
        [
            Agent("codex-root", SourceKind.Codex, AgentPhase.Generating),
            Agent("claude-child", SourceKind.ClaudeCode, AgentPhase.Generating, parent: "codex-root"),
            Agent("claude-root", SourceKind.ClaudeCode, AgentPhase.Tools),
            Agent("claude-sub", SourceKind.ClaudeCode, AgentPhase.Generating, parent: "claude-root"),
            Agent("codex-idle", SourceKind.Codex, AgentPhase.Idle),
        ],
        SubsetPeaks = new Dictionary<SourceSet, double> { [Partial] = 42 },
    };

    [Fact]
    public void AllSourcesReadingIsTheSnapshot()
    {
        var snapshot = Snapshot;

        var reading = OrbSubset.Reading(snapshot, null);

        Assert.Equal(snapshot.Live, reading.Live);
        Assert.Equal(snapshot.LiveEstimatedShare, reading.EstimatedShare);
        Assert.Same(snapshot.Agents, reading.Agents);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeCode], reading.GeneratingSources);
        Assert.Equal(snapshot.Today, reading.Today);
        Assert.Equal(400, reading.MeasuredPeakOutput);
        Assert.Empty(reading.Unavailable);
        Assert.Equal(SubsetState.Available, reading.State);
    }

    [Fact]
    public void ASubsetReadingCountsOnlyItsAvailableSources()
    {
        var reading = OrbSubset.Reading(Snapshot, Partial);

        Assert.Equal(new Rate(30, 60), reading.Live);
        Assert.Equal(0.1, reading.EstimatedShare, 12);
        Assert.Equal(["claude-child", "claude-root", "claude-sub"], reading.Agents.Select(a => a.Id));
        Assert.Equal([null, null, "claude-root"], reading.Agents.Select(a => a.ParentId));
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.ClaudeCode], reading.GeneratingSources);
        Assert.Equal(new TokenTotals(2, 50, 0, 0, 50), reading.Today);
        Assert.Equal(42, reading.MeasuredPeakOutput);
        Assert.Equal([SourceKind.ClaudeDesktop, SourceKind.Gemini], reading.Unavailable);
        Assert.Equal(SubsetState.Available, reading.State);

        var pair = OrbSubset.Reading(Snapshot, [SourceKind.Codex, SourceKind.ClaudeCode]);

        Assert.Equal(new Rate(80, 160), pair.Live);
        Assert.Equal(23.0 / 80, pair.EstimatedShare, 12);
        Assert.Equal(5, pair.Today.Requests);
        Assert.Null(pair.MeasuredPeakOutput);
        Assert.Equal([null, "codex-root", null, "claude-root", null], pair.Agents.Select(a => a.ParentId));
        Assert.Equal(Snapshot.Agents.Select(a => a.Id), pair.Agents.Select(a => a.Id));
    }

    [Fact]
    public void UnavailableStates()
    {
        var snapshot = Snapshot;

        var disabled = OrbSubset.Reading(snapshot, SourceSet.Of(SourceKind.ClaudeDesktop));
        Assert.Equal(SubsetState.AllDisabled, disabled.State);
        Assert.Equal([SourceKind.ClaudeDesktop], disabled.Unavailable);
        Assert.Equal(Rate.Zero, disabled.Live);
        Assert.Equal(0, disabled.EstimatedShare);
        Assert.Empty(disabled.Agents);
        Assert.Empty(disabled.GeneratingSources);

        Assert.Equal(SubsetState.AllNotFound, OrbSubset.Reading(snapshot, [SourceKind.Gemini, SourceKind.ClaudeDesktop]).State);
        Assert.Equal(SubsetState.AllNotFound, OrbSubset.Reading(snapshot, SourceSet.Of(SourceKind.Gemini)).State);
        Assert.Equal(SubsetState.Available, OrbSubset.Reading(snapshot, SourceSet.Of(SourceKind.DeepSeek)).State);
        Assert.Equal(SubsetState.AllNotFound, OrbSubset.Reading(snapshot, SourceSet.Empty).State);
        Assert.Equal(SubsetState.Available, OrbSubset.Reading(SpeedSnapshot.Empty, SourceSet.Of(SourceKind.Codex)).State);

        Assert.True(OrbSubset.IsUnavailable(Source(SourceKind.Codex, enabled: false, live: 10, requests: 3)));
        Assert.True(OrbSubset.IsUnavailable(Source(SourceKind.Codex, available: false)));
        Assert.False(OrbSubset.IsUnavailable(Source(SourceKind.Codex, available: false, live: 1)));
        Assert.False(OrbSubset.IsUnavailable(Source(SourceKind.Codex, available: false, requests: 1)));
        Assert.False(OrbSubset.IsUnavailable(Source(SourceKind.Codex)));
    }

    [Fact]
    public void EventsReachTheOrbsOfTheirSources()
    {
        var completion = new SpeedEvent.ResponseCompleted(
            new ResponseSummary(SourceKind.Codex, "p", null, 1, 1, null, null, DateTimeOffset.UnixEpoch));
        var peak = new SpeedEvent.NewPeak(120);

        Assert.True(OrbSubset.Accepts(completion, null));
        Assert.True(OrbSubset.Accepts(completion, [SourceKind.Codex, SourceKind.Gemini]));
        Assert.False(OrbSubset.Accepts(completion, SourceSet.Of(SourceKind.ClaudeCode)));
        Assert.True(OrbSubset.Accepts(peak, null));
        Assert.False(OrbSubset.Accepts(peak, SourceSet.Of(SourceKind.Codex)));
        Assert.False(OrbSubset.Accepts(peak, SourceSet.Everything));
    }

    [Fact]
    public void SnapshotsDefaultToNoSubsetPeaksAndAnUnknownDay()
    {
        Assert.Empty(SpeedSnapshot.Empty.SubsetPeaks);
        Assert.Equal(DateTimeOffset.MinValue, SpeedSnapshot.Empty.DayStart);
        Assert.Equal(Rate.Zero, new SourceStatus(SourceKind.Omp, true, "", true, 0, new Rate(5, 9), TokenTotals.Zero, 0).LiveEstimated);
    }

    private static SourceStatus Source(
        SourceKind kind,
        bool enabled = true,
        bool available = true,
        double live = 0,
        double estimated = 0,
        long requests = 0,
        long output = 0) =>
        new(kind, enabled, "/" + kind, available, 1, new Rate(live, live * 2), new TokenTotals(requests, output, 0, 0, output), 0,
            new Rate(estimated, estimated * 2));

    private static AgentStatus Agent(string id, SourceKind source, AgentPhase phase, string? parent = null) =>
        new(id, source, "p", null, phase, parent is not null, Rate.Zero, null, DateTimeOffset.UnixEpoch, parent);
}
