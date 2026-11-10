using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.OpenClaw;
using AgentSpeed.Core.OpenCode;

namespace AgentSpeed.Core.Locations;

/// <summary>
/// The roots each entry of a list names (METRICS 2 table, 2.1): the automatic entry's roots from this process's environment,
/// and the root a user location names for its source.
/// </summary>
internal static class LocationRoots
{
    /// <summary>
    /// The automatic roots of a folder as read: Antigravity's store folders, WorkBuddy's <c>projects</c> folders, OpenCode's and
    /// ZCode's data folders, OpenClaw's state folders, Hermes Agent's home, Devin's CLI database, Qoder IDE's database of each
    /// edition, Qoder CLI's <c>projects</c> folder of each edition. Codex's agent-scoped OpenClaw
    /// homes are not included.
    /// </summary>
    public static IReadOnlyList<string> Automatic(LocationFolder folder) => folder switch
    {
        LocationFolder.Codex => [LiveMonitor.DefaultCodexRoot],
        LocationFolder.Claude => [LiveMonitor.DefaultClaudeRoot],
        LocationFolder.Dsh => [LiveMonitor.DefaultDshRoot],
        LocationFolder.OpenCode => [LiveMonitor.DefaultOpenCodeRoot],
        LocationFolder.Gemini => LiveMonitor.DefaultGeminiRoots,
        LocationFolder.Omp => LiveMonitor.DefaultOmpRoots,
        LocationFolder.Antigravity => LiveMonitor.AntigravityStores(null),
        LocationFolder.WorkBuddy => [Monitoring.SourceLocations.WorkBuddyRootOf(LiveMonitor.DefaultWorkBuddyFolder)],
        LocationFolder.WorkBuddyAi => [Monitoring.SourceLocations.WorkBuddyRootOf(LiveMonitor.DefaultWorkBuddyAiFolder)],
        LocationFolder.ZCode => [LiveMonitor.DefaultZCodeRoot],
        LocationFolder.Kimi => [HarnessPaths.SessionsOf(LiveMonitor.DefaultKimiRoot)],
        LocationFolder.GrokBuild => [HarnessPaths.SessionsOf(LiveMonitor.DefaultGrokBuildRoot)],
        LocationFolder.Pi => LiveMonitor.DefaultPiRoots,
        LocationFolder.OpenClaw => LiveMonitor.DefaultOpenClawRoots,
        LocationFolder.Hermes => [LiveMonitor.DefaultHermesRoot],
        LocationFolder.Droid => [LiveMonitor.DefaultDroidRoot],
        LocationFolder.Devin => [Devin.DevinPaths.AutomaticDatabase()],
        LocationFolder.Qoder => Qoder.QoderPaths.AutomaticDatabases(),
        LocationFolder.QoderCli => Qoder.QoderCliPaths.AutomaticRoots(),
        _ => [],
    };

    /// <summary>
    /// The root a user location names: an Antigravity store as given (its <c>conversations</c> folder is read), a WorkBuddy data
    /// folder's <c>projects</c>, a Kimi Code CLI or Grok Build data directory's <c>sessions</c>, a <c>.factory</c> folder's
    /// <c>sessions</c>, a Qoder CLI config folder's <c>projects</c>; any other location is the root itself.
    /// </summary>
    public static string User(LocationFolder folder, string path) => folder switch
    {
        LocationFolder.WorkBuddy or LocationFolder.WorkBuddyAi => Monitoring.SourceLocations.WorkBuddyRootOf(path),
        LocationFolder.Kimi or LocationFolder.GrokBuild => HarnessPaths.SessionsOf(path),
        LocationFolder.Droid => Droid.DroidPaths.RootOf(path),
        LocationFolder.QoderCli => Qoder.QoderCliPaths.RootOf(path),
        _ => path,
    };

    /// <summary>
    /// What a root of the folder names when compared (METRICS 2.1 Editing 2): an Antigravity store's <c>conversations</c>
    /// folder, OpenCode's and ZCode's database file, an OpenClaw agent folder's database; any other root itself.
    /// </summary>
    public static string Named(LocationFolder folder, string root) => folder switch
    {
        LocationFolder.Antigravity => AntigravityPaths.ConversationsOf(root),
        LocationFolder.OpenCode => OpenCodeDatabase.DatabasePath(root),
        LocationFolder.ZCode => HarnessPaths.ZCodeDatabase(root),
        LocationFolder.OpenClaw => OpenClawDatabaseIn(root) ?? root,
        _ => root,
    };

    private static string? OpenClawDatabaseIn(string folder)
    {
        var database = Path.Combine(folder, OpenClawPaths.DatabaseName);
        try
        {
            var info = new FileInfo(database);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 ? database : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
