using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Devin;

/// <param name="Model"><c>sessions.model</c> by the session-model rule (never <c>adaptive</c>).</param>
internal sealed record DevinSession(string Id, string? WorkingDirectory, string? Model);

/// <param name="TimeMs"><c>created_at</c> in Unix milliseconds; null when it is no finite number.</param>
/// <param name="Message">Null for a malformed <c>chat_message</c>.</param>
internal readonly record struct DevinRow(long RowId, string? SessionId, long? TimeMs, DevinMessage? Message);

/// <summary>
/// Devin CLI's <c>sessions.db</c>, opened read-only with OpenCode's safeguards (METRICS 4.5, 4.17): never written, never
/// given a <c>-wal</c> or <c>-shm</c>. One connection per read, closed right after it.
/// </summary>
internal sealed class DevinDatabase : IDisposable
{
    public const int MaxMessageBytes = 32 << 20;

    private const double TwoTo63 = 9_223_372_036_854_775_808d;

    private const string Columns =
        "SELECT row_id, session_id, created_at, CASE WHEN typeof(chat_message) = 'text' AND length(CAST(chat_message AS BLOB)) <= 33554432 " +
        "THEN chat_message END FROM message_nodes";

    private static readonly string[] SessionColumns = ["id", "working_directory", "model"];
    private static readonly string[] RowColumns = ["row_id", "session_id", "chat_message", "created_at"];

    private readonly SqliteConnection _connection;

    private DevinDatabase(SqliteConnection connection) => _connection = connection;

    public string Path => _connection.Path;

    /// <exception cref="InvalidDataException">The tables METRICS 4.17 requires are missing (unsupported).</exception>
    /// <exception cref="SqliteException">The file cannot be opened or read as a database.</exception>
    public static DevinDatabase Open(string path)
    {
        if (!DevinPaths.IsRegularFile(path))
        {
            throw new FileNotFoundException("No Devin CLI database.", path);
        }

        var connection = SqliteReadOnly.Open(path, out _);
        try
        {
            if (!HasColumns(connection, "PRAGMA table_info(sessions)", SessionColumns)
                || !HasColumns(connection, "PRAGMA table_info(message_nodes)", RowColumns))
            {
                throw new InvalidDataException("Unsupported Devin CLI database schema.");
            }

            return new DevinDatabase(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="body"/> in one read transaction, so the sessions and the rows come from one snapshot.</summary>
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

    public List<DevinSession> Sessions()
    {
        var sessions = new List<DevinSession>();
        using var query = _connection.Prepare("SELECT id, working_directory, model FROM sessions");
        while (query.Step())
        {
            if (query.ColumnType(0) != SqliteNative.ColumnText || query.GetString(0) is not { } id)
            {
                continue;
            }

            var directory = query.ColumnType(1) == SqliteNative.ColumnText ? query.GetString(1) : null;
            var model = query.ColumnType(2) == SqliteNative.ColumnText ? query.GetString(2) : null;
            sessions.Add(new DevinSession(id, directory is { Length: > 0 } ? directory : null, DevinCalls.SessionModel(model)));
        }

        return sessions;
    }

    public long MaxRowId() => _connection.ScalarInt64("SELECT max(row_id) FROM message_nodes") ?? 0;

    /// <summary>Rows above <paramref name="rowId"/>, in <c>row_id</c> order.</summary>
    public List<DevinRow> RowsAfter(long rowId)
    {
        using var query = _connection.Prepare(Columns + " WHERE row_id > ? ORDER BY row_id");
        query.Bind(1, rowId);
        var rows = new List<DevinRow>();
        while (query.Step())
        {
            rows.Add(Row(query));
        }

        return rows;
    }

    /// <summary>
    /// The startup read (METRICS 4.17 <i>Reading</i>): from the highest <c>row_id</c> down while rows are not older than
    /// <paramref name="thresholdMs"/> (rows without a time do not stop it), returned in <c>row_id</c> order.
    /// </summary>
    public List<DevinRow> RecentRows(long thresholdMs)
    {
        var rows = new List<DevinRow>();
        using (var query = _connection.Prepare(Columns + " ORDER BY row_id DESC"))
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

    /// <summary>Every row of the given sessions in <c>row_id</c> order (history): one query per session, or one scan for many.</summary>
    public List<DevinRow> RowsOfSessions(IReadOnlySet<string> sessions)
    {
        var rows = new List<DevinRow>();
        if (sessions.Count > 8)
        {
            using var scan = _connection.Prepare(Columns + " ORDER BY row_id");
            while (scan.Step())
            {
                if (scan.ColumnType(1) == SqliteNative.ColumnText && scan.GetString(1) is { } id && sessions.Contains(id))
                {
                    rows.Add(Row(scan));
                }
            }

            return rows;
        }

        using var query = _connection.Prepare(Columns + " WHERE session_id = ? ORDER BY row_id");
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

    /// <summary>Each session's row count and highest <c>row_id</c> (history identities); reads no message.</summary>
    public Dictionary<string, (long Count, long MaxRowId)> RowStats()
    {
        var stats = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        using var query = _connection.Prepare("SELECT session_id, count(*), max(row_id) FROM message_nodes GROUP BY session_id");
        while (query.Step())
        {
            if (query.ColumnType(0) == SqliteNative.ColumnText && query.GetString(0) is { } id)
            {
                stats[id] = (query.GetInt64(1), query.GetInt64(2));
            }
        }

        return stats;
    }

    public string AgentId(string session) => Path + "#" + session;

    public void Dispose() => _connection.Dispose();

    private static DevinRow Row(SqliteStatement query)
    {
        var session = query.ColumnType(1) == SqliteNative.ColumnText ? query.GetString(1) : null;
        var time = TimeMs(query, 2);
        var message = query.ColumnType(3) == SqliteNative.ColumnText ? DevinMessage.Parse(query.GetUtf8(3)) : null;
        return new DevinRow(query.GetInt64(0), session, time, message);
    }

    /// <summary><c>created_at</c> (Unix seconds, an integer or a finite real) in milliseconds, truncated and saturated (METRICS 3.6).</summary>
    private static long? TimeMs(SqliteStatement query, int column)
    {
        switch (query.ColumnType(column))
        {
            case SqliteNative.ColumnInteger:
            {
                var seconds = query.GetInt64(column);
                return seconds > long.MaxValue / 1000 ? long.MaxValue : seconds < long.MinValue / 1000 ? long.MinValue : seconds * 1000;
            }

            case SqliteNative.ColumnFloat:
            {
                var ms = query.GetDouble(column) * 1000;
                return !double.IsFinite(ms) ? null : ms >= TwoTo63 ? long.MaxValue : ms <= -TwoTo63 ? long.MinValue : (long)Math.Truncate(ms);
            }

            default:
                return null;
        }
    }

    private static bool HasColumns(SqliteConnection connection, string pragma, string[] required)
    {
        using var query = connection.Prepare(pragma);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (query.Step())
        {
            if (query.GetString(1) is { } name)
            {
                columns.Add(name);
            }
        }

        return required.All(columns.Contains);
    }
}
