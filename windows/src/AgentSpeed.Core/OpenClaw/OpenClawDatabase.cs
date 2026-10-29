using System.Globalization;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pi;

namespace AgentSpeed.Core.OpenClaw;

/// <summary>One row of <c>session_windows</c> (METRICS 4.14); a missing column counts as empty or 0.</summary>
/// <param name="UpdatedMs"><c>transcript_updated_at</c>, else <c>created_at</c>; null when neither is an integer.</param>
/// <param name="Runtime"><c>agent_harness_id</c> trimmed and lower-cased; empty when absent.</param>
internal sealed record OpenClawWindow(string SessionId, long? CreatedMs, long? UpdatedMs, string? SpawnedBy, string Runtime, bool AcpOwned)
{
    /// <summary>The window's turns run in another program (METRICS 4.14).</summary>
    public bool Delegated => AcpOwned || Runtime is not ("" or "openclaw" or "pi" or "auto" or "default");

    /// <summary>Every assistant message of the window is a mirror.</summary>
    public bool MirrorsOnly => AcpOwned || Runtime is "codex" or "claude-cli" || Runtime.StartsWith("acp", StringComparison.Ordinal);

    public bool IsSubagent => SpawnedBy is { Length: > 0 };
}

/// <summary>
/// Read-only access to one OpenClaw agent database (METRICS 4.14): opened like OpenCode's (4.5), never written, and only
/// the tables <c>session_windows</c>, <c>session_nodes</c>, <c>transcript_events</c> and
/// <c>transcript_rewrite_watermarks</c> are read. Not thread-safe; callers keep reads inside <see cref="Read{T}"/>.
/// </summary>
internal sealed class OpenClawDatabase : IDisposable
{
    /// <summary>Largest decoded zstd entry (<c>event_utf8_bytes</c>).</summary>
    public const int MaxEventBytes = 4 * 1024 * 1024;

    /// <summary>A longer entry is skipped like a long line (METRICS 4).</summary>
    public const int MaxEntryBytes = 32 * 1024 * 1024;

    private readonly SqliteConnection _connection;
    private readonly string _windowsSql;
    private readonly string _rowsSql;
    private readonly bool _hasNodes;
    private readonly bool _hasWatermarks;

    private OpenClawDatabase(SqliteConnection connection, bool immutable, bool supported, HashSet<string> tables)
    {
        _connection = connection;
        IsImmutable = immutable;
        Supported = supported;
        _hasNodes = tables.Contains("session_nodes");
        _hasWatermarks = tables.Contains("transcript_rewrite_watermarks");
        if (!supported)
        {
            _windowsSql = _rowsSql = string.Empty;
            return;
        }

        var windows = Columns(connection, "session_windows");
        string Column(string name, string fallback) => windows.Contains(name) ? name : fallback;
        _windowsSql = "SELECT session_id, created_at, " + Column("transcript_updated_at", "NULL") + ", " + Column("spawned_by", "NULL")
            + ", " + Column("agent_harness_id", "NULL") + ", " + Column("acp_owned", "0")
            + " FROM session_windows ORDER BY created_at, session_id";
        var events = Columns(connection, "transcript_events");
        var zstd = events.Contains("event_zstd") && events.Contains("event_utf8_bytes") ? "event_zstd, event_utf8_bytes" : "NULL, NULL";
        _rowsSql = "SELECT seq, event_json, " + zstd + " FROM transcript_events WHERE session_id = ?1 AND seq > ?2 ORDER BY seq";
    }

    /// <summary>The database has the session tables (agent schema 8 and later).</summary>
    public bool Supported { get; }

    /// <summary>Opened with <c>immutable=1</c> because OpenClaw had closed it.</summary>
    public bool IsImmutable { get; }

    /// <exception cref="SqliteException">The database cannot be opened or read.</exception>
    public static OpenClawDatabase Open(string path)
    {
        var connection = SqliteReadOnly.Open(path, out var immutable);
        try
        {
            var tables = Tables(connection);
            return new OpenClawDatabase(connection, immutable, tables.Contains("session_windows") && tables.Contains("transcript_events"), tables);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="read"/> in one short read transaction.</summary>
    public T Read<T>(Func<OpenClawDatabase, T> read)
    {
        _connection.Execute("BEGIN");
        try
        {
            return read(this);
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

    /// <summary>Every window in reading order: <c>created_at</c>, ties by <c>session_id</c>.</summary>
    public List<OpenClawWindow> Windows()
    {
        var windows = new List<OpenClawWindow>();
        using var statement = _connection.Prepare(_windowsSql);
        while (statement.Step())
        {
            if (statement.IsNull(0))
            {
                continue;
            }

            var created = Integer(statement, 1);
            var spawned = statement.ColumnType(3) == SqliteNative.ColumnText ? statement.GetString(3) : null;
            var runtime = statement.ColumnType(4) == SqliteNative.ColumnText ? statement.GetString(4)!.Trim().ToLowerInvariant() : string.Empty;
            var acp = statement.ColumnType(5) switch
            {
                SqliteNative.ColumnInteger => statement.GetInt64(5) == 1,
                SqliteNative.ColumnFloat => statement.GetDouble(5) == 1.0,
                _ => false,
            };
            windows.Add(new OpenClawWindow(statement.GetString(0)!, created, Integer(statement, 2) ?? created, spawned, runtime, acp));
        }

        return windows;
    }

    /// <summary><c>session_nodes</c>: session key → its current window.</summary>
    public Dictionary<string, string> CurrentSessions()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_hasNodes)
        {
            return current;
        }

        try
        {
            using var statement = _connection.Prepare("SELECT session_key, current_session_id FROM session_nodes");
            while (statement.Step())
            {
                if (statement.GetString(0) is { } key && statement.GetString(1) is { } session)
                {
                    current[key] = session;
                }
            }
        }
        catch (SqliteException)
        {
        }

        return current;
    }

    /// <summary><c>transcript_rewrite_watermarks</c>: window → generation; a rewrite changes it.</summary>
    public Dictionary<string, string> Watermarks()
    {
        var generations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_hasWatermarks)
        {
            return generations;
        }

        try
        {
            using var statement = _connection.Prepare("SELECT session_id, generation FROM transcript_rewrite_watermarks");
            while (statement.Step())
            {
                if (statement.GetString(0) is { } session)
                {
                    generations[session] = statement.GetString(1) ?? string.Empty;
                }
            }
        }
        catch (SqliteException)
        {
        }

        return generations;
    }

    /// <summary>The highest <c>seq</c> of a window; null when it has no rows.</summary>
    public long? MaxSeq(string sessionId)
    {
        using var statement = _connection.Prepare("SELECT max(seq) FROM transcript_events WHERE session_id = ?1");
        statement.Bind(1, sessionId);
        return statement.Step() ? Integer(statement, 0) : null;
    }

    /// <summary>
    /// The window's rows above <paramref name="afterSeq"/> in <c>seq</c> order, each decoded to an entry; stops when
    /// <paramref name="onRow"/> returns false. Returns the highest <c>seq</c> visited.
    /// </summary>
    public long Rows(string sessionId, long afterSeq, Func<long, PiLine, bool> onRow)
    {
        using var statement = _connection.Prepare(_rowsSql);
        statement.Bind(1, sessionId).Bind(2, afterSeq);
        var last = afterSeq;
        while (statement.Step())
        {
            var seq = Integer(statement, 0) ?? last;
            last = Math.Max(last, seq);
            if (!onRow(seq, Entry(statement)))
            {
                break;
            }
        }

        return last;
    }

    /// <summary>The first row whose entry is the window header (METRICS 4.14, read before a window started at its end).</summary>
    public PiLine Header(string sessionId)
    {
        var header = new PiLine { Status = LineStatus.Blank };
        Rows(sessionId, long.MinValue, (_, line) =>
        {
            if (line is { Status: LineStatus.Valid, Kind: PiEntryKind.Session })
            {
                header = line;
                return false;
            }

            return true;
        });
        return header;
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// The entry of the current row: <c>event_json</c> when it is text, else the zstd frame in <c>event_zstd</c> decoded
    /// within <c>event_utf8_bytes</c>. Undecodable, invalid or non-object entries are malformed; longer ones are skipped.
    /// </summary>
    private static PiLine Entry(SqliteStatement statement)
    {
        if (statement.ColumnType(1) == SqliteNative.ColumnText)
        {
            return Parse(statement.GetUtf8(1));
        }

        if (statement.ColumnType(2) != SqliteNative.ColumnBlob || statement.ColumnType(3) != SqliteNative.ColumnInteger
            || statement.GetInt64(3) is <= 0 or > MaxEventBytes)
        {
            return Malformed;
        }

        var decoded = new byte[(int)statement.GetInt64(3)];
        try
        {
            using var decompressor = new ZstdSharp.Decompressor();
            var written = decompressor.Unwrap(statement.GetBlob(2), decoded);
            return Parse(decoded.AsSpan(0, written));
        }
        catch (Exception e) when (e is ZstdSharp.ZstdException or ArgumentException or InvalidOperationException)
        {
            return Malformed;
        }
    }

    // A row's entry is one JSON object: blank text, other JSON values and invalid UTF-8 are malformed (unlike a blank line).
    private static PiLine Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaxEntryBytes)
        {
            return new PiLine { Status = LineStatus.Blank };
        }

        if (JsonScan.IsBlank(raw))
        {
            return Malformed;
        }

        return PiLineReader.Read(raw);
    }

    private static PiLine Malformed => new() { Status = LineStatus.Malformed };

    private static long? Integer(SqliteStatement statement, int column) =>
        statement.ColumnType(column) == SqliteNative.ColumnInteger ? statement.GetInt64(column) : null;

    private static HashSet<string> Tables(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using var statement = connection.Prepare("SELECT name FROM sqlite_master WHERE type = 'table'");
        while (statement.Step())
        {
            if (statement.GetString(0) is { } name)
            {
                tables.Add(name);
            }
        }

        return tables;
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var statement = connection.Prepare(string.Create(CultureInfo.InvariantCulture, $"PRAGMA table_info({table})"));
        while (statement.Step())
        {
            if (statement.GetString(1) is { } name)
            {
                columns.Add(name);
            }
        }

        return columns;
    }
}
