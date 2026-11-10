using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Qoder;

/// <param name="ProjectPath">The path of <c>chat_session.project_uri</c> (METRICS 4.18); null when it has none.</param>
/// <param name="ParentSessionId"><c>chat_session.parent_session_id</c> when it is not blank: the session is a sub-agent.</param>
internal sealed record QoderSession(string Id, string? ProjectPath, string? ParentSessionId);

internal enum QoderRole : byte
{
    /// <summary><c>role</c> is not a string: the row is malformed.</summary>
    Missing,
    Other,
    User,
    Tool,
    Assistant,
}

/// <summary>One <c>chat_message</c> row as METRICS 4.18 reads it; never its content.</summary>
/// <param name="MessageId"><c>id</c> trimmed; null when it is no text or blank.</param>
/// <param name="TimeMs"><c>gmt_create</c> in Unix milliseconds; null when it is no finite number.</param>
/// <param name="Usage"><c>token_info</c> when it is a JSON object of at most 1 MiB.</param>
/// <param name="Model"><c>model_info.model_key</c> trimmed, when it is a string that is not blank.</param>
internal readonly record struct QoderRow(
    long RowId, string? MessageId, string? SessionId, QoderRole Role, long? TimeMs, QoderUsage? Usage, string? Model, string? RequestId)
{
    /// <summary>A row without a time, or whose role is not a string (it still counts toward <c>lastLineMs</c> when it has a time).</summary>
    public bool IsMalformed => TimeMs is null || Role == QoderRole.Missing;
}

/// <summary>
/// Qoder IDE's <c>local.db</c>, opened read-only with OpenCode's safeguards (METRICS 4.5, 4.18): never written, never given a
/// <c>-wal</c> or <c>-shm</c>. One connection per read, closed right after it.
/// </summary>
internal sealed class QoderDatabase : IDisposable
{
    public const int MaxJsonBytes = 1 << 20;

    private const double TwoTo63 = 9_223_372_036_854_775_808d;

    private static readonly string[] MessageColumns = ["id", "session_id", "role", "token_info", "model_info", "gmt_create"];
    private static readonly string[] SessionColumns = ["session_id", "project_uri"];

    private readonly SqliteConnection _connection;
    private readonly string _rows;
    private readonly string _sessions;
    private readonly bool _records;

    private QoderDatabase(SqliteConnection connection, bool requests, bool parents, bool records)
    {
        _connection = connection;
        _records = records;
        _rows = "SELECT rowid, id, session_id, role, gmt_create, " + Bounded("token_info") + ", " + Bounded("model_info") + ", "
            + (requests ? "request_id" : "NULL") + " FROM chat_message";
        _sessions = "SELECT session_id, project_uri, " + (parents ? "parent_session_id" : "NULL") + " FROM chat_session";
    }

    public string Path => _connection.Path;

    /// <exception cref="InvalidDataException">The columns METRICS 4.18 requires are missing (unsupported).</exception>
    /// <exception cref="SqliteException">The file cannot be opened or read as a database.</exception>
    public static QoderDatabase Open(string path)
    {
        if (!QoderPaths.IsRegularFile(path))
        {
            throw new FileNotFoundException("No Qoder IDE database.", path);
        }

        var connection = SqliteReadOnly.Open(path, out _);
        try
        {
            var message = Columns(connection, "chat_message");
            var session = Columns(connection, "chat_session");
            if (!MessageColumns.All(message.Contains) || !SessionColumns.All(session.Contains))
            {
                throw new InvalidDataException("Unsupported Qoder IDE database schema.");
            }

            var record = Columns(connection, "chat_record");
            return new QoderDatabase(connection, message.Contains("request_id"), session.Contains("parent_session_id"),
                record.Contains("request_id") && record.Contains("extra"));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="body"/> in one read transaction, so sessions, rows and turn records come from one snapshot.</summary>
    public T Snapshot<T>(Func<T> body)
    {
        _connection.Execute("BEGIN");
        try
        {
            return body();
        }
        finally
        {
            try
            {
                _connection.Execute("COMMIT");
            }
            catch (SqliteException)
            {
            }
        }
    }

    /// <summary>Every listed session whose id is text, in table order.</summary>
    public List<QoderSession> Sessions()
    {
        var sessions = new List<QoderSession>();
        using var query = _connection.Prepare(_sessions);
        while (query.Step())
        {
            if (query.ColumnType(0) != SqliteNative.ColumnText || query.GetString(0) is not { } id)
            {
                continue;
            }

            var uri = query.ColumnType(1) == SqliteNative.ColumnText ? query.GetString(1) : null;
            var parent = query.ColumnType(2) == SqliteNative.ColumnText ? query.GetString(2) : null;
            sessions.Add(new QoderSession(id, QoderValues.ProjectPath(uri), string.IsNullOrWhiteSpace(parent) ? null : parent));
        }

        return sessions;
    }

    public long MaxRowId() => _connection.ScalarInt64("SELECT max(rowid) FROM chat_message") ?? 0;

    /// <summary>Rows above <paramref name="rowId"/>, in <c>rowid</c> order.</summary>
    public List<QoderRow> RowsAfter(long rowId)
    {
        using var query = _connection.Prepare(_rows + " WHERE rowid > ? ORDER BY rowid");
        query.Bind(1, rowId);
        var rows = new List<QoderRow>();
        while (query.Step())
        {
            rows.Add(Row(query));
        }

        return rows;
    }

    /// <summary>
    /// The startup read (METRICS 4.18 <i>Reading</i>): from the highest <c>rowid</c> down while rows are not older than
    /// <paramref name="thresholdMs"/> (rows without a time do not stop it), returned in <c>rowid</c> order.
    /// </summary>
    public List<QoderRow> RecentRows(long thresholdMs)
    {
        var rows = new List<QoderRow>();
        using (var query = _connection.Prepare(_rows + " ORDER BY rowid DESC"))
        {
            while (query.Step())
            {
                var row = Row(query);
                if (row.TimeMs is { } time && time < thresholdMs)
                {
                    break;
                }

                rows.Add(row);
            }
        }

        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// The time of each session's row just below its first row read (the start of its first call there, METRICS 4.18): a session
    /// without such a row is absent, one whose row has no time maps to null.
    /// </summary>
    public Dictionary<string, long?> PreviousTimes(IReadOnlyDictionary<string, long> firstRowIds)
    {
        var times = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (firstRowIds.Count == 0)
        {
            return times;
        }

        using var query = _connection.Prepare("SELECT gmt_create FROM chat_message WHERE session_id = ? AND rowid < ? ORDER BY rowid DESC LIMIT 1");
        foreach (var (session, rowId) in firstRowIds)
        {
            query.Bind(1, session).Bind(2, rowId);
            if (query.Step())
            {
                times[session] = TimeMs(query, 0);
            }

            query.Reset();
        }

        return times;
    }

    /// <summary>Every row of the given sessions in <c>rowid</c> order (history): one query per session, or one scan for many.</summary>
    public List<QoderRow> RowsOfSessions(IReadOnlySet<string> sessions)
    {
        var rows = new List<QoderRow>();
        if (sessions.Count > 8)
        {
            using var scan = _connection.Prepare(_rows + " ORDER BY rowid");
            while (scan.Step())
            {
                if (scan.ColumnType(2) == SqliteNative.ColumnText && scan.GetString(2) is { } id && sessions.Contains(id))
                {
                    rows.Add(Row(scan));
                }
            }

            return rows;
        }

        using var query = _connection.Prepare(_rows + " WHERE session_id = ? ORDER BY rowid");
        foreach (var session in sessions)
        {
            query.Bind(1, session);
            while (query.Step())
            {
                rows.Add(Row(query));
            }

            query.Reset();
        }

        rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
        return rows;
    }

    /// <summary>Each session's row count and highest <c>rowid</c> (history identities); reads no message.</summary>
    public Dictionary<string, (long Count, long MaxRowId)> RowStats()
    {
        var stats = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        using var query = _connection.Prepare("SELECT session_id, count(*), max(rowid) FROM chat_message GROUP BY session_id");
        while (query.Step())
        {
            if (query.ColumnType(0) == SqliteNative.ColumnText && query.GetString(0) is { } id)
            {
                stats[id] = (query.GetInt64(1), query.GetInt64(2));
            }
        }

        return stats;
    }

    /// <summary>
    /// The latest <c>finishedAt</c> of the <c>chat_record</c> rows of each request (METRICS 4.18 <i>Phase</i>); requests without
    /// one are absent, and so is every request of a database without that table.
    /// </summary>
    public Dictionary<string, long> FinishedAt(IEnumerable<string> requests)
    {
        var finished = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!_records)
        {
            return finished;
        }

        using var query = _connection.Prepare("SELECT " + Bounded("extra") + " FROM chat_record WHERE request_id = ?");
        foreach (var request in requests.Distinct(StringComparer.Ordinal))
        {
            query.Bind(1, request);
            while (query.Step())
            {
                if (query.ColumnType(0) == SqliteNative.ColumnText && QoderValues.FinishedAt(query.GetUtf8(0)) is { } at)
                {
                    finished[request] = finished.TryGetValue(request, out var known) ? Math.Max(known, at) : at;
                }
            }

            query.Reset();
        }

        return finished;
    }

    public string AgentId(string session) => Path + "#" + session;

    public void Dispose() => _connection.Dispose();

    private static string Bounded(string column) =>
        $"CASE WHEN typeof({column}) = 'text' AND length(CAST({column} AS BLOB)) <= {MaxJsonBytes} THEN {column} END";

    private static QoderRow Row(SqliteStatement query)
    {
        var id = query.ColumnType(1) == SqliteNative.ColumnText ? QoderValues.Trimmed(query.GetString(1)) : null;
        var session = query.ColumnType(2) == SqliteNative.ColumnText ? query.GetString(2) : null;
        var role = query.ColumnType(3) != SqliteNative.ColumnText ? QoderRole.Missing : query.GetString(3) switch
        {
            "assistant" => QoderRole.Assistant,
            "user" => QoderRole.User,
            "tool" => QoderRole.Tool,
            _ => QoderRole.Other,
        };
        var usage = role == QoderRole.Assistant && query.ColumnType(5) == SqliteNative.ColumnText ? QoderValues.Usage(query.GetUtf8(5)) : null;
        var model = usage is not null && query.ColumnType(6) == SqliteNative.ColumnText ? QoderValues.ModelKey(query.GetUtf8(6)) : null;
        var request = query.ColumnType(7) == SqliteNative.ColumnText ? query.GetString(7) : null;
        return new QoderRow(query.GetInt64(0), id, session, role, TimeMs(query, 4), usage, model, request);
    }

    /// <summary><c>gmt_create</c> (Unix milliseconds, an integer or a finite real), truncated and saturated (METRICS 3.6).</summary>
    private static long? TimeMs(SqliteStatement query, int column)
    {
        switch (query.ColumnType(column))
        {
            case SqliteNative.ColumnInteger:
                return query.GetInt64(column);
            case SqliteNative.ColumnFloat:
            {
                var ms = query.GetDouble(column);
                return !double.IsFinite(ms) ? null : ms >= TwoTo63 ? long.MaxValue : ms <= -TwoTo63 ? long.MinValue : (long)Math.Truncate(ms);
            }

            default:
                return null;
        }
    }

    /// <param name="table">One of this reader's constant table names.</param>
    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var query = connection.Prepare("PRAGMA table_info(" + table + ")");
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (query.Step())
        {
            if (query.GetString(1) is { } name)
            {
                columns.Add(name);
            }
        }

        return columns;
    }
}
