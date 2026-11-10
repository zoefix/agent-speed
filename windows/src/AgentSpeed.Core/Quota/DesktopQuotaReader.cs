using System.Buffers.Binary;
using System.IO.Enumeration;

namespace AgentSpeed.Core.Quota;

/// <summary>
/// Claude Desktop's cached usage responses (QUOTAS 4.4): remembers the usage entries it found, checks their size and time
/// every 60 s and reads only a changed one; searches the cache folders for new entries at start, when Desktop starts, and
/// while Desktop runs with stale candidates. The newest observation wins. Not thread-safe: one caller at a time.
/// </summary>
public sealed class DesktopQuotaReader
{
    public const long CheckIntervalMs = 60_000;
    public const long SearchIntervalMs = 30 * 60_000;
    public const long StaleCandidateMs = 20 * 60_000;
    public const long MaxEntryAgeMs = 7 * 24 * 3_600_000L;
    public const int MaxHeaderReads = 4000;
    private const int MaxKeyLength = 8192;

    private sealed class Candidate(string path)
    {
        public string Path { get; } = path;

        public long Length { get; set; } = -1;

        public long ModifiedMs { get; set; }

        public DesktopUsageEntry? Entry { get; set; }
    }

    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private long? _lastCheckMs;
    private long? _lastSearchMs;
    private bool _searchWanted = true;

    /// <param name="folders">The cache folders (QUOTAS 4.1), resolved on every search.</param>
    public DesktopQuotaReader(Func<IReadOnlyList<string>> folders, Func<long>? nowMs = null)
    {
        _folders = folders;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public QuotaSnapshot? Snapshot { get; private set; }

    /// <summary>The file the snapshot came from (diagnostics, tests).</summary>
    public string? SnapshotFile { get; private set; }

    public int CandidateCount => _candidates.Count;

    /// <summary>The last search read this many entry headers.</summary>
    public int LastSearchReads { get; private set; }

    /// <summary>Claude Desktop started: search again on the next poll.</summary>
    public void DesktopStarted() => _searchWanted = true;

    /// <summary>Forgets everything (the switch was turned off); the next poll searches again.</summary>
    public void Reset()
    {
        _candidates.Clear();
        Snapshot = null;
        SnapshotFile = null;
        _lastCheckMs = null;
        _lastSearchMs = null;
        _searchWanted = true;
    }

    /// <summary>Runs what is due; true when the snapshot changed.</summary>
    public bool Poll(bool desktopRuns)
    {
        var now = _nowMs();
        var before = Snapshot;
        var searchDue = _searchWanted
            || (desktopRuns && (_lastSearchMs is not { } searched || now - searched >= SearchIntervalMs) && NewestCandidateAge(now) > StaleCandidateMs);
        if (searchDue)
        {
            Search(now);
        }

        if (searchDue || _lastCheckMs is not { } checkedAt || now - checkedAt >= CheckIntervalMs)
        {
            Check(now);
        }

        return !Equals(before, Snapshot);
    }

    private long NewestCandidateAge(long now) => _candidates.Count == 0 ? long.MaxValue : now - _candidates.Values.Max(c => c.ModifiedMs);

    /// <summary>Lists each folder, newest first, reading entry headers until the first usage entry (QUOTAS 4.4 <i>Search</i>).</summary>
    private void Search(long now)
    {
        _searchWanted = false;
        _lastSearchMs = now;
        LastSearchReads = 0;
        foreach (var folder in _folders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            List<(string Path, long Modified)> files;
            try
            {
                files = [.. new FileSystemEnumerable<(string, long)>(folder,
                        (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc.ToUnixTimeMilliseconds()),
                        new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && IsEntryName(e.FileName),
                    }
                    .Where(f => now - f.Item2 <= MaxEntryAgeMs)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            foreach (var (path, _) in files)
            {
                if (LastSearchReads >= MaxHeaderReads)
                {
                    return;
                }

                LastSearchReads++;
                if (IsUsageHeader(path))
                {
                    _candidates.TryAdd(path, new Candidate(path));
                    break;
                }
            }
        }
    }

    /// <summary><c>&lt;16 hex digits&gt;_0</c>.</summary>
    internal static bool IsEntryName(ReadOnlySpan<char> name)
    {
        if (name.Length != 18 || name[16] != '_' || name[17] != '0')
        {
            return false;
        }

        foreach (var c in name[..16])
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsageHeader(string path)
    {
        try
        {
            using var stream = Open(path);
            Span<byte> head = stackalloc byte[DesktopCacheEntry.HeaderSize];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            if (BinaryPrimitives.ReadUInt64LittleEndian(head) != DesktopCacheEntry.HeaderMagic
                || BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != DesktopCacheEntry.Version || length is 0 or > MaxKeyLength)
            {
                return false;
            }

            var keyLength = (int)length;

            var buffer = new byte[DesktopCacheEntry.HeaderSize + keyLength];
            head.CopyTo(buffer);
            if (stream.ReadAtLeast(buffer.AsSpan(DesktopCacheEntry.HeaderSize), keyLength, throwOnEndOfStream: false) < keyLength)
            {
                return false;
            }

            return DesktopCacheEntry.Key(buffer, out _) is { } key && DesktopCacheEntry.IsUsageUrl(DesktopCacheEntry.UrlOf(key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stats every candidate, reads the changed ones, drops the vanished ones and picks the newest observation.</summary>
    private void Check(long now)
    {
        _lastCheckMs = now;
        foreach (var candidate in _candidates.Values.ToList())
        {
            var info = new FileInfo(candidate.Path);
            if (!info.Exists)
            {
                _candidates.Remove(candidate.Path);
                continue;
            }

            var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info.Length == candidate.Length && modified == candidate.ModifiedMs)
            {
                continue;
            }

            candidate.Length = info.Length;
            candidate.ModifiedMs = modified;
            candidate.Entry = info.Length > DesktopCacheEntry.EntryLimit ? null : Read(candidate.Path, modified);
        }

        var best = Choose(_candidates.Values.Select(c => (c.Path, c.Entry, c.ModifiedMs)));
        Snapshot = best?.Snapshot;
        SnapshotFile = best?.File;
    }

    private static DesktopUsageEntry? Read(string path, long modifiedMs)
    {
        try
        {
            using var stream = Open(path);
            if (stream.Length > DesktopCacheEntry.EntryLimit)
            {
                return null;
            }

            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return DesktopCacheEntry.Parse(data, modifiedMs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

    /// <summary>
    /// The newest observation among usage entries (ties: the newer file); its windows may be empty (the account lost its
    /// limits). Null without any usage entry.
    /// </summary>
    internal static (QuotaSnapshot Snapshot, string File)? Choose(IEnumerable<(string File, DesktopUsageEntry? Entry, long ModifiedMs)> entries)
    {
        (long Observed, long Modified, string File, DesktopUsageEntry Entry)? best = null;
        foreach (var (file, entry, modified) in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (best is not { } b || entry.ObservedMs > b.Observed || (entry.ObservedMs == b.Observed && modified > b.Modified))
            {
                best = (entry.ObservedMs, modified, file, entry);
            }
        }

        return best is { } found
            ? (new QuotaSnapshot(SourceKind.ClaudeDesktop, found.Observed, DesktopUsageBody.Windows(found.Entry.Body)), found.File)
            : null;
    }
}
