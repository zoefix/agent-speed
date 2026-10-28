namespace AgentSpeed.Presentation.Settings;

/// <summary>The folders a Settings → Data sources row names (DESIGN section 5).</summary>
public static class SourceFolders
{
    /// <summary>
    /// Antigravity: the chosen folder alone, else the default stores that exist, else the first default store, so the
    /// row always names a place to look.
    /// </summary>
    public static IReadOnlyList<string> Antigravity(string? setting, IReadOnlyList<string> defaults, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(exists);
        if (!string.IsNullOrWhiteSpace(setting))
        {
            return [setting];
        }

        var found = defaults.Where(exists).ToArray();
        return found.Length > 0 ? found : [.. defaults.Take(1)];
    }
}
