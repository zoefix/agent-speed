using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Monitoring;

/// <summary>The remote hosts of a monitor session (REMOTE 7, DESIGN 13).</summary>
internal sealed partial class MonitorCore
{
    private readonly RemoteIngest _remote = new();

    /// <summary>Some remote host is configured: the monitor runs even without a local source (REMOTE 7).</summary>
    public bool HasRemoteHosts => _remote.HasHosts;

    internal RemoteIngest Remote => _remote;

    /// <summary>The engine's agents (tests and diagnostics).</summary>
    internal IReadOnlyDictionary<string, AgentInfo> EngineAgents => _engine.Agents;

    /// <summary>Applies the remote feed's changes between reads of local files; true when the engine changed.</summary>
    public bool ApplyRemote(IReadOnlyList<RemoteFeedChange> changes)
    {
        if (changes.Count == 0)
        {
            return false;
        }

        var began = MonotonicClock();
        return Observed(began, _remote.Apply(changes, _engine, _enabled, Clock));
    }

    /// <summary>A disabled source contributes nothing to a subset (METRICS 6.8); subsets with hosts are summed over their cells.</summary>
    private (IReadOnlyDictionary<SourceSet, double> All, IReadOnlyDictionary<SubsetKey, double> Hosts) SubsetPeaks(IReadOnlyCollection<SubsetKey> subsets)
    {
        var all = new Dictionary<SourceSet, double>();
        var hosts = new Dictionary<SubsetKey, double>();
        foreach (var key in subsets)
        {
            if (key.HasHosts)
            {
                hosts[key] = _engine.MeasuredPeakOutput(new SubsetKey(key.Sources.Intersect(_enabledSet), key.Hosts));
            }
            else
            {
                all[key.Sources] = _engine.MeasuredPeakOutput(key.Sources.Intersect(_enabledSet));
            }
        }

        return (all.AsReadOnly(), hosts.AsReadOnly());
    }

    /// <summary>The sources with every connected host's availability and today's sessions added (DESIGN 13).</summary>
    private List<SourceStatus> WithRemoteSources(List<SourceStatus> local)
    {
        if (!_remote.HasHosts)
        {
            return local;
        }

        var merged = new List<SourceStatus>(local.Count);
        foreach (var status in local)
        {
            if (!status.Enabled)
            {
                merged.Add(status);
                continue;
            }

            var available = status.Available;
            var sessions = status.SessionCount;
            foreach (var host in _remote.Hosts)
            {
                if (!_remote.IsConnected(host.Id) || !_remote.Reports.TryGetValue(host.Id, out var report))
                {
                    continue;
                }

                foreach (var state in report.Sources)
                {
                    if (state.Source == status.Kind)
                    {
                        available |= state.Available;
                        sessions += state.Sessions;
                    }
                }
            }

            merged.Add(status with { Available = available, SessionCount = sessions });
        }

        return merged;
    }

    /// <summary><see cref="SpeedSnapshot.Hosts"/> and <see cref="SpeedSnapshot.HostSources"/>: this computer first, then every configured host.</summary>
    private (List<HostStatus> Hosts, List<HostSourceStatus> Cells) HostStatuses(EngineFrame frame, List<SourceStatus> local, IReadOnlyList<MonitorWarning> warnings)
    {
        var unsupported = warnings.OfType<MonitorWarning.UnsupportedFormat>().Select(w => w.Source).ToHashSet();
        var hosts = new List<HostStatus>();
        var cells = new List<HostSourceStatus>();
        foreach (var info in _remote.Hosts.Prepend(new RemoteHostInfo(RemoteHost.LocalId, RemoteHost.LocalId)))
        {
            var isLocal = info.Id == RemoteHost.LocalId;
            var connected = isLocal || _remote.IsConnected(info.Id);
            var states = new Dictionary<SourceKind, RemoteSourceState>();
            if (!isLocal && _remote.Reports.TryGetValue(info.Id, out var report))
            {
                foreach (var state in report.Sources)
                {
                    states.TryAdd(state.Source, state);
                }
            }

            Rate live = Rate.Zero, estimated = Rate.Zero;
            var liveCost = LiveCost.Zero;
            var today = TokenTotals.Zero;
            var todayCost = CostTally.Zero;
            var sessions = 0;
            foreach (var status in local)
            {
                var kind = status.Kind;
                var cell = new HostSource(info.Id, kind);
                var values = frame.Cells.GetValueOrDefault(cell) ?? EngineCell.Empty;
                bool available = false, isUnsupported = false;
                var sessionCount = 0;
                var configured = ExecutionModes.Empty;
                if (isLocal)
                {
                    available = status.Available;
                    sessionCount = status.SessionCount;
                    configured = status.ConfiguredModes;
                    isUnsupported = unsupported.Contains(kind);
                }
                else if (states.TryGetValue(kind, out var state))
                {
                    available = connected && state.Available;
                    sessionCount = connected ? state.Sessions : 0;
                    configured = state.ConfiguredModes;
                    isUnsupported = state.Unsupported;
                }

                if (!status.Enabled)
                {
                    available = false;
                    sessionCount = 0;
                }

                var cellStatus = new HostSourceStatus(cell, available, isUnsupported, sessionCount, values.Live, values.LiveEstimated, values.LiveCost,
                    values.Intelligence, values.Today, values.TodayCost, UnpricedModels(values.Unpriced), configured);
                live = new Rate(live.Output + values.Live.Output, live.Total + values.Live.Total);
                estimated = new Rate(estimated.Output + values.LiveEstimated.Output, estimated.Total + values.LiveEstimated.Total);
                liveCost += values.LiveCost;
                today += values.Today;
                todayCost += values.TodayCost;
                sessions += sessionCount;
                if (available || sessionCount > 0 || values.Today.Requests > 0 || values.Live != Rate.Zero)
                {
                    cells.Add(cellStatus);
                }
            }

            hosts.Add(new HostStatus(info.Id, info.Name, isLocal ? null : _remote.Statuses.GetValueOrDefault(info.Id), live, estimated, liveCost, today,
                todayCost, sessions, connected));
        }

        return (hosts, cells);
    }

    /// <summary>Models with unpriced requests, most first, then by name; the unknown model is left out.</summary>
    private static List<string> UnpricedModels(IReadOnlyDictionary<string, long> counts) =>
        [.. counts.Where(c => c.Value > 0 && c.Key.Length > 0)
            .OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key)];
}
