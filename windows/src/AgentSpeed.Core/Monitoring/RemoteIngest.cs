using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// The remote hosts inside one monitor session (REMOTE 7): applies the feed's changes to the engine and keeps what the
/// snapshot shows about each host. Owned by the monitor thread.
/// </summary>
internal sealed class RemoteIngest
{
    private readonly Dictionary<string, RemoteHostStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteSourceReport> _reports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _parentsByHost = new(StringComparer.Ordinal);

    /// <summary>The configured hosts in settings order.</summary>
    public IReadOnlyList<RemoteHostInfo> Hosts { get; private set; } = [];

    public IReadOnlyDictionary<string, RemoteHostStatus> Statuses => _statuses;

    public IReadOnlyDictionary<string, RemoteSourceReport> Reports => _reports;

    public bool HasHosts => Hosts.Count > 0;

    /// <summary>The host's relay is connected (after <c>ready</c>).</summary>
    public bool IsConnected(string host) => _statuses.TryGetValue(host, out var status) && status.Status.Kind == RemoteStatusKind.Connected;

    /// <summary>Applies <paramref name="changes"/>; true when the engine or a host's state changed.</summary>
    /// <param name="liveNowMs">The wall clock for the age rule of live completions (METRICS 6.4).</param>
    public bool Apply(IReadOnlyList<RemoteFeedChange> changes, SpeedEngine engine, IReadOnlySet<SourceKind> enabledSources, Func<long> liveNowMs)
    {
        var changed = false;
        foreach (var change in changes)
        {
            switch (change)
            {
                case RemoteFeedChange.Hosts { Value: var hosts }:
                    Hosts = hosts;
                    engine.TracksCells = hosts.Count > 0;
                    break;
                case RemoteFeedChange.RemoveHost { Id: var id }:
                    engine.RemoveHost(id);
                    _statuses.Remove(id);
                    _reports.Remove(id);
                    _parentsByHost.Remove(id);
                    break;
                case RemoteFeedChange.Status { Host: var host, Value: var status }:
                    _statuses[host] = status;
                    break;
                case RemoteFeedChange.Report { Host: var host, Value: var report }:
                    _reports[host] = report;
                    break;
                case RemoteFeedChange.Agent { Value: var agent, ParentId: var parent }:
                    if (enabledSources.Contains(agent.Source))
                    {
                        engine.UpdateAgent(EngineAgent(agent, parent));
                    }
                    else
                    {
                        engine.RemoveAgent(agent.Id);
                    }

                    break;
                case RemoteFeedChange.AgentRemoved { Id: var id }:
                    engine.RemoveAgent(id);
                    break;
                case RemoteFeedChange.Parents { Host: var host, Links: var links }:
                    ApplyParents(engine, host, links);
                    break;
                case RemoteFeedChange.Response { Value: var response, Live: var live }:
                    if (!enabledSources.Contains(response.Record.Source))
                    {
                        continue;
                    }

                    engine.Upsert(response.Record, response.IsCompletion, seeded: !live, live ? liveNowMs() : null);
                    break;
                case RemoteFeedChange.Tool { Value: var tool }:
                    if (!enabledSources.Contains(tool.Source))
                    {
                        continue;
                    }

                    engine.CountCall(tool.AgentId, tool.Source, tool.Call, liveNowMs());
                    break;
                case RemoteFeedChange.Idle { Host: var idleHost }:
                    engine.SetIdle(idleHost);
                    break;
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>The engine's agent for a host's state; the project falls back to the relay id's folder name (display only).</summary>
    public static AgentInfo EngineAgent(RemoteAgent agent, string? parentId) => new(
        agent.Id,
        agent.Source,
        agent.Project ?? FallbackProject(agent.RelayId),
        agent.Model,
        agent.IsSubagent,
        agent.Phase,
        agent.InFlightStartMs,
        agent.LastLineMs,
        parentId)
    {
        Modes = agent.Modes,
        SessionModes = agent.SessionModes,
        ConfiguredModes = agent.ConfiguredModes,
        Work = agent.Actions,
        Host = agent.HostId,
    };

    /// <summary>The name of the folder that holds the transcript on the host (POSIX path), else its last component.</summary>
    internal static string FallbackProject(string relayId)
    {
        var parts = relayId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => relayId,
            1 => parts[0],
            _ => parts[^2],
        };
    }

    /// <summary>A host's complete parent map replaces its previous one; the host's agents take their new parents.</summary>
    private void ApplyParents(SpeedEngine engine, string host, IReadOnlyDictionary<string, string> links)
    {
        if (links.Count == 0)
        {
            _parentsByHost.Remove(host);
        }
        else
        {
            _parentsByHost[host] = links;
        }

        foreach (var agent in engine.Agents.Values.Where(a => a.Host == host).ToList())
        {
            engine.SetParent(agent.Id, links.GetValueOrDefault(agent.Id));
        }
    }
}
