namespace AgentSpeed.Core.WorkBuddy;

/// <summary>
/// Where the standalone CodeBuddy Code CLI writes WorkBuddy's transcript format (METRICS 2, 4.20): <c>&lt;config&gt;/projects</c>,
/// with <c>&lt;config&gt;</c> = <c>$CODEBUDDY_CONFIG_DIR</c> when it is not blank (used as it is), else <c>~/.codebuddy</c>.
/// </summary>
internal static class CodeBuddyCodePaths
{
    public const string ConfigVariable = "CODEBUDDY_CONFIG_DIR";
    public const string SettingsFile = "settings.json";

    private const string DefaultFolder = ".codebuddy";

    /// <summary>The automatic root of this process's environment.</summary>
    public static string AutomaticRoot() => AutomaticRoot(
        Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Pure string rules of the given platform.</summary>
    /// <param name="environment">Reads one environment variable; null means unset.</param>
    public static string AutomaticRoot(Func<string, string?> environment, bool windows, string home) =>
        Join(windows, ConfigFolder(environment, windows, home), WorkBuddyPaths.ProjectsFolder);

    /// <summary>The config folder: the variable's value as it is when it is not blank (Python's whitespace), else <c>~/.codebuddy</c>.</summary>
    public static string ConfigFolder(Func<string, string?> environment, bool windows, string home) =>
        environment(ConfigVariable) is { } value && !IsBlank(value) ? value : Join(windows, home, DefaultFolder);

    /// <summary>A user location, a config folder or its <c>projects</c> folder, read as that <c>projects</c> folder.</summary>
    public static string RootOf(string location) => RootOf(location, OperatingSystem.IsWindows());

    /// <summary>Pure string rules of the given platform; Windows compares the name case-insensitively, like its file system.</summary>
    public static string RootOf(string location, bool windows)
    {
        var trimmed = location.TrimEnd('/', '\\');
        var folder = trimmed.Length > 0 ? trimmed : location;
        var name = folder[(folder.LastIndexOfAny(['/', '\\']) + 1)..];
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(name, WorkBuddyPaths.ProjectsFolder, comparison) ? folder : Join(windows, folder, WorkBuddyPaths.ProjectsFolder);
    }

    /// <summary>The configured values' file (EXECUTION-MODES): <c>settings.json</c> in the config folder that holds the root.</summary>
    public static string SettingsPath(string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return Path.Combine(Path.GetDirectoryName(trimmed) ?? trimmed, SettingsFile);
    }

    private static bool IsBlank(string value)
    {
        foreach (var c in value)
        {
            if (!WorkBuddyLineReader.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
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
