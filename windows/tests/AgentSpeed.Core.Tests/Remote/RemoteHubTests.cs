using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 2.3, 4.4, 7 <i>Sources</i>: the hub keeps one connection per enabled host in step with the settings.</summary>
public sealed class RemoteHubTests
{
    internal const string A = "8C0D6B0A-1111-4222-8333-00000000000A";
    internal const string B = "8C0D6B0A-1111-4222-8333-00000000000B";
    internal static readonly byte[] Prices = """{"schema":3}"""u8.ToArray();

    [Fact]
    public void StartsEnabledHostsAndKeepsThemOnAnIdenticalUpdate()
    {
        using var harness = new Harness();
        RemoteHost[] hosts = [new("alpha", A), new("beta", B)];
        harness.Update(hosts);
        Assert.Equal(new HashSet<string> { A, B }, harness.Hub.ConnectedHostIds.ToHashSet());
        Assert.True(harness.Runs("alpha").Count == 1 && harness.Runs("beta").Count == 1);
        var first = harness.Hub.Connection(A);
        harness.Update(hosts);
        Assert.Same(first, harness.Hub.Connection(A));
        Assert.Equal(2, harness.Transport.Runs.Count);
    }

    [Fact]
    public void DisablingRemovingAndRenaming()
    {
        using var harness = new Harness();
        var alpha = new RemoteHost("alpha", A);
        var beta = new RemoteHost("beta", B);
        harness.Update([alpha, beta]);
        var alphaRun = harness.Runs("alpha")[0];
        var betaRun = harness.Runs("beta")[0];
        var connection = harness.Hub.Connection(A);
        alpha = alpha with { Name = "Renamed" };
        harness.Update([alpha, beta]);
        Assert.Same(connection, harness.Hub.Connection(A));
        alpha = alpha with { Enabled = false };
        harness.Update([alpha, beta]);
        Assert.True(alphaRun.IsInputClosed && alphaRun.WasTerminated);
        Assert.Equal(RemoteStatus.Disabled, harness.Events.Statuses(A)[^1]);
        Assert.Equal([B], harness.Hub.ConnectedHostIds);
        harness.Update([alpha]);
        Assert.True(betaRun.IsInputClosed && betaRun.WasTerminated);
        Assert.Empty(harness.Hub.ConnectedHostIds);
        Assert.Equal(RemoteStatus.Connecting, harness.Events.Statuses(B)[^1]);
        Assert.Single(harness.Events.Statuses(A), s => s == RemoteStatus.Disabled);
    }

    [Fact]
    public void EditingTheDestinationOrSourcesReconnects()
    {
        using var harness = new Harness();
        var alpha = new RemoteHost("alpha", A);
        harness.Update([alpha]);
        var first = harness.Transport.Runs[^1];
        alpha = alpha with { Destination = "alpha2" };
        harness.Update([alpha]);
        Assert.True(first.WasTerminated);
        Assert.Single(harness.Runs("alpha2"));
        alpha = alpha with { RelayFolder = "/opt/agent-speed" };
        harness.Update([alpha]);
        var third = harness.Transport.Runs[^1];
        Assert.Contains(" /opt/agent-speed ", third.RemoteCommand, StringComparison.Ordinal);
        harness.Update([alpha], sources: SourceSet.Of(SourceKind.Codex).With(SourceKind.Gemini));
        var fourth = harness.Transport.Runs[^1];
        Assert.True(third.WasTerminated && !ReferenceEquals(fourth, third));
        Assert.Equal(["codex", "gemini"], fourth.WrittenLines[0].GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public void MasterSwitchOffMeansNoSsh()
    {
        using var harness = new Harness();
        RemoteHost[] hosts = [new("alpha", A), new("beta", B) { Enabled = false }];
        harness.Update(hosts, network: false);
        Assert.Empty(harness.Transport.Launched);
        Assert.Equal([RemoteStatus.NetworkOff], harness.Events.Statuses(A));
        Assert.Equal([RemoteStatus.NetworkOff], harness.Events.Statuses(B));
        harness.Update(hosts, network: true);
        Assert.Equal([RemoteStatus.NetworkOff, RemoteStatus.Disabled], harness.Events.Statuses(B));
        Assert.Single(harness.Runs("alpha"));
        var run = harness.Runs("alpha")[0];
        harness.Update(hosts, network: false);
        Assert.True(run.WasTerminated);
        Assert.Equal(RemoteStatus.NetworkOff, harness.Events.Statuses(A)[^1]);
        Assert.Equal(2, harness.Transport.Launched.Count);
    }

    [Fact]
    public void PricesGoToRunningRelaysWithoutReconnecting()
    {
        using var harness = new Harness();
        RemoteHost[] hosts = [new("alpha", A)];
        harness.Update(hosts);
        var run = harness.Transport.Runs[^1];
        harness.Update(hosts, prices: """{"schema":4}"""u8.ToArray());
        harness.Update(hosts, prices: "not json"u8.ToArray());
        Assert.Single(harness.Transport.Runs);
        Assert.Equal(["start", "prices"], run.WrittenTypes);
        Assert.Equal(4, run.WrittenLines[^1].GetProperty("table").GetProperty("schema").GetInt32());
        harness.Hub.SetWatch(true);
        harness.Settle();
        Assert.Equal("watch", run.WrittenTypes[^1]);
    }

    [Fact]
    public void InvalidHostsGetAStatusAndNoConnection()
    {
        using var harness = new Harness();
        harness.Update([new("-oProxyCommand=x", A), new("beta", B) { RelayFolder = "~/../x" }, new("gamma", "local")]);
        Assert.Empty(harness.Transport.Launched);
        Assert.Equal([RemoteStatus.InvalidDestination], harness.Events.Statuses(A));
        Assert.Equal([RemoteStatus.InvalidDestination], harness.Events.Statuses(B));
        Assert.Empty(harness.Events.Statuses("local"));
    }

    [Fact]
    public void WakeRetriesWaitingHostsAndHistoryRoutesToTheHost()
    {
        using var harness = new Harness();
        harness.Update([new("unreachable", A), new("beta", B)]);
        Assert.Equal(RemoteStatus.Unreachable, harness.Events.Statuses(A)[^1]);
        Assert.Single(harness.Transport.Launched, p => p.Request.Arguments.Contains("unreachable"));
        harness.Hub.NoteWake();
        harness.Settle();
        Assert.Equal(2, harness.Transport.Launched.Count(p => p.Request.Arguments.Contains("unreachable")));
        var run = harness.Runs("beta")[0];
        run.Send(RelayLines.Hello());
        run.Send(RelayLines.Ready);
        harness.Settle();
        Assert.Equal(1, harness.Events.HistoryDueCount(B));
        harness.Hub.RequestHistory(B, new Dictionary<string, string> { ["2026-11-03/codex"] = "0123456789abcdef" });
        harness.Settle();
        Assert.Equal("history", run.WrittenTypes[^1]);
        harness.Hub.StopAll();
        harness.Settle();
        Assert.True(run.WasTerminated);
        Assert.Empty(harness.Hub.ConnectedHostIds);
    }

    internal sealed class Harness : IDisposable
    {
        public Harness()
        {
            Relays = new FakeRelayBundle();
            Hub = new RemoteHub(
                new RemoteEnvironment(Transport, Relays.Bundle, Scheduling, static () => TimeZoneInfo.Utc, new Dictionary<string, string>()),
                Events.Queue, Events.Handler);
            var relays = Relays;
            Transport.Script = process =>
            {
                if (!process.IsProbe)
                {
                    return;
                }

                if (process.Request.Arguments.Contains("unreachable"))
                {
                    process.SendStderr("ssh: Could not resolve hostname unreachable\n");
                    process.Finish(255);
                }
                else
                {
                    process.SendStdout(ConnectionHarness.ProbeOutput(relays, installed: true));
                    process.Finish(0);
                }
            };
        }

        public ManualScheduling Scheduling { get; } = new();

        public FakeTransport Transport { get; } = new();

        public FakeRelayBundle Relays { get; }

        public RemoteEventLog Events { get; } = new();

        public RemoteHub Hub { get; }

        public void Update(
            IReadOnlyList<RemoteHost> hosts, bool network = true, SourceSet? sources = null, byte[]? prices = null, RemoteQuotaSwitches? quota = null)
        {
            Hub.Update(hosts, network, sources ?? SourceSet.Of(SourceKind.Codex), prices ?? Prices, quota);
            Settle();
        }

        public void Settle()
        {
            for (var i = 0; i < 64; i++)
            {
                foreach (var id in Hub.ConnectedHostIds)
                {
                    Hub.Connection(id)?.Queue.Sync(() => { });
                }

                foreach (var id in Hub.RetiringHostIds)
                {
                    Hub.RetiringConnection(id)?.Queue.Sync(() => { });
                }

                Events.Queue.Sync(() => { });
            }
        }

        public List<FakeProcess> Runs(string destination) => [.. Transport.Runs.Where(p => p.Request.Arguments.Contains(destination))];

        public void Dispose()
        {
            Hub.StopAll();
            Relays.Dispose();
        }
    }
}
