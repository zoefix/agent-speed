using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Devin;

internal sealed record DevinLocation(string DisplayPath, string? DatabasePath, IReadOnlyList<string> DesktopFolders)
{
    public bool Exists => DatabasePath is not null && File.Exists(DatabasePath) || DesktopFolders.Any(Directory.Exists);
}

internal static class DevinPaths
{
    public static DevinLocation Automatic()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var cliBase = !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(LiveMonitor.UserHome, ".local", "share");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(xdg)) cliBase = roaming;
        var desktop = OperatingSystem.IsMacOS()
            ? Path.Combine(LiveMonitor.UserHome, "Library", "Application Support", "Devin", "User", "acp-events")
            : Path.Combine(roaming, "Devin", "User", "acp-events");
        return new DevinLocation(Path.Combine(cliBase, "devin"), Path.Combine(cliBase, "devin", "cli", "sessions.db"), [desktop]);
    }

    public static DevinLocation Resolve(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return Automatic();
        var path = SourceLocations.Normalize(setting);
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return new DevinLocation(path, path, []);
        if (string.Equals(Path.GetFileName(path), "acp-events", StringComparison.OrdinalIgnoreCase))
            return new DevinLocation(path, null, [path]);
        var direct = Path.Combine(path, "sessions.db");
        var database = File.Exists(direct) ? direct : Path.Combine(path, "cli", "sessions.db");
        var desktop = Path.Combine(path, string.Equals(Path.GetFileName(path), "User", StringComparison.OrdinalIgnoreCase) ? "acp-events" : Path.Combine("User", "acp-events"));
        return new DevinLocation(path, database, [desktop]);
    }

    public static IReadOnlyList<string> DesktopFiles(DevinLocation location)
    {
        var result = new List<string>();
        foreach (var folder in location.DesktopFolders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                result.AddRange(Directory.EnumerateFiles(folder, "*.ndjson", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith('.', StringComparison.Ordinal)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public static string ReadAllText(string path)
    {
        using var handle = TranscriptTailer.Open(path);
        var length = RandomAccess.GetLength(handle);
        if (length > 256 * 1024 * 1024) throw new IOException("Devin ACP file is too large.");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read <= 0) break;
            offset += read;
        }
        return System.Text.Encoding.UTF8.GetString(bytes, 0, offset);
    }
}
