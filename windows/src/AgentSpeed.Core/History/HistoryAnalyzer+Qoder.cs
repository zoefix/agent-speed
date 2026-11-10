using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Qoder;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryAnalyzer
{
    private const string QoderFamily = "qoder";

    /// <summary>Each Qoder IDE database's row statistics by its file stamp, kept for this analyzer's lifetime.</summary>
    private readonly Dictionary<string, (AntigravityStamp Stamp, Dictionary<string, (long Count, long MaxRowId)> Stats)> _qoderStats =
        new(StringComparer.Ordinal);

    private sealed record PlannedQoderSession(string Id, List<ParsedResponse> Responses, long Size, long Modified, StoredFile? Previous);

    /// <summary>
    /// Qoder IDE's databases in list order (METRICS 4.18 <i>History</i>, 2.1): one entry per chat session
    /// (<c>&lt;database&gt;#&lt;session&gt;</c>) whose identity is its row count and its highest <c>rowid</c>; only sessions whose identity
    /// changed are read, and a session an earlier database of the list has is left out.
    /// </summary>
    private void PlanQoder(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing)
    {
        if (!enabled.Contains(SourceKind.Qoder))
        {
            return;
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in QoderPaths.Databases(locations.QoderRoots))
        {
            try
            {
                using var database = QoderDatabase.Open(path);
                plan.QoderSessions.AddRange(database.Snapshot(() => PlanQoderDatabase(plan, database, claimed, zoneChanged, repricing)));
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                plan.QoderFailures++;
            }
        }
    }

    private List<PlannedQoderSession> PlanQoderDatabase(Plan plan, QoderDatabase database, HashSet<string> claimed, bool zoneChanged, bool repricing)
    {
        var sessions = database.Sessions().Where(session => claimed.Add(session.Id)).ToList();
        var stats = QoderStats(database);
        var changed = new Dictionary<string, (long Size, long Modified, StoredFile? Previous)>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!stats.TryGetValue(session.Id, out var stat))
            {
                continue;
            }

            plan.Discovered++;
            var id = database.AgentId(session.Id);
            var previous = _entries.GetValueOrDefault(id);
            if (previous is not null && !zoneChanged && previous.Version == _options.Version && previous.Family == QoderFamily
                && previous.Size == stat.Count && previous.Modified == stat.MaxRowId && !(repricing && previous.HasUnpriced))
            {
                continue;
            }

            changed[session.Id] = (stat.Count, stat.MaxRowId, previous);
        }

        if (changed.Count == 0)
        {
            return [];
        }

        var responses = new Dictionary<string, Dictionary<string, ParsedResponse>>(StringComparer.Ordinal);
        var previousTimes = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var row in database.RowsOfSessions(changed.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            if (row.SessionId is not { } id || !changed.ContainsKey(id))
            {
                continue;
            }

            // A call starts at the session's previous row, whatever that row is (METRICS 4.18 Calls).
            var start = previousTimes.GetValueOrDefault(id);
            previousTimes[id] = row.TimeMs;
            if (row.IsMalformed || QoderCalls.Response(row, id, row.TimeMs!.Value, start, database.AgentId(id)) is not { } response)
            {
                continue;
            }

            // The same key again in the session updates its call (METRICS 4.4): the last one of each key, in first-seen order.
            (responses.TryGetValue(id, out var latest) ? latest : responses[id] = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal))[response.Key] = response;
        }

        return [.. changed.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new PlannedQoderSession(
            database.AgentId(pair.Key), responses.TryGetValue(pair.Key, out var latest) ? [.. latest.Values] : [], pair.Value.Size,
            pair.Value.Modified, pair.Value.Previous))];
    }

    /// <summary>Row counts and highest row ids, read again only when the database or its <c>-wal</c> changed.</summary>
    private Dictionary<string, (long Count, long MaxRowId)> QoderStats(QoderDatabase database)
    {
        var stamp = AntigravityStamp.Of(database.Path);
        if (stamp is { } current && _qoderStats.TryGetValue(database.Path, out var cached) && cached.Stamp == current)
        {
            return cached.Stats;
        }

        var stats = database.RowStats();
        if (stamp is { } known)
        {
            _qoderStats[database.Path] = (known, stats);
        }

        return stats;
    }

    private bool AnalyzeQoderSessions(List<PlannedQoderSession> sessions, KeyLedger ledger, DayLabeler labeler,
        List<(StoredFile, ulong[])> pending, Action<bool> flush, ref HistoryProgress state, ref int analyzed,
        Action<HistoryProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var session in sessions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var responses = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal);
            var completions = new Perf.PerfCompletionLog();
            foreach (var response in session.Responses)
            {
                if (ledger.TryClaim(response.Key, session.Id, out var owner) || string.Equals(owner, session.Id, StringComparison.Ordinal))
                {
                    responses[response.Key] = response;
                    completions.Upsert(response);
                }
            }

            var entry = new StoredFile
            {
                Id = session.Previous?.Id ?? 0, Path = session.Id, Family = QoderFamily, Size = session.Size, Modified = session.Modified,
                Version = _options.Version, Days = DayAggregate.Contributions(responses.Values, SourceKind.Qoder, labeler, Pricing),
                PerfRecords = PerfRecordsOf(completions, responses, session.Id, SourceKind.Qoder, labeler),
            };
            _entries[session.Id] = entry;
            pending.Add((entry, ledger.FinishFile()));
            analyzed++;
            state = state with { FilesDone = state.FilesDone + 1 };
            flush(false);
            progress?.Invoke(state);
        }

        return true;
    }
}
