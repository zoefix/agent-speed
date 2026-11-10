using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// Resolved root folders of every source (METRICS 2): the union of each location list (METRICS 2.1). A single-root parameter
/// is the family's first root, the roots after it are in <c>SourceLocations.Lists.cs</c>; empty strings stand for "none".
/// </summary>
/// <param name="OmpRoots">omp <c>sessions</c> folders: the default root and every named profile, then the user locations.</param>
/// <param name="AntigravityStores">Antigravity store folders (or <c>conversations</c> folders); never a <c>*-backup</c> store.</param>
/// <param name="OmpAutomatic">The list has the automatic entry, so profiles created later join.</param>
/// <param name="WorkBuddyRoot">The first WorkBuddy <c>projects</c> folder; empty when none.</param>
/// <param name="WorkBuddyAiRoot">The first WorkBuddy AI <c>projects</c> folder; empty when none.</param>
/// <param name="PiRoots">Pi session folders: the default root and Pi's custom session folder, then the user locations.</param>
/// <param name="PiAutomatic">The list has the automatic entry, so a custom session folder set later joins.</param>
/// <param name="OpenClawRoots">OpenClaw state folders (every automatic one), then the user locations (folders or databases).</param>
/// <param name="OpenClawAutomatic">The list has the automatic entry, so new profiles join later.</param>
/// <param name="CodexAutomatic">
/// The Codex list has the automatic entry: the agent-scoped Codex home next to every OpenClaw agent database is a Codex root too
/// (METRICS 2, 4.14).
/// </param>
/// <param name="HermesRoot">The first Hermes root: the automatic home, or a user location (a home or a log folder).</param>
/// <param name="HermesChosen">The first Hermes root is a user location (METRICS 4.15).</param>
/// <param name="DroidRoot">The first Factory Droid sessions folder; empty when none.</param>
internal sealed partial record SourceLocations(
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
    string DroidRoot = "")
{
    /// <summary>The roots of every source's location list, each list's union in list order (METRICS 2.1).</summary>
    public static SourceLocations Resolve(MonitorConfiguration configuration)
    {
        List<string> Roots(LocationFolder folder) => LocationPaths.Unique(configuration.ListOf(folder)
            .SelectMany(entry => entry.Path is { } path ? [LocationRoots.User(folder, path)] : LocationRoots.Automatic(folder)));
        bool HasAutomatic(LocationFolder folder) => configuration.ListOf(folder).Any(entry => entry.IsAutomatic);

        var codex = Roots(LocationFolder.Codex);
        var claude = Roots(LocationFolder.Claude);
        var dsh = Roots(LocationFolder.Dsh);
        var workBuddy = Roots(LocationFolder.WorkBuddy);
        var workBuddyAi = Roots(LocationFolder.WorkBuddyAi);
        var kimi = Roots(LocationFolder.Kimi);
        var grokBuild = Roots(LocationFolder.GrokBuild);
        var droid = Roots(LocationFolder.Droid);
        var openCode = DatabaseRoots(configuration, LocationFolder.OpenCode, OpenCode.OpenCodeDatabase.DatabasePath);
        var zcode = DatabaseRoots(configuration, LocationFolder.ZCode, Harnesses.HarnessPaths.ZCodeDatabase);
        var hermes = HermesRootsOf(configuration);
        var openClawUser = LocationPaths.Unique(configuration.ListOf(LocationFolder.OpenClaw).Select(e => e.Path).OfType<string>());
        return new SourceLocations(
            First(codex),
            First(claude),
            First(dsh),
            First(openCode),
            Roots(LocationFolder.Gemini),
            Roots(LocationFolder.Omp),
            [.. Roots(LocationFolder.Antigravity).Where(store => !Antigravity.AntigravityPaths.IsBackupStore(store))],
            OmpAutomatic: HasAutomatic(LocationFolder.Omp),
            WorkBuddyRoot: First(workBuddy),
            WorkBuddyAiRoot: First(workBuddyAi),
            ZCodeRoot: First(zcode),
            KimiRoot: First(kimi),
            GrokBuildRoot: First(grokBuild),
            PiRoots: Roots(LocationFolder.Pi),
            PiAutomatic: HasAutomatic(LocationFolder.Pi),
            OpenClawRoots: Roots(LocationFolder.OpenClaw),
            OpenClawAutomatic: HasAutomatic(LocationFolder.OpenClaw),
            CodexAutomatic: HasAutomatic(LocationFolder.Codex),
            HermesRoot: hermes.Count > 0 ? hermes[0].Path : string.Empty,
            HermesChosen: hermes.Count > 0 && hermes[0].Chosen,
            DroidRoot: First(droid))
        {
            MoreCodexRoots = Rest(codex),
            MoreClaudeRoots = Rest(claude),
            MoreDshRoots = Rest(dsh),
            MoreOpenCodeRoots = Rest(openCode),
            MoreWorkBuddyRoots = Rest(workBuddy),
            MoreWorkBuddyAiRoots = Rest(workBuddyAi),
            MoreZCodeRoots = Rest(zcode),
            MoreKimiRoots = Rest(kimi),
            MoreGrokBuildRoots = Rest(grokBuild),
            MoreDroidRoots = Rest(droid),
            MoreHermesRoots = [.. hermes.Skip(1).Select(root => root.Path)],
            OpenClawUserRoots = openClawUser,
            GeminiSettings = GeminiSettingsOf(configuration),
            DevinRoots = DevinRootsOf(configuration),
            QoderRoots = QoderRootsOf(configuration),
            QoderCliRoots = Roots(LocationFolder.QoderCli),
        };
    }

    /// <summary>The Factory Droid root for a setting: its <c>sessions</c> folder or the folder itself; null: the automatic root (METRICS 4.16).</summary>
    public static string DroidRootOf(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? Normalize(LiveMonitor.DefaultDroidRoot) : Normalize(Droid.DroidPaths.RootOf(Normalize(setting)));

    /// <summary>
    /// The Hermes log folders now, of every root in list order, each once: a home's <c>logs</c> and every profile's, a chosen
    /// folder's (METRICS 4.15, 2.1).
    /// </summary>
    public IReadOnlyList<string> HermesFolders() =>
        LocationPaths.Unique(HermesRoots.SelectMany(root => Hermes.HermesPaths.FoldersOf(root.Path, root.Chosen)));

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
    /// The Codex roots OpenClaw adds while the Codex list has its automatic entry: <c>&lt;agent folder&gt;/codex-home/sessions</c>
    /// of every agent database found now (METRICS 2, 4.14), without a root the list names already.
    /// </summary>
    public IReadOnlyList<string> CodexHomeRoots()
    {
        if (!CodexAutomatic)
        {
            return [];
        }

        var listed = new HashSet<string>(MoreCodexRoots.Prepend(CodexRoot).Select(LocationPaths.Normalized), LocationPaths.Comparer);
        return [.. CodexHomesOf(OpenClawRoots ?? []).Select(Normalize).Where(root => !listed.Contains(LocationPaths.Normalized(root)))];
    }

    /// <summary>
    /// Transcript roots in family order, each family's in list order (METRICS 2.1): Codex (with OpenClaw's agent-scoped Codex
    /// homes after the automatic root), Gemini, omp, Pi and Qoder CLI may have several, Antigravity one <c>conversations</c> folder per
    /// store, WorkBuddy its <c>projects</c> folders. A root named twice, or resolving to the folder of an earlier root of its
    /// family, is returned once; an omp root that is a Pi root or lies inside one is Pi's only (METRICS 4.7).
    /// </summary>
    public IEnumerable<(RootFamily Family, string Path)> FileRoots()
    {
        var piRoots = Distinct(PiRoots ?? []).ToList();
        IEnumerable<(RootFamily, IEnumerable<string>)> families =
        [
            (RootFamily.Codex, [CodexRoot, .. CodexHomeRoots(), .. MoreCodexRoots]),
            (RootFamily.Claude, [ClaudeRoot, .. MoreClaudeRoots]),
            (RootFamily.DeepSeek, [DshRoot, .. MoreDshRoots]),
            (RootFamily.Gemini, GeminiRoots),
            (RootFamily.Omp, Pi.PiPaths.WithoutPi(OmpRoots ?? [], piRoots)),
            (RootFamily.Antigravity, (AntigravityStores ?? []).Select(Antigravity.AntigravityPaths.ConversationsOf)),
            (RootFamily.WorkBuddy, [WorkBuddyRoot, .. MoreWorkBuddyRoots]),
            (RootFamily.WorkBuddyAi, [WorkBuddyAiRoot, .. MoreWorkBuddyAiRoots]),
            (RootFamily.Kimi, [KimiRoot, .. MoreKimiRoots]),
            (RootFamily.GrokBuild, [GrokBuildRoot, .. MoreGrokBuildRoots]),
            (RootFamily.Pi, piRoots),
            (RootFamily.Droid, [DroidRoot, .. MoreDroidRoots]),
            (RootFamily.QoderCli, QoderCliRoots),
        ];
        foreach (var (family, roots) in families)
        {
            foreach (var root in Once(roots))
            {
                yield return (family, root);
            }
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
