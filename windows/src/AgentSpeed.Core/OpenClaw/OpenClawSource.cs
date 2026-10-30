using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pi;

namespace AgentSpeed.Core.OpenClaw;

/// <summary>What one read of the OpenClaw databases changed.</summary>
internal sealed class OpenClawUpdate : IResponseSink, ICallSink
{
    public List<(ParsedResponse Response, bool IsCompletion)> Responses { get; } = [];

    /// <summary>Responses and live tool results in the order they were read (METRICS 11.10 counts them in this order).</summary>
    public List<(ParsedResponse? Response, bool IsCompletion, string? AgentId, Work.WorkAction Call)> Completions { get; } = [];

    /// <summary>Agent ids of windows that are gone (pruned, or their database vanished or was replaced).</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Something was read or removed, or a database's status changed.</summary>
    public bool Changed { get; set; }

    public long BytesRead { get; set; }

    public int DatabasesRead { get; set; }

    public void OnResponse(ParsedResponse response, bool isCompletion)
    {
        Responses.Add((response, isCompletion));
        Completions.Add((response, isCompletion, null, default));
    }

    public void OnCallResult(string agentId, SourceKind source, Work.WorkAction action) => Completions.Add((null, false, agentId, action));
}

/// <summary>
/// Follows the OpenClaw agent databases for the live monitor (METRICS 4.14, 5): one agent per transcript window. At the
/// first read of a database, windows updated within <c>todayStartMs − 15 min</c> or later are read completely and older
/// ones start after their last row (their header is read when they grow); later reads take the rows above each window's
/// last <c>seq</c>, all rows of new windows, and read rewritten windows again. Each read opens the database read-only,
/// runs one short transaction and closes it, so OpenClaw can always replace or delete its files. Owned by the monitor thread.
/// </summary>
internal sealed class OpenClawSource
{
    private const long NoTraceSeq = -1;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IDeduper _deduper;
    private readonly Func<IReadOnlyList<string>>? _rediscover;
    private readonly Dictionary<string, TrackedDatabase> _databases = new(PathComparer);
    private long _removedMalformedLines;
    private long _thresholdMs;

    /// <param name="roots">The state folders (or the folder or database of the setting) to search.</param>
    /// <param name="rediscover">Resolves the roots again on every rediscovery (automatic profiles); null keeps them.</param>
    public OpenClawSource(IReadOnlyList<string> roots, Func<IReadOnlyList<string>>? rediscover, IDeduper deduper)
    {
        Roots = roots;
        _rediscover = rediscover;
        _deduper = deduper;
    }

    /// <summary>The roots as last resolved.</summary>
    public IReadOnlyList<string> Roots { get; private set; }

    /// <summary>A state folder (or the folder or file of the setting) exists (METRICS 2).</summary>
    public bool Available => Roots.Any(OpenClawPaths.Exists);

    /// <summary>A seeding read is in progress: actions read now are seeded (METRICS 6.10).</summary>
    private bool _seeding;

    public IEnumerable<OpenClawParser> Parsers => _databases.Values.SelectMany(d => d.Windows.Values).Select(w => w.Parser);

    public IReadOnlyCollection<string> DatabasePaths => _databases.Keys;

    public bool Unsupported => _databases.Values.Any(d => d.Unsupported);

    /// <summary>The database is tracked (a watcher path of an unknown database asks for discovery).</summary>
    public bool IsTracked(string path) => _databases.ContainsKey(DatabaseOf(path));

    public int UnreadableCount => _databases.Values.Count(d => d.Unreadable);

    public long MalformedLines => _removedMalformedLines + Parsers.Sum(p => p.MalformedLines);

    /// <summary>The folders a watcher observes: each state folder's <c>agents</c> folder, an agent folder, or a database's folder.</summary>
    public IReadOnlyList<string> WatchDirectories()
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var root in Roots)
        {
            string folder;
            try
            {
                folder = File.Exists(root) ? Path.GetDirectoryName(root) ?? string.Empty
                    : File.Exists(Path.Combine(root, OpenClawPaths.DatabaseName)) ? root
                    : Path.Combine(root, "agents");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (folder.Length > 0 && seen.Add(folder))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>A database (or its <c>-wal</c>) of this source, tracked or below a root; any other path is not.</summary>
    public bool IsWatchedPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name != OpenClawPaths.DatabaseName && name != OpenClawPaths.DatabaseName + SqliteReadOnly.WalSuffix)
        {
            return false;
        }

        var database = DatabaseOf(path);
        return _databases.ContainsKey(database) || Roots.Any(root => IsSameOrInside(database, root));
    }

    /// <summary>Main windows (not sub-agents) with an entry today (METRICS 2, DESIGN 5).</summary>
    public int ActiveSessionCount(long todayStartMs) =>
        Parsers.Count(p => !p.IsSubagent && p.LastLineMs >= todayStartMs);

    /// <summary>
    /// Startup (METRICS 4.14, 5): databases in the startup order (the newer of the database and its <c>-wal</c>, then the
    /// path). Windows updated within <c>todayStartMs − 15 min</c> or later are read completely; older ones start after
    /// their last row, so an old database costs one look at its windows.
    /// </summary>
    public OpenClawUpdate Seed(long todayStartMs, CancellationToken cancellationToken)
    {
        _thresholdMs = todayStartMs - Monitoring.MonitorCore.BacklogWindowMs;
        Roots = _rediscover?.Invoke() ?? Roots;
        var update = new OpenClawUpdate();
        _seeding = true;
        try
        {
            foreach (var (path, _) in Discover().OrderBy(d => d.Stamp.LastWriteUtc).ThenBy(d => d.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tracked = new TrackedDatabase(path);
                _databases[path] = tracked;
                Read(tracked, update);
            }
        }
        finally
        {
            _seeding = false;
        }

        return update;
    }

    /// <summary>Reads every tracked database whose file or <c>-wal</c> changed; with <paramref name="discover"/> also new and vanished ones.</summary>
    public OpenClawUpdate Refresh(bool discover = false)
    {
        var update = new OpenClawUpdate();
        if (discover)
        {
            Roots = _rediscover?.Invoke() ?? Roots;
            var found = Discover();
            var present = new HashSet<string>(found.Select(f => f.Path), PathComparer);
            foreach (var gone in _databases.Keys.Where(path => !present.Contains(path)).ToList())
            {
                Drop(_databases[gone], update);
                _databases.Remove(gone);
                update.Changed = true;
            }

            foreach (var (path, _) in found.OrderBy(d => d.Stamp.LastWriteUtc).ThenBy(d => d.Path, StringComparer.Ordinal))
            {
                if (!_databases.ContainsKey(path))
                {
                    var tracked = new TrackedDatabase(path);
                    _databases[path] = tracked;
                    Read(tracked, update);
                }
            }
        }

        foreach (var tracked in _databases.Values.ToList())
        {
            var stamp = AntigravityStamp.Of(tracked.Path);
            if (stamp is null)
            {
                if (discover)
                {
                    continue;
                }

                Drop(tracked, update);
                _databases.Remove(tracked.Path);
                update.Changed = true;
            }
            else if (stamp != tracked.Stamp || (discover && tracked.Unreadable))
            {
                Read(tracked, update);
            }
        }

        return update;
    }

    /// <summary>
    /// Reads one database completely (METRICS 4.14; daily history and fixtures): every window in reading order, each
    /// fed its rows in <c>seq</c> order. Null when the database has no session tables. <paramref name="sinkFor"/> gives
    /// each window's sink; <paramref name="windowDone"/> runs after each window; <paramref name="stopAfter"/> stops after
    /// that row of that window.
    /// </summary>
    /// <exception cref="SqliteException">The database cannot be opened or read.</exception>
    public static List<OpenClawParser>? ReadAll(
        string path, IDeduper deduper, Func<OpenClawParser, IResponseSink> sinkFor, Action<OpenClawParser>? windowDone = null,
        (string Session, long Seq)? stopAfter = null, CancellationToken cancellationToken = default)
    {
        using var database = OpenClawDatabase.Open(path);
        if (!database.Supported)
        {
            return null;
        }

        return database.Read(d =>
        {
            var parsers = new List<OpenClawParser>();
            foreach (var window in d.Windows())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parsers.Exists(p => p.SessionId == window.SessionId))
                {
                    continue;
                }

                var parser = new OpenClawParser(path, window, deduper);
                parser.ReadWholeFile();
                parsers.Add(parser);
                var sink = sinkFor(parser);
                var stop = stopAfter is { } at && at.Session == window.SessionId;
                parser.ApplyTraces(TracesAfter(d, window.SessionId, NoTraceSeq, out _), sink);
                d.Rows(window.SessionId, long.MinValue, (seq, line) =>
                {
                    parser.Apply(in line, sink);
                    return !(stop && seq == stopAfter!.Value.Seq);
                });
                windowDone?.Invoke(parser);
                if (stop)
                {
                    break;
                }
            }

            LinkParents(path, parsers, d.CurrentSessions());
            return parsers;
        });
    }

    /// <summary>
    /// A sub-agent's parent: the current window of the session key it was spawned by, in the same database and tracked
    /// (METRICS 4.14); a parent in another agent's database is not linked.
    /// </summary>
    private static void LinkParents(string path, IReadOnlyCollection<OpenClawParser> parsers, Dictionary<string, string> current)
    {
        var tracked = parsers.Select(p => p.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var parser in parsers)
        {
            var parent = parser.Window.SpawnedBy is { Length: > 0 } key && current.TryGetValue(key, out var session)
                && session != parser.SessionId && tracked.Contains(session)
                ? OpenClawParser.AgentIdOf(path, session)
                : null;
            parser.SetParent(parent);
        }
    }

    private List<(string Path, AntigravityStamp Stamp)> Discover()
    {
        var found = new List<(string, AntigravityStamp)>();
        foreach (var path in OpenClawPaths.Databases(Roots))
        {
            if (AntigravityStamp.Of(path) is { } stamp)
            {
                found.Add((path, stamp));
            }
        }

        return found;
    }

    private void Read(TrackedDatabase tracked, OpenClawUpdate update)
    {
        var stamp = AntigravityStamp.Of(tracked.Path);
        if (stamp is null)
        {
            return;
        }

        var identity = ReadIdentity(tracked.Path);
        if (tracked.Identity is { } known && identity is { } current && known != current)
        {
            // Replaced: every window is read again from its first row (parser state reset, never the dedupe set).
            Drop(tracked, update);
            tracked.Opened = false;
        }

        tracked.Identity = identity ?? tracked.Identity;
        var wasUnsupported = tracked.Unsupported;
        var wasUnreadable = tracked.Unreadable;
        try
        {
            using var database = OpenClawDatabase.Open(tracked.Path);
            tracked.Unreadable = false;
            tracked.Unsupported = !database.Supported;
            if (database.Supported)
            {
                database.Read(d =>
                {
                    ReadWindows(tracked, d, update);
                    return true;
                });
            }
            else
            {
                Drop(tracked, update);
            }

            tracked.Opened = true;
            tracked.Stamp = stamp;
            update.DatabasesRead++;
            update.BytesRead += stamp.Value.Length;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Busy, locked or unreadable: read again on the next change or rediscovery.
            tracked.Unreadable = true;
            tracked.Stamp = stamp;
        }

        update.Changed |= wasUnsupported != tracked.Unsupported || wasUnreadable != tracked.Unreadable;
    }

    private void ReadWindows(TrackedDatabase tracked, OpenClawDatabase database, OpenClawUpdate update)
    {
        var windows = database.Windows();
        var generations = database.Watermarks();
        var first = !tracked.Opened;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            if (!present.Add(window.SessionId))
            {
                continue;
            }

            var generation = generations.GetValueOrDefault(window.SessionId);
            if (!tracked.Windows.TryGetValue(window.SessionId, out var state))
            {
                var parser = new OpenClawParser(tracked.Path, window, _deduper);
                state = new WindowState(parser) { Generation = generation, UpdatedMs = window.UpdatedMs };
                tracked.Windows[window.SessionId] = state;
                if (first && (window.UpdatedMs ?? long.MinValue) < _thresholdMs)
                {
                    state.LastSeq = database.MaxSeq(window.SessionId) ?? long.MinValue;
                    state.NeedsHeader = true;
                    continue;
                }

                ReadRows(state, database, update);
                continue;
            }

            var rewritten = !string.Equals(generation, state.Generation, StringComparison.Ordinal);
            var runtime = state.Parser.RuntimeChanged(window);
            state.Parser.UpdateWindow(window);
            if (!rewritten && !runtime && window.UpdatedMs == state.UpdatedMs)
            {
                // A recorder's rows are buffered and often reach the database after the attempt's transcript rows.
                if (!state.NeedsHeader)
                {
                    ReadTraces(state, database, update);
                }

                continue;
            }

            if (!rewritten && !runtime && (database.MaxSeq(window.SessionId) ?? long.MinValue) < state.LastSeq)
            {
                rewritten = true;
            }

            if (rewritten || runtime)
            {
                state.Parser.Reset();
                state.LastSeq = long.MinValue;
                state.LastTraceSeq = NoTraceSeq;
                state.NeedsHeader = false;
                update.Changed = true;
            }

            state.Generation = generation;
            state.UpdatedMs = window.UpdatedMs;
            ReadRows(state, database, update);
        }

        foreach (var gone in tracked.Windows.Keys.Where(id => !present.Contains(id)).ToList())
        {
            RemoveWindow(tracked, gone, update);
        }

        LinkParents(tracked.Path, [.. tracked.Windows.Values.Select(w => w.Parser)], database.CurrentSessions());
    }

    private static List<OpenClawTrace> TracesAfter(OpenClawDatabase database, string sessionId, long afterSeq, out long lastSeq)
    {
        var traces = new List<OpenClawTrace>();
        lastSeq = database.Traces(sessionId, afterSeq, (_, trace) =>
        {
            if (trace is { } kept)
            {
                traces.Add(kept);
            }
        });
        return traces;
    }

    private static void ReadTraces(WindowState state, OpenClawDatabase database, OpenClawUpdate update)
    {
        var before = state.Parser.Version;
        var traces = TracesAfter(database, state.Parser.SessionId, state.LastTraceSeq, out var lastSeq);
        state.LastTraceSeq = lastSeq;
        state.Parser.ApplyTraces(traces, update);
        update.Changed |= state.Parser.Version != before;
    }

    private void ReadRows(WindowState state, OpenClawDatabase database, OpenClawUpdate update)
    {
        state.Parser.Seeding = _seeding;
        state.Parser.CallSink = update;
        try
        {
            ReadTraces(state, database, update);
            if (state.NeedsHeader)
            {
                // An older window read from its end: its header first, as omp does for a file read from an offset (4.7).
                state.NeedsHeader = false;
                var header = database.Header(state.Parser.SessionId);
                state.Parser.Apply(in header, update);
            }

            var before = state.Parser.Version;
            state.LastSeq = database.Rows(state.Parser.SessionId, state.LastSeq, (_, line) =>
            {
                state.Parser.Apply(in line, update);
                return true;
            });
            update.Changed |= state.Parser.Version != before;
        }
        finally
        {
            state.Parser.Seeding = false;
            state.Parser.CallSink = null;
        }
    }

    private void RemoveWindow(TrackedDatabase tracked, string sessionId, OpenClawUpdate update)
    {
        var parser = tracked.Windows[sessionId].Parser;
        tracked.Windows.Remove(sessionId);
        _removedMalformedLines += parser.MalformedLines;
        update.Removed.Add(parser.AgentId);
        update.Changed = true;
    }

    private void Drop(TrackedDatabase tracked, OpenClawUpdate update)
    {
        foreach (var session in tracked.Windows.Keys.ToList())
        {
            RemoveWindow(tracked, session, update);
        }
    }

    private static string DatabaseOf(string path) =>
        path.EndsWith(SqliteReadOnly.WalSuffix, StringComparison.Ordinal) ? path[..^SqliteReadOnly.WalSuffix.Length] : path;

    private static bool IsSameOrInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(trimmed, comparison)
            || (path.Length > trimmed.Length && path.StartsWith(trimmed, comparison)
                && (path[trimmed.Length] == Path.DirectorySeparatorChar || path[trimmed.Length] == Path.AltDirectorySeparatorChar));
    }

    private static FileIdentity? ReadIdentity(string path)
    {
        try
        {
            using var handle = TranscriptTailer.Open(path);
            return FileIdentity.From(handle);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class TrackedDatabase(string path)
    {
        public string Path { get; } = path;

        public Dictionary<string, WindowState> Windows { get; } = new(StringComparer.Ordinal);

        /// <summary>The database and its <c>-wal</c> as last read (or observed at startup without reading).</summary>
        public AntigravityStamp? Stamp { get; set; }

        public FileIdentity? Identity { get; set; }

        /// <summary>A read finished; the next one takes new rows only.</summary>
        public bool Opened { get; set; }

        public bool Unsupported { get; set; }

        public bool Unreadable { get; set; }
    }

    private sealed class WindowState(OpenClawParser parser)
    {
        public OpenClawParser Parser { get; } = parser;

        /// <summary>The highest <c>seq</c> read so far; <see cref="long.MinValue"/> before the first row.</summary>
        public long LastSeq { get; set; } = long.MinValue;

        /// <summary>The highest trajectory <c>seq</c> read so far.</summary>
        public long LastTraceSeq { get; set; } = NoTraceSeq;

        public long? UpdatedMs { get; set; }

        public string? Generation { get; set; }

        /// <summary>Started after its last row: the header is read before the next rows.</summary>
        public bool NeedsHeader { get; set; }
    }
}
