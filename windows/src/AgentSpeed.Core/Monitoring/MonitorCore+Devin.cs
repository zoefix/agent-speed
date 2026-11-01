using AgentSpeed.Core.Devin;

namespace AgentSpeed.Core.Monitoring;

internal sealed partial class MonitorCore
{
    private DevinSource? _devin;
    private SourceRoot? _devinRoot;
    private List<SourceRoot> _devinWatchRoots = [];
    private string _devinRootPath = string.Empty;
    private bool _devinSeeded;

    private void InitializeDevin(SourceLocations locations)
    {
        _devinRootPath = locations.DevinRoot;
        if (_enabled.Contains(SourceKind.Devin) && locations.DevinRoot.Length > 0)
        {
            _devin = new DevinSource(DevinPaths.Resolve(locations.DevinRoot), _deduper);
            _devinRoot = new SourceRoot(RootFamily.Devin, _devinRootPath, enabled: true);
            _devinWatchRoots = _devin.WatchDirectories.Select(path => new SourceRoot(RootFamily.Devin, path, enabled: true)).ToList();
        }
    }

    private void RefreshDevinAvailability()
    {
        if (_devin is null) return;
        _devinRoot!.Available = _devin.Available;
        _devinWatchRoots = _devin.WatchDirectories.Select(path => new SourceRoot(RootFamily.Devin, path, enabled: true)).ToList();
    }

    private void SeedDevin(long todayStartMs)
    {
        if (_devin is not null && !_devinSeeded)
        {
            ApplyDevin(_devin.Seed(todayStartMs), seeded: true);
            _devinSeeded = true;
        }
    }

    private bool RefreshDevin()
    {
        RefreshDevinAvailability();
        return _devin is not null && ApplyDevin(_devin.Refresh(), seeded: false);
    }

    private bool ApplyDevin(DevinUpdate update, bool seeded)
    {
        foreach (var key in update.RemovedResponses) _engine.RemoveResponse(key);
        foreach (var id in update.RemovedAgents) _engine.RemoveAgent(id);
        foreach (var state in update.Agents)
        {
            _engine.UpdateAgent(state.Info);
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
}
