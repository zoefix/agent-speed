using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.History;

/// <summary>What the store keeps for one analysed file (or OpenCode session): its identity and per-day contributions.</summary>
internal sealed class StoredFile
{
    /// <summary>Row id in the store; 0 until saved.</summary>
    public long Id { get; set; }

    public required string Path { get; init; }

    public required string Family { get; init; }

    public long Size { get; init; }

    /// <summary>Modification time in ticks (files) or Unix milliseconds (OpenCode sessions).</summary>
    public long Modified { get; init; }

    public int Version { get; init; }

    public required Dictionary<DayKey, DayAggregate> Days { get; init; }
}

/// <summary>
/// The persistent daily-history store (METRICS 8): per file its identity, the 64-bit hashes of the response keys it
/// owns and its per-day contributions, plus live day peaks. Only aggregates; never keys in plain text or content.
/// One owner thread.
/// </summary>
internal sealed class HistoryStore : IDisposable
{
    public const int SchemaVersion = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            family TEXT NOT NULL,
            size INTEGER NOT NULL,
            modified INTEGER NOT NULL,
            version INTEGER NOT NULL,
            keys BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS contributions (
            file_id INTEGER NOT NULL,
            day TEXT NOT NULL,
            source TEXT NOT NULL,
            requests INTEGER NOT NULL,
            input INTEGER NOT NULL,
            cached_input INTEGER NOT NULL,
            cache_write_input INTEGER NOT NULL,
            output INTEGER NOT NULL,
            reasoning INTEGER NOT NULL,
            measured_output INTEGER NOT NULL,
            measured_ms INTEGER NOT NULL,
            unfinalized INTEGER NOT NULL,
            agents INTEGER NOT NULL,
            PRIMARY KEY (file_id, day, source)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS contribution_models (
            file_id INTEGER NOT NULL,
            day TEXT NOT NULL,
            source TEXT NOT NULL,
            model TEXT NOT NULL,
            requests INTEGER NOT NULL,
            output INTEGER NOT NULL,
            PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS day_peaks (day TEXT PRIMARY KEY, peak REAL NOT NULL) WITHOUT ROWID;
        """;

    private readonly SqliteConnection _connection;

    private HistoryStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public string Path => _connection.Path;

    /// <summary>Opens (creating when needed) the store; a corrupt store is deleted and created again.</summary>
    /// <exception cref="SqliteException">The store cannot be opened (or was written by a newer schema).</exception>
    public static HistoryStore Open(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return OpenOnce(path);
        }
        catch (SqliteException e) when (e.IsCorrupt)
        {
            Delete(path);
            return OpenOnce(path);
        }
    }

    /// <summary>Removes the store and its WAL files.</summary>
    public static void Delete(string path)
    {
        foreach (var suffix in (string[])["", "-wal", "-shm", "-journal"])
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (suffix.Length == 0)
                {
                    throw;
                }
            }
        }
    }

    public string? Meta(string key)
    {
        using var statement = _connection.Prepare("SELECT value FROM meta WHERE key = ?1");
        statement.Bind(1, key);
        return statement.Step() ? statement.GetString(0) : null;
    }

    public void SetMeta(string key, string value)
    {
        using var statement = _connection.Prepare("INSERT OR REPLACE INTO meta (key, value) VALUES (?1, ?2)");
        statement.Bind(1, key).Bind(2, value).Run();
    }

    /// <summary>Every stored file with its contributions, by path.</summary>
    public Dictionary<string, StoredFile> LoadFiles(StringComparer pathComparer)
    {
        var byId = new Dictionary<long, StoredFile>();
        using (var files = _connection.Prepare("SELECT id, path, family, size, modified, version FROM files"))
        {
            while (files.Step())
            {
                var entry = new StoredFile
                {
                    Id = files.GetInt64(0),
                    Path = files.GetString(1) ?? string.Empty,
                    Family = files.GetString(2) ?? string.Empty,
                    Size = files.GetInt64(3),
                    Modified = files.GetInt64(4),
                    Version = (int)files.GetInt64(5),
                    Days = [],
                };
                byId[entry.Id] = entry;
            }
        }

        using (var days = _connection.Prepare(
            "SELECT file_id, day, source, requests, input, cached_input, cache_write_input, output, reasoning, " +
            "measured_output, measured_ms, unfinalized, agents FROM contributions"))
        {
            while (days.Step())
            {
                if (!byId.TryGetValue(days.GetInt64(0), out var entry) || Key(days.GetString(1), days.GetString(2)) is not { } key)
                {
                    continue;
                }

                entry.Days[key] = new DayAggregate
                {
                    Requests = days.GetInt64(3),
                    Input = days.GetInt64(4),
                    CachedInput = days.GetInt64(5),
                    CacheWriteInput = days.GetInt64(6),
                    Output = days.GetInt64(7),
                    Reasoning = days.GetInt64(8),
                    MeasuredOutput = days.GetInt64(9),
                    MeasuredMs = days.GetInt64(10),
                    Unfinalized = days.GetInt64(11),
                    Agents = (int)days.GetInt64(12),
                };
            }
        }

        using (var models = _connection.Prepare(
            "SELECT file_id, day, source, model, requests, output FROM contribution_models"))
        {
            while (models.Step())
            {
                if (byId.TryGetValue(models.GetInt64(0), out var entry)
                    && Key(models.GetString(1), models.GetString(2)) is { } key
                    && entry.Days.TryGetValue(key, out var day))
                {
                    day.Models[models.GetString(3) ?? string.Empty] = (models.GetInt64(4), models.GetInt64(5));
                }
            }
        }

        var result = new Dictionary<string, StoredFile>(byId.Count, pathComparer);
        foreach (var entry in byId.Values)
        {
            result[entry.Path] = entry;
        }

        return result;
    }

    /// <summary>Settles the keys of every stored file except <paramref name="excludedIds"/> (they are analysed again).</summary>
    public void LoadKeys(Parsing.KeyLedger ledger, IReadOnlySet<long> excludedIds)
    {
        using var statement = _connection.Prepare("SELECT id, keys FROM files");
        while (statement.Step())
        {
            if (!excludedIds.Contains(statement.GetInt64(0)))
            {
                SettleBlob(ledger, statement.GetBlob(1));
            }
        }
    }

    public void LoadKeys(Parsing.KeyLedger ledger, long fileId)
    {
        using var statement = _connection.Prepare("SELECT keys FROM files WHERE id = ?1");
        statement.Bind(1, fileId);
        if (statement.Step())
        {
            SettleBlob(ledger, statement.GetBlob(0));
        }
    }

    /// <summary>Writes files with their owned key hashes and contributions in one transaction; assigns new ids.</summary>
    public void Save(IReadOnlyList<(StoredFile Entry, ulong[] Keys)> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var insertFile = _connection.Prepare(
            "INSERT INTO files (path, family, size, modified, version, keys) VALUES (?1, ?2, ?3, ?4, ?5, ?6)");
        using var updateFile = _connection.Prepare(
            "UPDATE files SET family = ?2, size = ?3, modified = ?4, version = ?5, keys = ?6 WHERE id = ?1");
        using var findFile = _connection.Prepare("SELECT id FROM files WHERE path = ?1");
        using var deleteDays = _connection.Prepare("DELETE FROM contributions WHERE file_id = ?1");
        using var deleteModels = _connection.Prepare("DELETE FROM contribution_models WHERE file_id = ?1");
        using var insertDay = _connection.Prepare(
            "INSERT INTO contributions (file_id, day, source, requests, input, cached_input, cache_write_input, output, " +
            "reasoning, measured_output, measured_ms, unfinalized, agents) " +
            "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)");
        using var insertModel = _connection.Prepare(
            "INSERT INTO contribution_models (file_id, day, source, model, requests, output) VALUES (?1, ?2, ?3, ?4, ?5, ?6)");
        _connection.Transaction(() =>
        {
            foreach (var (entry, keys) in batch)
            {
                var blob = KeyBlob(keys);
                if (entry.Id == 0)
                {
                    findFile.Bind(1, entry.Path);
                    entry.Id = findFile.Step() ? findFile.GetInt64(0) : 0;
                    findFile.Reset();
                }

                var write = entry.Id == 0 ? insertFile.Bind(1, entry.Path) : updateFile.Bind(1, entry.Id);
                write.Bind(2, entry.Family).Bind(3, entry.Size).Bind(4, entry.Modified).Bind(5, entry.Version).BindBlob(6, blob).Run();
                if (entry.Id == 0)
                {
                    entry.Id = _connection.LastInsertRowId;
                }

                deleteDays.Bind(1, entry.Id).Run();
                deleteModels.Bind(1, entry.Id).Run();
                foreach (var (key, day) in entry.Days)
                {
                    var source = SourceNames.Of(key.Source);
                    insertDay.Bind(1, entry.Id).Bind(2, key.Day).Bind(3, source)
                        .Bind(4, day.Requests).Bind(5, day.Input).Bind(6, day.CachedInput).Bind(7, day.CacheWriteInput)
                        .Bind(8, day.Output).Bind(9, day.Reasoning).Bind(10, day.MeasuredOutput).Bind(11, day.MeasuredMs)
                        .Bind(12, day.Unfinalized).Bind(13, day.Agents).Run();
                    foreach (var (model, usage) in day.Models)
                    {
                        insertModel.Bind(1, entry.Id).Bind(2, key.Day).Bind(3, source).Bind(4, model)
                            .Bind(5, usage.Requests).Bind(6, usage.Output).Run();
                    }
                }
            }
        });
    }

    public Dictionary<string, double> Peaks()
    {
        using var statement = _connection.Prepare("SELECT day, peak FROM day_peaks");
        var peaks = new Dictionary<string, double>(StringComparer.Ordinal);
        while (statement.Step())
        {
            if (statement.GetString(0) is { } day)
            {
                peaks[day] = statement.GetDouble(1);
            }
        }

        return peaks;
    }

    public void SavePeaks(IReadOnlyDictionary<string, double> peaks)
    {
        if (peaks.Count == 0)
        {
            return;
        }

        using var statement = _connection.Prepare("INSERT OR REPLACE INTO day_peaks (day, peak) VALUES (?1, ?2)");
        _connection.Transaction(() =>
        {
            foreach (var (day, peak) in peaks)
            {
                statement.Bind(1, day).Bind(2, peak).Run();
            }
        });
    }

    public void Dispose() => _connection.Dispose();

    private static HistoryStore OpenOnce(string path)
    {
        var connection = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            connection.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
            var version = connection.ScalarInt64("PRAGMA user_version") ?? 0;
            if (version > SchemaVersion)
            {
                throw new SqliteException(SqliteNative.Error, "The history store was written by a newer version.");
            }

            if (version < SchemaVersion)
            {
                connection.Transaction(() =>
                {
                    connection.Execute(Schema);
                    connection.Execute($"PRAGMA user_version = {SchemaVersion}");
                });
            }

            return new HistoryStore(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static DayKey? Key(string? day, string? source) =>
        day is not null && SourceNames.Parse(source) is { } kind ? new DayKey(day, kind) : null;

    private static byte[] KeyBlob(ulong[] keys)
    {
        var blob = new byte[keys.Length * sizeof(ulong)];
        for (var i = 0; i < keys.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(i * sizeof(ulong)), keys[i]);
        }

        return blob;
    }

    private static void SettleBlob(Parsing.KeyLedger ledger, ReadOnlySpan<byte> blob)
    {
        var count = blob.Length / sizeof(ulong);
        if (count == 0)
        {
            return;
        }

        if (BitConverter.IsLittleEndian)
        {
            ledger.Settle(MemoryMarshal.Cast<byte, ulong>(blob[..(count * sizeof(ulong))]));
            return;
        }

        var keys = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(blob[(i * sizeof(ulong))..]);
        }

        ledger.Settle(keys);
    }
}

/// <summary>Stable source names, as in METRICS 1 and the dumps.</summary>
internal static class SourceNames
{
    public static string Of(SourceKind source) => source switch
    {
        SourceKind.Codex => "codex",
        SourceKind.ClaudeCode => "claudeCode",
        SourceKind.ClaudeDesktop => "claudeDesktop",
        SourceKind.DeepSeek => "deepseek",
        SourceKind.OpenCode => "opencode",
        SourceKind.Gemini => "gemini",
        SourceKind.Antigravity => "antigravity",
        SourceKind.AntigravityCli => "antigravityCli",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static SourceKind? Parse(string? name) => name switch
    {
        "codex" => SourceKind.Codex,
        "claudeCode" => SourceKind.ClaudeCode,
        "claudeDesktop" => SourceKind.ClaudeDesktop,
        "deepseek" => SourceKind.DeepSeek,
        "opencode" => SourceKind.OpenCode,
        "gemini" => SourceKind.Gemini,
        "antigravity" => SourceKind.Antigravity,
        "antigravityCli" => SourceKind.AntigravityCli,
        _ => null,
    };
}
