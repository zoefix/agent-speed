using AgentSpeed.Core.Devin;
using AgentSpeed.Core.Engine;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// Devin CLI (METRICS 4.17): one SQLite database per root of the list, watched in its folder; rediscovery looks the user
/// locations' databases up again, and a list of databases that changed starts every reader over.
/// </summary>
internal sealed partial class MonitorCore
{
    private readonly List<SourceRoot> _devinWatchRoots = [];
    private readonly Dictionary<string, DevinAgent> _devinPushed = new(StringComparer.Ordinal);
    private DevinSource? _devin;
    private IReadOnlyList<DevinRoot> _devinRoots = [];
    private bool _devinSeeded;

    internal DevinSource? Devin => _devin;

    private void InitializeDevin(SourceLocations locations)
    {
        _devinRoots = locations.DevinRoots;
        if (_enabled.Contains(SourceKind.Devin) && _devinRoots.Count > 0)
        {
            _devin = new DevinSource(_devinRoots, _deduper);
        }
    }

    /// <summary>One watched folder per database folder; the filter takes <c>sessions.db</c> and its <c>-wal</c>.</summary>
    private void RefreshDevinWatchRoots()
    {
        if (_devin is null)
        {
            return;
        }

        var wanted = _devin.WatchDirectories();
        _devinWatchRoots.RemoveAll(root => !wanted.Contains(root.Path, PathComparer));
        foreach (var path in wanted)
        {
            if (!_devinWatchRoots.Exists(root => PathComparer.Equals(root.Path, path)))
            {
                _devinWatchRoots.Add(new SourceRoot(RootFamily.Devin, path, enabled: true));
            }
        }

        foreach (var root in _devinWatchRoots)
        {
            root.Available = SafeDirectoryExists(root.Path);
        }
    }

    private void SeedDevin(long todayStartMs, ref int discovered, ref int filesRead, ref long bytesRead)
    {
        if (_devin is not { } devin || _devinSeeded)
        {
            return;
        }

        var (updates, found, read, bytes) = devin.Seed(todayStartMs);
        foreach (var update in updates)
        {
            ApplyDevin(update, seeded: true);
        }

        discovered += found;
        filesRead += read;
        bytesRead += bytes;
        _devinSeeded = true;
        RefreshDevinWatchRoots();
    }

    /// <summary>A watched path, the 1-second fallback: every database whose file or <c>-wal</c> changed.</summary>
    private bool RefreshDevin()
    {
        if (_devin is not { } devin || !_devinSeeded)
        {
            return false;
        }

        var changed = false;
        foreach (var update in devin.Refresh())
        {
            changed |= ApplyDevin(update, update.Seeded);
        }

        return changed;
    }

    private bool RediscoverDevin(long todayStartMs)
    {
        if (_devin is not { } devin || !_devinSeeded)
        {
            return false;
        }

        var changed = false;
        foreach (var update in devin.Rediscover(todayStartMs))
        {
            changed |= ApplyDevin(update, update.Seeded);
        }

        RefreshDevinWatchRoots();
        return changed;
    }

    private bool ApplyDevin(DevinUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _devinPushed.Remove(id);
            _engine.RemoveAgent(id);
        }

        var pushed = false;
        foreach (var agent in update.Agents.Values)
        {
            if (agent.LastLineMs is null || (_devinPushed.TryGetValue(agent.Id, out var known) && known == agent))
            {
                continue;
            }

            _devinPushed[agent.Id] = agent;
            _engine.UpdateAgent(new AgentInfo(agent.Id, SourceKind.Devin, agent.Project, agent.Model, false, agent.Phase,
                agent.InFlightStartMs, agent.LastLineMs));
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
    private (string Path, bool Available) DevinLocation()
    {
        if (_devin is { } devin)
        {
            return (devin.DisplayRoot, devin.Available);
        }

        var source = new DevinSource(_devinRoots, _deduper);
        return (source.DisplayRoot, source.Available);
    }
}
