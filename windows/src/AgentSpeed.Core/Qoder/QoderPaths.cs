using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Qoder;

/// <summary>
/// One root of Qoder IDE's location list (METRICS 2.1, 4.18): an automatic entry's database (one per edition), or a user
/// location (a folder) whose database is looked up again at every rediscovery.
/// </summary>
internal readonly record struct QoderRoot(string Path, bool IsDatabase)
{
    public static QoderRoot Database(string path) => new(path, true);

    public static QoderRoot Location(string folder) => new(folder, false);

    /// <summary>The database this root names now, if it exists.</summary>
    public string? DatabaseNow => IsDatabase ? (QoderPaths.IsRegularFile(Path) ? Path : null) : QoderPaths.DatabaseOf(Path);

    /// <summary>
    /// What the root names for the editing rules (METRICS 2.1): the database that exists, else where the folder would keep it,
    /// resolved. A data folder, its <c>SharedClientCache</c> folder and its <c>cache/db</c> folder name one <c>local.db</c>.
    /// </summary>
    public string LocationKey => LocationPaths.Resolved(DatabaseNow ?? (IsDatabase ? Path : QoderPaths.ExpectedDatabase(Path)));

    /// <summary>The folder Settings and the source row name: an automatic database's IDE data folder, else the location itself.</summary>
    public string DisplayFolder => IsDatabase ? QoderPaths.DataFolderOf(Path) : Path;
}

/// <summary>
/// Where Qoder IDE's agent core keeps its chats (METRICS 2, 4.18): <c>&lt;data&gt;/&lt;edition&gt;/SharedClientCache/cache/db/local.db</c>
/// for the international (<c>Qoder</c>) and the China (<c>QoderCN</c>) edition.
/// </summary>
internal static class QoderPaths
{
    public const string DatabaseName = "local.db";
    public const string WalSuffix = "-wal";
    public const string SharedCacheFolder = "SharedClientCache";

    private const string CacheFolder = "cache";
    private const string DbFolder = "db";

    /// <summary>The editions' data folder names, international first.</summary>
    public static readonly string[] Editions = ["Qoder", "QoderCN"];

    /// <summary>The automatic entry's databases of this process's environment, international edition first.</summary>
    public static IReadOnlyList<string> AutomaticDatabases() => AutomaticDatabases(
        Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), UserHome);

    /// <summary>The IDE data folders the automatic entry stands for in Settings (DESIGN 5), international edition first.</summary>
    public static IReadOnlyList<string> AutomaticDataFolders() => [.. AutomaticDatabases().Select(DataFolderOf)];

    /// <summary>Each edition's <c>SharedClientCache/cache/db/local.db</c> below <c>&lt;data&gt;</c>. Pure string rules of the given platform.</summary>
    /// <param name="environment">Reads one environment variable; null means unset.</param>
    public static IReadOnlyList<string> AutomaticDatabases(Func<string, string?> environment, bool windows, bool macOS, string home)
    {
        var data = DataFolder(environment, windows, macOS, home);
        return [.. Editions.Select(edition => DatabaseBelow(windows, Join(windows, data, edition)))];
    }

    /// <summary>
    /// <c>&lt;data&gt;</c> of METRICS 4.18, where VS Code forks keep their user data folders: Windows <c>%APPDATA%</c>, macOS
    /// <c>~/Library/Application Support</c>, Linux <c>$XDG_CONFIG_HOME</c> when not empty, else <c>~/.config</c>.
    /// </summary>
    public static string DataFolder(Func<string, string?> environment, bool windows, bool macOS, string home)
    {
        if (windows)
        {
            return environment("APPDATA") is { Length: > 0 } appData ? appData : Join(true, Join(true, home, "AppData"), "Roaming");
        }

        if (macOS)
        {
            return Join(false, Join(false, home, "Library"), "Application Support");
        }

        return environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Join(false, home, ".config");
    }

    /// <summary>
    /// A user location's database: the first that exists of <c>local.db</c> in it, <c>cache/db/local.db</c> (the
    /// <c>SharedClientCache</c> folder) and <c>SharedClientCache/cache/db/local.db</c> (the IDE's data folder); null when none does.
    /// </summary>
    public static string? DatabaseOf(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        string[] candidates =
        [
            System.IO.Path.Combine(folder, DatabaseName),
            System.IO.Path.Combine(folder, CacheFolder, DbFolder, DatabaseName),
            System.IO.Path.Combine(folder, SharedCacheFolder, CacheFolder, DbFolder, DatabaseName),
        ];
        return candidates.FirstOrDefault(IsRegularFile);
    }

    /// <summary>
    /// Where a folder without a database would keep it, judged by its name: a <c>db</c> folder, a <c>SharedClientCache</c> folder or
    /// an IDE data folder.
    /// </summary>
    public static string ExpectedDatabase(string folder)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        return string.Equals(name, DbFolder, LocationPaths.Comparison) ? System.IO.Path.Combine(folder, DatabaseName)
            : string.Equals(name, SharedCacheFolder, LocationPaths.Comparison) ? System.IO.Path.Combine(folder, CacheFolder, DbFolder, DatabaseName)
            : System.IO.Path.Combine(folder, SharedCacheFolder, CacheFolder, DbFolder, DatabaseName);
    }

    /// <summary>The IDE data folder above <c>SharedClientCache/cache/db/local.db</c>; the database's own folder for any other path.</summary>
    public static string DataFolderOf(string database)
    {
        var db = Parent(database);
        var cache = Parent(db);
        var shared = Parent(cache);
        var data = Parent(shared);
        return Named(db, DbFolder) && Named(cache, CacheFolder) && Named(shared, SharedCacheFolder) && data.Length > 0 ? data : db;
    }

    /// <summary>
    /// The databases the roots name now, in list order, each once (resolved) and normalized, so a database keeps one path
    /// (its agent ids and history keys) however a root spelled its separators.
    /// </summary>
    public static List<string> Databases(IEnumerable<QoderRoot> roots)
    {
        var seen = new HashSet<string>(LocationPaths.Comparer);
        var databases = new List<string>();
        foreach (var root in roots)
        {
            if (root.DatabaseNow is { } database && LocationPaths.Normalized(database) is { Length: > 0 } normalized
                && seen.Add(LocationPaths.Resolved(normalized)))
            {
                databases.Add(normalized);
            }
        }

        return databases;
    }

    /// <summary>A regular file, never a symbolic link or junction (METRICS 4.18 <i>Database</i>).</summary>
    public static bool IsRegularFile(string path) => LocationPaths.IsRegularFile(path);

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DatabaseBelow(bool windows, string dataFolder) =>
        Join(windows, Join(windows, Join(windows, Join(windows, dataFolder, SharedCacheFolder), CacheFolder), DbFolder), DatabaseName);

    private static string Parent(string path) => path.Length == 0 ? string.Empty : System.IO.Path.GetDirectoryName(path) ?? string.Empty;

    private static bool Named(string path, string name) =>
        path.Length > 0 && string.Equals(System.IO.Path.GetFileName(path), name, LocationPaths.Comparison);

    /// <summary>Joins with the given platform's separator, whatever this process runs on.</summary>
    internal static string Join(bool windows, string parent, string child)
    {
        if (parent.Length == 0)
        {
            return child;
        }

        var separator = windows ? '\\' : '/';
        return parent[^1] is '/' or '\\' ? parent + child : parent + separator + child;
    }
}
