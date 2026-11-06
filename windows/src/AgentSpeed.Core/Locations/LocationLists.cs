using System.Collections.Immutable;

namespace AgentSpeed.Core.Locations;

/// <summary>
/// Every source's location list (METRICS 2.1), immutable. A folder without a list of its own reads <c>[automatic]</c>; a list
/// is 1 to 8 entries, each once, the automatic entry at most once and first, user locations absolute and normalized.
/// </summary>
public sealed partial class LocationLists : IEquatable<LocationLists>
{
    public const int MaxEntries = 8;

    private readonly ImmutableDictionary<LocationFolder, ImmutableArray<DataLocation>> _lists;

    /// <summary>Lists of folders this build does not know, as stored (raw JSON), so a newer version's choice survives.</summary>
    private readonly ImmutableSortedDictionary<string, string> _unknown;

    private LocationLists(ImmutableDictionary<LocationFolder, ImmutableArray<DataLocation>> lists, ImmutableSortedDictionary<string, string> unknown)
    {
        _lists = lists;
        _unknown = unknown;
    }

    public static IReadOnlyList<DataLocation> AutomaticOnly { get; } = ImmutableArray.Create(DataLocation.Automatic);

    /// <summary>Every folder reads its automatic roots only: a list never edited.</summary>
    public static LocationLists Automatic { get; } = new(
        ImmutableDictionary<LocationFolder, ImmutableArray<DataLocation>>.Empty,
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    public IReadOnlyList<DataLocation> this[LocationFolder folder] => _lists.TryGetValue(folder, out var list) ? list : AutomaticOnly;

    /// <summary>True when every known folder reads its automatic roots only.</summary>
    public bool IsAutomaticOnly => _lists.IsEmpty;

    /// <summary>
    /// The lists of the single-folder settings of earlier versions: a stored path becomes the list <c>[path]</c>, without the
    /// automatic entry, so exactly the same folders are read after upgrading; an empty or relative path is no choice.
    /// </summary>
    public static LocationLists FromLegacy(IEnumerable<KeyValuePair<LocationFolder, string>> paths)
    {
        var lists = Automatic;
        foreach (var (folder, path) in paths)
        {
            if (LocationPaths.Standardized(path) is { } standardized)
            {
                lists = lists.With(folder, [DataLocation.At(standardized)]);
            }
        }

        return lists;
    }

    /// <summary>A copy with <paramref name="folder"/>'s list replaced; the list is normalized first.</summary>
    public LocationLists With(LocationFolder folder, IEnumerable<DataLocation> list)
    {
        var normalized = Normalized(list);
        var lists = normalized.Length == 1 && normalized[0].IsAutomatic ? _lists.Remove(folder) : _lists.SetItem(folder, normalized);
        return ReferenceEquals(lists, _lists) ? this : new LocationLists(lists, _unknown);
    }

    /// <summary>
    /// The rules every stored or edited list follows: user locations absolute and normalized, each entry once, the automatic
    /// entry first when present, at most <see cref="MaxEntries"/>; a list left empty reads <c>[automatic]</c>.
    /// </summary>
    public static ImmutableArray<DataLocation> Normalized(IEnumerable<DataLocation> list)
    {
        var seen = new HashSet<string>(LocationPaths.Comparer);
        var automatic = false;
        var paths = new List<DataLocation>();
        foreach (var entry in list)
        {
            if (entry.IsAutomatic)
            {
                automatic = true;
            }
            else if (LocationPaths.Standardized(entry.Path) is { } path && seen.Add(path))
            {
                paths.Add(DataLocation.At(path));
            }
        }

        if (automatic)
        {
            paths.Insert(0, DataLocation.Automatic);
        }

        return paths.Count == 0 ? [DataLocation.Automatic] : [.. paths.Take(MaxEntries)];
    }

    public bool Equals(LocationLists? other) =>
        other is not null
        && _lists.Count == other._lists.Count
        && _lists.All(pair => other._lists.TryGetValue(pair.Key, out var list) && list.SequenceEqual(pair.Value))
        && _unknown.Count == other._unknown.Count
        && _unknown.All(pair => other._unknown.TryGetValue(pair.Key, out var raw) && string.Equals(raw, pair.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as LocationLists);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var folder in LocationFolders.All)
        {
            if (_lists.TryGetValue(folder, out var list))
            {
                hash.Add(folder);
                foreach (var entry in list)
                {
                    hash.Add(entry);
                }
            }
        }

        hash.Add(_unknown.Count);
        return hash.ToHashCode();
    }
}
