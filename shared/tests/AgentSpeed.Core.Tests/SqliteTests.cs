using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Tests;

public sealed class SqliteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LoadsTheSystemLibrary()
    {
        Assert.True(SqliteNative.IsAvailable);
        Assert.Matches(@"^3\.\d+", SqliteConnection.LibraryVersion);
    }

    [Fact]
    public void WritesAndReadsEveryColumnType()
    {
        var path = _temp.Combine("a.sqlite");
        using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute("CREATE TABLE t (i INTEGER, r REAL, s TEXT, b BLOB, n TEXT)");
        using (var insert = db.Prepare("INSERT INTO t VALUES (?1, ?2, ?3, ?4, ?5)"))
        {
            insert.Bind(1, long.MaxValue).Bind(2, 1.5).Bind(3, "héllo ✓ 𝄞").BindBlob(4, [1, 2, 3]).Bind(5, (string?)null).Run();
            insert.Bind(1, -1).Bind(2, 0.0).Bind(3, new string('x', 2000)).BindBlob(4, []).Bind(5, "").Run();
        }

        Assert.Equal(2, db.LastInsertRowId);
        using var select = db.Prepare("SELECT i, r, s, b, n FROM t ORDER BY rowid");
        Assert.True(select.Step());
        Assert.Equal(long.MaxValue, select.GetInt64(0));
        Assert.Equal(1.5, select.GetDouble(1));
        Assert.Equal("héllo ✓ 𝄞", select.GetString(2));
        Assert.Equal(new byte[] { 1, 2, 3 }, select.GetBlob(3).ToArray());
        Assert.True(select.IsNull(4));
        Assert.Null(select.GetString(4));
        Assert.True(select.Step());
        Assert.Equal(2000, select.GetUtf8(2).Length);
        Assert.False(select.IsNull(3));
        Assert.Equal(0, select.GetBlob(3).Length);
        Assert.Equal(string.Empty, select.GetString(4));
        Assert.False(select.Step());
    }

    [Fact]
    public void ReadOnlyConnectionsNeverWrite()
    {
        var path = _temp.Combine("ro.sqlite");
        using (var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            db.Execute("CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);");
        }

        using var reader = SqliteConnection.Open(path, SqliteOpenMode.ReadOnly);
        Assert.Equal(1, reader.ScalarInt64("SELECT count(*) FROM t"));
        var error = Assert.Throws<SqliteException>(() => reader.Execute("INSERT INTO t VALUES (2)"));
        Assert.Equal(8, error.PrimaryCode);
    }

    [Fact]
    public void MissingDatabasesAreNotCreatedReadOnly()
    {
        var path = _temp.Combine("missing.sqlite");
        Assert.Throws<SqliteException>(() => SqliteConnection.Open(path, SqliteOpenMode.ReadOnly));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TransactionsRollBackOnFailure()
    {
        using var db = SqliteConnection.Open(_temp.Combine("tx.sqlite"), SqliteOpenMode.ReadWriteCreate);
        db.Execute("CREATE TABLE t (x INTEGER PRIMARY KEY)");
        Assert.Throws<SqliteException>(() => db.Transaction(() =>
        {
            db.Execute("INSERT INTO t VALUES (1)");
            db.Execute("INSERT INTO t VALUES (1)");
        }));

        Assert.Equal(0, db.ScalarInt64("SELECT count(*) FROM t"));
        Assert.Throws<SqliteException>(() => db.Prepare("SELECT nope FROM t"));
    }

    [Fact]
    public void DisposingTheConnectionFinalizesOpenStatements()
    {
        var db = SqliteConnection.Open(_temp.Combine("s.sqlite"), SqliteOpenMode.ReadWriteCreate);
        var statement = db.Prepare("SELECT 1");
        Assert.True(statement.Step());
        db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => statement.Step());
        statement.Dispose();
        db.Dispose();
    }
}
