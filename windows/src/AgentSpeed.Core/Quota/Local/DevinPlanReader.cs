using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// Devin Desktop's cached plan (QUOTAS 9.3): the newest <c>Devin</c> or <c>Windsurf</c> folder with
/// <c>User/globalStorage/state.vscdb</c>, queried read-only, timed by the newest launch folder under <c>logs</c>. Not
/// thread-safe.
/// </summary>
public sealed class DevinPlanReader(IReadOnlyList<string> folders)
{
    private (string Folder, long Size, DateTime Modified, DateTime Logs)? _stamp;

    public IReadOnlyList<string> Folders { get; } = folders;

    public QuotaSnapshot? Snapshot { get; private set; }

    /// <summary><c>%APPDATA%\Devin</c> and <c>%APPDATA%\Windsurf</c>.</summary>
    public static IReadOnlyList<string> DefaultFolders(string appData) => [Path.Combine(appData, "Devin"), Path.Combine(appData, "Windsurf")];

    public static string Database(string folder) => Path.Combine(folder, "User", "globalStorage", "state.vscdb");

    /// <summary>True when the snapshot changed.</summary>
    public bool Refresh(TimeZoneInfo zone)
    {
        var found = Folders.Select(folder => (Folder: folder, Info: new FileInfo(Database(folder)))).Where(f => f.Info.Exists).ToList();
        if (found.Count == 0)
        {
            _stamp = null;
            return false;
        }

        var (folder, info) = found.MaxBy(f => f.Info.LastWriteTimeUtc);
        var logs = Path.Combine(folder, "logs");
        var stamp = (folder, info.Length, info.LastWriteTimeUtc, Directory.Exists(logs) ? Directory.GetLastWriteTimeUtc(logs) : default);
        if (_stamp == stamp)
        {
            return false;
        }

        _stamp = stamp;
        if (NewestLaunch(folder, zone) is not { } observed)
        {
            return false;
        }

        var next = new QuotaSnapshot(SourceKind.Devin, observed, DevinPlan.Windows(Rows(Database(folder))));
        if (next.Equals(Snapshot))
        {
            return false;
        }

        Snapshot = next;
        return true;
    }

    public static long? NewestLaunch(string folder, TimeZoneInfo zone)
    {
        try
        {
            return Directory.EnumerateDirectories(Path.Combine(folder, "logs")).Take(4096)
                .Select(path => DevinPlan.LaunchMs(Path.GetFileName(path), zone)).Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The plan rows' values: <c>cachedPlanInfoData</c> keys, else the older <c>cachedPlanInfo</c> ones.</summary>
    public static IReadOnlyList<ReadOnlyMemory<byte>> Rows(string database)
    {
        try
        {
            using var connection = SqliteReadOnly.Open(database, out _);
            foreach (var pattern in DevinPlan.KeyPatterns)
            {
                using var statement = connection.Prepare("SELECT value FROM ItemTable WHERE key LIKE ?1");
                statement.Bind(1, pattern);
                var rows = new List<ReadOnlyMemory<byte>>();
                while (rows.Count < 64 && statement.Step())
                {
                    rows.Add(statement.ColumnType(0) == SqliteNative.ColumnText ? statement.GetUtf8(0).ToArray() : statement.GetBlob(0).ToArray());
                }

                if (rows.Count > 0)
                {
                    return rows;
                }
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // An unreadable database has no plan.
        }

        return [];
    }
}
