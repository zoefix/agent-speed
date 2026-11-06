using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Devin;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryAnalyzer
{
    private const string DevinFamily = "devin";

    /// <summary>Each Devin database's row statistics by its file stamp, kept for this analyzer's lifetime.</summary>
    private readonly Dictionary<string, (AntigravityStamp Stamp, Dictionary<string, (long Count, long MaxRowId)> Stats)> _devinStats =
        new(StringComparer.Ordinal);

    private sealed record PlannedDevinSession(string Id, List<ParsedResponse> Responses, long Size, long Modified, StoredFile? Previous);

    /// <summary>
    /// Devin's databases in list order (METRICS 4.17 <i>History</i>, 2.1): one entry per CLI session (<c>&lt;database&gt;#&lt;session&gt;</c>)
    /// whose identity is its row count, its highest <c>row_id</c> and its session model; only sessions whose identity changed are
    /// read, and a session an earlier database of the list has is left out.
    /// </summary>
    private void PlanDevin(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing)
    {
        if (!enabled.Contains(SourceKind.Devin))
        {
            return;
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in DevinPaths.Databases(locations.DevinRoots))
        {
            try
            {
                using var database = DevinDatabase.Open(path);
                plan.DevinSessions.AddRange(database.Snapshot(() => PlanDevinDatabase(plan, database, claimed, zoneChanged, repricing)));
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                plan.DevinFailures++;
            }
        }
    }

    private List<PlannedDevinSession> PlanDevinDatabase(Plan plan, DevinDatabase database, HashSet<string> claimed, bool zoneChanged, bool repricing)
    {
        var sessions = database.Sessions().Where(session => claimed.Add(session.Id)).ToList();
        var stats = DevinStats(database);
        var changed = new Dictionary<string, (DevinSession Session, long Size, long Modified, StoredFile? Previous)>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!stats.TryGetValue(session.Id, out var stat))
            {
                continue;
            }

            plan.Discovered++;
            var id = database.AgentId(session.Id);
            var modified = DevinModified(stat.MaxRowId, session.Model);
            var previous = _entries.GetValueOrDefault(id);
            if (previous is not null && !zoneChanged && previous.Version == _options.Version && previous.Family == DevinFamily
                && previous.Size == stat.Count && previous.Modified == modified && !(repricing && previous.HasUnpriced))
            {
                continue;
            }

            changed[session.Id] = (session, stat.Count, modified, previous);
        }

        if (changed.Count == 0)
        {
            return [];
        }

        var responses = new Dictionary<string, Dictionary<string, ParsedResponse>>(StringComparer.Ordinal);
        foreach (var row in database.RowsOfSessions(changed.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            if (row.SessionId is not { } id || !changed.TryGetValue(id, out var entry) || row.TimeMs is not { } time
                || row.Message is not { } message
                || DevinCalls.Response(message, id, row.RowId, time, entry.Session.Model, database.AgentId(id)) is not { } response)
            {
                continue;
            }

            // A call's stored copies update it (METRICS 4.4): the last one of each key, in first-seen order.
            (responses.TryGetValue(id, out var latest) ? latest : responses[id] = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal))[response.Key] = response;
        }

        return [.. changed.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new PlannedDevinSession(
            database.AgentId(pair.Key), responses.TryGetValue(pair.Key, out var latest) ? [.. latest.Values] : [], pair.Value.Size,
            pair.Value.Modified, pair.Value.Previous))];
    }

    /// <summary>Row counts and highest row ids, read again only when the database or its <c>-wal</c> changed.</summary>
    private Dictionary<string, (long Count, long MaxRowId)> DevinStats(DevinDatabase database)
    {
        var stamp = AntigravityStamp.Of(database.Path);
        if (stamp is { } current && _devinStats.TryGetValue(database.Path, out var cached) && cached.Stamp == current)
        {
            return cached.Stats;
        }

        var stats = database.RowStats();
        if (stamp is { } known)
        {
            _devinStats[database.Path] = (known, stats);
        }

        return stats;
    }

    /// <summary>The highest row id and the session model in one number: a call without a model of its own takes the latter.</summary>
    internal static long DevinModified(long maxRowId, string? model) =>
        unchecked((long)KeyHash.Fnv1a(maxRowId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (model ?? string.Empty)));

    private bool AnalyzeDevinSessions(List<PlannedDevinSession> sessions, KeyLedger ledger, DayLabeler labeler,
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
                Id = session.Previous?.Id ?? 0, Path = session.Id, Family = DevinFamily, Size = session.Size, Modified = session.Modified,
                Version = _options.Version, Days = DayAggregate.Contributions(responses.Values, SourceKind.Devin, labeler, Pricing),
                PerfRecords = PerfRecordsOf(completions, responses, session.Id, SourceKind.Devin, labeler),
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
