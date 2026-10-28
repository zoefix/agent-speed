namespace AgentSpeed.Linux.Platform;

internal static class LinuxPaths
{
    public static string Config => Resolve("XDG_CONFIG_HOME", ".config");
    public static string Data => Resolve("XDG_DATA_HOME", ".local/share");
    public static string Cache => Resolve("XDG_CACHE_HOME", ".cache");
    public static string History => Path.Combine(Data, "history.sqlite");
    public static string Settings => Path.Combine(Config, "settings.json");
    public static string Resolve(string variable, string fallback)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        return Path.Combine(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback), "agent-speed");
    }
    public static void EnsurePrivate(string directory)
    {
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
