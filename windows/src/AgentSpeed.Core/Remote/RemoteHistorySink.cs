namespace AgentSpeed.Core.Remote;

/// <summary>Sends <c>history</c> requests to the hosts' relays; usable from any thread.</summary>
internal interface IRemoteHistoryRequests
{
    /// <summary><paramref name="digests"/>: <c>"&lt;day&gt;/&lt;source&gt;"</c> → digest of every day the app holds for the host. Ignored before <c>ready</c>.</summary>
    void RequestHistory(string hostId, IReadOnlyDictionary<string, string> digests);
}

/// <summary>
/// Receives what the daily history sync needs (REMOTE 8.2, 8.3), in order, from the coordinator's event thread:
/// <see cref="RemoteHostEventKind.HistoryDue"/>, the <c>historyDay</c>, <c>historyPerf</c>, <c>historyDone</c> and
/// <c>historyProgress</c> messages, a <c>historyFailed</c> error and every status change of a host. A broken connection (a
/// status that is not <c>connected</c>) ends a batch.
/// </summary>
internal interface IRemoteHistorySink
{
    /// <summary>Answer <see cref="RemoteHostEventKind.HistoryDue"/> with <paramref name="requests"/> and the digests held for the host.</summary>
    void RemoteHistory(RemoteHostEvent hostEvent, IRemoteHistoryRequests requests);
}
