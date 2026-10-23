namespace AgentSpeed.Core.Monitoring;

/// <summary>Resolved root folders of every source (METRICS 2); empty strings stand for "not available".</summary>
internal sealed record SourceLocations(
    string CodexRoot,
    string ClaudeRoot,
    string DshRoot,
    string OpenCodeRoot,
    IReadOnlyList<string> GeminiRoots,
    string AntigravityRoot = "",
    string AntigravityCliRoot = "")
{
    public static SourceLocations Resolve(MonitorConfiguration configuration) => new(
        Normalize(configuration.CodexRoot ?? LiveMonitor.DefaultCodexRoot),
        Normalize(configuration.ClaudeRoot ?? LiveMonitor.DefaultClaudeRoot),
        Normalize(configuration.DshRoot ?? LiveMonitor.DefaultDshRoot),
        Normalize(configuration.OpenCodeRoot ?? LiveMonitor.DefaultOpenCodeRoot),
        configuration.GeminiRoot is { } gemini ? [Normalize(gemini)] : [.. LiveMonitor.DefaultGeminiRoots.Select(Normalize)],
        Normalize(configuration.AntigravityRoot ?? LiveMonitor.DefaultAntigravityRoot),
        Normalize(configuration.AntigravityCliRoot ?? LiveMonitor.DefaultAntigravityCliRoot));

    public IEnumerable<(SourceKind Kind, string Path)> AntigravityRoots()
    {
        yield return (SourceKind.Antigravity, AntigravityRoot);
        yield return (SourceKind.AntigravityCli, AntigravityCliRoot);
    }

    /// <summary>Codex <c>archived_sessions</c>, the sibling of the sessions root (METRICS 8).</summary>
    public string CodexArchiveRoot
    {
        get
        {
            var trimmed = Path.TrimEndingDirectorySeparator(CodexRoot);
            var parent = trimmed.Length > 0 ? Path.GetDirectoryName(trimmed) : null;
            return string.IsNullOrEmpty(parent) ? string.Empty : Path.Combine(parent, "archived_sessions");
        }
    }

    /// <summary>Transcript roots in family order; Gemini may contribute several.</summary>
    public IEnumerable<(RootFamily Family, string Path)> FileRoots()
    {
        yield return (RootFamily.Codex, CodexRoot);
        yield return (RootFamily.Claude, ClaudeRoot);
        yield return (RootFamily.DeepSeek, DshRoot);
        foreach (var root in GeminiRoots)
        {
            yield return (RootFamily.Gemini, root);
        }
    }

    public static string Normalize(string? root)
    {
        try
        {
            return string.IsNullOrWhiteSpace(root) ? string.Empty : Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
