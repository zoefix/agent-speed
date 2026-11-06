using System.Collections;
using System.Collections.Immutable;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Presentation.Settings.Locations;

/// <summary>
/// The single-folder values of earlier versions (<c>sources.codexRoot</c>, … <c>sources.droidRoot</c>) as loaded: read only to
/// migrate while <c>sourceLocations</c> is absent and written back unchanged (METRICS 2.1 Migration). Compared by value.
/// </summary>
public sealed class LegacyLocations : IEquatable<LegacyLocations>, IEnumerable<KeyValuePair<LocationFolder, string>>
{
    private readonly ImmutableSortedDictionary<LocationFolder, string> _paths;

    private LegacyLocations(ImmutableSortedDictionary<LocationFolder, string> paths)
    {
        _paths = paths;
    }

    public static LegacyLocations Empty { get; } = new(ImmutableSortedDictionary<LocationFolder, string>.Empty);

    public int Count => _paths.Count;

    public string? this[LocationFolder folder] => _paths.GetValueOrDefault(folder);

    public LegacyLocations With(LocationFolder folder, string path) => new(_paths.SetItem(folder, path));

    /// <summary>The values an earlier version itself kept: absolute paths of known folders.</summary>
    public LegacyLocations Cleaned()
    {
        var clean = _paths.Where(pair => Enum.IsDefined(pair.Key) && AppSettings.CleanPath(pair.Value) is not null)
            .ToImmutableSortedDictionary(pair => pair.Key, pair => AppSettings.CleanPath(pair.Value)!);
        return clean.Count == _paths.Count && clean.All(pair => _paths[pair.Key] == pair.Value) ? this : new LegacyLocations(clean);
    }

    public bool Equals(LegacyLocations? other) =>
        other is not null && _paths.Count == other._paths.Count
        && _paths.All(pair => other._paths.TryGetValue(pair.Key, out var path) && string.Equals(path, pair.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as LegacyLocations);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (folder, path) in _paths)
        {
            hash.Add(folder);
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<KeyValuePair<LocationFolder, string>> GetEnumerator() => _paths.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
