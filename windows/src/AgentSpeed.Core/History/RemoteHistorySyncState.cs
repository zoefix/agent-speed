using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

/// <summary>What <see cref="RemoteHistorySyncState"/> asks the ledger to do with one host event.</summary>
internal abstract record RemoteSyncAction
{
    private RemoteSyncAction()
    {
    }

    public static RemoteSyncAction None { get; } = new Nothing();

    public sealed record Nothing : RemoteSyncAction;

    /// <summary>Answer <c>historyDue</c> with the host's stored digests.</summary>
    public sealed record Request(string Host) : RemoteSyncAction;

    /// <summary>Move these complete days of a large batch to the staging tables; <paramref name="First"/>: the batch's first ones (staged rows left by a dropped batch go first).</summary>
    public sealed record Stage(string Host, IReadOnlyList<RemoteBatchDay> Days, bool First) : RemoteSyncAction;

    /// <summary><c>historyDone</c> of <paramref name="Connection"/>: commit the staged days (when <paramref name="Staged"/>) and these.</summary>
    public sealed record Commit(string Host, int Connection, IReadOnlyList<RemoteBatchDay> Days, bool Staged) : RemoteSyncAction;

    /// <summary>The batch exceeds the bounds: it is discarded with its staged rows, and the rest of it ignored.</summary>
    public sealed record Overflow(string Host) : RemoteSyncAction;

    /// <summary><c>historyDone</c> of a batch that exceeded the bounds.</summary>
    public sealed record TooLarge(string Host) : RemoteSyncAction;
}

/// <summary>
/// The sync rules of REMOTE 8.2 and 8.3 over the hosts' events, without storage: when to answer with the digests, what to
/// stage, commit or discard, and which batch to drop.
/// </summary>
internal sealed class RemoteHistorySyncState(RemoteHistoryLimits? limits = null, long flushBytes = RemoteHistorySyncState.DefaultFlushBytes)
{
    /// <summary>Above this much JSON text, a batch stages its complete days early so a host's memory stays bounded.</summary>
    public const long DefaultFlushBytes = 32L << 20;

    private readonly Dictionary<string, RemoteHistoryBatch> _batches = new(StringComparer.Ordinal);

    /// <summary>Hosts whose batch exceeded the bounds, with that connection, until its <c>historyDone</c>.</summary>
    private readonly Dictionary<string, int> _overflowed = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _connected = new(StringComparer.Ordinal);

    public RemoteHistoryLimits Limits { get; } = limits ?? new RemoteHistoryLimits();

    /// <summary>Hosts whose last status is <c>connected</c>, with that connection.</summary>
    public IReadOnlyDictionary<string, int> Connected => _connected;

    public RemoteSyncAction Apply(RemoteHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        var host = hostEvent.HostId;
        if (_batches.TryGetValue(host, out var current) && current.Connection != hostEvent.Connection)
        {
            _batches.Remove(host);
        }

        if (_overflowed.TryGetValue(host, out var overflowed) && overflowed != hostEvent.Connection)
        {
            _overflowed.Remove(host);
        }

        switch (hostEvent.Kind)
        {
            case RemoteHostEventKind.Status { Value.Status.Kind: RemoteStatusKind.Connected }:
                _connected[host] = hostEvent.Connection;
                return RemoteSyncAction.None;
            case RemoteHostEventKind.Status { Value.Status.Kind: RemoteStatusKind.Seeding }:
                _connected.Remove(host);
                return RemoteSyncAction.None;
            case RemoteHostEventKind.Status:
                _connected.Remove(host);
                _batches.Remove(host);
                _overflowed.Remove(host);
                return RemoteSyncAction.None;
            case RemoteHostEventKind.HistoryDue:
                return new RemoteSyncAction.Request(host);
            case RemoteHostEventKind.Message message:
                return Apply(message.Value, host, hostEvent.Connection);
            default:
                return RemoteSyncAction.None;
        }
    }

    /// <summary>Forgets a host's batch (its history was deleted).</summary>
    public void Drop(string host)
    {
        _batches.Remove(host);
        _overflowed.Remove(host);
    }

    /// <summary>Forgets every batch (<i>Clear history</i>); the connection states stay.</summary>
    public void DropBatches()
    {
        _batches.Clear();
        _overflowed.Clear();
    }

    private RemoteSyncAction Apply(RelayMessage message, string host, int connection)
    {
        switch (message)
        {
            case RelayMessage.HistoryDay day:
            {
                if (_overflowed.ContainsKey(host))
                {
                    return RemoteSyncAction.None;
                }

                if (!_batches.Remove(host, out var batch))
                {
                    batch = new RemoteHistoryBatch(host, connection);
                }

                batch.Add(day.Value);
                return Keep(batch, host, connection);
            }

            case RelayMessage.HistoryPerf perf:
            {
                if (_overflowed.ContainsKey(host) || !_batches.Remove(host, out var batch))
                {
                    return RemoteSyncAction.None;
                }

                batch.Add(perf.Value);
                return Keep(batch, host, connection);
            }

            case RelayMessage.HistoryDone:
            {
                if (_overflowed.Remove(host))
                {
                    return new RemoteSyncAction.TooLarge(host);
                }

                _batches.Remove(host, out var batch);
                return new RemoteSyncAction.Commit(host, connection, batch?.Days ?? [], batch?.IsStaged ?? false);
            }

            case { IsHistoryFailure: true }:
                _batches.Remove(host);
                _overflowed.Remove(host);
                return RemoteSyncAction.None;
            default:
                return RemoteSyncAction.None;
        }
    }

    /// <summary>Keeps the batch, staging its complete days when it holds too much text; a batch beyond the bounds overflows.</summary>
    private RemoteSyncAction Keep(RemoteHistoryBatch batch, string host, int connection)
    {
        if (batch.Exceeds(Limits))
        {
            _overflowed[host] = connection;
            return new RemoteSyncAction.Overflow(host);
        }

        var first = !batch.IsStaged;
        var early = batch.ByteCount > flushBytes ? batch.TakeCompleted() : [];
        _batches[host] = batch;
        return early.Count == 0 ? RemoteSyncAction.None : new RemoteSyncAction.Stage(host, early, first);
    }
}
