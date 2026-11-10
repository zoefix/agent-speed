using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// A Qoder IDE <c>local.db</c> written the way the agent core does (its tables of Qoder IDE 1.39.2, rows appended by
/// <c>rowid</c>, content stored encrypted), optionally in WAL mode like the core keeps it.
/// </summary>
internal sealed class QoderIdeDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _wal;
    private int _messages;

    public QoderIdeDatabase(string path, bool wal = false)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connection = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        _connection.Execute("""
            CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text, project_name text, gmt_create INTEGER,
                gmt_modified INTEGER, mode text, parent_session_id varchar(64), parent_tool_call_id varchar(64), session_type text);
            CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), request_id varchar(64), role text,
                content text, summary text, summary_modified INTEGER, summary_trigger text, tool_result text, token_info text,
                model_info text, extra text, gmt_create INTEGER);
            CREATE INDEX idx_chat_message_session ON chat_message (session_id);
            CREATE TABLE chat_record (request_id varchar(64) PRIMARY KEY, session_id varchar(64), question text, answer text,
                extra text, gmt_create INTEGER, gmt_modified INTEGER);
            """);
        _wal = wal;
        if (wal)
        {
            _connection.Execute("PRAGMA journal_mode=WAL");
        }
    }

    public string Path { get; }

    public void Session(string id, string? projectUri = "/w/aurora", string? parent = null)
    {
        using var insert = _connection.Prepare("INSERT OR REPLACE INTO chat_session(session_id, project_uri, parent_session_id) VALUES (?, ?, ?)");
        insert.Bind(1, id).Bind(2, projectUri).Bind(3, parent).Run();
    }

    /// <returns>The row's message id.</returns>
    public string Row(string session, string role, long ms, string? request = null, string? tokenInfo = null, string? modelInfo = null,
        string? id = null)
    {
        id ??= "m-" + ++_messages;
        using var insert = _connection.Prepare(
            "INSERT INTO chat_message(id, session_id, request_id, role, content, token_info, model_info, gmt_create) VALUES (?, ?, ?, ?, ?, ?, ?, ?)");
        insert.Bind(1, id).Bind(2, session).Bind(3, request).Bind(4, role).Bind(5, "ZW5jcnlwdGVk").Bind(6, tokenInfo).Bind(7, modelInfo)
            .Bind(8, ms).Run();
        return id;
    }

    /// <summary>One assistant row with the server's usage (prompt tokens include the cached ones) and the selected tier.</summary>
    /// <returns>The row's message id.</returns>
    public string Call(string session, long ms, string? request = null, int prompt = 1000, int completion = 40, int cached = 900,
        string model = "auto", string? id = null) =>
        Row(session, "assistant", ms, request,
            "{\"prompt_tokens\":" + prompt + ",\"completion_tokens\":" + completion + ",\"cached_tokens\":" + cached
            + ",\"max_input_tokens\":200000}", "{\"model_key\":\"" + model + "\"}", id);

    /// <summary>The turn record of a request, with its <c>finishedAt</c>.</summary>
    public void Finish(string request, string session, long finishedAt)
    {
        using var insert = _connection.Prepare("INSERT OR REPLACE INTO chat_record(request_id, session_id, extra) VALUES (?, ?, ?)");
        insert.Bind(1, request).Bind(2, session)
            .Bind(3, "{\"ai-coding/turn-info\":{\"startedAt\":" + (finishedAt - 5000) + ",\"finishedAt\":" + finishedAt + ",\"status\":\"finished\"}}")
            .Run();
    }

    /// <summary>
    /// A clean shutdown: a WAL database is checkpointed and left alone, its <c>-wal</c> and <c>-shm</c> removed the way the core's
    /// SQLite does (Apple's SQLite keeps an empty WAL by default).
    /// </summary>
    public void Dispose()
    {
        if (_wal)
        {
            _connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        _connection.Dispose();
        if (_wal)
        {
            File.Delete(Path + "-wal");
            File.Delete(Path + "-shm");
        }
    }
}
