using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

/// <summary>
/// The stored days of the remote hosts (REMOTE 8.3) in the form the history publishes: each day as a <see cref="DailyUsage"/>
/// (without its peak), its digest, its performance records, and the hosts' names. A row whose text no longer decodes is left
/// out with its digest, so the next sync sends it again.
/// </summary>
internal sealed class RemoteHistoryRows
{
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Host, DayKey Key), (DailyUsage Usage, string Digest)> _days = [];
    private readonly Dictionary<(string Host, DayKey Key), Dictionary<string, PerfSums>> _perf = [];

    /// <summary>Host id → its last name, for every host with a stored name.</summary>
    public IReadOnlyDictionary<string, string> Names => _names;

    /// <summary>Host id → the zone its rows were computed in (REMOTE 8.3 <i>Time zone</i>); absent for rows stored before zones.</summary>
    public IReadOnlyDictionary<string, string> Zones => _zones;

    public bool IsEmpty => _days.Count == 0 && _perf.Count == 0;

    /// <summary>The hosts with stored rows.</summary>
    public IReadOnlySet<string> Hosts => _days.Keys.Select(k => k.Host).Concat(_perf.Keys.Select(k => k.Host)).ToHashSet(StringComparer.Ordinal);

    /// <exception cref="IO.Sqlite.SqliteException">The side tables cannot be read.</exception>
    public static RemoteHistoryRows Load(HistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var rows = new RemoteHistoryRows();
        foreach (var (id, name) in store.RemoteHostNames())
        {
            if (name is not null)
            {
                rows._names[id] = name;
            }
        }

        foreach (var (id, zone) in store.RemoteHostZones())
        {
            rows._zones[id] = zone;
        }

        foreach (var row in store.RemoteDayRows())
        {
            if (SourceNames.Parse(row.Source) is { } source && RelayLineDecoder.StoredAggregate(row.Data) is { } aggregate)
            {
                rows._days[(row.Host, new DayKey(row.Day, source))] =
                    (RemoteHistoryMerge.Usage(aggregate, row.Day, source, row.Host), row.Digest);
            }
        }

        foreach (var row in store.RemotePerfRows())
        {
            if (SourceNames.Parse(row.Source) is { } source && RelayLineDecoder.StoredRecord(row.Data) is { } record)
            {
                var key = (row.Host, new DayKey(row.Day, source));
                if (!rows._perf.TryGetValue(key, out var models))
                {
                    rows._perf[key] = models = new Dictionary<string, PerfSums>(StringComparer.Ordinal);
                }

                models[row.Model] = record;
            }
        }

        return rows;
    }

    /// <summary><c>"&lt;day&gt;/&lt;source&gt;"</c> → digest of every stored day of <paramref name="host"/> (REMOTE 5.3 <c>history</c>).</summary>
    public Dictionary<string, string> Digests(string host)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ((owner, key), day) in _days)
        {
            if (owner == host)
            {
                digests[RemoteHistoryDigest.Key(key.Day, key.Source)] = day.Digest;
            }
        }

        return digests;
    }

    /// <summary>The days of <paramref name="sources"/>, each with the day's live peak (all hosts and sources, METRICS 8).</summary>
    public List<DailyUsage> Usage(IReadOnlySet<SourceKind> sources, Func<string, double?> peak)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(peak);
        var list = new List<DailyUsage>();
        foreach (var ((_, key), day) in _days)
        {
            if (sources.Contains(key.Source))
            {
                list.Add(day.Usage with { Peak = peak(key.Day) });
            }
        }

        return list;
    }

    /// <summary>The records of <paramref name="sources"/>, by host, then day, source and model.</summary>
    public List<PerfRecord> PerfRecords(IReadOnlySet<SourceKind> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var records = new List<(string Host, PerfRecord Record)>();
        foreach (var ((host, key), models) in _perf)
        {
            if (!sources.Contains(key.Source))
            {
                continue;
            }

            foreach (var (model, sums) in models)
            {
                records.Add((host, new PerfRecord(key.Day, key.Source, model, sums)));
            }
        }

        records.Sort(static (a, b) => a.Host != b.Host ? string.CompareOrdinal(a.Host, b.Host) : PerfRecord.Compare(a.Record, b.Record));
        return [.. records.Select(r => r.Record)];
    }

    /// <summary>The committed days of a batch: each replaces its row and its records; with <paramref name="replace"/>, the host's other rows go.</summary>
    public void Store(IReadOnlyList<RemoteBatchDay> batch, string host, string? name, string zone, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (replace)
        {
            RemoveRows(host);
        }

        if (batch.Count == 0 && !Hosts.Contains(host))
        {
            _names.Remove(host);
            _zones.Remove(host);
            return;
        }

        if (name is not null)
        {
            _names[host] = name;
        }

        _zones[host] = zone;
        foreach (var day in batch)
        {
            var key = (host, day.Key);
            _days[key] = (RemoteHistoryMerge.Usage(day.Aggregate, day.Day, day.Source, host), day.Digest);
            if (day.Perf.Count == 0)
            {
                _perf.Remove(key);
                continue;
            }

            var records = new Dictionary<string, PerfSums>(StringComparer.Ordinal);
            foreach (var item in day.Perf)
            {
                records[item.Model] = item.Record;
            }

            _perf[key] = records;
        }
    }

    public void Rename(string host, string name) => _names[host] = name;

    public void Remove(string host)
    {
        _names.Remove(host);
        _zones.Remove(host);
        RemoveRows(host);
    }

    private void RemoveRows(string host)
    {
        foreach (var key in _days.Keys.Where(k => k.Host == host).ToArray())
        {
            _days.Remove(key);
        }

        foreach (var key in _perf.Keys.Where(k => k.Host == host).ToArray())
        {
            _perf.Remove(key);
        }
    }
}
