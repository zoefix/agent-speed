using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteLive;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 7: a host's lines in the live monitor (prefixing, seeding, reconnect, disconnect, restart, removal).</summary>
public sealed class RemoteIngestTests
{
    private static readonly RemoteAgent[] Seeded =
    [
        L.Agent(L.A, "a", lastLineMs: L.Now - 200),
        L.Agent(L.A, "b", SourceKind.ClaudeCode, lastLineMs: L.Now - 30_000),
    ];

    private static readonly RemoteAgent Generating = L.Agent(L.A, "a", phase: AgentPhase.Generating, inFlightStartMs: L.Now - 1500, lastLineMs: L.Now - 200);

    private static readonly Parsing.ParsedResponse[] SeededResponses =
    [
        L.Record("k1", L.A, start: L.Now - 60_000, end: L.Now - 50_000, output: 500),
        L.Record("k2", L.A, "b", SourceKind.ClaudeCode, L.Now - 40_000, L.Now - 30_000, 300),
    ];

    [Fact]
    public void SeededLinesAreSilentAndLiveLinesAnnounce()
    {
        using var harness = new IngestHarness();
        harness.Send(L.Seeding(L.A, 7, SeedingLines()));
        var (snapshot, events) = harness.Sink.Establish();
        Assert.Empty(events);
        Assert.Equal(800, snapshot.Today.Output);
        Assert.Contains(snapshot.Agents, a => a.Id == L.AgentId(L.A, "a"));
        Assert.Equal(L.AgentId(L.A, "a"), snapshot.Agents.First(a => a.Id == L.AgentId(L.A, "b")).ParentId);
        Assert.All(snapshot.Agents, a => Assert.Equal(L.A, a.Host));

        var live = L.Record("k3", L.A, start: L.Now - 2000, end: L.Now - 100, output: 200);
        harness.Send([L.Message(L.A, 7, L.Response(live), seeded: false)]);
        (snapshot, events) = harness.Sink.Advance(L.Now + 500);
        var completions = SessionSink.Completions(events);
        Assert.Single(completions);
        Assert.Equal(L.A, completions[0].Host);
        Assert.Equal(200, completions[0].OutputTokens);
        Assert.Equal(1000, snapshot.Today.Output);
        Assert.True(snapshot.Sources.First(s => s.Kind == SourceKind.Codex).Live.Output > 0);
    }

    [Fact]
    public void AReconnectSeedsAgainWithoutCountingTwice()
    {
        using var harness = new IngestHarness();
        harness.Send(L.Seeding(L.A, 7, SeedingLines()));
        var (first, _) = harness.Sink.Establish();
        harness.Send([L.Status(L.A, 7, RemoteStatus.Unreachable), L.Status(L.A, 9, RemoteStatus.Connecting),
            L.Message(L.A, 9, L.Hello, seeded: true), L.Status(L.A, 9, RemoteStatus.Seeding(0))]);
        harness.Send([L.Message(L.A, 9, new RelayMessage.Agent(Seeded[0]), seeded: true),
            .. SeededResponses.Select(r => L.Message(L.A, 9, L.Response(r), seeded: true))]);
        Assert.True(harness.EngineAgents.ContainsKey(L.AgentId(L.A, "b")), "the old state stays until ready");
        var (snapshot, events) = harness.Sink.Advance(L.Now + 500);
        Assert.Empty(events);
        Assert.Equal(first.Today, snapshot.Today);
        Assert.Equal(first.PeakToday, snapshot.PeakToday);

        harness.Send([L.Message(L.A, 9, new RelayMessage.Ready(), seeded: true), L.Status(L.A, 9, RemoteStatus.Connected)]);
        (snapshot, events) = harness.Sink.Advance(L.Now + 1000);
        Assert.Empty(events);
        Assert.False(harness.EngineAgents.ContainsKey(L.AgentId(L.A, "b")), "an agent the new connection did not send leaves");
        Assert.True(harness.EngineAgents.ContainsKey(L.AgentId(L.A, "a")));
        Assert.Equal(first.Today, snapshot.Today);
        Assert.Empty(harness.Feed.Mirror(L.A)!.Parents);
    }

    [Fact]
    public void ADisconnectedHostsAgentsBecomeIdle()
    {
        using var harness = new IngestHarness();
        harness.Send(L.Seeding(L.A, 3, SeedingLines(generating: true)));
        var (before, _) = harness.Sink.Establish();
        Assert.True(before.Sources.First(s => s.Kind == SourceKind.Codex).LiveEstimated.Output > 0, "the in-flight estimate runs while connected");
        harness.Send([L.Status(L.A, 3, RemoteStatus.Unreachable)]);
        var agent = harness.EngineAgents[L.AgentId(L.A, "a")];
        Assert.Equal(AgentPhase.Idle, agent.Phase);
        Assert.Null(agent.InFlightStartMs);
        var (after, _) = harness.Sink.Advance(L.Now + 500);
        Assert.Equal(0, after.Sources.First(s => s.Kind == SourceKind.Codex).LiveEstimated.Output);
        Assert.Equal(before.Today, after.Today);
        Assert.All(harness.Feed.Mirror(L.A)!.Agents.Values, a => Assert.Equal(AgentPhase.Idle, a.Phase));
    }

    [Fact]
    public void ANewSessionReplaysTheMirrorAsSeeded()
    {
        using var harness = new IngestHarness();
        harness.Send(L.Seeding(L.A, 4, SeedingLines()));
        harness.Send([L.Message(L.A, 4, L.Response(L.Record("k3", L.A, start: L.Now - 2000, end: L.Now - 100, output: 200)), seeded: false)]);
        var (old, _) = harness.Sink.Establish();

        using var restarted = new SessionSink(harness.Directory);
        restarted.Core.ApplyRemote(harness.Feed.Attach(restarted));
        var (snapshot, events) = restarted.Establish();
        Assert.Empty(events);
        Assert.Equal(old.Today, snapshot.Today);
        Assert.Equal(old.Agents.Select(a => a.Id).ToHashSet(), snapshot.Agents.Select(a => a.Id).ToHashSet());
        Assert.Equal(L.AgentId(L.A, "a"), snapshot.Agents.First(a => a.Id == L.AgentId(L.A, "b")).ParentId);
        Assert.True(restarted.Core.Remote.IsConnected(L.A));

        var batches = harness.Sink.Batches;
        harness.Send([L.Message(L.A, 4, L.Response(L.Record("k4", L.A, start: L.Now - 1000, end: L.Now, output: 50)), seeded: false)]);
        Assert.Equal(batches, harness.Sink.Batches);
        (snapshot, events) = restarted.Advance(L.Now + 500);
        Assert.Equal(old.Today.Output + 50, snapshot.Today.Output);
        Assert.Equal([50L], SessionSink.Completions(events).Select(c => c.OutputTokens));
    }

    [Fact]
    public void RemovedHostsLeaveAndUnknownHostsAreIgnored()
    {
        using var harness = new IngestHarness(new RemoteHostInfo(L.A, "devbox"), new RemoteHostInfo(L.B, "gpu"));
        harness.Send(L.Seeding(L.A, 1, SeedingLines()));
        harness.Send(L.Seeding(L.B, 2, [L.Response(L.Record("b1", L.B, start: L.Now - 9000, end: L.Now - 8000, output: 70))]));
        harness.Send([L.Message("8C0D6B0A-1111-4222-8333-0000000000FF", 5, L.Response(L.Record("x", L.B, start: L.Now - 9000, end: L.Now - 8000, output: 1)),
            seeded: false)]);
        var (snapshot, _) = harness.Sink.Advance();
        Assert.Equal(870, snapshot.Today.Output);
        harness.Feed.SetHosts([new RemoteHostInfo(L.B, "gpu")]);
        (snapshot, _) = harness.Sink.Advance(L.Now + 500);
        Assert.Equal(70, snapshot.Today.Output);
        Assert.All(snapshot.Agents, a => Assert.NotEqual(L.A, a.Host));
        Assert.Null(harness.Feed.Mirror(L.A));
    }

    [Fact]
    public void DisabledSourcesStayOut()
    {
        using var directory = new TempDirectory();
        var feed = L.Feed();
        using var sink = new SessionSink(directory, SourceKind.Codex);
        sink.Core.ApplyRemote(feed.Attach(sink));
        feed.SetHosts([new RemoteHostInfo(L.A, "devbox")]);
        foreach (var hostEvent in L.Seeding(L.A, 1, SeedingLines()))
        {
            feed.Receive(hostEvent);
        }

        var (snapshot, _) = sink.Advance();
        Assert.Equal(500, snapshot.Today.Output);
        Assert.All(snapshot.Agents, a => Assert.Equal(SourceKind.Codex, a.Source));
    }

    [Fact]
    public void TheSnapshotListsHostsAndCells()
    {
        using var harness = new IngestHarness(new RemoteHostInfo(L.A, "devbox"), new RemoteHostInfo(L.B, "gpu"));
        harness.Send(L.Seeding(L.A, 1, SeedingLines()));
        harness.Send([L.Status(L.B, 0, RemoteStatus.Disabled)]);
        var (snapshot, _) = harness.Sink.Advance();
        Assert.Equal([RemoteHost.LocalId, L.A, L.B], snapshot.Hosts.Select(h => h.Id));
        Assert.Equal([RemoteHost.LocalId, "devbox", "gpu"], snapshot.Hosts.Select(h => h.Name));
        Assert.Equal([true, true, false], snapshot.Hosts.Select(h => h.IsConnected));
        Assert.Equal(RemoteStatus.Connected, snapshot.Hosts[1].Remote!.Status);
        Assert.Equal(RemoteStatus.Disabled, snapshot.Hosts[2].Remote!.Status);
        Assert.Equal(800, snapshot.Hosts[1].Today.Output);
        Assert.Equal(2, snapshot.Hosts[1].Sessions);
        Assert.Equal(L.A, snapshot.Agents.First(a => a.Id == L.AgentId(L.A, "a")).Host);
        var codex = snapshot.Sources.First(s => s.Kind == SourceKind.Codex);
        Assert.True(codex.Available, "not detected on this computer, but on the connected host");
        Assert.Equal(1, codex.SessionCount);
        var cell = snapshot.HostSources.Single(c => c.Cell == new HostSource(L.A, SourceKind.Codex));
        Assert.True(cell.Available);
        Assert.Equal(500, cell.Today.Output);
        Assert.Equal(1, cell.SessionCount);
        foreach (var status in snapshot.Sources)
        {
            var cells = snapshot.HostSources.Where(c => c.Source == status.Kind).ToList();
            Assert.Equal(status.Today, cells.Aggregate(TokenTotals.Zero, (sum, c) => sum + c.Today));
            Assert.Equal(status.Live.Output, cells.Sum(c => c.Live.Output), 9);
        }

        using var plain = new IngestHarness();
        plain.Feed.SetHosts([]);
        var (quiet, _) = plain.Sink.Advance();
        Assert.Empty(quiet.Hosts);
        Assert.Empty(quiet.HostSources);
    }

    [Fact]
    public void HostSubsetPeaksAreReportedForTheirCells()
    {
        using var harness = new IngestHarness();
        harness.Send(L.Seeding(L.A, 1, SeedingLines()));
        var codex = SourceSet.Of(SourceKind.Codex);
        SubsetKey[] keys = [new(codex), new(codex, [L.A]), new(codex, [RemoteHost.LocalId]), new(SourceSet.Everything, [L.A])];
        var (snapshot, _) = harness.Sink.Advance(subsets: keys);
        Assert.Equal([codex], snapshot.SubsetPeaks.Keys);
        Assert.Equal(3, snapshot.HostSubsetPeaks.Count);
        Assert.Equal(snapshot.SubsetPeaks[codex], snapshot.HostSubsetPeaks[new SubsetKey(codex, [L.A])], 9);
        Assert.True(snapshot.HostSubsetPeaks[new SubsetKey(codex, [L.A])] > 0);
        Assert.Equal(0, snapshot.HostSubsetPeaks[new SubsetKey(codex, [RemoteHost.LocalId])]);
        Assert.Equal(snapshot.PeakToday.Output, snapshot.MeasuredPeak(new SubsetKey(SourceSet.Everything, [L.A]))!.Value, 9);
        Assert.Null(snapshot.MeasuredPeak(new SubsetKey(SourceSet.Of(SourceKind.Gemini), [L.A])));
    }

    [Fact]
    public void MirrorKeepsItsResponsesBounded()
    {
        var mirror = new RemoteHostMirror(L.A) { ResponseLimit = 20 };
        for (var i = 0; i < 30; i++)
        {
            var record = L.Record($"k{i}", L.A, start: L.Now - 60_000 + i, end: L.Now - 50_000 + i, output: 1);
            mirror.Apply(L.Message(L.A, 1, L.Response(record), seeded: true), L.Now, L.Day);
        }

        Assert.True(mirror.Responses.Count <= 20);
        Assert.True(mirror.Responses.ContainsKey("k29"));
        Assert.False(mirror.Responses.ContainsKey("k0"));
        var yesterday = L.Record("y", L.A, start: L.Day - 9_000_000, end: L.Day - 8_000_000, output: 1);
        mirror.Apply(L.Message(L.A, 1, L.Response(yesterday), seeded: true), L.Now + 120_000, L.Day);
        Assert.False(mirror.Responses.ContainsKey("y"), "outside today and the retention window");
    }

    private static List<RelayMessage> SeedingLines(bool generating = false) =>
    [
        .. (generating ? [Seeded[1], Generating] : Seeded).Select(a => (RelayMessage)new RelayMessage.Agent(a)),
        .. SeededResponses.Select(r => L.Response(r)),
        new RelayMessage.Parents(new Dictionary<string, string> { [L.AgentId(L.A, "b")] = L.AgentId(L.A, "a") }),
        L.Report((SourceKind.Codex, true, 1), (SourceKind.ClaudeCode, true, 1)),
    ];
}
