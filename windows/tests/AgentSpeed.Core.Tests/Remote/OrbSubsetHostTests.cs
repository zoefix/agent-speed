using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>DESIGN 13: orbs and boards that track a set of hosts read METRICS 6.8 over the selected (host, source) cells.</summary>
public sealed class OrbSubsetHostTests
{
    private const string Local = RemoteHost.LocalId;
    private const string A = RemoteLive.A;
    private const string B = RemoteLive.B;

    [Fact]
    public void EveryHostIsTheSourceOnlyReading()
    {
        var snapshot = Snapshot();
        foreach (var sources in new SourceSet?[] { null, SourceSet.Of(SourceKind.Codex), SourceSet.Of(SourceKind.Codex).With(SourceKind.Gemini) })
        {
            AssertSame(OrbSubset.Reading(snapshot, sources), OrbSubset.Reading(snapshot, sources, null));
            var cost = OrbSubset.Cost(snapshot, sources, SourceSet.Everything);
            var hostCost = OrbSubset.Cost(snapshot, sources, null, SourceSet.Everything);
            Assert.Equal((cost.MicroUsdPerSecond, cost.LiveOutput, cost.Today, cost.State), (hostCost.MicroUsdPerSecond, hostCost.LiveOutput, hostCost.Today, hostCost.State));
        }
    }

    [Fact]
    public void OneHostCountsItsCellsAndAgents()
    {
        var all = OrbSubset.Reading(Snapshot(), null, Hosts(A));
        Assert.Equal(50, all.Live.Output);
        Assert.Equal(500, all.Today.Output);
        Assert.Equal(["a-codex", "a-claude"], all.Agents.Select(a => a.Id));
        Assert.Equal("a-codex", all.Agents[^1].ParentId);
        Assert.Equal([SourceKind.Codex], all.GeneratingSources);
        Assert.Equal(SubsetState.Available, all.State);
        Assert.Empty(all.Unavailable);

        var codex = OrbSubset.Reading(Snapshot(), SourceSet.Of(SourceKind.Codex), Hosts(A));
        Assert.Equal(new Rate(20, 40), codex.Live);
        Assert.Equal(0.25, codex.EstimatedShare, 12);
        Assert.Equal(["a-codex"], codex.Agents.Select(a => a.Id));
        Assert.Equal(77, codex.MeasuredPeakOutput);
        Assert.Null(OrbSubset.Reading(Snapshot(), SourceSet.Of(SourceKind.Codex), Hosts(Local)).MeasuredPeakOutput);

        var pair = OrbSubset.Reading(Snapshot(), SourceSet.Of(SourceKind.Codex), Hosts(Local, A));
        Assert.Equal(30, pair.Live.Output);
        Assert.Equal(300, pair.Today.Output);
        Assert.Equal(["local-codex", "a-codex"], pair.Agents.Select(a => a.Id));
    }

    [Fact]
    public void ASourceIsAvailableOnlyWhereItIsDetectedOrHasData()
    {
        var claudeAndGemini = SourceSet.Of(SourceKind.ClaudeCode).With(SourceKind.Gemini);
        var reading = OrbSubset.Reading(Snapshot(), claudeAndGemini, Hosts(Local));
        Assert.Equal(SubsetState.AllNotFound, reading.State);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.Gemini], reading.Unavailable);
        var remote = OrbSubset.Reading(Snapshot(), claudeAndGemini, Hosts(A));
        Assert.Equal(SubsetState.Available, remote.State);
        Assert.Equal([SourceKind.Gemini], remote.Unavailable);
    }

    [Fact]
    public void OnlyDisconnectedHostsAreOffline()
    {
        var codex = SourceSet.Of(SourceKind.Codex);
        var offline = OrbSubset.Reading(Snapshot(), codex, Hosts(B));
        Assert.Equal(SubsetState.HostsOffline, offline.State);
        Assert.Equal([SourceKind.Codex], offline.Unavailable);
        Assert.Equal(50, offline.Today.Output);
        Assert.Equal(SubsetState.HostsOffline, OrbSubset.Reading(Snapshot(), null, Hosts(B)).State);
        Assert.Equal(SubsetState.Available, OrbSubset.Reading(Snapshot(), codex, Hosts(B, Local)).State);
        Assert.Equal(SubsetState.Available, OrbSubset.Reading(Snapshot(), codex, Hosts(B, A)).State);
        Assert.Equal(SubsetState.HostsOffline, OrbSubset.Reading(Snapshot(), null, Hosts("removed")).State);
    }

    [Fact]
    public void EventsFollowHostsAndSources()
    {
        var completed = new SpeedEvent.ResponseCompleted(new ResponseSummary(SourceKind.Codex, "p", null, 1, 2, null, null, DateTimeOffset.UnixEpoch) { Host = A });
        Assert.True(OrbSubset.Accepts(completed, null, null));
        Assert.True(OrbSubset.Accepts(completed, SourceSet.Of(SourceKind.Codex), Hosts(A)));
        Assert.False(OrbSubset.Accepts(completed, null, Hosts(Local)));
        Assert.False(OrbSubset.Accepts(completed, SourceSet.Of(SourceKind.ClaudeCode), Hosts(A)));
        Assert.True(OrbSubset.Accepts(new SpeedEvent.NewPeak(50), null, null));
        Assert.False(OrbSubset.Accepts(new SpeedEvent.NewPeak(50), null, Hosts(Local, A, B)));
    }

    [Fact]
    public void CostCountsTheSelectedCells()
    {
        var reading = OrbSubset.Cost(Snapshot(), null, Hosts(A), SourceSet.Everything);
        Assert.Equal(5, reading.MicroUsdPerSecond);
        Assert.Equal(50, reading.LiveOutput);
        Assert.Equal(500, reading.Today.CostMicroUsd);
        var billable = OrbSubset.Cost(Snapshot(), null, Hosts(A), SourceSet.Of(SourceKind.Codex));
        Assert.Equal(2, billable.MicroUsdPerSecond);
        Assert.Equal([SourceKind.ClaudeCode], billable.Excluded);
    }

    [Fact]
    public void WithoutRemoteHostsOnlyThisComputerExists()
    {
        var snapshot = Snapshot() with
        {
            Hosts = [],
            HostSources = [],
            HostSubsetPeaks = new Dictionary<SubsetKey, double> { [new SubsetKey(SourceSet.Of(SourceKind.Codex), [Local])] = 12 },
            SubsetPeaks = new Dictionary<SourceSet, double> { [SourceSet.Of(SourceKind.Codex)] = 9 },
        };
        var local = OrbSubset.Reading(snapshot, SourceSet.Of(SourceKind.Codex), Hosts(Local));
        Assert.Equal(12, local.MeasuredPeakOutput);
        AssertSame(OrbSubset.Reading(snapshot, SourceSet.Of(SourceKind.Codex)), local with { MeasuredPeakOutput = 9 });
        Assert.Equal(snapshot.Agents, OrbSubset.Reading(snapshot, null, Hosts(Local)).Agents);
        Assert.Equal(SubsetState.HostsOffline, OrbSubset.Reading(snapshot, null, Hosts(A)).State);
    }

    [Fact]
    public void SubsetKeysCompareTheirHostsAsASet()
    {
        var codex = SourceSet.Of(SourceKind.Codex);
        Assert.Equal(new SubsetKey(codex, [A, Local]), new SubsetKey(codex, [Local, A, A]));
        Assert.Equal(new SubsetKey(codex, [A, Local]).GetHashCode(), new SubsetKey(codex, [Local, A]).GetHashCode());
        Assert.NotEqual(new SubsetKey(codex), new SubsetKey(codex, [Local]));
        Assert.NotEqual(new SubsetKey(codex, []), new SubsetKey(codex));
        SubsetKey implicitKey = codex;
        Assert.Equal(new SubsetKey(codex), implicitKey);
        Assert.True(new SubsetKey(codex, [A]).Contains(new HostSource(A, SourceKind.Codex)));
        Assert.False(new SubsetKey(codex, [A]).Contains(HostSource.Local(SourceKind.Codex)));
        Assert.True(new SubsetKey(codex).Contains(new HostSource(B, SourceKind.Codex)));
    }

    private static HashSet<string> Hosts(params string[] ids) => new(ids, StringComparer.Ordinal);

    private static void AssertSame(SubsetReading expected, SubsetReading actual)
    {
        Assert.Equal(expected.Live, actual.Live);
        Assert.Equal(expected.EstimatedShare, actual.EstimatedShare);
        Assert.Equal(expected.Agents, actual.Agents);
        Assert.Equal(expected.GeneratingSources, actual.GeneratingSources);
        Assert.Equal(expected.Today, actual.Today);
        Assert.Equal(expected.MeasuredPeakOutput, actual.MeasuredPeakOutput);
        Assert.Equal(expected.Unavailable, actual.Unavailable);
        Assert.Equal(expected.State, actual.State);
    }

    private static HostSourceStatus Cell(string host, SourceKind source, bool available = true, double live = 0, double estimated = 0, long output = 0,
        double cost = 0) =>
        new(new HostSource(host, source), available, false, 1, new Rate(live, live * 2), new Rate(estimated, estimated * 2), new LiveCost(cost, live),
            IntelligenceSums.Zero, new TokenTotals(output > 0 ? 1 : 0, Output: output), new CostTally(output), [], ExecutionModes.Empty);

    private static AgentStatus Agent(string id, string host, SourceKind source, AgentPhase phase, string? parent = null) =>
        new(id, source, "p", null, phase, parent is not null, Rate.Zero, null, DateTimeOffset.UnixEpoch, parent) { Host = host };

    private static SpeedSnapshot Snapshot()
    {
        HostSourceStatus[] cells =
        [
            Cell(Local, SourceKind.Codex, live: 10, output: 100, cost: 1),
            Cell(A, SourceKind.Codex, live: 20, estimated: 5, output: 200, cost: 2),
            Cell(A, SourceKind.ClaudeCode, live: 30, output: 300, cost: 3),
            Cell(B, SourceKind.Codex, available: false, output: 50),
        ];
        var sources = Enum.GetValues<SourceKind>().Select(kind =>
        {
            var members = cells.Where(c => c.Source == kind).ToList();
            return new SourceStatus(kind, kind != SourceKind.Gemini, "/", kind is SourceKind.Codex or SourceKind.ClaudeCode, members.Count,
                new Rate(members.Sum(m => m.Live.Output), members.Sum(m => m.Live.Total)), members.Aggregate(TokenTotals.Zero, (s, m) => s + m.Today), 0,
                new Rate(members.Sum(m => m.LiveEstimated.Output), members.Sum(m => m.LiveEstimated.Total)))
            {
                LiveCost = members.Aggregate(LiveCost.Zero, (s, m) => s + m.LiveCost),
                TodayCost = members.Aggregate(CostTally.Zero, (s, m) => s + m.TodayCost),
            };
        }).ToList();
        return SpeedSnapshot.Empty with
        {
            State = MonitorState.Running,
            HostSources = cells,
            Hosts =
            [
                new HostStatus(Local, Local, null, Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero, CostTally.Zero, 0, true),
                new HostStatus(A, "devbox", new RemoteHostStatus(RemoteStatus.Connected), Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero, CostTally.Zero, 0, true),
                new HostStatus(B, "gpu", new RemoteHostStatus(RemoteStatus.Unreachable), Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero, CostTally.Zero, 0,
                    false),
            ],
            Sources = sources,
            Live = new Rate(60, 120),
            Today = new TokenTotals(4, Output: 650),
            Agents =
            [
                Agent("local-codex", Local, SourceKind.Codex, AgentPhase.Generating),
                Agent("a-codex", A, SourceKind.Codex, AgentPhase.Generating),
                Agent("a-claude", A, SourceKind.ClaudeCode, AgentPhase.Tools, "a-codex"),
                Agent("b-codex", B, SourceKind.Codex, AgentPhase.Idle),
            ],
            HostSubsetPeaks = new Dictionary<SubsetKey, double> { [new SubsetKey(SourceSet.Of(SourceKind.Codex), [A])] = 77 },
        };
    }
}
