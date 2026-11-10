using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 2, 4.4, 7, 8.2, 11.1: the coordinator connects a host to the running live monitor and routes history and quota events.</summary>
public sealed class RemoteCoordinatorTests
{
    private const string HostId = "8C0D6B0A-1111-4222-8333-0000000000C1";

    [Fact]
    public async Task AHostReachesTheSnapshotAndSurvivesARestart()
    {
        using var directory = new TempDirectory();
        using var relay = new Relay();
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex, SourceKind.ClaudeCode },
            CodexRoot: directory.Combine("missing-codex"), ClaudeRoot: directory.Combine("missing-claude"));
        var hub = await Task.Run(() => new SpeedHub());
        SpeedSnapshot latest = SpeedSnapshot.Empty;
        hub.SnapshotChanged += (_, snapshot) => Volatile.Write(ref latest, snapshot);
        using var monitor = new LiveMonitor(hub, configuration);
        var environment = new RemoteEnvironment(relay.Transport, relay.Relays.Bundle, SystemRemoteScheduling.Instance, static () => TimeZoneInfo.Utc,
            new Dictionary<string, string> { ["HOME"] = "/Users/me", [RemoteSsh.ConfigFileVariable] = "/tmp/agent-speed-test/ssh_config" });
        using var coordinator = new RemoteCoordinator(monitor, environment);
        var history = new HistoryLog();
        coordinator.HistorySink = history;
        coordinator.Update(new RemoteHostsDocument([new RemoteHost("devbox", HostId)]), networkEnabled: true);
        monitor.Start();

        var agentId = $"ssh://{HostId}//home/u/.codex/sessions/a.jsonl";
        bool ShowsTheHost()
        {
            var snapshot = Volatile.Read(ref latest);
            return snapshot.State.Kind == MonitorStateKind.Running && snapshot.Today.Output == 340
                && snapshot.Hosts.Select(h => h.Id).SequenceEqual([RemoteHost.LocalId, HostId]) && snapshot.Hosts[^1].IsConnected
                && snapshot.Agents.Any(a => a.Id == agentId && a.Host == HostId)
                // The host's source detection reaches the snapshot on its own turn, after its first agents.
                && snapshot.Sources.Single(s => s.Kind == SourceKind.Codex).Available;
        }

        Assert.True(await WaitUntil(ShowsTheHost), Describe(Volatile.Read(ref latest)));
        Assert.Equal("devbox", latest.Hosts[^1].Name);
        Assert.Equal([["codex", "claudeCode"]], relay.Starts);
        var run = relay.Transport.Runs[0];
        var arguments = run.Request.Arguments.ToList();
#if DEBUG
        Assert.Equal("/tmp/agent-speed-test/ssh_config", arguments[arguments.IndexOf("-F") + 1]);
        Assert.True(arguments.IndexOf("-F") < arguments.IndexOf("--"));
#else
        Assert.DoesNotContain("-F", arguments);
#endif

        Assert.True(await WaitUntil(() => run.WrittenTypes.Contains("history")));
        Assert.Contains("historyDue", history.Kinds);
        Assert.Contains("status:connected", history.Kinds);
        run.Send("""{"t":"historyDone","days":0,"analyzed":3}""");
        Assert.True(await WaitUntil(() => history.Kinds.Contains("message:HistoryDone")));
        Assert.Equal(RemoteStatus.Connected, coordinator.Statuses[HostId].Status);

        monitor.Update(configuration with { CodexRoot = directory.Combine("other-codex") });
        Assert.True(await WaitUntil(ShowsTheHost));
        Assert.Single(relay.Transport.Runs);

        hub.PerfWatch = new Core.Reporting.ReportPerfWatch(1, true);
        Assert.True(await WaitUntil(() => run.WrittenTypes.Contains("watch")));

        monitor.Update(configuration with { CodexRoot = directory.Combine("other-codex"), EnabledSources = new HashSet<SourceKind> { SourceKind.Codex } });
        Assert.True(await WaitUntil(() => relay.Starts.Count == 2));
        Assert.Equal(["codex"], relay.Starts[^1]);
        Assert.True(await WaitUntil(ShowsTheHost));

        coordinator.Update(RemoteHostsDocument.Empty, networkEnabled: true);
        Assert.True(await WaitUntil(() => Volatile.Read(ref latest).Hosts.Count == 0 && Volatile.Read(ref latest).Today.Output == 0));
        Assert.Empty(coordinator.Statuses);
    }

    [Fact]
    public void TheMasterSwitchStopsEveryHost()
    {
        using var relay = new Relay();
        var hub = new SpeedHub();
        using var monitor = new LiveMonitor(hub, new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex }));
        var environment = new RemoteEnvironment(relay.Transport, relay.Relays.Bundle, SystemRemoteScheduling.Instance, static () => TimeZoneInfo.Utc,
            new Dictionary<string, string>());
        using var coordinator = new RemoteCoordinator(monitor, environment);
        var statuses = new List<RemoteStatus>();
        coordinator.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status.Status);
            }
        };
        coordinator.Update(new RemoteHostsDocument([new RemoteHost("devbox", HostId)]), networkEnabled: false);
        Assert.True(SpinWait.SpinUntil(() => coordinator.Statuses.ContainsKey(HostId), TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteStatus.NetworkOff, coordinator.Statuses[HostId].Status);
        Assert.Empty(relay.Transport.Launched);
        Assert.Empty(coordinator.ConnectedHostIds);
    }

    [Fact]
    public void QuotaLinesRouteOnlyToTheQuotaEvent()
    {
        static RemoteHostEvent Event(RelayMessage message) => new(HostId, 1, new RemoteHostEventKind.Message(message, Seeded: false));
        var report = new RemoteQuotaReport(HostId, new QuotaSnapshot(SourceKind.Kimi, 1, []), true, true, QuotaRouteStatus.Ok(1));
        Assert.Equal((false, false, true), RemoteCoordinator.RouteOf(Event(new RelayMessage.Quota(report))));
        Assert.Equal((false, false, true), RemoteCoordinator.RouteOf(Event(new RelayMessage.QuotaRemoved(HostId, SourceKind.Kimi))));
        Assert.Equal((false, false, true),
            RemoteCoordinator.RouteOf(Event(new RelayMessage.QuotaBridge(new RemoteQuotaBridge(HostId, ClaudeCliBridgeStatus.Ok, "~")))));
        Assert.Equal((true, false, false), RemoteCoordinator.RouteOf(Event(new RelayMessage.Ready())));
        Assert.Equal((false, true, false), RemoteCoordinator.RouteOf(Event(new RelayMessage.HistoryDone(0, 0))));
        Assert.Equal((true, true, false), RemoteCoordinator.RouteOf(new RemoteHostEvent(HostId, 1, new RemoteHostEventKind.Status(new RemoteHostStatus(RemoteStatus.Connected)))));
    }

    [Fact]
    public async Task AHostsQuotaLinesReachTheQuotaEvent()
    {
        using var relay = new Relay();
        var hub = new SpeedHub();
        using var monitor = new LiveMonitor(hub, new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex, SourceKind.ClaudeCode }));
        var environment = new RemoteEnvironment(relay.Transport, relay.Relays.Bundle, SystemRemoteScheduling.Instance, static () => TimeZoneInfo.Utc,
            new Dictionary<string, string>());
        using var coordinator = new RemoteCoordinator(monitor, environment);
        var received = new List<RemoteQuotaEventArgs>();
        coordinator.QuotaReceived += (_, e) =>
        {
            lock (received)
            {
                received.Add(e);
            }
        };
        int Received()
        {
            lock (received)
            {
                return received.Count;
            }
        }

        var document = new RemoteHostsDocument([new RemoteHost("devbox", HostId) { QuotaClaudeCli = true }]);
        coordinator.Update(document, networkEnabled: true, new RemoteQuotaSwitches([SourceKind.Codex, SourceKind.Kimi]));
        Assert.True(await WaitUntil(() => coordinator.Statuses.TryGetValue(HostId, out var status) && status.Status == RemoteStatus.Connected));
        var run = relay.Transport.Runs[0];
        var start = run.WrittenLines[0].GetProperty("quota");
        Assert.Equal(["codex", "claudeCode"], QuotaLines.Sources(start));
        Assert.True(start.GetProperty("claudeBridge").GetBoolean());

        var observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        run.Send(QuotaLines.Quota(source: "codex", observed: observed, windows: []));
        run.Send(QuotaLines.Removed("kimi"));
        run.Send(QuotaLines.Bridge("ok"));
        Assert.True(await WaitUntil(() => Received() == 3));
        List<RemoteQuotaEventArgs> events;
        lock (received)
        {
            events = [.. received];
        }

        Assert.All(events, e => Assert.Equal(HostId, e.HostId));
        Assert.Equal(SourceKind.Codex, events[0].Report?.Source);
        Assert.Null(events[0].RemovedSource);
        Assert.InRange(events[0].Report!.Snapshot.ObservedMs, observed - 5_000, observed + 5_000);
        Assert.Equal(SourceKind.Kimi, events[1].RemovedSource);
        Assert.Equal(ClaudeCliBridgeStatus.Ok, events[2].Bridge?.State);

        // New switches go to the relay without a reconnect; an update without switches keeps them.
        coordinator.Update(document, networkEnabled: true, new RemoteQuotaSwitches([SourceKind.Codex], ClaudeCode: false));
        coordinator.Hub.Connection(HostId)!.Queue.Sync(() => { });
        Assert.Equal(["quota"], run.WrittenTypes.Where(t => t == "quota"));
        Assert.Equal(["codex"], QuotaLines.Sources(run.WrittenLines[^1].GetProperty("config")));
        coordinator.Update(document, networkEnabled: true);
        coordinator.Hub.Connection(HostId)!.Queue.Sync(() => { });
        Assert.Single(run.WrittenTypes, t => t == "quota");
        Assert.Single(relay.Transport.Runs);

        // Lines of a host that is not configured are dropped.
        coordinator.Route(new RemoteHostEvent("8C0D6B0A-1111-4222-8333-0000000000FF", 1,
            new RemoteHostEventKind.Message(new RelayMessage.QuotaRemoved("8C0D6B0A-1111-4222-8333-0000000000FF", SourceKind.Kimi), Seeded: false)));
        Assert.Equal(3, Received());
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    private static string Describe(SpeedSnapshot snapshot) =>
        $"{snapshot.State.Kind} today {snapshot.Today.Output} hosts [{string.Join(", ", snapshot.Hosts.Select(h => $"{h.Id}:{h.Remote?.Status.Code}:{h.IsConnected}"))}] " +
        $"agents [{string.Join(", ", snapshot.Agents.Select(a => a.Id))}]";

    /// <summary>A scripted relay: the probe finds it installed; every <c>start</c> line seeds one agent and one response.</summary>
    private sealed class Relay : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<List<string>> _starts = [];

        public Relay()
        {
            var relays = Relays;
            Transport.Script = process =>
            {
                if (process.IsProbe)
                {
                    process.SendStdout(ConnectionHarness.ProbeOutput(relays, installed: true));
                    process.Finish(0);
                }
                else if (process.IsRun)
                {
                    process.OnWrite = (p, data) =>
                    {
                        foreach (var text in Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            using var line = JsonDocument.Parse(text);
                            if (line.RootElement.GetProperty("t").GetString() != "start")
                            {
                                continue;
                            }

                            lock (_lock)
                            {
                                _starts.Add([.. line.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString()!)]);
                            }

                            Seed(p);
                        }
                    };
                }
            };
        }

        public FakeTransport Transport { get; } = new();

        public FakeRelayBundle Relays { get; } = new();

        public List<List<string>> Starts
        {
            get
            {
                lock (_lock)
                {
                    return [.. _starts];
                }
            }
        }

        public void Dispose() => Relays.Dispose();

        private static void Seed(FakeProcess process)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            process.Send(RelayLines.Hello(now));
            process.Send(RelayLines.Agent(agent =>
            {
                agent["phase"] = "idle";
                agent["inFlightStartMs"] = null;
                agent["lastLineMs"] = now - 1000;
                agent["actions"] = new List<object?>();
            }));
            process.Send(RelayLines.Response(response =>
            {
                response["startMs"] = now - 60_000;
                response["endMs"] = now - 50_000;
                response["firstMs"] = now - 59_000;
            }));
            process.Send(RelayLines.Ready);
            process.Send(RelayLines.Json(new()
            {
                ["t"] = "status",
                ["sources"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["source"] = "codex", ["available"] = true, ["sessions"] = 1, ["unsupported"] = false, ["configuredModes"] = new List<object?>(),
                    },
                },
                ["malformed"] = 0, ["unreadable"] = 0, ["partialHistory"] = false,
            }));
        }
    }

    private sealed class HistoryLog : IRemoteHistorySink
    {
        private readonly Lock _lock = new();
        private readonly List<string> _kinds = [];

        public List<string> Kinds
        {
            get
            {
                lock (_lock)
                {
                    return [.. _kinds];
                }
            }
        }

        public void RemoteHistory(RemoteHostEvent hostEvent, IRemoteHistoryRequests requests)
        {
            var kind = hostEvent.Kind switch
            {
                RemoteHostEventKind.Status { Value: var status } => "status:" + status.Status.Code,
                RemoteHostEventKind.HistoryDue => "historyDue",
                RemoteHostEventKind.Message { Value: var message } => "message:" + message.GetType().Name,
                _ => "other",
            };
            lock (_lock)
            {
                _kinds.Add(kind);
            }

            if (hostEvent.Kind is RemoteHostEventKind.HistoryDue)
            {
                requests.RequestHistory(hostEvent.HostId, new Dictionary<string, string> { ["2026-11-04/codex"] = "0" });
            }
        }
    }
}
