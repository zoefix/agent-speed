using System.Text;

namespace AgentSpeed.Core.IO.Sqlite;

/// <summary>
/// Read-only incremental access to one blob (<c>sqlite3_blob_open</c>): only the bytes asked for are copied out, so a
/// large value is never loaded whole. Not thread-safe.
/// </summary>
internal sealed unsafe class SqliteBlob : IDisposable
{
    private readonly SqliteConnection _connection;
    private IntPtr _blob;

    private SqliteBlob(SqliteConnection connection, IntPtr blob)
    {
        _connection = connection;
        _blob = blob;
        Length = SqliteNative.sqlite3_blob_bytes(blob);
    }

    public int Length { get; }

    /// <exception cref="SqliteException">The blob cannot be opened.</exception>
    public static SqliteBlob Open(SqliteConnection connection, string table, string column, long rowid)
    {
        var database = Utf8("main");
        var tableName = Utf8(table);
        var columnName = Utf8(column);
        int code;
        IntPtr blob;
        fixed (byte* d = database, t = tableName, c = columnName)
        {
            code = SqliteNative.sqlite3_blob_open(connection.Handle, d, t, c, rowid, 0, out blob);
        }

        if (code != SqliteNative.Ok || blob == IntPtr.Zero)
        {
            if (blob != IntPtr.Zero)
            {
                SqliteNative.sqlite3_blob_close(blob);
            }

            throw connection.Failure(code);
        }

        return new SqliteBlob(connection, blob);
    }

    /// <summary><paramref name="size"/> bytes from <paramref name="offset"/>.</summary>
    /// <exception cref="SqliteException">A range past the blob, or the read fails.</exception>
    public byte[] Read(int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > Length - size)
        {
            throw new SqliteException(SqliteNative.Error, "read past the blob");
        }

        var bytes = new byte[size];
        if (size == 0)
        {
            return bytes;
        }

        int code;
        fixed (byte* pointer = bytes)
        {
            code = SqliteNative.sqlite3_blob_read(_blob, pointer, size, offset);
        }

        if (code != SqliteNative.Ok)
        {
            throw _connection.Failure(code);
        }

        return bytes;
    }

    public void Dispose()
    {
        if (_blob != IntPtr.Zero)
        {
            SqliteNative.sqlite3_blob_close(_blob);
            _blob = IntPtr.Zero;
        }
    }

    private static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
