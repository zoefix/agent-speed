using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Devin;

internal sealed record DevinSession(string Id, string? WorkingDirectory, string? Model, string? Title, long LastActivityMs);
internal sealed record DevinMessageRow(long RowId, string SessionId, long CreatedMs, string? Role, byte[] Metadata);

internal sealed class DevinDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _hasTitle;

    private DevinDatabase(SqliteConnection connection, bool hasTitle)
    {
        _connection = connection;
        _hasTitle = hasTitle;
    }

    public static DevinDatabase Open(string path)
    {
        var connection = SqliteReadOnly.Open(path, out _);
        try
        {
            using (connection.Prepare("SELECT id,working_directory,model,last_activity_at FROM sessions LIMIT 0")) { }
            using (connection.Prepare("SELECT row_id,session_id,chat_message,created_at FROM message_nodes LIMIT 0")) { }
            var hasTitle = false;
            using (var columns = connection.Prepare("SELECT name FROM pragma_table_info('sessions')"))
            {
                while (columns.Step()) hasTitle |= columns.GetString(0) == "title";
            }
            return new DevinDatabase(connection, hasTitle);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public List<DevinSession> Sessions()
    {
        using var rows = _connection.Prepare($"SELECT id,working_directory,model,{(_hasTitle ? "title" : "NULL")},last_activity_at FROM sessions");
        var result = new List<DevinSession>();
        while (rows.Step())
        {
            if (rows.GetString(0) is { } id)
                result.Add(new DevinSession(id, rows.GetString(1), rows.GetString(2), rows.GetString(3), Milliseconds(rows.GetInt64(4))));
        }
        return result;
    }

    public List<DevinMessageRow> Messages(long afterRowId, long sinceMs)
    {
        using var rows = _connection.Prepare("""
            SELECT row_id,session_id,created_at,
                   CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.role') ELSE 'invalid' END,
                   CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.metadata') END
            FROM message_nodes WHERE row_id>?1 AND created_at>=?2 ORDER BY row_id
            """);
        rows.Bind(1, afterRowId);
        rows.Bind(2, sinceMs <= 0 ? 0 : sinceMs / 1000);
        var result = new List<DevinMessageRow>();
        while (rows.Step())
        {
            if (rows.GetString(1) is { } session)
                result.Add(new DevinMessageRow(rows.GetInt64(0), session, Milliseconds(rows.GetInt64(2)), rows.GetString(3), rows.GetUtf8(4).ToArray()));
        }
        return result;
    }

    private static long Milliseconds(long seconds)
    {
        try { return checked(seconds * 1000); }
        catch (OverflowException) { return seconds < 0 ? long.MinValue : long.MaxValue; }
    }

    public void Dispose() => _connection.Dispose();
}
