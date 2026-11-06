using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// The hosts an orb or board tracks (DESIGN 13 <c>hosts</c>): every host, including ones added later, or an explicit list of
/// host ids (<c>local</c> for this computer, remote host ids) with <c>local</c> first. Ids this computer does not know (a
/// host of another computer's settings, a newer version's) are kept and ignored, like unknown sources. <c>default</c> is all
/// hosts.
/// </summary>
public readonly record struct OrbHosts
{
    public const int MaxIdsRead = 64;
    public const int MaxIdLength = 64;

    private readonly ImmutableArray<string> _ids;

    private OrbHosts(ImmutableArray<string> ids)
    {
        _ids = ids;
    }

    public static OrbHosts All => default;

    public bool IsAll => _ids.IsDefaultOrEmpty;

    /// <summary>The ids of an explicit list, <c>local</c> first; empty for all hosts.</summary>
    public ImmutableArray<string> Ids => _ids.IsDefault ? [] : _ids;

    /// <summary>The engine's host set; null for all hosts.</summary>
    public IReadOnlySet<string>? HostSet => IsAll ? null : _ids.ToHashSet(StringComparer.Ordinal);

    /// <summary><c>local</c> first, duplicates, empty and over-long ids dropped; nothing left is all hosts.</summary>
    public static OrbHosts Normalized(IEnumerable<string?> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = ids.Take(MaxIdsRead).Where(id => id is { Length: > 0 and <= MaxIdLength } && seen.Add(id)).Select(id => id!).ToList();
        if (unique.Count == 0)
        {
            return All;
        }

        return new OrbHosts([.. unique.Where(IsLocal), .. unique.Where(id => !IsLocal(id))]);
    }

    public static OrbHosts Of(params string[] ids) => Normalized(ids);

    public bool Tracks(string hostId) => IsAll || _ids.Contains(hostId, StringComparer.Ordinal);

    /// <summary>A host removed from the settings leaves the selection; a selection left empty becomes all hosts (REMOTE 2.4).</summary>
    public OrbHosts Removing(string hostId) => IsAll ? this : Normalized(_ids.Where(id => id != hostId));

    /// <summary>The ids in settings order (<paramref name="order"/>: <c>local</c>, then the remote hosts as added); unknown ids stay, after the known ones.</summary>
    public OrbHosts Ordered(IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (IsAll)
        {
            return this;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            rank.TryAdd(order[i], i);
        }

        var known = _ids.Where(rank.ContainsKey).OrderBy(id => rank[id]);
        return Normalized([.. known, .. _ids.Where(id => !rank.ContainsKey(id))]);
    }

    /// <summary>
    /// The pickers' rule (DESIGN 13) over the hosts this computer knows (<paramref name="known"/>, in settings order): on an
    /// all-hosts selection the host becomes the only one; otherwise it is added or removed, in settings order. Removing the
    /// last known host is refused (null); unknown ids alone never count as a selection.
    /// </summary>
    public OrbHosts? Toggling(string hostId, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (IsAll)
        {
            return Of(hostId).Ordered(known);
        }

        if (_ids.Contains(hostId, StringComparer.Ordinal))
        {
            var remaining = _ids.Where(id => id != hostId).ToList();
            return remaining.Any(known.Contains) ? Normalized(remaining).Ordered(known) : null;
        }

        return Normalized([.. _ids, hostId]).Ordered(known);
    }

    /// <summary>The rows of a Hosts picker or submenu: every choice in its order, checked when an explicit selection tracks it; the last tracked one cannot be unchecked.</summary>
    public IReadOnlyList<OrbHostEntry> Entries(IReadOnlyList<OrbHostChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var self = this;
        var checkedCount = choices.Count(c => !self.IsAll && self.Tracks(c.Id));
        return [.. choices.Select(c =>
        {
            var isChecked = !self.IsAll && self.Tracks(c.Id);
            return new OrbHostEntry(c.Id, c.Name, isChecked, !(isChecked && checkedCount == 1));
        })];
    }

    /// <summary>The engine's subset for <paramref name="sources"/> on these hosts (METRICS 6.8, DESIGN 13); null for every source on every host.</summary>
    public SubsetKey? SubsetKey(SourceSet? sources) =>
        sources is null && IsAll ? null : new SubsetKey(sources ?? SourceSet.Everything, IsAll ? null : _ids);

    public bool Equals(OrbHosts other) => Ids.SequenceEqual(other.Ids, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var id in Ids)
        {
            hash = HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(id));
        }

        return hash;
    }

    public override string ToString() => IsAll ? "all" : "[" + string.Join(", ", Ids) + "]";

    private static bool IsLocal(string id) => id == RemoteHost.LocalId;
}

/// <summary>A host an orb or board can track (DESIGN 13): this computer (<c>local</c>) or a remote host of the settings, with its display name.</summary>
public sealed record OrbHostChoice(string Id, string Name)
{
    /// <summary>Choices exist only while a remote host is configured (DESIGN 13: invisible until the first host is added).</summary>
    public static bool PickerShown(IReadOnlyList<OrbHostChoice> choices) => choices.Any(c => c.Id != RemoteHost.LocalId);

    /// <summary><c>local</c> (named by the UI, <c>remote.local</c>) then every remote host of <paramref name="document"/> with its display name.</summary>
    public static IReadOnlyList<OrbHostChoice> Of(RemoteHostsDocument document, string localName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var names = document.DisplayNames();
        return [new OrbHostChoice(RemoteHost.LocalId, localName), .. document.Hosts.Select(h => new OrbHostChoice(h.Id, names[h.Id]))];
    }
}

/// <summary>One row of a Hosts picker or submenu.</summary>
/// <param name="Enabled">False for the last tracked host (<c>remote.hosts.lastOne</c>).</param>
public sealed record OrbHostEntry(string Id, string Name, bool Checked, bool Enabled);
