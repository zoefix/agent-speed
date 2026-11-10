using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Qoder;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// Qoder IDE (METRICS 4.18): one SQLite database per root of the list, watched in its folder; rediscovery looks the user
/// locations' databases up again, and a list of databases that changed starts every reader over. Sub-agent sessions link to
/// their parent session of the same database (6.7).
/// </summary>
internal sealed partial class MonitorCore
{
    private readonly List<SourceRoot> _qoderWatchRoots = [];
    private readonly Dictionary<string, QoderAgent> _qoderPushed = new(StringComparer.Ordinal);
    private QoderSource? _qoder;
    private IReadOnlyList<QoderRoot> _qoderRoots = [];
    private bool _qoderSeeded;

    internal QoderSource? Qoder => _qoder;

    private void InitializeQoder(SourceLocations locations)
    {
        _qoderRoots = locations.QoderRoots;
        if (_enabled.Contains(SourceKind.Qoder) && _qoderRoots.Count > 0)
        {
            _qoder = new QoderSource(_qoderRoots, _deduper);
        }
    }

    /// <summary>One watched folder per database folder; the filter takes <c>local.db</c> and its <c>-wal</c>.</summary>
    private void RefreshQoderWatchRoots()
    {
        if (_qoder is null)
        {
            return;
        }

        var wanted = _qoder.WatchDirectories();
        _qoderWatchRoots.RemoveAll(root => !wanted.Contains(root.Path, PathComparer));
        foreach (var path in wanted)
        {
            if (!_qoderWatchRoots.Exists(root => PathComparer.Equals(root.Path, path)))
            {
                _qoderWatchRoots.Add(new SourceRoot(RootFamily.Qoder, path, enabled: true));
            }
        }

        foreach (var root in _qoderWatchRoots)
        {
            root.Available = SafeDirectoryExists(root.Path);
        }
    }

    private void SeedQoder(long todayStartMs, ref int discovered, ref int filesRead, ref long bytesRead)
    {
        if (_qoder is not { } qoder || _qoderSeeded)
        {
            return;
        }

        var (updates, found, read, bytes) = qoder.Seed(todayStartMs);
        foreach (var update in updates)
        {
            ApplyQoder(update, seeded: true);
        }

        discovered += found;
        filesRead += read;
        bytesRead += bytes;
        _qoderSeeded = true;
        RefreshQoderWatchRoots();
    }

    /// <summary>A watched path, the 1-second fallback: every database whose file or <c>-wal</c> changed.</summary>
    private bool RefreshQoder()
    {
        if (_qoder is not { } qoder || !_qoderSeeded)
        {
            return false;
        }

        var changed = false;
        foreach (var update in qoder.Refresh())
        {
            changed |= ApplyQoder(update, update.Seeded);
        }

        return changed;
    }

    private bool RediscoverQoder(long todayStartMs)
    {
        if (_qoder is not { } qoder || !_qoderSeeded)
        {
            return false;
        }

        var changed = false;
        foreach (var update in qoder.Rediscover(todayStartMs))
        {
            changed |= ApplyQoder(update, update.Seeded);
        }

        RefreshQoderWatchRoots();
        return changed;
    }

    private bool ApplyQoder(QoderUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _qoderPushed.Remove(id);
            _engine.RemoveAgent(id);
            _linksChanged |= _links.Remove(id);
        }

        var pushed = false;
        foreach (var agent in update.Agents.Values)
        {
            if (agent.LastLineMs is null || (_qoderPushed.TryGetValue(agent.Id, out var known) && known == agent))
            {
                continue;
            }

            var link = new AgentLink(agent.Id, LinkKind.QoderSession, agent.Id, agent.ParentId, null);
            if (!_links.TryGetValue(agent.Id, out var previous) || previous != link)
            {
                _links[agent.Id] = link;
                _linksChanged = true;
            }

            _qoderPushed[agent.Id] = agent;
            _engine.UpdateAgent(new AgentInfo(agent.Id, SourceKind.Qoder, agent.Project, agent.Model, agent.IsSubagent, agent.Phase,
                agent.InFlightStartMs, agent.LastLineMs, _parents.GetValueOrDefault(agent.Id)));
            pushed = true;
        }

        long? liveNow = seeded ? null : Clock();
        foreach (var (response, completion) in update.Responses)
        {
            _responsesSeen++;
            _engine.Upsert(response, completion, seeded, liveNow);
            _observer?.OnResponse(response, completion, seeded);
        }

        return pushed || update.Responses.Count > 0 || update.Removed.Count > 0;
    }

    /// <summary>The first root that names an existing database, else the first root, and whether any database exists (METRICS 2.1).</summary>
    private (string Path, bool Available) QoderLocation()
    {
        if (_qoder is { } qoder)
        {
            return (qoder.DisplayRoot, qoder.Available);
        }

        var source = new QoderSource(_qoderRoots, _deduper);
        return (source.DisplayRoot, source.Available);
    }
}
