using System.Text;
using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Tests;

/// <summary>A minimal protobuf encoder for test blobs.</summary>
internal static class Pb
{
    public static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return [.. bytes];
    }

    public static byte[] Field(int number, long value) => [.. Varint((ulong)number << 3), .. Varint(unchecked((ulong)value))];

    public static byte[] Field(int number, byte[] payload) =>
        [.. Varint(((ulong)number << 3) | 2), .. Varint((ulong)payload.Length), .. payload];

    public static byte[] Field(int number, string text) => Field(number, Encoding.UTF8.GetBytes(text));

    public static byte[] Message(params byte[][] fields) => [.. fields.SelectMany(f => f)];

    public static byte[] Timestamp(int number, long unixMs) =>
        Field(number, Message(Field(1, unixMs / 1000), Field(2, unixMs % 1000 * 1_000_000)));
}

/// <summary>The blobs of an Antigravity conversation (METRICS 4.8 field numbers), with an unknown field in each.</summary>
internal static class AgBlobs
{
    public static byte[] Step(long? created = null, long? finished = null, long? completed = null, byte[]? usage = null) =>
        Pb.Message(
            created is { } c ? Pb.Timestamp(1, c) : [],
            Pb.Field(21, 3),
            finished is { } f ? Pb.Timestamp(7, f) : [],
            completed is { } d ? Pb.Timestamp(8, d) : [],
            usage is not null ? Pb.Field(9, usage) : []);

    public static byte[] Usage(long input, long output, long cacheRead = 0, long thinking = 0, long response = 0, string? responseId = null) =>
        Pb.Message(
            Pb.Field(1, 1007),
            Pb.Field(2, input),
            Pb.Field(3, output),
            Pb.Field(5, cacheRead),
            Pb.Field(9, thinking),
            Pb.Field(10, response),
            responseId is not null ? Pb.Field(11, responseId) : []);

    public static byte[] Generation(string? model, params long[] steps) =>
        Pb.Message(
            model is not null ? Pb.Field(1, Pb.Message(Pb.Field(19, model), Pb.Field(11, Pb.Message(Pb.Field(1, 2))))) : [],
            Pb.Field(2, Pb.Message([.. steps.Select(s => Pb.Varint((ulong)s))])));

    public static byte[] Executor(long reason, long lastStep, string? model = null) =>
        Pb.Message(
            Pb.Field(1, reason),
            Pb.Field(3, lastStep),
            model is not null ? Pb.Field(10, Pb.Message(Pb.Field(1, Pb.Message(Pb.Field(28, model))))) : []);

    public static byte[] Trajectory(long createdMs, string workspaceUri, string? parent = null) =>
        Pb.Message(
            Pb.Timestamp(2, createdMs),
            Pb.Field(7, workspaceUri),
            parent is not null ? Pb.Field(5, parent) : [],
            Pb.Field(15, Pb.Message(Pb.Varint(7), Pb.Varint(9))));
}

/// <summary>Writes an Antigravity conversation database the way the language server does (WAL, a connection kept open).</summary>
internal sealed class AgWriter : IDisposable
{
    public const long UserInput = 14;
    public const long Planner = 15;
    public const long RunCommand = 21;
    public const long Running = 2;
    public const long Done = 3;
    public const long Canceled = 6;
    public const long Generating = 8;

    private const string Schema = """
        PRAGMA user_version=1;
        CREATE TABLE IF NOT EXISTS trajectory_meta(trajectory_id TEXT,cascade_id TEXT,trajectory_type INTEGER,source INTEGER);
        CREATE TABLE IF NOT EXISTS steps(idx INTEGER,step_type INTEGER,status INTEGER,has_subtrajectory INTEGER,metadata BLOB,error_details BLOB,permissions BLOB,task_details BLOB,render_info BLOB,step_payload BLOB,step_format INTEGER);
        CREATE TABLE IF NOT EXISTS gen_metadata(idx INTEGER,data BLOB,size INTEGER);
        CREATE TABLE IF NOT EXISTS executor_metadata(idx INTEGER,data BLOB);
        CREATE TABLE IF NOT EXISTS parent_references(idx INTEGER,data BLOB);
        CREATE TABLE IF NOT EXISTS trajectory_metadata_blob(id TEXT,data BLOB);
        CREATE TABLE IF NOT EXISTS battle_mode_infos(idx INTEGER,data BLOB);
        """;

    private SqliteConnection? _db;

    public AgWriter(string path, bool schema = true)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        _db.Execute("PRAGMA journal_mode=WAL");
        if (schema)
        {
            _db.Execute(Schema);
        }
    }

    public string Path { get; }

    private SqliteConnection Db => _db ?? throw new ObjectDisposedException(nameof(AgWriter));

    public static string ConversationPath(string conversations, string id) => System.IO.Path.Combine(conversations, id + ".db");

    public AgWriter Sql(string sql)
    {
        Db.Execute(sql);
        return this;
    }

    public AgWriter Trajectory(byte[] data)
    {
        using var statement = Db.Prepare("INSERT INTO trajectory_metadata_blob (id, data) VALUES ('t', ?1)");
        statement.BindBlob(1, data).Run();
        return this;
    }

    public AgWriter Step(long idx, long type, long status, byte[]? metadata)
    {
        using var statement = Db.Prepare("INSERT INTO steps (idx, step_type, status, metadata, step_payload) VALUES (?1, ?2, ?3, ?4, x'0a0474657874')");
        statement.Bind(1, idx).Bind(2, type).Bind(3, status);
        if (metadata is null)
        {
            statement.Bind(4, (string?)null);
        }
        else
        {
            statement.BindBlob(4, metadata);
        }

        statement.Run();
        return this;
    }

    public AgWriter UpdateStep(long idx, long status, byte[] metadata)
    {
        using var statement = Db.Prepare("UPDATE steps SET status = ?2, metadata = ?3 WHERE idx = ?1");
        statement.Bind(1, idx).Bind(2, status).BindBlob(3, metadata).Run();
        return this;
    }

    public AgWriter Generation(long idx, byte[] data, long? size = null)
    {
        using var statement = Db.Prepare("INSERT INTO gen_metadata (idx, data, size) VALUES (?1, ?2, ?3)");
        statement.Bind(1, idx).BindBlob(2, data).Bind(3, size ?? data.Length).Run();
        return this;
    }

    public AgWriter Executor(long idx, byte[] data)
    {
        using var statement = Db.Prepare("INSERT INTO executor_metadata (idx, data) VALUES (?1, ?2)");
        statement.Bind(1, idx).BindBlob(2, data).Run();
        return this;
    }

    /// <summary>
    /// A clean shutdown: checkpoint, close, and leave the database file alone (Apple's SQLite keeps an empty WAL by
    /// default, so it is removed here the way the language server's own SQLite does).
    /// </summary>
    public void Dispose()
    {
        if (_db is null)
        {
            return;
        }

        _db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        _db.Dispose();
        _db = null;
        File.Delete(Path + "-wal");
        File.Delete(Path + "-shm");
    }
}
