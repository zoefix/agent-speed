using AgentSpeed.Core.Engine;
using AgentSpeed.Core.ZCode;

namespace AgentSpeed.Core.Monitoring;

internal sealed partial class MonitorCore
{
    private readonly List<SourceRoot> _zcodeWatchRoots = [];
    private ZCodeSourceSet? _zcode;
    private IReadOnlyList<string> _zcodeRootPaths = [];
    private bool _zcodeSeeded;

    internal ZCodeSourceSet? ZCode => _zcode;

    private void InitializeZCode(SourceLocations locations)
    {
        _zcodeRootPaths = locations.ZCodeRoots;
        if (_enabled.Contains(SourceKind.ZCode) && _zcodeRootPaths.Count > 0)
        {
            _zcode = new ZCodeSourceSet(_zcodeRootPaths, _deduper);
        }
    }

    /// <summary>One watched folder per database folder; a database folder can change while its data folder fills in.</summary>
    private void RefreshZCodeAvailability()
    {
        if (_zcode is null)
        {
            return;
        }

        var wanted = _zcode.WatchDirectories();
        _zcodeWatchRoots.RemoveAll(root => !wanted.Contains(root.Path, PathComparer));
        foreach (var path in wanted)
        {
            if (!_zcodeWatchRoots.Exists(root => PathComparer.Equals(root.Path, path)))
            {
                _zcodeWatchRoots.Add(new SourceRoot(RootFamily.ZCode, path, enabled: true));
            }
        }

        foreach (var root in _zcodeWatchRoots)
        {
            root.Available = _zcode.Sources.Any(source => PathComparer.Equals(source.WatchDirectory, root.Path) && source.Available);
        }
    }

    private void SeedZCode(long todayStartMs)
    {
        if (_zcode is not null && !_zcodeSeeded)
        {
            foreach (var update in _zcode.Seed(todayStartMs))
            {
                ApplyZCode(update, seeded: true);
            }

            _zcodeSeeded = true;
        }
    }

    private bool RefreshZCode()
    {
        RefreshZCodeAvailability();
        var changed = false;
        foreach (var update in _zcode?.Refresh() ?? [])
        {
            changed |= ApplyZCode(update, seeded: false);
        }

        return changed;
    }

    private bool ApplyZCode(ZCodeUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _engine.RemoveAgent(id);
            _linksChanged |= _links.Remove(id);
        }

        foreach (var state in update.Agents)
        {
            var link = new AgentLink(state.Id, LinkKind.ZCodeSession, state.SessionId, state.ParentSessionId, state.CreatedMs);
            if (!_links.TryGetValue(state.Id, out var previous) || previous != link)
            {
                _links[state.Id] = link;
                _linksChanged = true;
            }

            _engine.UpdateAgent(new AgentInfo(state.Id, SourceKind.ZCode, state.Project, state.Model,
                state.ParentSessionId is not null, state.Phase, state.StartMs, state.LastMs, _parents.GetValueOrDefault(state.Id)) { Modes = state.Modes });
        }

        long? liveNow = seeded ? null : Clock();
        foreach (var (response, completion) in update.Responses)
        {
            _responsesSeen++;
            _engine.Upsert(response, completion, seeded, liveNow);
            _observer?.OnResponse(response, completion, seeded);
        }

        return update.Changed;
    }

    /// <summary>The first root whose database exists, else the first root, and whether any database exists (METRICS 2.1).</summary>
    private (string Path, bool Available) ZCodeStatus()
    {
        if (_zcode is { } zcode)
        {
            return (zcode.DisplayRoot, zcode.Available);
        }

        var existing = _zcodeRootPaths.FirstOrDefault(root => SafeFileExists(ZCodeDatabase.DatabasePath(root)));
        return (existing ?? (_zcodeRootPaths.Count > 0 ? _zcodeRootPaths[0] : string.Empty), existing is not null);
    }
}
