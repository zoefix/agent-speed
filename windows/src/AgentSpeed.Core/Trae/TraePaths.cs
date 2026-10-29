namespace AgentSpeed.Core.Trae;

internal static class TraePaths
{
    public static IReadOnlyList<string> DefaultFolders
    {
        get
        {
            var basePath = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [.. new[] { "Trae", "Trae CN", "TRAE SOLO", "TRAE SOLO CN" }.Select(name => Path.Combine(basePath, name))];
        }
    }

    public static IReadOnlyList<string> Stores(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? DefaultFolders : [Path.GetFullPath(setting)];

    public static string DatabasePath(string folder)
    {
        if (folder.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || File.Exists(folder)) return folder;
        foreach (var relative in new[] { "database.db", "ai-agent/database.db", "ModularData/ai-agent/database.db", "ai-chat/database.db", "ModularData/ai-chat/database.db" })
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
        }
        return Path.Combine(folder, "ModularData", "ai-agent", "database.db");
    }
}
