using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core;

/// <summary>
/// Orbs and boards that track a set of hosts (DESIGN 13): METRICS 6.8 evaluated over the (host, source) cells whose host and
/// source are both selected. Without hosts (null, every host) every function is exactly its source-only form.
/// </summary>
public static partial class OrbSubset
{
    /// <summary>
    /// The reading of <paramref name="sources"/> (null = every source) on <paramref name="hosts"/>. A source is available when
    /// it is enabled and, on a selected host, detected or showing data. With every selected host remote and disconnected the
    /// state is <see cref="SubsetState.HostsOffline"/> (its values still count, like a disconnected host's responses).
    /// </summary>
    public static SubsetReading Reading(SpeedSnapshot snapshot, SourceSet? sources, IReadOnlySet<string>? hosts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (hosts is null)
        {
            return Reading(snapshot, sources);
        }

        var result = Reading(Restricted(snapshot, hosts), sources ?? SourceSet.Everything);
        if (sources is null)
        {
            result = result with { Unavailable = [], State = SubsetState.Available };
        }

        result = result with { MeasuredPeakOutput = snapshot.MeasuredPeak(new SubsetKey(sources ?? SourceSet.Everything, hosts)) };
        return IsOffline(snapshot, hosts)
            ? result with { State = SubsetState.HostsOffline, Unavailable = sources is { } set ? [.. set] : [] }
            : result;
    }

    /// <summary>The money-unit reading of <paramref name="sources"/> on <paramref name="hosts"/> (null = every host).</summary>
    public static CostReading Cost(SpeedSnapshot snapshot, SourceSet? sources, IReadOnlySet<string>? hosts, SourceSet billable)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return hosts is null ? Cost(snapshot, sources, billable) : Cost(Restricted(snapshot, hosts), sources, billable);
    }

    /// <summary>
    /// Whether an orb tracking <paramref name="sources"/> on <paramref name="hosts"/> receives <paramref name="speedEvent"/>:
    /// completions of its own sources read on its own hosts, and the all-sources new peak only for an orb of every source
    /// on every host.
    /// </summary>
    public static bool Accepts(SpeedEvent speedEvent, SourceSet? sources, IReadOnlySet<string>? hosts) => speedEvent switch
    {
        SpeedEvent.ResponseCompleted completed => (sources?.Contains(completed.Response.Source) ?? true)
            && (hosts?.Contains(completed.Response.Host) ?? true),
        SpeedEvent.NewPeak => sources is null && hosts is null,
        _ => false,
    };

    /// <summary>No selected host can be seen: this computer is not selected and no selected remote host is connected.</summary>
    public static bool IsOffline(SpeedSnapshot snapshot, IReadOnlySet<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(hosts);
        return !hosts.Contains(RemoteHost.LocalId) && !snapshot.Hosts.Any(h => hosts.Contains(h.Id) && h.IsConnected);
    }

    /// <summary>
    /// The snapshot as seen from <paramref name="hosts"/>: every source's values summed over the selected hosts' cells, and the
    /// agents of those hosts. Without remote hosts only this computer exists.
    /// </summary>
    public static SpeedSnapshot Restricted(SpeedSnapshot snapshot, IReadOnlySet<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(hosts);
        if (snapshot.Hosts.Count == 0 && hosts.Contains(RemoteHost.LocalId))
        {
            return snapshot;
        }

        var cells = new Dictionary<HostSource, HostSourceStatus>();
        foreach (var cell in snapshot.HostSources)
        {
            if (hosts.Contains(cell.Host))
            {
                cells[cell.Cell] = cell;
            }
        }

        var selected = snapshot.Hosts.Select(h => h.Id).Where(hosts.Contains).ToList();
        var agents = Filter(snapshot.Agents, SourceSet.Everything, hosts);
        Rate live = Rate.Zero;
        var today = TokenTotals.Zero;
        var liveCost = LiveCost.Zero;
        var todayCost = CostTally.Zero;
        var intelligence = Intelligence.IntelligenceSums.Zero;
        var estimated = 0.0;
        var sources = new List<SourceStatus>(snapshot.Sources.Count);
        foreach (var status in snapshot.Sources)
        {
            bool available = false;
            var sessions = 0;
            Rate sourceLive = Rate.Zero, sourceEstimated = Rate.Zero;
            var sourceCost = LiveCost.Zero;
            var sourceToday = TokenTotals.Zero;
            var sourceTodayCost = CostTally.Zero;
            var sourceIntelligence = Intelligence.IntelligenceSums.Zero;
            var unpriced = new List<string>();
            foreach (var host in selected)
            {
                if (!cells.TryGetValue(new HostSource(host, status.Kind), out var cell))
                {
                    continue;
                }

                available |= cell.Available;
                sessions += cell.SessionCount;
                sourceLive = new Rate(sourceLive.Output + cell.Live.Output, sourceLive.Total + cell.Live.Total);
                sourceEstimated = new Rate(sourceEstimated.Output + cell.LiveEstimated.Output, sourceEstimated.Total + cell.LiveEstimated.Total);
                sourceCost += cell.LiveCost;
                sourceToday += cell.Today;
                sourceTodayCost += cell.TodayCost;
                sourceIntelligence += cell.Intelligence;
                unpriced.AddRange(cell.UnpricedModels.Where(m => !unpriced.Contains(m)));
            }

            var restricted = status with
            {
                Available = available,
                SessionCount = sessions,
                Live = sourceLive,
                LiveEstimated = sourceEstimated,
                Today = sourceToday,
                ActiveAgents = agents.Count(a => a.Source == status.Kind && a.Phase == AgentPhase.Generating),
                LiveCost = sourceCost,
                TodayCost = sourceTodayCost,
                Intelligence = sourceIntelligence,
                UnpricedModels = unpriced,
                UnpricedFacts = [],
            };
            sources.Add(restricted);
            live = new Rate(live.Output + sourceLive.Output, live.Total + sourceLive.Total);
            estimated += sourceEstimated.Output;
            today += sourceToday;
            liveCost += sourceCost;
            todayCost += sourceTodayCost;
            intelligence += sourceIntelligence;
        }

        return snapshot with
        {
            Agents = agents,
            Live = live,
            Today = today,
            Sources = sources,
            LiveEstimatedShare = live.Output > 0 ? estimated / live.Output : 0,
            GeneratingCount = agents.Count(a => a.Phase == AgentPhase.Generating),
            ToolsCount = agents.Count(a => a.Phase == AgentPhase.Tools),
            LiveCost = liveCost,
            TodayCost = todayCost,
        };
    }
}
