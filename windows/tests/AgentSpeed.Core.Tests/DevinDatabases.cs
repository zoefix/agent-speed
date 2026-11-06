using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Tests;

/// <summary>A Devin CLI database written the way the CLI does (the public readers' tables, rows appended by <c>row_id</c>).</summary>
internal sealed class DevinCliDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public DevinCliDatabase(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connection = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        _connection.Execute("""
            CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT NOT NULL, model TEXT NOT NULL, title TEXT,
                hidden INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL DEFAULT 0, last_activity_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE message_nodes (row_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                node_id INTEGER NOT NULL DEFAULT 0, parent_node_id INTEGER, chat_message TEXT NOT NULL, created_at INTEGER NOT NULL,
                metadata TEXT);
            """);
    }

    public string Path { get; }

    public void Session(string id, string model = "swe-1-6", string directory = "/w/aurora")
    {
        using var insert = _connection.Prepare("INSERT OR REPLACE INTO sessions(id, working_directory, model) VALUES (?, ?, ?)");
        insert.Bind(1, id).Bind(2, directory).Bind(3, model).Run();
    }

    public void Row(string session, string message, long seconds)
    {
        using var insert = _connection.Prepare("INSERT INTO message_nodes(session_id, chat_message, created_at) VALUES (?, ?, ?)");
        insert.Bind(1, session).Bind(2, message).Bind(3, seconds).Run();
    }

    /// <summary>One assistant row with metrics: 900 cached tokens, 2 s total, 0.5 s to the first token.</summary>
    public void Call(string session, string? request, long seconds, int input = 100, int output = 40, bool tools = false)
    {
        var id = request is null ? string.Empty : ",\"request_id\":\"" + request + "\"";
        var toolCalls = tools ? ",\"tool_calls\":[{\"id\":\"c1\"}]" : string.Empty;
        Row(session, "{\"role\":\"assistant\"" + toolCalls + ",\"metadata\":{\"generation_model\":\"swe-1-6\"" + id
            + ",\"metrics\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output
            + ",\"cache_read_tokens\":900,\"total_time_ms\":2000,\"ttft_ms\":500}}}", seconds);
    }

    public void Dispose() => _connection.Dispose();
}
