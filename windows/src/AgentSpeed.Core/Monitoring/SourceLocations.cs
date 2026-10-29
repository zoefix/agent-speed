namespace AgentSpeed.Core.Monitoring;

/// <summary>Resolved root folders of every source (METRICS 2); empty strings stand for "not available".</summary>
/// <param name="OmpRoots">omp <c>sessions</c> folders: the default root and every named profile, or the one chosen.</param>
/// <param name="AntigravityStores">Antigravity store folders (or <c>conversations</c> folders); never a <c>*-backup</c> store.</param>
/// <param name="OmpAutomatic">The omp roots follow the profiles on disk (no folder chosen), so new profiles join later.</param>
/// <param name="WorkBuddyRoot">The WorkBuddy <c>projects</c> folder; empty when not available.</param>
/// <param name="WorkBuddyAiRoot">The WorkBuddy AI <c>projects</c> folder; empty when not available.</param>
/// <param name="PiRoots">Pi session folders: the default root and Pi's custom session folder, or the one chosen.</param>
/// <param name="PiAutomatic">The Pi roots follow Pi's settings (no folder chosen), so a custom session folder set later joins.</param>
/// <param name="OpenClawRoots">OpenClaw state folders (every automatic one), or the one chosen folder or database.</param>
/// <param name="OpenClawAutomatic">The OpenClaw roots follow the profiles on disk (nothing chosen), so new profiles join later.</param>
/// <param name="CodexAutomatic">
/// No Codex folder is chosen: the agent-scoped Codex home next to every OpenClaw agent database is a Codex root too
/// (METRICS 2, 4.14).
/// </param>
/// <param name="HermesRoot">The Hermes home (automatic), or the chosen home or log folder; empty when not available.</param>
/// <param name="HermesChosen">The Hermes root is the user setting, which replaces the automatic home (METRICS 4.15).</param>
/// <param name="DroidRoot">The Factory Droid sessions folder (automatic or from the setting); empty when not available.</param>
internal sealed record SourceLocations(
    string CodexRoot,
    string ClaudeRoot,
    string DshRoot,
    string OpenCodeRoot,
    IReadOnlyList<string> GeminiRoots,
    IReadOnlyList<string>? OmpRoots = null,
    IReadOnlyList<string>? AntigravityStores = null,
    bool OmpAutomatic = false,
    string WorkBuddyRoot = "",
    string WorkBuddyAiRoot = "",
    string ZCodeRoot = "",
    string KimiRoot = "",
    string GrokBuildRoot = "",
    IReadOnlyList<string>? PiRoots = null,
    bool PiAutomatic = false,
    IReadOnlyList<string>? OpenClawRoots = null,
    bool OpenClawAutomatic = false,
    bool CodexAutomatic = true,
    string HermesRoot = "",
    bool HermesChosen = false,
    string DroidRoot = "",
    IReadOnlyList<string>? TraeStores = null)
{
    public static SourceLocations Resolve(MonitorConfiguration configuration) => new(
        Normalize(configuration.CodexRoot ?? LiveMonitor.DefaultCodexRoot),
        Normalize(configuration.ClaudeRoot ?? LiveMonitor.DefaultClaudeRoot),
        Normalize(configuration.DshRoot ?? LiveMonitor.DefaultDshRoot),
        Normalize(configuration.OpenCodeRoot ?? LiveMonitor.DefaultOpenCodeRoot),
        configuration.GeminiRoot is { } gemini ? [Normalize(gemini)] : [.. LiveMonitor.DefaultGeminiRoots.Select(Normalize)],
        configuration.OmpRoot is { } omp ? [Normalize(omp)] : NormalizeAll(LiveMonitor.DefaultOmpRoots),
        NormalizeAll(LiveMonitor.AntigravityStores(configuration.AntigravityRoot)),
        OmpAutomatic: configuration.OmpRoot is null,
        WorkBuddyRoot: WorkBuddyRootOf(configuration.WorkBuddyRoot ?? LiveMonitor.DefaultWorkBuddyFolder),
        WorkBuddyAiRoot: WorkBuddyRootOf(configuration.WorkBuddyAiRoot ?? LiveMonitor.DefaultWorkBuddyAiFolder),
        ZCodeRoot: Normalize(configuration.ZCodeRoot ?? LiveMonitor.DefaultZCodeRoot),
        KimiRoot: Normalize(Harnesses.HarnessPaths.SessionsOf(configuration.KimiRoot ?? LiveMonitor.DefaultKimiRoot)),
        GrokBuildRoot: Normalize(Harnesses.HarnessPaths.SessionsOf(configuration.GrokBuildRoot ?? LiveMonitor.DefaultGrokBuildRoot)),
        PiRoots: configuration.PiRoot is { } pi ? [Normalize(pi)] : NormalizeAll(LiveMonitor.DefaultPiRoots),
        PiAutomatic: configuration.PiRoot is null,
        OpenClawRoots: configuration.OpenClawRoot is { } openClaw ? NormalizeAll([openClaw]) : NormalizeAll(LiveMonitor.DefaultOpenClawRoots),
        OpenClawAutomatic: configuration.OpenClawRoot is null,
        CodexAutomatic: configuration.CodexRoot is null,
        HermesRoot: Normalize(configuration.HermesRoot ?? LiveMonitor.DefaultHermesRoot),
        HermesChosen: configuration.HermesRoot is not null,
        DroidRoot: DroidRootOf(configuration.DroidRoot),
        TraeStores: NormalizeAll(Trae.TraePaths.Stores(configuration.TraeRoot)));

    /// <summary>The Factory Droid root for a setting: its <c>sessions</c> folder or the folder itself; null: the automatic root (METRICS 4.16).</summary>
    public static string DroidRootOf(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? Normalize(LiveMonitor.DefaultDroidRoot) : Normalize(Droid.DroidPaths.RootOf(Normalize(setting)));

    /// <summary>The Hermes log folders now: the home's <c>logs</c> and every profile's, or the chosen folder's (METRICS 4.15).</summary>
    public IReadOnlyList<string> HermesFolders() =>
        HermesRoot.Length == 0 ? [] : NormalizeAll(Hermes.HermesPaths.FoldersOf(HermesRoot, HermesChosen));

    /// <summary>Codex <c>archived_sessions</c>, the sibling of the sessions root (METRICS 8).</summary>
    public string CodexArchiveRoot => ArchiveRootOf(CodexRoot);

    /// <summary>The <c>archived_sessions</c> sibling of a Codex <c>sessions</c> root; empty when it has no parent.</summary>
    public static string ArchiveRootOf(string codexRoot)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(codexRoot);
        var parent = trimmed.Length > 0 ? Path.GetDirectoryName(trimmed) : null;
        return string.IsNullOrEmpty(parent) ? string.Empty : Path.Combine(parent, "archived_sessions");
    }

    /// <summary>
    /// The Codex roots OpenClaw adds while no Codex folder is chosen: <c>&lt;agent folder&gt;/codex-home/sessions</c> of every
    /// agent database found now (METRICS 2, 4.14), without the main Codex root.
    /// </summary>
    public IReadOnlyList<string> CodexHomeRoots() => CodexAutomatic
        ? [.. Distinct(OpenClaw.OpenClawPaths.CodexRoots(OpenClaw.OpenClawPaths.Databases(OpenClawRoots ?? [])).Select(Normalize))
            .Where(root => !string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(CodexRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))]
        : [];

    /// <summary>
    /// Transcript roots in family order; Codex (OpenClaw's agent-scoped Codex homes), Gemini, omp and Pi may contribute several, Antigravity one <c>conversations</c>
    /// folder per store, each WorkBuddy edition its <c>projects</c> folder, Factory Droid its sessions folder. A path listed twice within a family is returned
    /// once; an omp root that is a Pi root or lies inside one is Pi's only (METRICS 4.7).
    /// </summary>
    public IEnumerable<(RootFamily Family, string Path)> FileRoots()
    {
        var piRoots = Distinct(PiRoots ?? []).ToList();
        yield return (RootFamily.Codex, CodexRoot);
        foreach (var home in CodexHomeRoots())
        {
            yield return (RootFamily.Codex, home);
        }

        yield return (RootFamily.Claude, ClaudeRoot);
        yield return (RootFamily.DeepSeek, DshRoot);
        foreach (var root in GeminiRoots)
        {
            yield return (RootFamily.Gemini, root);
        }

        foreach (var root in Distinct(Pi.PiPaths.WithoutPi(OmpRoots ?? [], piRoots)))
        {
            yield return (RootFamily.Omp, root);
        }

        foreach (var store in Distinct((AntigravityStores ?? []).Select(Antigravity.AntigravityPaths.ConversationsOf)))
        {
            yield return (RootFamily.Antigravity, store);
        }

        if (WorkBuddyRoot.Length > 0)
        {
            yield return (RootFamily.WorkBuddy, WorkBuddyRoot);
        }

        if (WorkBuddyAiRoot.Length > 0)
        {
            yield return (RootFamily.WorkBuddyAi, WorkBuddyAiRoot);
        }

        if (KimiRoot.Length > 0)
        {
            yield return (RootFamily.Kimi, KimiRoot);
        }

        if (GrokBuildRoot.Length > 0)
        {
            yield return (RootFamily.GrokBuild, GrokBuildRoot);
        }

        foreach (var root in piRoots)
        {
            yield return (RootFamily.Pi, root);
        }

        if (DroidRoot.Length > 0)
        {
            yield return (RootFamily.Droid, DroidRoot);
        }
    }

    /// <summary>The <c>projects</c> folder for a WorkBuddy data folder or a chosen <c>projects</c> folder (METRICS 4.9).</summary>
    public static string WorkBuddyRootOf(string? dataOrProjects) =>
        string.IsNullOrWhiteSpace(dataOrProjects) ? string.Empty : Normalize(WorkBuddy.WorkBuddyPaths.ProjectsOf(dataOrProjects));

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

    internal static IReadOnlyList<string> NormalizeAll(IEnumerable<string> roots) =>
        [.. roots.Select(Normalize).Where(r => r.Length > 0)];

    private static IEnumerable<string> Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return paths.Where(p => p.Length > 0 && seen.Add(Path.TrimEndingDirectorySeparator(p)));
    }
}
