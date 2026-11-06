using System.Collections.Immutable;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The settings value <c>remoteHosts</c> (REMOTE 2.1): at most 8 hosts in the order added. Immutable; reading is
/// tolerant (<see cref="RemoteHostsJson"/>): a host without a valid id is skipped, a repeated id keeps the first, more than
/// 8 hosts are cut; unknown members of the document and of each host are kept.
/// </summary>
public sealed class RemoteHostsDocument : IEquatable<RemoteHostsDocument>
{
    public const int MaxHosts = 8;
    public const int CurrentVersion = 1;

    public RemoteHostsDocument(IEnumerable<RemoteHost>? hosts = null, int version = CurrentVersion, string? extraJson = null)
    {
        Hosts = Normalized(hosts ?? []);
        Version = Math.Max(CurrentVersion, version);
        ExtraJson = extraJson;
    }

    public static RemoteHostsDocument Empty { get; } = new();

    public int Version { get; }

    public ImmutableArray<RemoteHost> Hosts { get; }

    /// <summary>Unknown members of the document as one JSON object text; null when there are none.</summary>
    public string? ExtraJson { get; }

    public bool CanAdd => Hosts.Length < MaxHosts;

    public RemoteHost? Host(string id) => IndexOf(id) is >= 0 and var index ? Hosts[index] : null;

    private int IndexOf(string id)
    {
        for (var i = 0; i < Hosts.Length; i++)
        {
            if (string.Equals(Hosts[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The document with <paramref name="host"/> appended; null at the limit or when its id is invalid or taken.</summary>
    public RemoteHostsDocument? Adding(RemoteHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return CanAdd && RemoteHostValidation.IsValidId(host.Id) && Host(host.Id) is null
            ? new RemoteHostsDocument(Hosts.Add(host), Version, ExtraJson)
            : null;
    }

    /// <summary>The host with the same id replaced in place (keeping its unknown members unless the new one has some); null when absent.</summary>
    public RemoteHostsDocument? Updating(RemoteHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var index = IndexOf(host.Id);
        if (index < 0)
        {
            return null;
        }

        var updated = host.ExtraJson is null ? host with { ExtraJson = Hosts[index].ExtraJson } : host;
        return new RemoteHostsDocument(Hosts.SetItem(index, updated), Version, ExtraJson);
    }

    public RemoteHostsDocument Removing(string id) =>
        Host(id) is null ? this : new RemoteHostsDocument(Hosts.Where(h => !string.Equals(h.Id, id, StringComparison.Ordinal)), Version, ExtraJson);

    /// <summary>
    /// The display name of every host: its custom name, else its automatic name made distinct with <c> (2)</c>, <c> (3)</c> …
    /// from every custom name and every earlier host's name (REMOTE 2.1), compared without case.
    /// </summary>
    public IReadOnlyDictionary<string, string> DisplayNames()
    {
        var taken = new HashSet<string>(Hosts.Where(h => h.Name is not null).Select(h => h.Name!), StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in Hosts)
        {
            if (host.Name is { } name)
            {
                names[host.Id] = name;
                continue;
            }

            var baseName = host.AutomaticName;
            var candidate = baseName;
            for (var counter = 2; taken.Contains(candidate); counter++)
            {
                candidate = $"{baseName} ({counter})";
            }

            taken.Add(candidate);
            names[host.Id] = candidate;
        }

        return names;
    }

    public string? DisplayName(string id) => DisplayNames().GetValueOrDefault(id);

    /// <summary>Valid ids only, the first of a repeated id, at most <see cref="MaxHosts"/>.</summary>
    public static ImmutableArray<RemoteHost> Normalized(IEnumerable<RemoteHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. hosts.Where(h => h is not null && RemoteHostValidation.IsValidId(h.Id) && seen.Add(h.Id)).Take(MaxHosts)];
    }

    public bool Equals(RemoteHostsDocument? other) =>
        other is not null && Version == other.Version && Hosts.SequenceEqual(other.Hosts)
        && string.Equals(ExtraJson, other.ExtraJson, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RemoteHostsDocument other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        foreach (var host in Hosts)
        {
            hash.Add(host);
        }

        hash.Add(ExtraJson, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(RemoteHostsDocument? left, RemoteHostsDocument? right) => Equals(left, right);

    public static bool operator !=(RemoteHostsDocument? left, RemoteHostsDocument? right) => !Equals(left, right);
}
