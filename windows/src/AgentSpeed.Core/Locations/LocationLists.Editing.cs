namespace AgentSpeed.Core.Locations;

/// <summary>What adding a location did (METRICS 2.1 Editing).</summary>
public enum LocationAddOutcome
{
    /// <summary>Appended as a user location.</summary>
    Added,

    /// <summary>The default location while the automatic entry was absent: the automatic entry is back, first.</summary>
    RestoredAutomatic,

    /// <summary>The same location is an entry already; nothing changed.</summary>
    AlreadyListed,

    /// <summary>The automatic entry reads this location already; nothing changed.</summary>
    AlreadyAutomatic,

    /// <summary>The list has <see cref="LocationLists.MaxEntries"/> entries; nothing changed.</summary>
    Full,

    /// <summary>Not an absolute path; nothing changed.</summary>
    Invalid,
}

/// <summary>Editing a list (METRICS 2.1, DESIGN 5): add, remove, reset, with the limits and the same-location rules.</summary>
public sealed partial class LocationLists
{
    public bool CanAdd(LocationFolder folder) => this[folder].Count < MaxEntries;

    /// <summary>The only remaining entry cannot be removed: turning a source off is its toggle's job.</summary>
    public bool CanRemove(LocationFolder folder) => this[folder].Count > 1;

    public bool CanReset(LocationFolder folder) => _lists.ContainsKey(folder);

    /// <summary>
    /// Adds a location the user picked, in the order of METRICS 2.1 Editing. <paramref name="automaticKeys"/> are the keys of what
    /// the automatic entry reads now, the default location's first; null derives them from this process's environment.
    /// </summary>
    public (LocationLists Lists, LocationAddOutcome Outcome) Add(string? path, LocationFolder folder, IReadOnlyList<string>? automaticKeys = null)
    {
        if (LocationPaths.Standardized(path) is not { } location)
        {
            return (this, LocationAddOutcome.Invalid);
        }

        var key = LocationKey(location, folder);
        var list = this[folder];
        if (list.Any(entry => entry.Path is { } listed && LocationPaths.Comparer.Equals(LocationKey(listed, folder), key)))
        {
            return (this, LocationAddOutcome.AlreadyListed);
        }

        var automatic = automaticKeys ?? AutomaticKeys(folder);
        if (list.Any(entry => entry.IsAutomatic))
        {
            if (automatic.Contains(key, LocationPaths.Comparer))
            {
                return (this, LocationAddOutcome.AlreadyAutomatic);
            }
        }
        else if (!folder.AutomaticIsWiderThanDefault() && automatic.Count > 0 && LocationPaths.Comparer.Equals(automatic[0], key))
        {
            return list.Count >= MaxEntries
                ? (this, LocationAddOutcome.Full)
                : (With(folder, [DataLocation.Automatic, .. list]), LocationAddOutcome.RestoredAutomatic);
        }

        return list.Count >= MaxEntries
            ? (this, LocationAddOutcome.Full)
            : (With(folder, [.. list, DataLocation.At(location)]), LocationAddOutcome.Added);
    }

    /// <summary>Removes an entry, unless it is the only one.</summary>
    public LocationLists Remove(DataLocation location, LocationFolder folder) =>
        CanRemove(folder) ? With(folder, this[folder].Where(entry => entry != location)) : this;

    /// <summary>Back to <c>[automatic]</c>.</summary>
    public LocationLists Reset(LocationFolder folder) => With(folder, AutomaticOnly);

    /// <summary>
    /// The root or database a location names, resolved: two locations are the same when their keys are equal (a WorkBuddy data
    /// folder and its <c>projects</c>, a <c>.factory</c> folder and its <c>sessions</c>, an OpenCode folder and its
    /// <c>opencode.db</c>, a Devin data folder and its <c>cli</c> folder, a Qoder IDE data folder, its <c>SharedClientCache</c> and
    /// its <c>cache/db</c> folder, a Qoder CLI config folder and its <c>projects</c>, …; a Hermes home and its <c>logs</c> folder differ).
    /// </summary>
    public static string LocationKey(string path, LocationFolder folder) => folder switch
    {
        LocationFolder.Devin => Devin.DevinRoot.Location(path).LocationKey,
        LocationFolder.Qoder => Qoder.QoderRoot.Location(path).LocationKey,
        _ => LocationPaths.Resolved(LocationRoots.Named(folder, LocationRoots.User(folder, path))),
    };

    /// <summary>
    /// The keys of what a folder's automatic entry reads now, the default location's first; Codex's include the agent-scoped
    /// Codex home of every OpenClaw agent database these lists read (METRICS 2, 4.14).
    /// </summary>
    public IReadOnlyList<string> AutomaticKeys(LocationFolder folder)
    {
        var roots = folder switch
        {
            LocationFolder.Devin => LocationRoots.Automatic(folder).Select(root => Devin.DevinRoot.Database(root).LocationKey),
            LocationFolder.Qoder => LocationRoots.Automatic(folder).Select(root => Qoder.QoderRoot.Database(root).LocationKey),
            _ => LocationRoots.Automatic(folder).Select(root => LocationRoots.Named(folder, root)),
        };
        if (folder == LocationFolder.Codex)
        {
            roots = roots.Concat(Monitoring.SourceLocations.CodexHomesOf(OpenClawRoots()));
        }

        var seen = new HashSet<string>(LocationPaths.Comparer);
        return [.. roots.Select(LocationPaths.Resolved).Where(key => key.Length > 0 && seen.Add(key))];
    }

    /// <summary>OpenClaw's roots under these lists: every automatic state folder and every user location, each once.</summary>
    internal IReadOnlyList<string> OpenClawRoots() => LocationPaths.Unique(this[LocationFolder.OpenClaw]
        .SelectMany(entry => entry.Path is { } path ? [path] : LocationRoots.Automatic(LocationFolder.OpenClaw)));
}
