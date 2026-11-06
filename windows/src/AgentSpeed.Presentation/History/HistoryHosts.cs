using AgentSpeed.Core;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Presentation.History;

/// <summary>A host of the Usage History window's host picker (DESIGN 13): this computer or a remote host with stored history.</summary>
/// <param name="Id"><see cref="RemoteHost.LocalId"/> or a remote host's id.</param>
/// <param name="Name">The remote host's last name (its id when none was stored); null for this computer, which reads <c>remote.local</c>.</param>
public sealed record HistoryHost(string Id, string? Name)
{
    public bool IsLocal => Id == RemoteHost.LocalId;
}

/// <summary>What the window shows for a host filter (DESIGN 13).</summary>
/// <param name="Host">The filter in effect: every host when null, else <see cref="RemoteHost.LocalId"/> or a remote host's id.</param>
/// <param name="Hosts">The picker's hosts; empty while no remote host has history, so nothing about hosts shows.</param>
/// <param name="Usage">The records of <paramref name="Host"/>; every record for every host.</param>
public sealed record HistoryHostView(string? Host, IReadOnlyList<HistoryHost> Hosts, IReadOnlyList<DailyUsage> Usage)
{
    /// <summary>The CSV's trailing <c>host</c> value, only while host history exists.</summary>
    public string? CsvHost => Hosts.Count == 0 ? null : HistoryHosts.CsvValue(Host, Hosts);

    /// <summary>The peak (METRICS 8) is shown only for every host (and every source).</summary>
    public bool ShowsPeak => Host is null;

    /// <summary>Agent traffic is this computer's own (REMOTE 7): a remote host has none.</summary>
    public bool HasTraffic => Host is null or RemoteHost.LocalId;

    /// <summary>The selected host's name under the title (<paramref name="localName"/> for this computer); null for every host.</summary>
    public string? Name(string localName) => Host switch
    {
        null => null,
        RemoteHost.LocalId => localName,
        _ => Hosts.FirstOrDefault(h => h.Id == Host)?.Name ?? Host,
    };
}

/// <summary>The Usage History window's host picker and filter (DESIGN 13); with no host history, everything stays as it was.</summary>
public static class HistoryHosts
{
    /// <summary>The CSV <c>host</c> of this computer's rows.</summary>
    public const string LocalCsvValue = "local";

    /// <summary>The CSV <c>host</c> of rows that add up every host.</summary>
    public const string AllCsvValue = "all";

    /// <summary>
    /// This computer, then every remote host with usage by name (removed hosts included under their last name), plus the
    /// selected remote host; empty while no remote host has history and none is selected.
    /// </summary>
    /// <param name="names">Host id → display name (<see cref="Names"/>).</param>
    public static IReadOnlyList<HistoryHost> WithHistory(IEnumerable<DailyUsage> usage, IReadOnlyDictionary<string, string> names,
        string? selected = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(names);
        var remote = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in usage)
        {
            if (record is not null && !record.IsLocal && HistoryAggregator.HasUsage(record))
            {
                remote.Add(record.Host);
            }
        }

        if (selected is not null && selected != RemoteHost.LocalId)
        {
            remote.Add(selected);
        }

        if (remote.Count == 0)
        {
            return [];
        }

        return [new HistoryHost(RemoteHost.LocalId, null), .. remote
            .Select(id => new HistoryHost(id, names.TryGetValue(id, out var name) ? name : id))
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Id, StringComparer.Ordinal)];
    }

    /// <summary>The records of one host; every record when <paramref name="host"/> is null.</summary>
    public static IReadOnlyList<DailyUsage> Records(IReadOnlyList<DailyUsage> usage, string? host)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return host is null ? usage : [.. usage.Where(r => r is not null && r.Host == host)];
    }

    /// <summary>The picker, the filter in effect and its records: this computer alone is every host while no remote host has history.</summary>
    public static HistoryHostView Select(IReadOnlyList<DailyUsage> usage, IReadOnlyDictionary<string, string> names, string? selected)
    {
        var hosts = WithHistory(usage, names, selected);
        var host = hosts.Count == 0 ? null : selected;
        return new HistoryHostView(host, hosts, Records(usage, host));
    }

    /// <summary>The remote hosts' names: the settings' for configured hosts, the last stored one for removed hosts.</summary>
    public static IReadOnlyDictionary<string, string> Names(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string>? configured)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (configured is not { Count: > 0 })
        {
            return stored;
        }

        var names = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        foreach (var (id, name) in configured)
        {
            names[id] = name;
        }

        return names;
    }

    /// <summary>The CSV <c>host</c> value of a filter: the host's name, <c>local</c> for this computer, <c>all</c> for every host.</summary>
    public static string CsvValue(string? host, IReadOnlyList<HistoryHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return host switch
        {
            null => AllCsvValue,
            RemoteHost.LocalId => LocalCsvValue,
            _ => hosts.FirstOrDefault(h => h.Id == host)?.Name ?? host,
        };
    }
}
