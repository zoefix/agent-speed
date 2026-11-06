using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.History;

/// <summary>One stored day of a remote host (<c>remote_days</c>).</summary>
internal sealed record RemoteDayRow(string Host, string Day, string Source, string Data, string Digest);

/// <summary>One stored performance record of a remote host (<c>remote_perf</c>).</summary>
internal sealed record RemotePerfRow(string Host, string Day, string Source, string Model, string Data);

/// <summary>A committed batch would leave the host with more rows than REMOTE 8.3 allows; nothing was changed.</summary>
internal sealed class RemoteHistoryTooLargeException() : Exception("The remote history batch exceeds the bounds.");

/// <summary>
/// The side tables of the remote hosts' daily history (REMOTE 8.3), created whenever the store is opened; the schema version
/// does not change for them, so older builds ignore them. <c>data</c> is the aggregate's or the record's exact JSON text as
/// the relay sent it, so the digest stays verifiable. A large batch waits in the connection's temporary staging tables until
/// it commits, so nothing of it reaches the store before its <c>historyDone</c>.
/// </summary>
internal sealed partial class HistoryStore
{
    private const string RemoteSchema = """
        CREATE TABLE IF NOT EXISTS remote_hosts (id TEXT PRIMARY KEY, name TEXT, zone TEXT);
        CREATE TABLE IF NOT EXISTS remote_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,
            PRIMARY KEY (host, day, source));
        CREATE TABLE IF NOT EXISTS remote_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,
            PRIMARY KEY (host, day, source, model));
        CREATE TEMP TABLE IF NOT EXISTS remote_stage_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,
            PRIMARY KEY (host, day, source));
        CREATE TEMP TABLE IF NOT EXISTS remote_stage_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,
            PRIMARY KEY (host, day, source, model));
        """;

    /// <summary>Tests: the store's own connection (the staging tables belong to it).</summary>
    internal SqliteConnection Connection => _connection;

    /// <summary><c>remote_hosts</c>: id → name (null when it was stored without one).</summary>
    public Dictionary<string, string?> RemoteHostNames()
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var rows = _connection.Prepare("SELECT id, name FROM remote_hosts");
        while (rows.Step())
        {
            if (rows.GetString(0) is { } id)
            {
                names[id] = rows.GetString(1);
            }
        }

        return names;
    }

    /// <summary><c>remote_hosts</c>: id → the zone its rows were computed in, for every host stored with one.</summary>
    public Dictionary<string, string> RemoteHostZones()
    {
        var zones = new Dictionary<string, string>(StringComparer.Ordinal);
        using var rows = _connection.Prepare("SELECT id, zone FROM remote_hosts WHERE zone IS NOT NULL");
        while (rows.Step())
        {
            if (rows.GetString(0) is { } id && rows.GetString(1) is { } zone)
            {
                zones[id] = zone;
            }
        }

        return zones;
    }

    public List<RemoteDayRow> RemoteDayRows()
    {
        var list = new List<RemoteDayRow>();
        using var rows = _connection.Prepare("SELECT host, day, source, data, digest FROM remote_days");
        while (rows.Step())
        {
            if (rows.GetString(0) is { } host && rows.GetString(1) is { } day && rows.GetString(2) is { } source
                && rows.GetString(3) is { } data && rows.GetString(4) is { } digest)
            {
                list.Add(new RemoteDayRow(host, day, source, data, digest));
            }
        }

        return list;
    }

    public List<RemotePerfRow> RemotePerfRows()
    {
        var list = new List<RemotePerfRow>();
        using var rows = _connection.Prepare("SELECT host, day, source, model, data FROM remote_perf");
        while (rows.Step())
        {
            if (rows.GetString(0) is { } host && rows.GetString(1) is { } day && rows.GetString(2) is { } source
                && rows.GetString(3) is { } model && rows.GetString(4) is { } data)
            {
                list.Add(new RemotePerfRow(host, day, source, model, data));
            }
        }

        return list;
    }

    /// <summary>Moves complete days of a large batch to the staging tables; <paramref name="first"/> clears what a dropped batch left there.</summary>
    public void StageRemote(IReadOnlyList<RemoteBatchDay> days, string host, bool first) => _connection.Transaction(() =>
    {
        if (first)
        {
            ClearRemoteStaging(host);
        }

        WriteRemote(days, host, "temp.remote_stage_days", "temp.remote_stage_perf");
    });

    public void ClearRemoteStaging(string host)
    {
        foreach (var table in (string[])["temp.remote_stage_perf", "temp.remote_stage_days"])
        {
            using var statement = _connection.Prepare($"DELETE FROM {table} WHERE host = ?1");
            statement.Bind(1, host).Run();
        }
    }

    /// <summary>
    /// Commits one batch of a host in a single transaction (REMOTE 8.3): its staged days (when <paramref name="staged"/>), then
    /// <paramref name="days"/>, each replacing its row and its performance records; with <paramref name="replace"/>, the host's
    /// other rows go first. The host keeps <paramref name="name"/> (or its stored name when null) and <paramref name="zone"/>.
    /// The staging rows go either way.
    /// </summary>
    /// <exception cref="RemoteHistoryTooLargeException">The host would have more rows than <paramref name="limits"/>; nothing changed.</exception>
    public void CommitRemote(IReadOnlyList<RemoteBatchDay> days, string host, string? name, string zone, bool replace, bool staged,
        RemoteHistoryLimits limits)
    {
        try
        {
            _connection.Transaction(() =>
            {
                if (replace)
                {
                    DeleteRemoteRows(host);
                }

                if (staged)
                {
                    foreach (var sql in (string[])
                    [
                        "DELETE FROM remote_perf WHERE host = ?1 " +
                        "AND (day, source) IN (SELECT day, source FROM temp.remote_stage_days WHERE host = ?1)",
                        "INSERT OR REPLACE INTO remote_days SELECT * FROM temp.remote_stage_days WHERE host = ?1",
                        "INSERT OR REPLACE INTO remote_perf SELECT * FROM temp.remote_stage_perf WHERE host = ?1",
                    ])
                    {
                        using var statement = _connection.Prepare(sql);
                        statement.Bind(1, host).Run();
                    }
                }

                WriteRemote(days, host, "remote_days", "remote_perf");
                var dayRows = RemoteCount("remote_days", host);
                var perfRows = RemoteCount("remote_perf", host);
                if (dayRows > limits.Days || perfRows > limits.Perf)
                {
                    throw new RemoteHistoryTooLargeException();
                }

                if (dayRows == 0 && perfRows == 0)
                {
                    using var delete = _connection.Prepare("DELETE FROM remote_hosts WHERE id = ?1");
                    delete.Bind(1, host).Run();
                    return;
                }

                using var upsert = _connection.Prepare(
                    "INSERT INTO remote_hosts (id, name, zone) VALUES (?1, ?2, ?3) " +
                    "ON CONFLICT(id) DO UPDATE SET name = coalesce(excluded.name, remote_hosts.name), zone = excluded.zone");
                upsert.Bind(1, host).Bind(2, name).Bind(3, zone).Run();
            });
        }
        finally
        {
            try
            {
                ClearRemoteStaging(host);
            }
            catch (SqliteException)
            {
            }
        }
    }

    /// <summary>Sets a stored host's name; a host without rows is left absent.</summary>
    public void RenameRemoteHost(string id, string name)
    {
        using var statement = _connection.Prepare("UPDATE remote_hosts SET name = ?2 WHERE id = ?1");
        statement.Bind(1, id).Bind(2, name).Run();
    }

    /// <summary>Deletes every row of a host (REMOTE 2.4).</summary>
    public void DeleteRemoteHost(string id)
    {
        ClearRemoteStaging(id);
        _connection.Transaction(() =>
        {
            DeleteRemoteRows(id);
            using var statement = _connection.Prepare("DELETE FROM remote_hosts WHERE id = ?1");
            statement.Bind(1, id).Run();
        });
    }

    private void DeleteRemoteRows(string host)
    {
        foreach (var table in (string[])["remote_perf", "remote_days"])
        {
            using var statement = _connection.Prepare($"DELETE FROM {table} WHERE host = ?1");
            statement.Bind(1, host).Run();
        }
    }

    /// <summary>Each day replaces its row and its performance records in the given tables.</summary>
    private void WriteRemote(IReadOnlyList<RemoteBatchDay> batch, string host, string days, string perf)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var upsertDay = _connection.Prepare(
            $"INSERT OR REPLACE INTO {days} (host, day, source, data, digest) VALUES (?1, ?2, ?3, ?4, ?5)");
        using var clearPerf = _connection.Prepare($"DELETE FROM {perf} WHERE host = ?1 AND day = ?2 AND source = ?3");
        using var insertPerf = _connection.Prepare(
            $"INSERT OR REPLACE INTO {perf} (host, day, source, model, data) VALUES (?1, ?2, ?3, ?4, ?5)");
        foreach (var day in batch)
        {
            var source = SourceNames.Of(day.Source);
            upsertDay.Bind(1, host).Bind(2, day.Day).Bind(3, source).Bind(4, day.AggregateJson).Bind(5, day.Digest).Run();
            clearPerf.Bind(1, host).Bind(2, day.Day).Bind(3, source).Run();
            foreach (var record in day.Perf)
            {
                insertPerf.Bind(1, host).Bind(2, day.Day).Bind(3, source).Bind(4, record.Model).Bind(5, record.Json).Run();
            }
        }
    }

    private long RemoteCount(string table, string host)
    {
        using var statement = _connection.Prepare($"SELECT count(*) FROM {table} WHERE host = ?1");
        statement.Bind(1, host);
        return statement.Step() ? statement.GetInt64(0) : 0;
    }

    /// <summary>The side tables; <c>remote_hosts.zone</c> is added to a table created before it existed.</summary>
    private static void CreateRemoteTables(SqliteConnection connection)
    {
        connection.Execute(RemoteSchema);
        AddMissingColumns(connection, "remote_hosts", ("zone", "TEXT"));
    }
}
