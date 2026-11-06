using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>Remote hosts' lines as the feed receives them (the macOS <c>RemoteLive</c>).</summary>
internal static class RemoteLive
{
    public const string A = "8C0D6B0A-1111-4222-8333-00000000000A";
    public const string B = "8C0D6B0A-1111-4222-8333-00000000000B";
    public const long Day = 1_800_000_000_000;
    public const long Now = Day + 3_600_000;

    public static string AgentId(string host, string name = "a") =>
        host == RemoteHost.LocalId ? $"/local/{name}.jsonl" : $"ssh://{host}//home/u/.codex/sessions/{name}.jsonl";

    public static ParsedResponse Record(string key, string host, string agent = "a", SourceKind source = SourceKind.Codex, long start = Now - 2000,
        long end = Now - 1000, long output = 100, long? cost = null, bool final = true) =>
        new(key, source, AgentId(host, agent), "gpt-5.5", start, end, new TokenCounts(Input: 100, Output: output), Final: final, CostMicroUsd: cost)
        {
            Host = host,
        };

    public static RemoteAgent Agent(string host, string name = "a", SourceKind source = SourceKind.Codex, AgentPhase phase = AgentPhase.Idle,
        long? inFlightStartMs = null, long lastLineMs = Now - 1000) =>
        new(host, AgentId(host, name), $"/home/u/.codex/sessions/{name}.jsonl", source, "proj", "/home/u/proj", "gpt-5.5", false, phase,
            inFlightStartMs, lastLineMs, ExecutionModes.Empty, ExecutionModes.Empty, ExecutionModes.Empty, []);

    public static RelayMessage Response(ParsedResponse record, bool completion = true) =>
        new RelayMessage.Response(new RemoteResponse(record.Host, record, completion, "proj"));

    public static RemoteHostEvent Message(string host, int connection, RelayMessage message, bool seeded) =>
        new(host, connection, new RemoteHostEventKind.Message(message, seeded));

    public static RemoteHostEvent Status(string host, int connection, RemoteStatus status) =>
        new(host, connection, new RemoteHostEventKind.Status(new RemoteHostStatus(status)));

    public static readonly RelayMessage Hello = new RelayMessage.Hello(new RelayHello(1, "1.7.0", "x86_64", Now));

    public static RelayMessage Report(params (SourceKind Source, bool Available, int Sessions)[] sources) =>
        new RelayMessage.Status(new RemoteSourceReport([.. sources.Select(s => new RemoteSourceState(s.Source, s.Available, s.Sessions, false, ExecutionModes.Empty))], 0, 0,
            false));

    public static List<RemoteHostEvent> Seeding(string host, int connection, IEnumerable<RelayMessage> lines) =>
    [
        Status(host, connection, RemoteStatus.Connecting), Message(host, connection, Hello, seeded: true), Status(host, connection, RemoteStatus.Seeding(0)),
        .. lines.Select(line => Message(host, connection, line, seeded: true)),
        Message(host, connection, new RelayMessage.Ready(), seeded: true), Status(host, connection, RemoteStatus.Connected),
    ];

    public static RemoteFeed Feed() => new(() => DateTimeOffset.FromUnixTimeMilliseconds(Now), static () => TimeZoneInfo.Utc);
}

/// <summary>A monitor session without local sources that applies the feed's changes at once (the macOS <c>SessionSink</c>).</summary>
internal sealed class SessionSink : IRemoteFeedSink, IDisposable
{
    public SessionSink(TempDirectory directory, params SourceKind[] sources)
    {
        var enabled = sources.Length == 0 ? [SourceKind.Codex, SourceKind.ClaudeCode] : sources;
        var configuration = new MonitorConfiguration(new HashSet<SourceKind>(enabled));
        Core = new MonitorCore(configuration, directory.Combine("missing-codex"), directory.Combine("missing-claude"), directory.Combine("missing-dsh"),
            RemoteLive.Day) { Clock = () => RemoteLive.Now };
    }

    public MonitorCore Core { get; }

    public int Batches { get; private set; }

    public void RemoteFeed(IReadOnlyList<RemoteFeedChange> changes)
    {
        Batches++;
        Core.ApplyRemote([.. changes]);
    }

    public (SpeedSnapshot Snapshot, IReadOnlyList<SpeedEvent> Events) Advance(long nowMs = RemoteLive.Now, IReadOnlyCollection<SubsetKey>? subsets = null) =>
        Core.BuildSnapshot(nowMs, RemoteLive.Day, MonitorState.Running, observeLive: true, subsets);

    public (SpeedSnapshot Snapshot, IReadOnlyList<SpeedEvent> Events) Establish(long nowMs = RemoteLive.Now) =>
        Core.BuildSnapshot(nowMs, RemoteLive.Day, MonitorState.Running, observeLive: false, (IReadOnlyCollection<SubsetKey>?)null);

    public static List<ResponseSummary> Completions(IReadOnlyList<SpeedEvent> events) =>
        [.. events.OfType<SpeedEvent.ResponseCompleted>().Select(e => e.Response)];

    public void Dispose() => Core.Dispose();
}

/// <summary>A feed with one attached session, the hosts configured.</summary>
internal sealed class IngestHarness : IDisposable
{
    private readonly TempDirectory _directory = new();

    public IngestHarness(params RemoteHostInfo[] hosts)
    {
        Sink = new SessionSink(_directory);
        Sink.Core.ApplyRemote(Feed.Attach(Sink));
        Feed.SetHosts(hosts.Length == 0 ? [new RemoteHostInfo(RemoteLive.A, "devbox")] : hosts);
    }

    public RemoteFeed Feed { get; } = RemoteLive.Feed();

    public SessionSink Sink { get; }

    public TempDirectory Directory => _directory;

    public IReadOnlyDictionary<string, Engine.AgentInfo> EngineAgents => Sink.Core.EngineAgents;

    public void Send(IEnumerable<RemoteHostEvent> events)
    {
        foreach (var hostEvent in events)
        {
            Feed.Receive(hostEvent);
        }
    }

    public void Dispose()
    {
        Sink.Dispose();
        _directory.Dispose();
    }
}
