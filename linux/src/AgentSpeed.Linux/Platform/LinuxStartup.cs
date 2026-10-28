namespace AgentSpeed.Linux.Platform;

internal static class LinuxStartup
{
    private static string FilePath => Path.Combine(Path.GetDirectoryName(LinuxPaths.Config)!, "autostart", "agent-speed.desktop");
    public static bool Enabled => File.Exists(FilePath);
    public static void Set(bool enabled)
    {
        if (!enabled) { File.Delete(FilePath); return; }
        var executable = Environment.ProcessPath ?? "/usr/bin/agent-speed";
        var args = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? Quote(typeof(LinuxStartup).Assembly.Location) + " " : "";
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var content = $"[Desktop Entry]\nType=Application\nName=Agent Speed\nExec={Quote(executable)} {args}--background\nIcon=agent-speed\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
        File.WriteAllText(FilePath + ".tmp", content); File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }
    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";
}
