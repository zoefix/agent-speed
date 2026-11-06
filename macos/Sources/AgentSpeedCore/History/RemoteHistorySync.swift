import Foundation
import os

/// The daily history of the remote hosts (REMOTE 8.2, 8.3): answers each host's `historyDue` with the digests it
/// holds, stores the days the relay sends in batches and has the history service publish them with the local days.
/// Usable from any thread; the work runs on the history service's queue.
///
/// Hand it to `RemoteCoordinator.historySink` (held weakly there) and keep it in step with `setHosts`. Before the
/// history service starts, the last status and a pending sync of every host wait here.
public final class RemoteHistorySync: RemoteHistorySink, Sendable {
    public typealias RequestHistory = @Sendable (_ hostId: String, _ digests: [String: String]) -> Void

    private struct State {
        weak var worker: HistoryWorker?
        var request: RequestHistory?
        var hosts: [String: String]?
        /// Without a worker: each host's last status, then its sync request.
        var statuses: [String: RemoteHostEvent] = [:]
        var due: [String: RemoteHostEvent] = [:]
        var deletions: [String] = []
    }

    private let state: OSAllocatedUnfairLock<State>

    /// `requestHistory` sends a `history` request (`RemoteCoordinator.historyRequests.requestHistory`); without it, the
    /// requests that come with `remoteHistory(_:requests:)` are used.
    public init(requestHistory: RequestHistory? = nil) {
        state = OSAllocatedUnfairLock(initialState: State(request: requestHistory))
    }

    /// `RemoteHistorySink`.
    public func remoteHistory(_ event: RemoteHostEvent, requests: RemoteHistoryRequests) {
        state.withLock { state in
            if state.request == nil { state.request = { requests.requestHistory(hostId: $0, digests: $1) } }
        }
        receive(event)
    }

    /// A host's event, in the order the host sent them.
    public func receive(_ event: RemoteHostEvent) {
        let worker = state.withLock { state -> HistoryWorker? in
            if let worker = state.worker { return worker }
            switch event.kind {
            case .status: state.statuses[event.hostId] = event
            case .historyDue: state.due[event.hostId] = event
            case .message: break
            }
            return nil
        }
        worker?.remote(event)
    }

    /// The hosts of the settings with their display names (REMOTE 2.1): stored hosts take the new names; hosts no
    /// longer listed keep their rows and their last name (REMOTE 2.2).
    public func setHosts(_ hosts: [(id: String, name: String)]) {
        let names = Dictionary(hosts.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first })
        let worker = state.withLock { state -> HistoryWorker? in
            state.hosts = names
            return state.worker
        }
        worker?.setRemoteHosts(names)
    }

    /// Deletes a host's daily history (*Also delete its usage history*, REMOTE 2.4).
    public func deleteHistory(ofHost id: String) {
        let worker = state.withLock { state -> HistoryWorker? in
            if state.worker == nil { state.deletions.append(id) }
            state.statuses[id] = nil
            state.due[id] = nil
            return state.worker
        }
        worker?.deleteRemoteHost(id)
    }

    /// The history service's worker started (or stopped, nil); what waited goes to it.
    func attach(_ worker: HistoryWorker?) {
        let waiting = state.withLock { state -> (hosts: [String: String]?, deletions: [String], events: [RemoteHostEvent]) in
            state.worker = worker
            guard worker != nil else { return (nil, [], []) }
            let events = state.statuses.keys.sorted().compactMap { state.statuses[$0] }
                + state.due.keys.sorted().compactMap { state.due[$0] }
            let waiting = (state.hosts, state.deletions, events)
            state.statuses = [:]
            state.due = [:]
            state.deletions = []
            return waiting
        }
        guard let worker else { return }
        if let hosts = waiting.hosts { worker.setRemoteHosts(hosts) }
        for id in waiting.deletions { worker.deleteRemoteHost(id) }
        for event in waiting.events { worker.remote(event) }
    }

    func request(hostId: String, digests: [String: String]) {
        state.withLock { $0.request }?(hostId, digests)
    }
}
