using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.OpenCode;

/// <summary>
/// OpenCode's databases, one per root of its location list (METRICS 2.1, 4.5), read in list order. A session another database
/// already reads is skipped, so a copied database adds nothing. Not thread-safe: owned by the monitor thread.
/// </summary>
internal sealed class OpenCodeSourceSet : IDisposable
{
    private readonly SessionClaims _claims = new();

    public OpenCodeSourceSet(IReadOnlyList<string> roots, IDeduper deduper)
    {
        Sources = [.. roots.Select((root, index) => new OpenCodeSource(root, deduper, _claims, index))];
    }

    public IReadOnlyList<OpenCodeSource> Sources { get; }

    /// <summary>The first root whose database exists, else the first root (the source's path, METRICS 2.1).</summary>
    public string DisplayRoot => Sources.FirstOrDefault(s => s.DatabaseExists)?.RootPath ?? (Sources.Count > 0 ? Sources[0].RootPath : string.Empty);

    public bool Unsupported => Sources.Any(s => s.Status == OpenCodeStatus.Unsupported);

    public int UnreadableCount => Sources.Count(s => s.Status == OpenCodeStatus.Unreadable);

    public long MalformedRows => Sources.Sum(s => s.MalformedRows);

    public IEnumerable<OpenCodeAgentState> Agents => Sources.SelectMany(s => s.Agents);

    /// <summary>Every database's folder, each once: a watcher observes <c>opencode.db</c> and its <c>-wal</c> there.</summary>
    public IReadOnlyList<string> WatchDirectories() =>
        LocationPaths.Unique(Sources.Select(s => s.WatchDirectory).Where(d => d.Length > 0));

    /// <summary>Any database file exists (cheap; does not open them).</summary>
    public bool CheckExists()
    {
        var any = false;
        foreach (var source in Sources)
        {
            any |= source.CheckExists();
        }

        return any;
    }

    public bool IsWatchedPath(string path) => Sources.Any(s => s.IsWatchedPath(path));

    public int ActiveSessionCount(long todayStartMs) => Sources.Sum(s => s.ActiveSessionCount(todayStartMs));

    /// <summary>Startup read of every database that exists, in list order; the bytes and count of the databases read.</summary>
    public (List<OpenCodeUpdate> Updates, int DatabasesRead, long Bytes) Seed(long todayStartMs)
    {
        var updates = new List<OpenCodeUpdate>();
        var read = 0;
        var bytes = 0L;
        foreach (var source in Sources)
        {
            if (!source.CheckExists())
            {
                continue;
            }

            source.RefreshAvailability();
            updates.Add(source.Seed(todayStartMs));
            if (source.Status == OpenCodeStatus.Ready)
            {
                read++;
                bytes += source.DatabaseBytes;
            }
        }

        return (updates, read, bytes);
    }

    /// <summary>Reads the databases the watcher paths name.</summary>
    public List<OpenCodeUpdate> Refresh(IReadOnlyCollection<string> paths) =>
        [.. Sources.Where(s => paths.Any(s.IsWatchedPath)).Select(s => s.Refresh())];

    /// <summary>The 1-second fallback: the databases whose file or WAL changed.</summary>
    public List<OpenCodeUpdate> RefreshIfChanged() => [.. Sources.Select(s => s.RefreshIfChanged()).OfType<OpenCodeUpdate>()];

    /// <summary>Periodic check of every database in list order; an update with <see cref="OpenCodeUpdate.Reset"/> is a new startup read.</summary>
    public List<OpenCodeUpdate> Rediscover(long nowMs, long todayStartMs) => [.. Sources.Select(s => s.Rediscover(nowMs, todayStartMs))];

    public void Dispose()
    {
        foreach (var source in Sources)
        {
            source.Dispose();
        }
    }
}
