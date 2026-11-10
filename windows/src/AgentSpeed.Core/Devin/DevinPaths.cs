using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Devin;

/// <summary>
/// One root of Devin's location list (METRICS 2.1, 4.17): the automatic entry's CLI database, or a user location (a folder)
/// whose database is looked up again at every rediscovery.
/// </summary>
internal readonly record struct DevinRoot(string Path, bool IsDatabase)
{
    public static DevinRoot Database(string path) => new(path, true);

    public static DevinRoot Location(string folder) => new(folder, false);

    /// <summary>The database this root names now, if it exists.</summary>
    public string? DatabaseNow => IsDatabase ? (DevinPaths.IsRegularFile(Path) ? Path : null) : DevinPaths.DatabaseOf(Path);

    /// <summary>
    /// What the root names for the editing rules (METRICS 2.1): the database that exists, else where a CLI folder keeps it,
    /// resolved.
    /// </summary>
    public string LocationKey => LocationPaths.Resolved(DatabaseNow ?? (IsDatabase ? Path : System.IO.Path.Combine(Path, DevinPaths.DatabaseName)));

    /// <summary>The folder Settings and the source row name: a database's folder, else the location itself.</summary>
    public string DisplayFolder => IsDatabase ? System.IO.Path.GetDirectoryName(Path) ?? Path : Path;
}

/// <summary>
/// Where the Devin CLI keeps its sessions (METRICS 2, 4.17): one SQLite database. Devin Desktop's own files are never read.
/// </summary>
internal static class DevinPaths
{
    public const string DatabaseName = "sessions.db";
    public const string WalSuffix = "-wal";
    private const string CliFolder = "cli";

    /// <summary>The automatic CLI database of this process's environment.</summary>
    public static string AutomaticDatabase() => AutomaticDatabase(
        Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// <c>CHISEL_SESSION_DB</c> when not empty; else Windows <c>%APPDATA%\devin\cli\sessions.db</c> (the CLI reads no
    /// <c>XDG_*</c> variable there), macOS and Linux <c>$XDG_DATA_HOME/devin/cli/sessions.db</c>, else
    /// <c>~/.local/share/devin/cli/sessions.db</c>. Pure string rules of the given platform.
    /// </summary>
    /// <param name="environment">Reads one environment variable; null means unset.</param>
    public static string AutomaticDatabase(Func<string, string?> environment, bool windows, string home)
    {
        if (environment("CHISEL_SESSION_DB") is { Length: > 0 } overridden)
        {
            return overridden;
        }

        string data;
        if (windows)
        {
            data = environment("APPDATA") is { Length: > 0 } appData ? appData : Join(windows, Join(windows, home, "AppData"), "Roaming");
        }
        else
        {
            data = environment("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Join(windows, Join(windows, home, ".local"), "share");
        }

        return Join(windows, Join(windows, Join(windows, data, "devin"), CliFolder), DatabaseName);
    }

    /// <summary>A user location's database: <c>sessions.db</c> in it, else in its <c>cli</c> folder; null when neither exists.</summary>
    public static string? DatabaseOf(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var direct = Path.Combine(folder, DatabaseName);
        if (IsRegularFile(direct))
        {
            return direct;
        }

        var nested = Path.Combine(folder, CliFolder, DatabaseName);
        return IsRegularFile(nested) ? nested : null;
    }

    /// <summary>The databases the roots name now, in list order, each once (resolved).</summary>
    public static List<string> Databases(IEnumerable<DevinRoot> roots)
    {
        var seen = new HashSet<string>(LocationPaths.Comparer);
        var databases = new List<string>();
        foreach (var root in roots)
        {
            if (root.DatabaseNow is { } database && seen.Add(LocationPaths.Resolved(database)))
            {
                databases.Add(database);
            }
        }

        return databases;
    }

    /// <summary>A regular file, never a symbolic link or junction (the folders above it may be links).</summary>
    public static bool IsRegularFile(string path) => LocationPaths.IsRegularFile(path);

    private static string Join(bool windows, string parent, string child)
    {
        if (parent.Length == 0)
        {
            return child;
        }

        var separator = windows ? '\\' : '/';
        return parent[^1] is '/' or '\\' ? parent + child : parent + separator + child;
    }
}
