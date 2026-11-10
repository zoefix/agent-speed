namespace AgentSpeed.Core.Locations;

/// <summary>
/// A settings group with its own location list (METRICS 2.1): Claude Code and Claude Desktop share <see cref="Claude"/>,
/// every other source has its own. <see cref="LocationFolders.Key"/> names the list in the stored document.
/// </summary>
public enum LocationFolder
{
    Codex,
    Claude,
    Dsh,
    OpenCode,
    Gemini,
    Omp,
    Antigravity,
    WorkBuddy,
    WorkBuddyAi,
    ZCode,
    Kimi,
    GrokBuild,
    Pi,
    OpenClaw,
    Hermes,
    Droid,
    Devin,
    Qoder,
    QoderCli,
}

public static class LocationFolders
{
    private static readonly LocationFolder[] Values = Enum.GetValues<LocationFolder>();

    public static IReadOnlyList<LocationFolder> All => Values;

    /// <summary>The list's member name in the stored document (<c>codex</c>, <c>workbuddyAi</c>, <c>grokBuild</c>, …).</summary>
    public static string Key(this LocationFolder folder) => folder switch
    {
        LocationFolder.Codex => "codex",
        LocationFolder.Claude => "claude",
        LocationFolder.Dsh => "dsh",
        LocationFolder.OpenCode => "opencode",
        LocationFolder.Gemini => "gemini",
        LocationFolder.Omp => "omp",
        LocationFolder.Antigravity => "antigravity",
        LocationFolder.WorkBuddy => "workbuddy",
        LocationFolder.WorkBuddyAi => "workbuddyAi",
        LocationFolder.ZCode => "zcode",
        LocationFolder.Kimi => "kimi",
        LocationFolder.GrokBuild => "grokBuild",
        LocationFolder.Pi => "pi",
        LocationFolder.OpenClaw => "openclaw",
        LocationFolder.Hermes => "hermes",
        LocationFolder.Droid => "droid",
        LocationFolder.Devin => "devin",
        LocationFolder.Qoder => "qoder",
        LocationFolder.QoderCli => "qoderCli",
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    /// <summary>The single-folder setting of earlier versions (<c>sources.codexRoot</c>, …), read only to migrate.</summary>
    public static string LegacyKey(this LocationFolder folder) => folder.Key() + "Root";

    public static bool TryParse(string? key, out LocationFolder folder)
    {
        foreach (var candidate in Values)
        {
            if (string.Equals(candidate.Key(), key, StringComparison.Ordinal))
            {
                folder = candidate;
                return true;
            }
        }

        folder = default;
        return false;
    }

    /// <summary>The list a source reads.</summary>
    public static LocationFolder Of(SourceKind source) => source switch
    {
        SourceKind.Codex => LocationFolder.Codex,
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => LocationFolder.Claude,
        SourceKind.DeepSeek => LocationFolder.Dsh,
        SourceKind.OpenCode => LocationFolder.OpenCode,
        SourceKind.Gemini => LocationFolder.Gemini,
        SourceKind.Omp => LocationFolder.Omp,
        SourceKind.Antigravity => LocationFolder.Antigravity,
        SourceKind.WorkBuddy => LocationFolder.WorkBuddy,
        SourceKind.WorkBuddyAi => LocationFolder.WorkBuddyAi,
        SourceKind.ZCode => LocationFolder.ZCode,
        SourceKind.Kimi => LocationFolder.Kimi,
        SourceKind.GrokBuild => LocationFolder.GrokBuild,
        SourceKind.Pi => LocationFolder.Pi,
        SourceKind.OpenClaw => LocationFolder.OpenClaw,
        SourceKind.Hermes => LocationFolder.Hermes,
        SourceKind.Droid => LocationFolder.Droid,
        SourceKind.Devin => LocationFolder.Devin,
        SourceKind.Qoder => LocationFolder.Qoder,
        SourceKind.QoderCli => LocationFolder.QoderCli,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>The sources read through this list, in source order.</summary>
    public static IReadOnlyList<SourceKind> Sources(this LocationFolder folder) =>
        [.. Enum.GetValues<SourceKind>().Where(source => Of(source) == folder)];

    /// <summary>
    /// omp, Antigravity, Pi and OpenClaw: one chosen folder is narrower than the automatic set, so adding the default location
    /// adds it as a user location instead of bringing the automatic entry back (METRICS 2.1 Editing 4).
    /// </summary>
    public static bool AutomaticIsWiderThanDefault(this LocationFolder folder) =>
        folder is LocationFolder.Omp or LocationFolder.Antigravity or LocationFolder.Pi or LocationFolder.OpenClaw;

    /// <summary>The picker also accepts a database file: <c>openclaw-agent.sqlite</c> (DESIGN 5).</summary>
    public static bool AcceptsDatabaseFile(this LocationFolder folder) => folder == LocationFolder.OpenClaw;
}
