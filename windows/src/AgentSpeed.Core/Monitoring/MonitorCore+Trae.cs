using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Monitoring;

internal sealed partial class MonitorCore
{
    private readonly List<TraeSource> _trae = [];
    private readonly Dictionary<TraeSource, TraeRead> _traeReads = [];
    private readonly List<SourceRoot> _traeRoots = [];
    private IReadOnlyList<string> _traeStores = [];
    private bool _traeSeeded;

    private void InitializeTrae(SourceLocations locations)
    {
        _traeStores = locations.TraeStores ?? [];
        if (!_enabled.Contains(SourceKind.Trae)) return;
        foreach (var store in _traeStores.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            _trae.Add(new TraeSource(store, _deduper) { WallClockMs = () => Clock() });
        RefreshTraeAvailability();
    }

    private void RefreshTraeAvailability()
    {
        for (var index = 0; index < _trae.Count; index++)
        {
            var source = _trae[index];
            if (index >= _traeRoots.Count)
                _traeRoots.Add(new SourceRoot(RootFamily.Trae, source.WatchDirectory, enabled: true));
            else if (_traeRoots[index].Path != source.WatchDirectory)
                _traeRoots[index] = new SourceRoot(RootFamily.Trae, source.WatchDirectory, enabled: true);
            _traeRoots[index].Available = source.Available;
        }
    }

    private void SeedTrae(long todayStartMs)
    {
        if (_traeSeeded) return;
        foreach (var source in _trae) ApplyTrae(source.Seed(todayStartMs), true);
        _traeSeeded = true;
    }

    private bool RefreshTrae()
    {
        RefreshTraeAvailability();
        var changed = false;
        foreach (var source in _trae)
            changed |= ReadWholeFilesInBackground ? StartTraeRead(source) : ApplyTrae(source.Refresh(), false);
        return changed;
    }

    private bool StartTraeRead(TraeSource source)
    {
        if (_traeReads.TryGetValue(source, out var running))
        {
            running.Again = true;
            return false;
        }

        var changed = ApplyTrae(source.BeginRefresh(false, out var request), false);
        if (request is null) return changed;
        var token = _backgroundStop.Token;
        var before = BeforeBackgroundRead;
        var clock = MonotonicClock;
        var task = Task.Run(() =>
        {
            before?.Invoke(request.Path);
            var began = clock();
            return (source.Read(request, token), began);
        }, token);
        _traeReads[source] = new TraeRead(task);
        task.ContinueWith(static (_, state) => ((Action?)state)?.Invoke(), BackgroundReadCompleted,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return changed;
    }

    private bool ApplyCompletedTraeReads()
    {
        var changed = false;
        foreach (var (source, read) in _traeReads.Where(item => item.Value.Task.IsCompleted).ToArray())
        {
            _traeReads.Remove(source);
            try
            {
                var (prepared, began) = read.Task.GetAwaiter().GetResult();
                if (ApplyTrae(source.Commit(prepared), false))
                {
                    Observe(began);
                    changed = true;
                }
            }
            catch (OperationCanceledException) { continue; }
            if (read.Again) changed |= StartTraeRead(source);
        }
        return changed;
    }

    private sealed class TraeRead(Task<(TraeSource.Prepared Prepared, long Began)> task)
    {
        public Task<(TraeSource.Prepared Prepared, long Began)> Task { get; } = task;
        public bool Again { get; set; }
    }

    private bool ApplyTrae(TraeUpdate update, bool seeded)
    {
        foreach (var id in update.Removed) _engine.RemoveAgent(id);
        foreach (var agent in update.Agents)
            _engine.UpdateAgent(new AgentInfo(agent.Id, SourceKind.Trae, agent.Project, agent.Model,
                false, agent.Phase, agent.InFlightStartMs, agent.LastMs, null) { ReferenceResponses = agent.ReferenceResponses });
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
