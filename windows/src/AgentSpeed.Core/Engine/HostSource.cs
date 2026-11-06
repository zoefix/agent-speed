using System.Collections.Immutable;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core;

/// <summary>
/// One host and one source: the cell that live values are kept in for host subsets (DESIGN 13, METRICS 6.8 evaluated over
/// (host, source) cells).
/// </summary>
/// <param name="Host"><c>local</c>, or a remote host's id.</param>
public readonly record struct HostSource(string Host, SourceKind Source)
{
    public static HostSource Local(SourceKind source) => new(RemoteHost.LocalId, source);

    public bool IsLocal => Host == RemoteHost.LocalId;
}

/// <summary>
/// A set of sources on a set of hosts (null = every host): the key of <see cref="SpeedSnapshot.HostSubsetPeaks"/> and of the
/// subsets <see cref="LiveMonitor.SetSubsets(IReadOnlyCollection{SubsetKey})"/> requests. With every host it is exactly the
/// source set of METRICS 6.8. Hosts compare as a set.
/// </summary>
public readonly struct SubsetKey : IEquatable<SubsetKey>
{
    private readonly ImmutableArray<string> _hosts;

    public SubsetKey(SourceSet sources, IEnumerable<string>? hosts = null)
    {
        Sources = sources;
        _hosts = hosts is null ? default : [.. hosts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public SourceSet Sources { get; }

    /// <summary>The selected hosts in ordinal order; null for every host.</summary>
    public IReadOnlyList<string>? Hosts => _hosts.IsDefault ? null : _hosts;

    public bool HasHosts => !_hosts.IsDefault;

    public static implicit operator SubsetKey(SourceSet sources) => new(sources);

    public bool Contains(HostSource cell) => Sources.Contains(cell.Source) && (_hosts.IsDefault || _hosts.Contains(cell.Host, StringComparer.Ordinal));

    public bool Equals(SubsetKey other) =>
        Sources == other.Sources && _hosts.IsDefault == other._hosts.IsDefault
        && (_hosts.IsDefault || _hosts.AsSpan().SequenceEqual(other._hosts.AsSpan()));

    public override bool Equals(object? obj) => obj is SubsetKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Sources);
        if (!_hosts.IsDefault)
        {
            foreach (var host in _hosts)
            {
                hash.Add(host, StringComparer.Ordinal);
            }
        }
        else
        {
            hash.Add(-1);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(SubsetKey left, SubsetKey right) => left.Equals(right);

    public static bool operator !=(SubsetKey left, SubsetKey right) => !left.Equals(right);

    public override string ToString() => HasHosts ? $"{Sources} on [{string.Join(", ", _hosts)}]" : Sources.ToString();
}
