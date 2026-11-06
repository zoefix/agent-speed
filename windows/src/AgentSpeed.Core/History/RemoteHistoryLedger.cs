using System.Diagnostics;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

internal enum RemoteHistoryEffectKind
{
    None,

    /// <summary>The stored rows or names changed: publish again.</summary>
    Changed,

    /// <summary>Only <see cref="RemoteHistoryLedger.TooLarge"/> changed.</summary>
    TooLargeChanged,

    /// <summary>Send <c>history</c> with <see cref="RemoteHistoryEffect.Digests"/>.</summary>
    Request,
}

/// <summary>What the history service does after the ledger applied something.</summary>
internal sealed record RemoteHistoryEffect(RemoteHistoryEffectKind Kind, string? Host = null, IReadOnlyDictionary<string, string>? Digests = null)
{
    public static RemoteHistoryEffect None { get; } = new(RemoteHistoryEffectKind.None);

    public static RemoteHistoryEffect Changed { get; } = new(RemoteHistoryEffectKind.Changed);

    public static RemoteHistoryEffect TooLargeChanged { get; } = new(RemoteHistoryEffectKind.TooLargeChanged);

    public static RemoteHistoryEffect Request(string host, IReadOnlyDictionary<string, string> digests) =>
        new(RemoteHistoryEffectKind.Request, host, digests);

    public bool Equals(RemoteHistoryEffect? other) => other is not null && Kind == other.Kind && Host == other.Host
        && (Digests?.Count ?? 0) == (other.Digests?.Count ?? 0)
        && (Digests ?? new Dictionary<string, string>()).All(d => other.Digests!.TryGetValue(d.Key, out var value) && value == d.Value);

    public override int GetHashCode() => HashCode.Combine(Kind, Host, Digests?.Count ?? 0);

    public override string ToString() => Digests is null
        ? $"{Kind} {Host}"
        : $"{Kind} {Host} {{{string.Join(", ", Digests.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}: {d.Value}"))}}}";
}

/// <summary>
/// The remote hosts' daily history inside the history service (REMOTE 8.2, 8.3): the sync state of every host and the stored
/// rows, written through the service's store. Confined to the history thread.
/// </summary>
internal sealed class RemoteHistoryLedger
{
    private readonly RemoteHistorySyncState _sync;
    private readonly Func<string> _zone;

    /// <summary>The settings' display names by host id.</summary>
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);

    /// <summary>The zone each host's last request ran in, with its connection.</summary>
    private readonly Dictionary<string, (int Connection, string Zone)> _requested = new(StringComparer.Ordinal);

    /// <summary>Hosts whose current batch could not be staged: it commits nothing, so the next sync sends its days again.</summary>
    private readonly HashSet<string> _unstaged = new(StringComparer.Ordinal);

    private readonly HashSet<string> _tooLarge = new(StringComparer.Ordinal);

    /// <param name="zone">The app's time zone id as the relay gets it (REMOTE 5.3 <c>zone</c>); the local zone's by default.</param>
    public RemoteHistoryLedger(RemoteHistoryLimits? limits = null, long flushBytes = RemoteHistorySyncState.DefaultFlushBytes,
        Func<string>? zone = null)
    {
        _sync = new RemoteHistorySyncState(limits, flushBytes);
        _zone = zone ?? (static () => RemoteZone.Current().Id);
    }

    public RemoteHistoryRows Rows { get; private set; } = new();

    /// <summary>Bumped whenever <see cref="Rows"/> changes.</summary>
    public int Version { get; private set; }

    /// <summary>Hosts whose last batch exceeded the bounds (<c>historyTooLarge</c>, REMOTE 8.3) until a sync succeeds.</summary>
    public IReadOnlySet<string> TooLarge => _tooLarge;

    /// <summary>Hosts whose relay is connected, in id order.</summary>
    public IReadOnlyList<string> ConnectedHosts => [.. _sync.Connected.Keys.Order(StringComparer.Ordinal)];

    public RemoteHistoryLimits Limits => _sync.Limits;

    /// <summary>Reads the rows of <paramref name="store"/> (none when they cannot be read) and gives stored hosts their settings names.</summary>
    public void Load(HistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            Rows = RemoteHistoryRows.Load(store);
        }
        catch (SqliteException)
        {
            Trace.TraceWarning("Remote history rows could not be read");
            Rows = new RemoteHistoryRows();
        }

        Version++;
        ApplyNames(store);
    }

    /// <summary><i>Clear history</i>: the store was deleted, with every batch in progress.</summary>
    public void Reset()
    {
        _sync.DropBatches();
        _requested.Clear();
        _unstaged.Clear();
        Rows = new RemoteHistoryRows();
        Version++;
    }

    public RemoteHistoryEffect Receive(RemoteHostEvent hostEvent, HistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        ArgumentNullException.ThrowIfNull(store);
        switch (_sync.Apply(hostEvent))
        {
            case RemoteSyncAction.Request(var host):
            {
                // Rows of another zone are not compared: the whole history comes again and replaces them.
                var current = _zone();
                _requested[host] = (hostEvent.Connection, current);
                var sameZone = !Rows.Zones.TryGetValue(host, out var stored) || stored == current;
                return RemoteHistoryEffect.Request(host, sameZone ? Rows.Digests(host) : new Dictionary<string, string>());
            }

            case RemoteSyncAction.Stage(var host, var days, var first):
                if (first)
                {
                    _unstaged.Remove(host);
                }

                try
                {
                    store.StageRemote(days, host, first);
                }
                catch (SqliteException)
                {
                    Trace.TraceWarning("Remote history could not be staged");
                    _unstaged.Add(host);
                }

                return RemoteHistoryEffect.None;
            case RemoteSyncAction.Commit(var host, var connection, var days, var staged):
                if (staged && _unstaged.Remove(host))
                {
                    _requested.Remove(host);
                    TryClearStaging(store, host);
                    return RemoteHistoryEffect.None;
                }

                return Commit(days, host, connection, staged, store);
            case RemoteSyncAction.Overflow(var host):
                _unstaged.Remove(host);
                TryClearStaging(store, host);
                return RemoteHistoryEffect.None;
            case RemoteSyncAction.TooLarge(var host):
                _requested.Remove(host);
                return _tooLarge.Add(host) ? RemoteHistoryEffect.TooLargeChanged : RemoteHistoryEffect.None;
            default:
                return RemoteHistoryEffect.None;
        }
    }

    /// <summary>The settings' hosts with their display names; stored hosts get the new names, removed ones keep theirs.</summary>
    public RemoteHistoryEffect SetHosts(IReadOnlyDictionary<string, string> hosts, HistoryStore? store)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        _names = new Dictionary<string, string>(hosts, StringComparer.Ordinal);
        return store is not null && ApplyNames(store) ? RemoteHistoryEffect.Changed : RemoteHistoryEffect.None;
    }

    /// <summary>Deletes a host's rows and batch (REMOTE 2.4).</summary>
    public RemoteHistoryEffect DeleteHost(string id, HistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _sync.Drop(id);
        _requested.Remove(id);
        _unstaged.Remove(id);
        var wasTooLarge = _tooLarge.Remove(id);
        var unchanged = wasTooLarge ? RemoteHistoryEffect.TooLargeChanged : RemoteHistoryEffect.None;
        try
        {
            store.DeleteRemoteHost(id);
        }
        catch (SqliteException)
        {
            Trace.TraceWarning("Remote history could not be deleted");
            return unchanged;
        }

        if (!Rows.Hosts.Contains(id) && !Rows.Names.ContainsKey(id))
        {
            return unchanged;
        }

        Rows.Remove(id);
        Version++;
        return RemoteHistoryEffect.Changed;
    }

    /// <summary>
    /// A <c>historyDone</c>: the batch merges into the host's rows, or replaces them when its request ran in another zone than
    /// they were computed in. Rows stored without a zone take the batch's. Without a request of this connection, the rows keep
    /// their zone.
    /// </summary>
    private RemoteHistoryEffect Commit(IReadOnlyList<RemoteBatchDay> days, string host, int connection, bool staged, HistoryStore store)
    {
        string? requestZone = _requested.Remove(host, out var request) && request.Connection == connection ? request.Zone : null;
        string? stored = Rows.Zones.TryGetValue(host, out var known) ? known : null;
        var zone = requestZone ?? stored ?? _zone();
        var replace = stored is not null && stored != zone;
        var relabels = stored is null && Rows.Hosts.Contains(host);
        if (!replace && !staged && days.Count == 0 && !relabels)
        {
            return Succeeded(host);
        }

        var name = _names.GetValueOrDefault(host);
        try
        {
            store.CommitRemote(days, host, name, zone, replace, staged, _sync.Limits);
        }
        catch (RemoteHistoryTooLargeException)
        {
            return _tooLarge.Add(host) ? RemoteHistoryEffect.TooLargeChanged : RemoteHistoryEffect.None;
        }
        catch (SqliteException)
        {
            // Nothing is stored, so the next sync sends the days again.
            Trace.TraceWarning("Remote history could not be saved");
            return RemoteHistoryEffect.None;
        }

        Succeeded(host);
        if (staged)
        {
            Load(store);
        }
        else
        {
            Rows.Store(days, host, name, zone, replace);
            Version++;
        }

        return RemoteHistoryEffect.Changed;
    }

    private RemoteHistoryEffect Succeeded(string host) =>
        _tooLarge.Remove(host) ? RemoteHistoryEffect.TooLargeChanged : RemoteHistoryEffect.None;

    private bool ApplyNames(HistoryStore store)
    {
        var changed = false;
        foreach (var host in Rows.Hosts.Order(StringComparer.Ordinal))
        {
            if (!_names.TryGetValue(host, out var name) || (Rows.Names.TryGetValue(host, out var stored) && stored == name))
            {
                continue;
            }

            try
            {
                store.RenameRemoteHost(host, name);
            }
            catch (SqliteException)
            {
                continue;
            }

            Rows.Rename(host, name);
            changed = true;
        }

        if (changed)
        {
            Version++;
        }

        return changed;
    }

    private static void TryClearStaging(HistoryStore store, string host)
    {
        try
        {
            store.ClearRemoteStaging(host);
        }
        catch (SqliteException)
        {
        }
    }
}
