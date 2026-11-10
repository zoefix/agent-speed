using System.Reflection;

namespace AgentSpeed.App;

internal static class AppPaths
{
    public const string ProductFolder = "AgentSpeed";
    public const string RepositoryUrl = "https://github.com/zoefix/agent-speed";
    public const string LicenseUrl = "https://github.com/zoefix/agent-speed/blob/main/LICENSE";
    public const string ReleasesUrl = "https://github.com/zoefix/agent-speed/releases/latest";

    /// <summary>
    /// The settings file; the developer variable <c>AGENT_SPEED_SETTINGS_FILE</c> (a fully qualified local path) points a test
    /// session at its own file so measurements never touch the user's settings.
    /// </summary>
    public static string SettingsFile =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder, "settings.json");

    public static string LogsDirectory =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_LOGS_DIR") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder, "logs");

    /// <summary>
    /// The app's data folder <c>%LOCALAPPDATA%\AgentSpeed</c> (QUOTAS 5.1: the status line helper's copy and the quota
    /// files); <c>AGENT_SPEED_DATA_DIR</c> points a test session at its own folder.
    /// </summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_DATA_DIR") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    public static string? ExecutablePath => Environment.ProcessPath;

    public static string Version
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            }

            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }
}
