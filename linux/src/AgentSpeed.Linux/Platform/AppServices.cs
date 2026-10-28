using AgentSpeed.Linux.Platform;

namespace AgentSpeed.App;

internal static class AppPaths
{
    public const string ProductFolder = "agent-speed";
    public static string SettingsFile => LinuxPaths.Settings;
}

internal static class AppLog
{
    public static void Info(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
    public static void Warn(string message, Exception? error = null) => Info(message + (error is null ? "" : $" ({error.GetType().Name})"));
}
