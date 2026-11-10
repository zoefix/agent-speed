namespace AgentSpeed.Core.Qoder;

/// <summary>
/// Where Qoder CLI writes its transcripts (METRICS 2, 4.19): <c>&lt;config&gt;/projects</c> of each edition's config folder,
/// <c>$QODER_CONFIG_DIR</c> else <c>~/.qoder</c> (international), <c>$QODERCN_CONFIG_DIR</c> else <c>~/.qoder-cn</c> (China).
/// </summary>
internal static class QoderCliPaths
{
    public const string ProjectsFolder = "projects";

    /// <summary>The automatic roots of this process's environment, international edition first.</summary>
    public static IReadOnlyList<string> AutomaticRoots() => AutomaticRoots(
        Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Pure string rules of the given platform.</summary>
    /// <param name="environment">Reads one environment variable; null means unset.</param>
    public static IReadOnlyList<string> AutomaticRoots(Func<string, string?> environment, bool windows, string home) =>
    [
        Join(windows, environment("QODER_CONFIG_DIR") is { Length: > 0 } international ? international : Join(windows, home, ".qoder"), ProjectsFolder),
        Join(windows, environment("QODERCN_CONFIG_DIR") is { Length: > 0 } china ? china : Join(windows, home, ".qoder-cn"), ProjectsFolder),
    ];

    /// <summary>A user location, a config folder or its <c>projects</c> folder, read as that <c>projects</c> folder.</summary>
    public static string RootOf(string location) => RootOf(location, OperatingSystem.IsWindows());

    /// <summary>Pure string rules of the given platform; Windows compares the name case-insensitively, like its file system.</summary>
    public static string RootOf(string location, bool windows)
    {
        var trimmed = location.TrimEnd('/', '\\');
        var folder = trimmed.Length > 0 ? trimmed : location;
        var name = folder[(folder.LastIndexOfAny(['/', '\\']) + 1)..];
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(name, ProjectsFolder, comparison) ? folder : Join(windows, folder, ProjectsFolder);
    }

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
