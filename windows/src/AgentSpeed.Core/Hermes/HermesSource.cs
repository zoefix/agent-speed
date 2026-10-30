using System.Buffers;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.Core.Hermes;

/// <summary>What one read of the Hermes log sets changed.</summary>
internal sealed class HermesUpdate : IResponseSink
{
    public List<(ParsedResponse Response, bool IsCompletion)> Responses { get; } = [];

    /// <summary>Agent ids of log folders that are gone.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Lines were read, a switch changed or a folder was added or removed.</summary>
    public bool Changed { get; set; }

    public long BytesRead { get; set; }

    public int FilesRead { get; set; }

    public void OnResponse(ParsedResponse response, bool isCompletion) => Responses.Add((response, isCompletion));
}

/// <summary>
/// Follows Hermes Agent's logs for the live monitor (METRICS 4.15 Reading and rotation, 5): one <see cref="HermesLogSet"/>
/// per log folder, its files followed by identity across renames (Windows: volume serial number and file index), so a
/// rotated file continues from its offset and the new <c>agent.log</c> starts at 0. Each read opens a file with read,
/// write and delete sharing and closes it, so Hermes can rename and delete its logs. Owned by the monitor thread.
/// </summary>
internal sealed class HermesSource
{
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>An identity missing from this many rediscoveries in a row is forgotten (a rename may race one listing).</summary>
    private const int MissingLimit = 2;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IDeduper _deduper;
    private readonly bool _chosen;
    private readonly List<TrackedSet> _sets = [];
    private readonly HashSet<string> _unreadable = new(PathComparer);
    private List<string> _folders;
    private TimeZoneInfo _zone;
    private long _removedMalformedLines;

    /// <param name="root">The automatic Hermes home, or the chosen home or log folder.</param>
    /// <param name="chosen">The root is the user setting (a home or one log folder).</param>
    /// <param name="zone">The zone whose rules convert the logs' wall times.</param>
    public HermesSource(string root, bool chosen, IDeduper deduper, TimeZoneInfo zone)
    {
        Root = root;
        _chosen = chosen;
        _deduper = deduper;
        _zone = zone;
        _folders = Folders();
    }

    public string Root { get; }

    /// <summary>The Hermes home (or the chosen folder) exists: installed, maybe never run (METRICS 2, DESIGN 5).</summary>
    public bool Available => HermesPaths.Exists(Root);

    /// <summary>This computer's zone rules at the time of reading; later lines use a changed zone.</summary>
    public TimeZoneInfo Zone
    {
        get => _zone;
        set
        {
            _zone = value;
            foreach (var tracked in _sets)
            {
                tracked.Set.Zone = value;
            }
        }
    }

    public IReadOnlyList<HermesLogSet> Sets => [.. _sets.Select(s => s.Set)];

    public IEnumerable<HermesAgent> Agents => _sets.SelectMany(s => s.Set.Agents);

    public long MalformedLines => _removedMalformedLines + _sets.Sum(s => s.Set.MalformedLines);

    public long OversizedLines => _sets.Sum(s => s.Logs.Values.Sum(l => l.Lines.OversizedLines));

    public int UnreadableCount => _unreadable.Count;

    /// <summary>A file over 256 MiB was started at its last 64 MiB (METRICS 5).</summary>
    public bool PartialHistory { get; private set; }

    /// <summary>The log folders as last resolved.</summary>
    public IReadOnlyList<string> WatchDirectories() => _folders;

    /// <summary>A log file name directly in a tracked log folder (also the old name of a renamed file).</summary>
    public bool IsWatchedPath(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is not null && HermesPaths.IsLogName(Path.GetFileName(path.AsSpan()))
            && _sets.Exists(s => PathComparer.Equals(Path.TrimEndingDirectorySeparator(s.Set.Folder), folder));
    }

    /// <summary>Today's main sessions: distinct session ids with a turn-start line today whose platform is not <c>subagent</c>.</summary>
    public int ActiveSessionCount(long todayStartMs) => Agents.Count(a => a.LastMainTurnMs >= todayStartMs);

    /// <summary>
    /// Startup (METRICS 4.15, 5): every log set in folder order checks its <c>config.yaml</c>, then reads its files in the
    /// startup order (modification time, then path) under the startup rule.
    /// </summary>
    public HermesUpdate Seed(long todayStartMs, CancellationToken cancellationToken)
    {
        var update = new HermesUpdate();
        _folders = Folders();
        foreach (var folder in _folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tracked = Add(folder);
            tracked.Set.CheckConfig();
            ReadSet(tracked, update, todayStartMs - Monitoring.MonitorCore.BacklogWindowMs, forget: false, cancellationToken);
            tracked.Set.RefreshSessions();
        }

        return update;
    }

    /// <summary>
    /// Reads what changed. With <paramref name="discover"/> (the 10-second rediscovery) also resolves the log folders again
    /// (new profiles join, vanished folders leave with their agents) and checks every set's <c>config.yaml</c> before its lines.
    /// </summary>
    public HermesUpdate Refresh(bool discover, CancellationToken cancellationToken = default)
    {
        var update = new HermesUpdate();
        if (discover)
        {
            var folders = _folders = Folders();
            foreach (var gone in _sets.Where(s => !folders.Contains(s.Set.Folder, PathComparer)).ToList())
            {
                _sets.Remove(gone);
                _removedMalformedLines += gone.Set.MalformedLines;
                update.Removed.AddRange(gone.Set.Agents.Select(a => a.AgentId));
                update.Changed = true;
            }

            foreach (var folder in folders)
            {
                if (!_sets.Exists(s => PathComparer.Equals(s.Set.Folder, folder)))
                {
                    Add(folder);
                    update.Changed = true;
                }
            }

            _sets.Sort((a, b) => folders.FindIndex(f => PathComparer.Equals(f, a.Set.Folder))
                .CompareTo(folders.FindIndex(f => PathComparer.Equals(f, b.Set.Folder))));
        }

        foreach (var tracked in _sets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discover)
            {
                update.Changed |= tracked.Set.CheckConfig();
            }

            ReadSet(tracked, update, thresholdMs: null, forget: discover, cancellationToken);
            if (discover)
            {
                update.Changed |= tracked.Set.RefreshSessions();
            }
        }

        return update;
    }

    private List<string> Folders() =>
        [.. HermesPaths.FoldersOf(Root, _chosen).Select(f => Path.TrimEndingDirectorySeparator(f))];

    private TrackedSet Add(string folder)
    {
        var tracked = new TrackedSet(new HermesLogSet(folder, _deduper, _zone));
        _sets.Add(tracked);
        return tracked;
    }

    /// <param name="thresholdMs">Startup: files modified before it start at their end; null while running (new files start at 0).</param>
    /// <param name="forget">Rediscovery: identities missing from the listing are forgotten after <see cref="MissingLimit"/> listings.</param>
    private void ReadSet(TrackedSet tracked, HermesUpdate update, long? thresholdMs, bool forget, CancellationToken cancellationToken)
    {
        var listed = new List<(string Path, DateTime WriteUtc, long Length)>();
        foreach (var path in HermesPaths.LogFiles(tracked.Set.Folder))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    listed.Add((path, info.LastWriteTimeUtc, info.Length));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        listed.Sort(static (a, b) =>
        {
            var byTime = a.WriteUtc.CompareTo(b.WriteUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
        });
        var seenKeys = new HashSet<HermesFileKey>();
        var stamps = new Dictionary<string, (DateTime, long)>(PathComparer);
        foreach (var (path, writeUtc, length) in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (thresholdMs is null && tracked.Stamps.TryGetValue(path, out var known) && known == (writeUtc, length)
                && !_unreadable.Contains(path))
            {
                stamps[path] = known;
                if (tracked.KeyOf.TryGetValue(path, out var unchanged))
                {
                    seenKeys.Add(unchanged);
                }

                continue;
            }

            if (ReadFile(tracked, path, writeUtc, thresholdMs, update, cancellationToken) is { } read)
            {
                stamps[path] = (read.WriteUtc, read.Length);
                tracked.KeyOf[path] = read.Key;
                seenKeys.Add(read.Key);
            }
        }

        tracked.Stamps.Clear();
        foreach (var (path, stamp) in stamps)
        {
            tracked.Stamps[path] = stamp;
        }

        foreach (var path in tracked.KeyOf.Keys.Where(p => !stamps.ContainsKey(p)).ToList())
        {
            tracked.KeyOf.Remove(path);
        }

        _unreadable.RemoveWhere(path => !listed.Exists(l => PathComparer.Equals(l.Path, path))
            && PathComparer.Equals(Path.GetDirectoryName(path), tracked.Set.Folder));

        foreach (var (key, log) in tracked.Logs.ToList())
        {
            if (seenKeys.Contains(key))
            {
                log.Missing = 0;
            }
            else if (forget && ++log.Missing >= MissingLimit)
            {
                tracked.Logs.Remove(key);
            }
        }
    }

    private (HermesFileKey Key, DateTime WriteUtc, long Length)? ReadFile(
        TrackedSet tracked, string path, DateTime listedWriteUtc, long? thresholdMs, HermesUpdate update, CancellationToken cancellationToken)
    {
        SafeFileHandle handle;
        try
        {
            handle = TranscriptTailer.Open(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _unreadable.Remove(path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unreadable.Add(path);
            return null;
        }

        using (handle)
        {
            try
            {
                var key = HermesFileKey.Of(handle, path);
                var writeUtc = File.GetLastWriteTimeUtc(handle);
                var length = RandomAccess.GetLength(handle);
                if (!tracked.Logs.TryGetValue(key, out var log))
                {
                    log = new TrackedLog();
                    tracked.Logs[key] = log;
                    if (thresholdMs is { } threshold)
                    {
                        StartAt(log, length, listedWriteUtc, threshold);
                    }
                }

                ReadAppended(tracked.Set, log, handle, length, update, cancellationToken);
                _unreadable.Remove(path);
                return (key, writeUtc, length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _unreadable.Add(path);
                return null;
            }
        }
    }

    /// <summary>The startup rule of METRICS 5 for one file.</summary>
    private void StartAt(TrackedLog log, long length, DateTime writeUtc, long thresholdMs)
    {
        var modifiedMs = new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        if (modifiedMs < thresholdMs)
        {
            log.Offset = length;
            log.Alignment = length > 0 ? StartAlignment.SkipPartialLine : StartAlignment.None;
        }
        else if (length > Monitoring.MonitorCore.LargeFileBytes)
        {
            log.Offset = length - Monitoring.MonitorCore.LargeFileTailBytes;
            log.Alignment = StartAlignment.SkipToNextLine;
            PartialHistory = true;
        }
    }

    private static void ReadAppended(
        HermesLogSet set, TrackedLog log, SafeFileHandle handle, long length, HermesUpdate update, CancellationToken cancellationToken)
    {
        if (length < log.Offset)
        {
            // Shrunk: read again from the start; the dedupe set discards what was counted (METRICS 5).
            log.Offset = 0;
            log.Alignment = StartAlignment.None;
            log.Lines.Clear();
        }

        if (log.Alignment != StartAlignment.None)
        {
            if (log.Alignment == StartAlignment.SkipToNextLine)
            {
                log.Lines.AlignToNextLine = true;
            }
            else if (log.Offset <= length)
            {
                Span<byte> previous = stackalloc byte[1];
                log.Lines.AlignToNextLine = RandomAccess.Read(handle, previous, log.Offset - 1) != 1 || previous[0] != (byte)'\n';
            }

            log.Alignment = StartAlignment.None;
        }

        if (length <= log.Offset)
        {
            return;
        }

        var sink = new SetSink(set, update);
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(ChunkBytes, length - log.Offset));
        try
        {
            while (log.Offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - log.Offset)), log.Offset);
                if (read <= 0)
                {
                    break;
                }

                log.Offset += read;
                update.BytesRead += read;
                log.Lines.Consume(buffer.AsSpan(0, read), sink);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        log.Lines.Trim();
        update.FilesRead++;
        update.Changed |= sink.Lines > 0;
    }

    private sealed class SetSink(HermesLogSet set, HermesUpdate update) : ILineSink
    {
        public int Lines { get; private set; }

        public void OnLine(ReadOnlySpan<byte> line)
        {
            Lines++;
            set.Line(line, update);
        }

        public void OnMalformed()
        {
        }
    }

    private sealed class TrackedSet(HermesLogSet set)
    {
        public HermesLogSet Set { get; } = set;

        /// <summary>Files followed by identity: a renamed file keeps its offset and its partial line.</summary>
        public Dictionary<HermesFileKey, TrackedLog> Logs { get; } = [];

        /// <summary>The time and size each path had when it was last read (a changed stamp means read it).</summary>
        public Dictionary<string, (DateTime WriteUtc, long Length)> Stamps { get; } = new(PathComparer);

        /// <summary>The identity last read under each path.</summary>
        public Dictionary<string, HermesFileKey> KeyOf { get; } = new(PathComparer);
    }

    private sealed class TrackedLog
    {
        public long Offset { get; set; }

        public StartAlignment Alignment { get; set; }

        public LineSplitter Lines { get; } = new();

        public int Missing { get; set; }
    }
}

/// <summary>
/// A log file's identity across renames (METRICS 4.15): Windows volume serial number and file index (not the creation
/// time, which file-system tunneling hands from a renamed file to a new one of the same name); elsewhere the identity the
/// platform offers; the path when it offers none.
/// </summary>
internal readonly record struct HermesFileKey(uint Volume, ulong FileIndex, long CreationTicks, string? FallbackPath)
{
    public static HermesFileKey Of(SafeFileHandle handle, string path) => FileIdentity.From(handle) is { } identity
        ? OperatingSystem.IsWindows()
            ? new HermesFileKey(identity.Volume, identity.FileIndex, 0, null)
            : new HermesFileKey(identity.Volume, identity.FileIndex, identity.CreationTicks, null)
        : new HermesFileKey(0, 0, 0, path);

    /// <summary>Stable text for the history store (METRICS 8: Hermes logs are cached by identity).</summary>
    public string Text => FallbackPath is null
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"hermes-file:{Volume:x8}:{FileIndex:x16}:{CreationTicks:x16}")
        : "hermes-path:" + FallbackPath;
}
