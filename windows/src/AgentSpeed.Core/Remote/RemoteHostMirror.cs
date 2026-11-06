using AgentSpeed.Core.Engine;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// Everything received from one host that a new monitor session needs (REMOTE 7 <i>Monitor restart</i>): its agents,
/// parents, responses of today and the retention window, status and source report. It also turns each event into the
/// changes a session applies, so a session and a replay see the same state. Not thread-safe.
/// </summary>
internal sealed class RemoteHostMirror(string hostId)
{
    public const long PruneIntervalMs = 60_000;

    private readonly Dictionary<string, RemoteAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteResponse> _responses = new(StringComparer.Ordinal);

    /// <summary>Agents the current connection sent; at its <c>ready</c> every other agent of the host is removed.</summary>
    private readonly HashSet<string> _sentAgents = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _parents = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _sentParents;
    private long? _lastPruneMs;

    public string HostId { get; } = hostId;

    /// <summary>The connection the latest messages came from.</summary>
    public int Connection { get; private set; }

    public RemoteHostStatus? Status { get; private set; }

    /// <summary>The current connection sent <c>ready</c>.</summary>
    public bool Ready { get; private set; }

    public IReadOnlyDictionary<string, RemoteAgent> Agents => _agents;

    public IReadOnlyDictionary<string, string> Parents => _parents;

    public IReadOnlyDictionary<string, RemoteResponse> Responses => _responses;

    public RemoteSourceReport? Report { get; private set; }

    public int ResponseLimit { get; set; } = RemoteLimits.Responses;

    public List<RemoteFeedChange> Apply(RemoteHostEvent hostEvent, long nowMs, long todayStartMs)
    {
        if (hostEvent.Connection != 0 && hostEvent.Connection != Connection)
        {
            Connection = hostEvent.Connection;
            Ready = false;
            _sentAgents.Clear();
            _sentParents = false;
        }

        switch (hostEvent.Kind)
        {
            case RemoteHostEventKind.Status { Value: var value }:
                Status = value;
                var changes = new List<RemoteFeedChange> { new RemoteFeedChange.Status(HostId, value) };
                if (!value.Status.IsLive && IdleAgents())
                {
                    changes.Add(new RemoteFeedChange.Idle(HostId));
                }

                return changes;
            case RemoteHostEventKind.Message { Value: var message, Seeded: var seeded }:
                return Apply(message, seeded, nowMs, todayStartMs);
            default:
                return [];
        }
    }

    private List<RemoteFeedChange> Apply(RelayMessage message, bool seeded, long nowMs, long todayStartMs)
    {
        switch (message)
        {
            case RelayMessage.Agent { Value: var agent }:
                _sentAgents.Add(agent.Id);
                _agents[agent.Id] = agent;
                return [new RemoteFeedChange.Agent(agent, _parents.GetValueOrDefault(agent.Id))];
            case RelayMessage.AgentRemoved { Id: var id }:
                _sentAgents.Remove(id);
                return _agents.Remove(id) ? [new RemoteFeedChange.AgentRemoved(id)] : [];
            case RelayMessage.Parents { Links: var links }:
                _sentParents = true;
                _parents = links;
                return [new RemoteFeedChange.Parents(HostId, links)];
            case RelayMessage.Response { Value: var response }:
                Store(response, nowMs, todayStartMs);
                return [new RemoteFeedChange.Response(response, Live: !seeded)];
            case RelayMessage.Tool { Value: var tool }:
                return [new RemoteFeedChange.Tool(tool)];
            case RelayMessage.Status { Value: var report }:
                Report = report;
                return [new RemoteFeedChange.Report(HostId, report)];
            case RelayMessage.Ready:
                Ready = true;
                var changes = new List<RemoteFeedChange>();
                foreach (var id in _agents.Keys.Where(id => !_sentAgents.Contains(id)).Order(StringComparer.Ordinal).ToList())
                {
                    _agents.Remove(id);
                    changes.Add(new RemoteFeedChange.AgentRemoved(id));
                }

                if (!_sentParents && _parents.Count > 0)
                {
                    _parents = new Dictionary<string, string>(StringComparer.Ordinal);
                    changes.Add(new RemoteFeedChange.Parents(HostId, _parents));
                }

                return changes;
            default:
                return [];
        }
    }

    /// <summary>The host's state as seeded changes, for a new session.</summary>
    public List<RemoteFeedChange> Replay()
    {
        var changes = new List<RemoteFeedChange>();
        if (Status is { } status)
        {
            changes.Add(new RemoteFeedChange.Status(HostId, status));
        }

        if (Report is { } report)
        {
            changes.Add(new RemoteFeedChange.Report(HostId, report));
        }

        if (_parents.Count > 0)
        {
            changes.Add(new RemoteFeedChange.Parents(HostId, _parents));
        }

        foreach (var id in _agents.Keys.Order(StringComparer.Ordinal))
        {
            changes.Add(new RemoteFeedChange.Agent(_agents[id], _parents.GetValueOrDefault(id)));
        }

        foreach (var response in OlderFirst(_responses.Values))
        {
            changes.Add(new RemoteFeedChange.Response(response, Live: false));
        }

        return changes;
    }

    /// <summary>Sets every agent idle without an in-flight start; true when one changed.</summary>
    private bool IdleAgents()
    {
        var changed = false;
        foreach (var (id, agent) in _agents.ToList())
        {
            if (agent.Phase != AgentPhase.Idle || agent.InFlightStartMs is not null)
            {
                _agents[id] = agent with { Phase = AgentPhase.Idle, InFlightStartMs = null };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Keeps today's responses and the retention window, at most <see cref="ResponseLimit"/> (the oldest go first).</summary>
    private void Store(RemoteResponse response, long nowMs, long todayStartMs)
    {
        _responses[response.Record.Key] = response;
        if (_lastPruneMs is not { } last || nowMs - last >= PruneIntervalMs)
        {
            _lastPruneMs = nowMs;
            var cutoff = Math.Min(todayStartMs, nowMs - SpeedEngine.RetentionMs);
            foreach (var key in _responses.Where(r => r.Value.Record.EndMs < cutoff).Select(r => r.Key).ToList())
            {
                _responses.Remove(key);
            }
        }

        if (_responses.Count <= ResponseLimit)
        {
            return;
        }

        var excess = _responses.Count - ResponseLimit + Math.Max(1, ResponseLimit / 100);
        foreach (var old in OlderFirst(_responses.Values).Take(excess).ToList())
        {
            _responses.Remove(old.Record.Key);
        }
    }

    private static IEnumerable<RemoteResponse> OlderFirst(IEnumerable<RemoteResponse> responses) =>
        responses.OrderBy(r => r.Record.EndMs).ThenBy(r => r.Record.Key, StringComparer.Ordinal);
}
