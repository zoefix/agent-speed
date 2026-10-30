using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Hermes;

namespace AgentSpeed.Core.Monitoring;

internal sealed partial class MonitorCore
{
    private readonly Dictionary<string, int> _hermesPushed = new(StringComparer.Ordinal);
    private readonly List<SourceRoot> _hermesWatchRoots = [];
    private HermesSource? _hermes;
    private string _hermesRootPath = string.Empty;
    private bool _hermesSeeded;

    internal HermesSource? Hermes => _hermes;

    /// <summary>This computer's time-zone rules, which convert Hermes's wall-clock times from now on (METRICS 4.15 Time).</summary>
    internal TimeZoneInfo HermesZone
    {
        set
        {
            if (_hermes is not null)
            {
                _hermes.Zone = value;
            }
        }
    }

    private void InitializeHermes(SourceLocations locations, TimeZoneInfo? zone)
    {
        _hermesRootPath = locations.HermesRoot;
        if (_enabled.Contains(SourceKind.Hermes) && locations.HermesRoot.Length > 0)
        {
            _hermes = new HermesSource(locations.HermesRoot, locations.HermesChosen, _deduper, zone ?? TimeZoneInfo.Local);
            RefreshHermesWatchRoots();
        }
    }

    /// <summary>One watched folder per log folder (files directly inside; the filter takes <c>agent.log*</c>).</summary>
    private void RefreshHermesWatchRoots()
    {
        if (_hermes is null)
        {
            return;
        }

        var wanted = _hermes.WatchDirectories();
        _hermesWatchRoots.RemoveAll(root => !wanted.Contains(root.Path, PathComparer));
        foreach (var path in wanted)
        {
            if (!_hermesWatchRoots.Exists(root => PathComparer.Equals(root.Path, path)))
            {
                _hermesWatchRoots.Add(new SourceRoot(RootFamily.Hermes, path, enabled: true));
            }
        }

        foreach (var root in _hermesWatchRoots)
        {
            root.Available = SafeDirectoryExists(root.Path);
        }
    }

    private void SeedHermes(long todayStartMs, CancellationToken cancellationToken, ref int discovered, ref int filesRead, ref long bytesRead)
    {
        if (_hermes is not { } hermes || _hermesSeeded)
        {
            return;
        }

        var update = hermes.Seed(todayStartMs, cancellationToken);
        discovered += hermes.Sets.Count;
        filesRead += update.FilesRead;
        bytesRead += update.BytesRead;
        _partialHistory |= hermes.PartialHistory;
        ApplyHermes(update, seeded: true);
        _hermesSeeded = true;
        RefreshHermesWatchRoots();
    }

    private bool RefreshHermes(bool discover)
    {
        if (_hermes is not { } hermes || !_hermesSeeded)
        {
            return false;
        }

        var changed = ApplyHermes(hermes.Refresh(discover), seeded: false);
        if (discover)
        {
            RefreshHermesWatchRoots();
        }

        return changed;
    }

    /// <summary>Applies one read of the log sets; true when anything changed.</summary>
    private bool ApplyHermes(HermesUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _hermesPushed.Remove(id);
            _engine.RemoveAgent(id);
        }

        var pushed = false;
        foreach (var agent in _hermes?.Agents ?? [])
        {
            pushed |= PushHermesAgent(agent);
        }

        long? liveNowMs = seeded ? null : Clock();
        foreach (var (response, isCompletion) in update.Responses)
        {
            _responsesSeen++;
            _engine.Upsert(response, isCompletion, seeded, liveNowMs);
            _observer?.OnResponse(response, isCompletion, seeded);
        }

        return pushed || update.Changed || update.Responses.Count > 0 || update.Removed.Count > 0;
    }

    private bool PushHermesAgent(HermesAgent agent)
    {
        if (agent.LastLineMs is null
            || (_hermesPushed.TryGetValue(agent.AgentId, out var version) && version == agent.Version))
        {
            return false;
        }

        _hermesPushed[agent.AgentId] = agent.Version;
        _engine.UpdateAgent(new AgentInfo(
            agent.AgentId, SourceKind.Hermes, null, agent.Model, agent.IsSubagent, agent.Phase, agent.InFlightStartMs, agent.LastLineMs)
        { SessionModes = agent.SessionModes, ConfiguredModes = agent.ConfiguredModes });
        return true;
    }

    /// <summary>The Hermes home (or the chosen folder) and whether it exists (METRICS 2, detection; DESIGN 5).</summary>
    private (string Path, bool Available) HermesStatus() => (_hermesRootPath, _hermes?.Available ?? HermesPaths.Exists(_hermesRootPath));
}
