namespace AgentSpeed.Core.Locations;

/// <summary>Path rules of the location lists (METRICS 2.1): normalization, resolution and "named once".</summary>
internal static class LocationPaths
{
    /// <summary>Longest path a list keeps; anything longer is not a location this app can read.</summary>
    public const int MaxPathLength = 4096;

    private const int MaxLinkDepth = 16;

    private static readonly char[] Separators = ['/', '\\'];

    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>An absolute path without <c>.</c> or <c>..</c> segments and without a trailing separator; null otherwise.</summary>
    public static string? Standardized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.Contains('\0', StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A root as compared: full and without a trailing separator; empty for an empty or invalid path.</summary>
    public static string Normalized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The real path: every existing component that is a symbolic link or junction replaced by its target, so two paths that
    /// name the same folder compare equal; components that do not exist are kept as written.
    /// </summary>
    public static string Resolved(string? path) => Resolve(Normalized(path), 0);

    /// <summary>A regular file, never a symbolic link or junction (the folders above it may be links).</summary>
    public static bool IsRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Normalized paths in order, each once.</summary>
    public static List<string> Unique(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(Comparer);
        return [.. paths.Select(Normalized).Where(p => p.Length > 0 && seen.Add(p))];
    }

    /// <summary>Normalized roots in order, without a later root whose database resolves to the same file as an earlier one's.</summary>
    public static List<string> UniqueDatabases(IEnumerable<string> roots, Func<string, string> database)
    {
        var seen = new HashSet<string>(Comparer);
        return [.. roots.Select(Normalized).Where(root => root.Length > 0 && seen.Add(Resolved(database(root))))];
    }

    private static string Resolve(string path, int depth)
    {
        var root = path.Length > 0 ? Path.GetPathRoot(path) : null;
        if (string.IsNullOrEmpty(root) || depth > MaxLinkDepth)
        {
            return path;
        }

        var current = root;
        var parts = path[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            if (LinkTargetOf(next) is { } target)
            {
                next = Resolve(Normalized(Path.GetFullPath(target, current)), depth + 1);
            }
            else if (!Exists(next))
            {
                return Path.Combine([current, .. parts[i..]]);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string? LinkTargetOf(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return null;
            }

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return Path.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
