using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Qoder;

/// <summary>
/// Follows Qoder IDE for the live monitor (METRICS 4.18): the databases of its location list, one reader each in list order,
/// every session read by one database only. Not thread-safe: owned by the monitor thread.
/// </summary>
internal sealed class QoderSource(IReadOnlyList<QoderRoot> roots, IDeduper deduper)
{
    private readonly SessionClaims _claims = new();
    private List<QoderReader> _readers = [];
    private long _todayStartMs;
    private long _removedMalformedRows;

    public IReadOnlyList<QoderRoot> Roots { get; } = roots;

    public IReadOnlyList<QoderReader> Readers => _readers;

    /// <summary>The first root that names an existing database (an automatic one shows its IDE data folder), else the first root (METRICS 2.1).</summary>
    public string DisplayRoot
    {
        get
        {
            foreach (var root in Roots)
            {
                if (root.DatabaseNow is not null)
                {
                    return root.DisplayFolder;
                }
            }

            return Roots.Count > 0 ? Roots[0].DisplayFolder : string.Empty;
        }
    }

    /// <summary>Detected: a database exists (METRICS 4.18).</summary>
    public bool Available => _readers.Any(r => r.Exists) || QoderPaths.Databases(Roots).Count > 0;

    public bool Unsupported => _readers.Any(r => r.Status == QoderStatus.Unsupported);

    public int UnreadableCount => _readers.Count(r => r.Status == QoderStatus.Unreadable);

    public long MalformedRows => _removedMalformedRows + _readers.Sum(r => r.MalformedRows);

    public IEnumerable<QoderAgent> Agents => _readers.SelectMany(r => r.Agents);

    /// <summary>Every database's folder, each once: a watcher observes <c>local.db</c> and its <c>-wal</c> there.</summary>
    public IReadOnlyList<string> WatchDirectories() =>
        LocationPaths.Unique(_readers.Select(r => r.WatchDirectory).Where(d => d.Length > 0));

    public bool IsWatchedPath(string path) => _readers.Any(r => r.IsWatchedPath(path));

    public int ActiveSessionCount(long todayStartMs) => _readers.Sum(r => r.ActiveSessionCount(todayStartMs));

    /// <summary>Startup read (METRICS 4.18, 5): every database in list order, the rows of the startup window.</summary>
    public (List<QoderUpdate> Updates, int Discovered, int Read, long Bytes) Seed(long todayStartMs)
    {
        _todayStartMs = todayStartMs;
        var updates = new List<QoderUpdate>();
        if (Sync(QoderPaths.Databases(Roots), force: true) is { } removed)
        {
            updates.Add(removed);
        }

        var (read, bytes) = (0, 0L);
        foreach (var reader in _readers)
        {
            if (reader.Refresh(todayStartMs, deduper) is { } update)
            {
                updates.Add(update);
            }

            read += reader.Status == QoderStatus.Ready ? 1 : 0;
            bytes += reader.Status == QoderStatus.Ready ? reader.DatabaseBytes : 0;
        }

        return (updates, _readers.Count(r => r.Exists), read, bytes);
    }

    /// <summary>The watcher's changed paths and the 1-second fallback: every database whose file or <c>-wal</c> changed.</summary>
    public List<QoderUpdate> Refresh() => [.. _readers.Select(r => r.Refresh(_todayStartMs, deduper)).OfType<QoderUpdate>()];

    /// <summary>
    /// Every 10 seconds: the roots resolved again. A list of databases that changed (one appeared, vanished or moved) starts every
    /// reader over like at startup, so the earliest database keeps its sessions; each database keeps owning its keys.
    /// </summary>
    public List<QoderUpdate> Rediscover(long todayStartMs)
    {
        _todayStartMs = todayStartMs;
        var updates = new List<QoderUpdate>();
        if (Sync(QoderPaths.Databases(Roots), force: false) is { } removed)
        {
            updates.Add(removed);
        }

        updates.AddRange(Refresh());
        return updates;
    }

    /// <summary>One reader per database in list order; the agents of the readers it replaced leave.</summary>
    private QoderUpdate? Sync(List<string> databases, bool force)
    {
        if (!force && databases.SequenceEqual(_readers.Select(r => r.Path), StringComparer.Ordinal))
        {
            return null;
        }

        var removed = new QoderUpdate();
        foreach (var reader in _readers)
        {
            removed.Removed.AddRange(reader.AgentIds);
            _removedMalformedRows += reader.MalformedRows;
            reader.Release();
        }

        _readers = [.. databases.Select((path, position) => new QoderReader(path, _claims, position))];
        return removed.Changed ? removed : null;
    }
}
