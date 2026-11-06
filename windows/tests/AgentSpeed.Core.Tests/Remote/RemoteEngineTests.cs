using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteLive;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 7 in the engine: cross-host deduplication, (host, source) cells, host subset peaks, limits, idle and removal.</summary>
public sealed class RemoteEngineTests
{
    private const string Local = RemoteHost.LocalId;

    [Fact]
    public void TheFirstArrivalOfAKeyWinsAcrossHosts()
    {
        var engine = new SpeedEngine(L.Day, zone: TimeZoneInfo.Utc);
        var localFirst = L.Record("k1", Local, start: L.Now - 5000, end: L.Now - 3000, output: 100);
        Assert.True(engine.Upsert(localFirst, isCompletion: true, seeded: true));
        Assert.False(engine.Upsert(L.Record("k1", L.A, start: L.Now - 5000, end: L.Now - 3000, output: 999), isCompletion: true, seeded: false, L.Now));
        Assert.True(engine.Upsert(L.Record("k2", L.A, start: L.Now - 4000, end: L.Now - 2000, output: 50), isCompletion: true, seeded: true));
        Assert.False(engine.Upsert(L.Record("k2", Local, start: L.Now - 4000, end: L.Now - 2000, output: 777), isCompletion: true, seeded: false, L.Now));
        Assert.False(engine.Upsert(L.Record("k2", L.B, start: L.Now - 4000, end: L.Now - 2000, output: 555), isCompletion: true, seeded: false, L.Now));
        Assert.True(engine.Upsert(localFirst with { Tokens = localFirst.Tokens with { Output = 120 } }, isCompletion: false, seeded: false, L.Now));
        var frame = engine.Compute(L.Now, L.Day, observeLive: false);
        Assert.Equal(170, frame.Today.Output);
        Assert.Equal(2, frame.Today.Requests);
        Assert.Empty(frame.Events);
    }

    [Fact]
    public void AKeyOfTodayStaysWithItsHostAfterTheLiveWindow()
    {
        var engine = new SpeedEngine(L.Day, zone: TimeZoneInfo.Utc);
        engine.Upsert(L.Record("k1", L.A, start: L.Day + 1000, end: L.Day + 2000, output: 10), isCompletion: true, seeded: true);
        engine.Compute(L.Now, L.Day, observeLive: false);
        Assert.False(engine.Upsert(L.Record("k1", Local, start: L.Day + 1000, end: L.Day + 2000, output: 10), isCompletion: true, seeded: true));
        Assert.Equal(10, engine.Compute(L.Now, L.Day, observeLive: false).Today.Output);
    }

    [Fact]
    public void CellsAddUpToThePerSourceValues()
    {
        var engine = BusyEngine(tracksCells: true);
        var frame = engine.Compute(L.Now, L.Day, observeLive: false);
        Assert.Equal(new HashSet<string> { Local, L.A, L.B }, frame.Cells.Keys.Select(c => c.Host).ToHashSet());
        foreach (var kind in new[] { SourceKind.Codex, SourceKind.ClaudeCode })
        {
            var cells = frame.Cells.Where(c => c.Key.Source == kind).Select(c => c.Value).ToList();
            var i = (int)kind;
            Assert.Equal(frame.SourceLive[i].Output, cells.Sum(c => c.Live.Output), 9);
            Assert.Equal(frame.SourceLive[i].Total, cells.Sum(c => c.Live.Total), 9);
            Assert.Equal(frame.SourceLiveEstimated[i].Output, cells.Sum(c => c.LiveEstimated.Output), 9);
            Assert.Equal(frame.SourceLiveCost[i].MicroUsdPerSecond, cells.Sum(c => c.LiveCost.MicroUsdPerSecond), 9);
            Assert.Equal(frame.SourceLiveCost[i].KnownOutput, cells.Sum(c => c.LiveCost.KnownOutput), 9);
            Assert.Equal(frame.SourceIntelligence[i].Output, cells.Sum(c => c.Intelligence.Output), 9);
            Assert.Equal(frame.SourceToday[i], cells.Aggregate(TokenTotals.Zero, (sum, c) => sum + c.Today));
            Assert.Equal(frame.SourceTodayCost[i], cells.Aggregate(CostTally.Zero, (sum, c) => sum + c.TodayCost));
        }

        Assert.True(frame.Cells[new HostSource(L.A, SourceKind.Codex)].LiveEstimated.Output > 0, "the unmeasured a3 and the in-flight agent");
        var b = frame.Cells[new HostSource(L.B, SourceKind.Codex)];
        Assert.Equal(new Dictionary<string, long> { ["gpt-5.5"] = 1 }, b.Unpriced);
        Assert.Equal(1, b.Today.Requests);
    }

    [Fact]
    public void WithoutCellsTheOutputIsUnchanged()
    {
        var a = BusyEngine(tracksCells: false).Compute(L.Now, L.Day, observeLive: true);
        var b = BusyEngine(tracksCells: true).Compute(L.Now, L.Day, observeLive: true);
        Assert.Empty(a.Cells);
        Assert.NotEmpty(b.Cells);
        Assert.Equal(a.Live, b.Live);
        Assert.Equal(a.EstimatedShare, b.EstimatedShare);
        Assert.Equal(a.SourceLive, b.SourceLive);
        Assert.Equal(a.SourceLiveEstimated, b.SourceLiveEstimated);
        Assert.Equal(a.SourceLiveCost, b.SourceLiveCost);
        Assert.Equal(a.Agents.Select(x => (x.Info.Id, x.Phase, x.Live)), b.Agents.Select(x => (x.Info.Id, x.Phase, x.Live)));
        Assert.Equal(a.History, b.History);
        Assert.Equal(a.Today, b.Today);
        Assert.Equal(a.PeakToday, b.PeakToday);
    }

    [Fact]
    public void HostSubsetPeaksCountTheirCellsOnly()
    {
        Parsing.ParsedResponse[] records =
        [
            L.Record("l1", Local, start: L.Day + 10_000, end: L.Day + 13_000, output: 900),
            L.Record("a1", L.A, start: L.Day + 11_000, end: L.Day + 14_000, output: 600),
            L.Record("a2", L.A, "c", SourceKind.ClaudeCode, L.Day + 11_000, L.Day + 13_000, 800),
            L.Record("b1", L.B, start: L.Day + 50_000, end: L.Day + 52_000, output: 2000),
        ];
        static TodayLedger Ledger(IEnumerable<Parsing.ParsedResponse> members)
        {
            var ledger = new TodayLedger(L.Day);
            foreach (var record in members)
            {
                ledger.Upsert(record);
            }

            return ledger;
        }

        var all = Ledger(records);
        var codex = SourceSet.Of(SourceKind.Codex);
        (SubsetKey Key, Parsing.ParsedResponse[] Members)[] cases =
        [
            (new SubsetKey(codex), [.. records.Where(r => r.Source == SourceKind.Codex)]),
            (new SubsetKey(codex, [Local]), [records[0]]),
            (new SubsetKey(codex, [L.A]), [records[1]]),
            (new SubsetKey(SourceSet.Everything, [L.A]), [records[1], records[2]]),
            (new SubsetKey(codex, [Local, L.A]), [records[0], records[1]]),
            (new SubsetKey(SourceSet.Everything, ["unknown"]), []),
        ];
        foreach (var (key, members) in cases)
        {
            Assert.Equal(Ledger(members).Peak().Output, all.MeasuredPeakOutput(key), 3);
        }

        Assert.Equal(all.Peak().Output, all.MeasuredPeakOutput(SourceSet.Everything), 3);

        var localCodex = new SubsetKey(codex, [Local]);
        var scans = all.SourcePeakChunkScans;
        all.Upsert(L.Record("b2", L.B, start: L.Day + 60_000, end: L.Day + 61_000, output: 100));
        all.MeasuredPeakOutput(localCodex);
        Assert.Equal(scans, all.SourcePeakChunkScans);
        all.MeasuredPeakOutput(new SubsetKey(codex));
        Assert.True(all.SourcePeakChunkScans > scans, "the all-hosts codex subset contains host B's codex cell");
    }

    [Fact]
    public void AHostAboveItsLimitLosesItsOldestResponses()
    {
        var engine = new SpeedEngine(L.Day, zone: TimeZoneInfo.Utc) { RemoteResponseLimit = 10 };
        engine.Upsert(L.Record("local", Local, start: L.Now - 3000, end: L.Now - 2500, output: 1), isCompletion: true, seeded: true);
        for (var i = 0; i < 15; i++)
        {
            var end = L.Now - 60_000 + (i * 1000);
            engine.Upsert(L.Record($"a{i}", L.A, start: end - 500, end: end, output: 10), isCompletion: true, seeded: true);
        }

        var frame = engine.Compute(L.Now, L.Day, observeLive: false);
        var held = frame.Today.Requests - 1;
        Assert.InRange(held, 9, 10);
        Assert.False(engine.Upsert(L.Record("a14", Local, start: L.Now - 46_500, end: L.Now - 46_000, output: 10), isCompletion: true, seeded: true));
        Assert.True(engine.Upsert(L.Record("a0", Local, start: L.Now - 60_500, end: L.Now - 60_000, output: 10), isCompletion: true, seeded: true),
            "the oldest left, so its key is free again");
    }

    [Fact]
    public void IdleAndRemovalTouchOnlyTheirHost()
    {
        var engine = BusyEngine(tracksCells: true);
        engine.SetIdle(L.A);
        Assert.Equal(AgentPhase.Idle, engine.Agents[L.AgentId(L.A, "a")].Phase);
        Assert.Null(engine.Agents[L.AgentId(L.A, "a")].InFlightStartMs);
        Assert.Equal(AgentPhase.Generating, engine.Agents[L.AgentId(Local, "a")].Phase);
        engine.RemoveHost(L.A);
        Assert.False(engine.Agents.ContainsKey(L.AgentId(L.A, "a")));
        var frame = engine.Compute(L.Now, L.Day, observeLive: false);
        Assert.All(frame.Cells.Keys, c => Assert.NotEqual(L.A, c.Host));
        Assert.Equal(400 + 300 + 75, frame.Today.Output);
    }

    [Fact]
    public void RemoteAgentsCarryTheirHostAndFallBackToTheirFolder()
    {
        var agent = RemoteIngest.EngineAgent(L.Agent(L.A) with { Project = null }, parentId: "p");
        Assert.Equal(L.A, agent.Host);
        Assert.Equal("sessions", agent.Project);
        Assert.Equal("p", agent.ParentId);
        Assert.Equal("x", RemoteIngest.FallbackProject("x"));
    }

    private static SpeedEngine BusyEngine(bool tracksCells)
    {
        var engine = new SpeedEngine(L.Day, zone: TimeZoneInfo.Utc) { TracksCells = tracksCells };
        Parsing.ParsedResponse[] records =
        [
            L.Record("l1", Local, start: L.Now - 2500, end: L.Now - 500, output: 400, cost: 3000),
            L.Record("l2", Local, "c", SourceKind.ClaudeCode, L.Now - 2000, L.Now, 300),
            L.Record("a1", L.A, start: L.Now - 2800, end: L.Now - 200, output: 260, cost: 1200),
            L.Record("a2", L.A, "c", SourceKind.ClaudeCode, L.Now - 1500, L.Now - 100, 140, 500),
            L.Record("a3", L.A, start: L.Now - 2600, end: L.Now - 900, output: 90, final: false),
            L.Record("b1", L.B, start: L.Now - 2900, end: L.Now - 300, output: 75),
            L.Record("old", L.B, start: L.Day - 20_000, end: L.Day - 10_000, output: 5000, cost: 9000),
        ];
        foreach (var record in records)
        {
            engine.Upsert(record, isCompletion: true, seeded: true);
        }

        engine.UpdateAgent(RemoteIngest.EngineAgent(L.Agent(L.A, "a", phase: AgentPhase.Generating, inFlightStartMs: L.Now - 1500, lastLineMs: L.Now - 100), null));
        engine.UpdateAgent(new AgentInfo(L.AgentId(Local, "a"), SourceKind.Codex, "p", "gpt-5.5", false, AgentPhase.Generating, L.Now - 800, L.Now - 100));
        return engine;
    }
}
