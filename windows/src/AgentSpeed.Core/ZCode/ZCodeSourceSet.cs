using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.ZCode;

/// <summary>
/// ZCode's databases, one per root of its location list (METRICS 2.1, 4.10), read in list order. A session another database
/// already reads is skipped, so a copied database adds nothing. Not thread-safe: owned by the monitor thread.
/// </summary>
internal sealed class ZCodeSourceSet
{
    private readonly SessionClaims _claims = new();

    public ZCodeSourceSet(IReadOnlyList<string> roots, IDeduper deduper)
    {
        Sources = [.. roots.Select((root, index) => new ZCodeSource(root, deduper, _claims, index))];
    }

    public IReadOnlyList<ZCodeSource> Sources { get; }

    /// <summary>The first root whose database exists, else the first root (the source's path, METRICS 2.1).</summary>
    public string DisplayRoot => Sources.FirstOrDefault(s => s.Available)?.RootPath ?? (Sources.Count > 0 ? Sources[0].RootPath : string.Empty);

    public bool Available => Sources.Any(s => s.Available);

    public bool Unsupported => Sources.Any(s => s.Unsupported);

    public int UnreadableCount => Sources.Count(s => s.Unreadable);

    /// <summary>Every database's folder, each once.</summary>
    public IReadOnlyList<string> WatchDirectories() =>
        LocationPaths.Unique(Sources.Select(s => s.WatchDirectory).Where(d => d.Length > 0));

    public bool IsWatchedPath(string path) => Sources.Any(s => s.IsWatchedPath(path));

    public int ActiveSessionCount(long todayStartMs) => Sources.Sum(s => s.ActiveSessionCount(todayStartMs));

    /// <summary>Startup read of every database, in list order.</summary>
    public List<ZCodeUpdate> Seed(long todayStartMs) => [.. Sources.Select(s => s.Seed(todayStartMs))];

    /// <summary>Reads every database that changed, in list order.</summary>
    public List<ZCodeUpdate> Refresh() => [.. Sources.Select(s => s.Refresh())];
}
