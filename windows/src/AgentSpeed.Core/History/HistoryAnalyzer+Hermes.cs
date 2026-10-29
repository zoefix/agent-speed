using System.Buffers;
using AgentSpeed.Core.Hermes;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.History;

/// <summary>
/// Hermes Agent logs (METRICS 4.15, 8), cached by file identity instead of path: a rotation renames a log without
/// changing it, so it keeps its contributions and keys under its new name, and the oldest log, which rotation deletes,
/// keeps them like any deleted file. Each log is a marker entry <c>&lt;identity&gt;|&lt;log folder&gt;</c> that owns the
/// file's keys, plus one entry per session (<c>…#&lt;session id&gt;</c>) with its contributions; a day's agents count
/// distinct sessions across files.
/// </summary>
internal sealed partial class HistoryAnalyzer
{
    private const string HermesFamily = "hermes";
    private const string HermesLogFamily = "hermesLog";
    private const string HermesZoneMeta = "hermesTimeZone";

    private sealed record PlannedHermesLog(
        string Folder, string Path, string Marker, long Size, DateTime LastWriteUtc, StoredFile? Previous, List<StoredFile> Sessions);

    /// <summary>The agent id (<c>&lt;log folder&gt;#&lt;session&gt;</c>) of a Hermes session entry.</summary>
    private static string HermesAgentOf(string entryPath) => entryPath[(entryPath.IndexOf('|', StringComparison.Ordinal) + 1)..];

    private void PlanHermes(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing, TimeZoneInfo zone)
    {
        if (!enabled.Contains(SourceKind.Hermes) || locations.HermesRoot.Length == 0)
        {
            return;
        }

        var hermesZone = _options.HermesZone ?? zone;
        plan.HermesZone = hermesZone;
        var changedZone = zoneChanged || (_entries.Values.Any(e => e.Family == HermesLogFamily)
            && !string.Equals(_store.Meta(HermesZoneMeta), hermesZone.Id, StringComparison.Ordinal));
        foreach (var folder in locations.HermesFolders())
        {
            var files = new List<PlannedHermesLog>();
            foreach (var path in HermesPaths.LogFiles(folder))
            {
                if (Identify(path) is not { } file)
                {
                    continue;
                }

                plan.Discovered++;
                var marker = file.Key.Text + "|" + folder;
                var previous = _entries.GetValueOrDefault(marker);
                var sessions = _entries.Values
                    .Where(e => e.Family == HermesFamily && e.Path.StartsWith(marker + "#", StringComparison.Ordinal))
                    .ToList();
                if (previous is not null && !changedZone && previous.Version == _options.Version && previous.Family == HermesLogFamily
                    && previous.Size == file.Length && previous.Modified == file.WriteUtc.Ticks
                    && !(repricing && sessions.Exists(s => s.HasUnpriced)))
                {
                    continue;
                }

                files.Add(new PlannedHermesLog(folder, path, marker, file.Length, file.WriteUtc, previous, sessions));
            }

            // Startup order within a log set (METRICS 5); sets in folder order, as they are read live.
            files.Sort(static (a, b) =>
            {
                var byTime = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
            });
            plan.HermesLogs.AddRange(files);
        }
    }

    private static (HermesFileKey Key, long Length, DateTime WriteUtc)? Identify(string path)
    {
        try
        {
            using var handle = TranscriptTailer.Open(path);
            return (HermesFileKey.Of(handle, path), RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<long> HermesReplacedIds(Plan plan) =>
        plan.HermesLogs.SelectMany(l => l.Sessions.Select(s => s.Id).Append(l.Previous?.Id ?? 0));

    private bool AnalyzeHermes(
        Plan plan,
        KeyLedger ledger,
        DayLabeler labeler,
        List<(StoredFile, ulong[])> pending,
        Action<bool> flush,
        ref HistoryProgress state,
        ref int analyzed,
        ref long bytes,
        ref int unreadable,
        Action<HistoryProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var log in plan.HermesLogs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            state = state with { FilesDone = state.FilesDone + 1, BytesDone = state.BytesDone + log.Size };
            var completions = new Perf.PerfCompletionLog();
            var sink = new LatestResponses(merge: false, completions);
            long size;
            DateTime writeUtc;
            try
            {
                (size, writeUtc) = ReadHermesLog(log, new HermesLogSet(log.Folder, ledger, plan.HermesZone!), sink, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ledger.AbandonFile();
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable or vanished: keep what was stored, including its key ownership.
                ledger.AbandonFile();
                unreadable++;
                if (log.Previous is { Id: > 0 } previous)
                {
                    _store.LoadKeys(ledger, previous.Id);
                }

                progress?.Invoke(state);
                continue;
            }

            var marker = new StoredFile
            {
                Id = log.Previous?.Id ?? 0,
                Path = log.Marker,
                Family = HermesLogFamily,
                Size = size,
                Modified = writeUtc.Ticks,
                Version = _options.Version,
                Days = [],
            };
            _entries[log.Marker] = marker;
            pending.Add((marker, ledger.FinishFile()));
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in sink.Items.Values.GroupBy(r => r.AgentId, StringComparer.Ordinal))
            {
                var path = log.Marker + group.Key[log.Folder.Length..];
                written.Add(path);
                AddHermesEntry(pending, log, path, size, writeUtc,
                    DayAggregate.Contributions(group, SourceKind.Hermes, labeler, Pricing),
                    PerfRecordsOf(completions, sink.Items, group.Key, SourceKind.Hermes, labeler, group.Key));
            }

            // Sessions this file no longer has (it was rewritten) keep nothing of it.
            foreach (var stale in log.Sessions.Where(s => !written.Contains(s.Path)))
            {
                AddHermesEntry(pending, log, stale.Path, size, writeUtc, [], []);
            }

            analyzed++;
            bytes += size;
            flush(false);
            progress?.Invoke(state);
        }

        return true;
    }

    private void AddHermesEntry(List<(StoredFile, ulong[])> pending, PlannedHermesLog log, string path, long size, DateTime writeUtc,
        Dictionary<DayKey, DayAggregate> days, IReadOnlyList<Perf.PerfRecord> perf)
    {
        var entry = new StoredFile
        {
            Id = log.Sessions.Find(s => s.Path == path)?.Id ?? 0,
            Path = path,
            Family = HermesFamily,
            Size = size,
            Modified = writeUtc.Ticks,
            Version = _options.Version,
            Days = days,
            PerfRecords = perf,
        };
        _entries[path] = entry;
        pending.Add((entry, []));
    }

    /// <summary>Reads one log completely with a fresh parser state (responses do not depend on the phase), sharing read, write and delete.</summary>
    private static (long Size, DateTime WriteUtc) ReadHermesLog(
        PlannedHermesLog log, HermesLogSet set, IResponseSink sink, CancellationToken cancellationToken)
    {
        using var handle = TranscriptTailer.Open(log.Path);
        var writeUtc = File.GetLastWriteTimeUtc(handle);
        var length = RandomAccess.GetLength(handle);
        var lines = new LineSplitter();
        var feed = new HermesFeed(set, sink);
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(length, 1, 1024 * 1024));
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
                lines.Consume(buffer.AsSpan(0, read), feed);
            }

            return (offset, writeUtc);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class HermesFeed(HermesLogSet set, IResponseSink sink) : ILineSink
    {
        public void OnLine(ReadOnlySpan<byte> line) => set.Line(line, sink);

        public void OnMalformed()
        {
        }
    }
}
