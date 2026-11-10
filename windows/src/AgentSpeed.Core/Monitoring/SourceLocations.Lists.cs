using System.Text;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// The union of a source's location list (METRICS 2.1): the positional root of a single-root family is its first root, these
/// properties hold the roots after it, in list order.
/// </summary>
internal sealed partial record SourceLocations
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings = new Dictionary<string, string>();

    public IReadOnlyList<string> MoreCodexRoots { get; init; } = [];

    public IReadOnlyList<string> MoreClaudeRoots { get; init; } = [];

    public IReadOnlyList<string> MoreDshRoots { get; init; } = [];

    /// <summary>OpenCode data folders (or database files) after the first, each naming another database.</summary>
    public IReadOnlyList<string> MoreOpenCodeRoots { get; init; } = [];

    public IReadOnlyList<string> MoreWorkBuddyRoots { get; init; } = [];

    public IReadOnlyList<string> MoreWorkBuddyAiRoots { get; init; } = [];

    /// <summary>ZCode data folders (or database files) after the first, each naming another database.</summary>
    public IReadOnlyList<string> MoreZCodeRoots { get; init; } = [];

    public IReadOnlyList<string> MoreKimiRoots { get; init; } = [];

    public IReadOnlyList<string> MoreGrokBuildRoots { get; init; } = [];

    public IReadOnlyList<string> MoreDroidRoots { get; init; } = [];

    /// <summary>Hermes homes or log folders the user added after the first root; each is a user location.</summary>
    public IReadOnlyList<string> MoreHermesRoots { get; init; } = [];

    /// <summary>OpenClaw's user locations; with the automatic entry they join the state folders found at every rediscovery.</summary>
    public IReadOnlyList<string> OpenClawUserRoots { get; init; } = [];

    /// <summary>Devin's roots in list order (METRICS 4.17): the automatic CLI database and the user locations, each once.</summary>
    public IReadOnlyList<Devin.DevinRoot> DevinRoots { get; init; } = [];

    /// <summary>Qoder IDE's roots in list order (METRICS 4.18): each edition's automatic database and the user locations, each once.</summary>
    public IReadOnlyList<Qoder.QoderRoot> QoderRoots { get; init; } = [];

    /// <summary>Qoder CLI's <c>projects</c> folders in list order (METRICS 4.19): both editions' automatic roots, then the user locations.</summary>
    public IReadOnlyList<string> QoderCliRoots { get; init; } = [];

    /// <summary>Gemini CLI's <c>settings.json</c> for each of its roots (EXECUTION-MODES); a root missing here takes the default rule.</summary>
    public IReadOnlyDictionary<string, string> GeminiSettings { get; init; } = NoSettings;

    /// <summary>One root per OpenCode database, in list order.</summary>
    public IReadOnlyList<string> OpenCodeRoots => NonEmpty([OpenCodeRoot, .. MoreOpenCodeRoots]);

    /// <summary>One root per ZCode database, in list order.</summary>
    public IReadOnlyList<string> ZCodeRoots => NonEmpty([ZCodeRoot, .. MoreZCodeRoots]);

    /// <summary>Hermes Agent's roots in list order: the automatic home (not chosen) or a user location (chosen).</summary>
    public IReadOnlyList<(string Path, bool Chosen)> HermesRoots =>
        HermesRoot.Length == 0 ? [] : [(HermesRoot, HermesChosen), .. MoreHermesRoots.Select(root => (root, true))];

    /// <summary>The Gemini CLI settings file of a root: the one its list entry names, else the automatic rule.</summary>
    public string GeminiSettingsOf(string root) =>
        GeminiSettings.TryGetValue(LocationPaths.Normalized(root), out var file) ? file : ConfiguredValues.GeminiSettingsPath(root);

    /// <summary>
    /// Everything these locations read, as one comparable text: two configurations whose lists name the same folders read the
    /// same and need no new reading.
    /// </summary>
    public string Signature()
    {
        var text = new StringBuilder();
        void Add(string label, IEnumerable<string> paths) => text.Append(label).Append('|').AppendJoin('|', paths).Append('\n');
        foreach (var group in FileRoots().GroupBy(root => root.Family))
        {
            Add(group.Key.ToString(), group.Select(root => LocationPaths.Normalized(root.Path)));
        }

        Add("opencode", OpenCodeRoots.Select(LocationPaths.Normalized));
        Add("zcode", ZCodeRoots.Select(LocationPaths.Normalized));
        Add("openclaw", (OpenClawRoots ?? []).Select(LocationPaths.Normalized));
        Add("hermes", HermesRoots.Select(root => (root.Chosen ? "+" : "-") + LocationPaths.Normalized(root.Path)));
        Add("gemini", GeminiRoots.Select(root => GeminiSettingsOf(root)));
        Add("devin", DevinRoots.Select(root => (root.IsDatabase ? "=" : "+") + root.Path));
        Add("qoder", QoderRoots.Select(root => (root.IsDatabase ? "=" : "+") + root.Path));
        text.Append(OmpAutomatic).Append(PiAutomatic).Append(OpenClawAutomatic).Append(CodexAutomatic);
        return text.ToString();
    }

    /// <summary>The agent-scoped Codex home of every agent database under these OpenClaw roots (METRICS 2, 4.14).</summary>
    public static IReadOnlyList<string> CodexHomesOf(IEnumerable<string> openClawRoots) =>
        OpenClaw.OpenClawPaths.CodexRoots(OpenClaw.OpenClawPaths.Databases(openClawRoots));

    private static string First(List<string> roots) => roots.Count > 0 ? roots[0] : string.Empty;

    private static IReadOnlyList<string> Rest(List<string> roots) => roots.Count > 1 ? roots[1..] : [];

    private static List<string> NonEmpty(IEnumerable<string> roots) => [.. roots.Where(root => root.Length > 0)];

    /// <summary>One root per database of a database source, in list order; roots naming the same database file are one.</summary>
    private static List<string> DatabaseRoots(MonitorConfiguration configuration, LocationFolder folder, Func<string, string> database) =>
        LocationPaths.UniqueDatabases(
            configuration.ListOf(folder).SelectMany(entry => entry.Path is { } path ? [path] : LocationRoots.Automatic(folder)), database);

    /// <summary>
    /// Devin's roots (METRICS 2, 4.17): the automatic entry's CLI database and every user location (a folder whose database is
    /// looked up at every rediscovery), each once.
    /// </summary>
    private static List<Devin.DevinRoot> DevinRootsOf(MonitorConfiguration configuration)
    {
        var roots = new List<Devin.DevinRoot>();
        foreach (var entry in configuration.ListOf(LocationFolder.Devin))
        {
            var root = entry.Path is { } user
                ? Devin.DevinRoot.Location(LocationPaths.Normalized(user))
                : Devin.DevinRoot.Database(LocationPaths.Normalized(LocationRoots.Automatic(LocationFolder.Devin)[0]));
            if (root.Path.Length > 0 && !roots.Exists(known => known.IsDatabase == root.IsDatabase && LocationPaths.Comparer.Equals(known.Path, root.Path)))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    /// <summary>
    /// Qoder IDE's roots (METRICS 2, 4.18): each edition's automatic database and every user location (a folder whose database is
    /// looked up at every rediscovery), each once.
    /// </summary>
    private static List<Qoder.QoderRoot> QoderRootsOf(MonitorConfiguration configuration)
    {
        var roots = new List<Qoder.QoderRoot>();
        foreach (var entry in configuration.ListOf(LocationFolder.Qoder))
        {
            IEnumerable<Qoder.QoderRoot> named = entry.Path is { } user
                ? [Qoder.QoderRoot.Location(LocationPaths.Normalized(user))]
                : LocationRoots.Automatic(LocationFolder.Qoder).Select(database => Qoder.QoderRoot.Database(LocationPaths.Normalized(database)));
            foreach (var root in named)
            {
                if (root.Path.Length > 0
                    && !roots.Exists(known => known.IsDatabase == root.IsDatabase && LocationPaths.Comparer.Equals(known.Path, root.Path)))
                {
                    roots.Add(root);
                }
            }
        }

        return roots;
    }

    /// <summary>Hermes Agent's roots: the automatic home and every user location, each path once (METRICS 2, 4.15).</summary>
    private static List<(string Path, bool Chosen)> HermesRootsOf(MonitorConfiguration configuration)
    {
        var seen = new HashSet<string>(LocationPaths.Comparer);
        var roots = new List<(string Path, bool Chosen)>();
        foreach (var entry in configuration.ListOf(LocationFolder.Hermes))
        {
            var (path, chosen) = entry.Path is { } user ? (user, true) : (LiveMonitor.DefaultHermesRoot, false);
            var normalized = LocationPaths.Normalized(path);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                roots.Add((normalized, chosen));
            }
        }

        return roots;
    }

    /// <summary>
    /// Each Gemini CLI root's <c>settings.json</c> (METRICS 2.1 Configured values): the automatic roots share
    /// <c>&lt;home&gt;/.gemini/settings.json</c>; a user location's file is beside its <c>tmp</c> folder, or inside the location
    /// when it has another name. A root named by two entries takes the first entry's file.
    /// </summary>
    private static Dictionary<string, string> GeminiSettingsOf(MonitorConfiguration configuration)
    {
        var settings = new Dictionary<string, string>(LocationPaths.Comparer);
        foreach (var entry in configuration.ListOf(LocationFolder.Gemini))
        {
            IReadOnlyList<string> roots = entry.Path is { } path ? [path] : LocationRoots.Automatic(LocationFolder.Gemini);
            var file = entry.Path is { } user
                ? UserGeminiSettings(user)
                : ConfiguredValues.GeminiSettingsPath(LiveMonitor.DefaultGeminiRoot);
            foreach (var root in roots.Select(LocationPaths.Normalized).Where(root => root.Length > 0))
            {
                settings.TryAdd(root, file);
            }
        }

        return settings;
    }

    private static string UserGeminiSettings(string location)
    {
        var folder = LocationPaths.Normalized(location);
        var home = string.Equals(Path.GetFileName(folder), "tmp", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(folder) ?? folder
            : folder;
        return Path.Combine(home, "settings.json");
    }

    /// <summary>Roots in order, without empty ones, a root named twice, or one resolving to an earlier root's folder.</summary>
    private static IEnumerable<string> Once(IEnumerable<string> roots)
    {
        var named = new HashSet<string>(LocationPaths.Comparer);
        var resolved = new HashSet<string>(LocationPaths.Comparer);
        foreach (var root in roots)
        {
            var normalized = LocationPaths.Normalized(root);
            if (normalized.Length > 0 && named.Add(normalized) && resolved.Add(LocationPaths.Resolved(normalized)))
            {
                yield return root;
            }
        }
    }
}
