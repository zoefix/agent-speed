import Foundation
import os

/// Where the remote feed delivers its changes: the running monitor, which applies them on its queue.
protocol RemoteFeedSink: AnyObject, Sendable {
    /// Called with the feed's lock held, so it must only enqueue.
    func remoteFeed(_ changes: [RemoteFeedChange])
}

/// The live side of every remote host (REMOTE 7): it mirrors what each host sent, so a monitor restart replays it as
/// seeded without reconnecting, and forwards every change to the attached monitor in the order the events came.
///
/// Thread-safe: events arrive on the coordinator's queue, hosts from the main actor and `attach` from the monitor's
/// queue; one lock orders them.
final class RemoteFeed: Sendable {
    private struct State: Sendable {
        var hosts: [RemoteHostInfo] = []
        var mirrors: [String: RemoteHostMirror] = [:]
        weak var sink: (any RemoteFeedSink)?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let now: @Sendable () -> Date
    private let todayStart: @Sendable (Date) -> Date

    init(
        now: @escaping @Sendable () -> Date = { Date() },
        todayStart: @escaping @Sendable (Date) -> Date = { Calendar.current.startOfDay(for: $0) }
    ) {
        self.now = now
        self.todayStart = todayStart
    }

    /// Some remote host is configured.
    var hasHosts: Bool { state.withLock { !$0.hosts.isEmpty } }

    var hosts: [RemoteHostInfo] { state.withLock { $0.hosts } }

    /// The configured hosts in settings order with their display names. A host that left the settings is forgotten.
    func setHosts(_ hosts: [RemoteHostInfo]) {
        state.withLock { state in
            guard hosts != state.hosts else { return }
            let ids = Set(hosts.map(\.id))
            var changes: [RemoteFeedChange] = []
            for id in state.mirrors.keys.sorted() where !ids.contains(id) {
                state.mirrors[id] = nil
                changes.append(.removeHost(id))
            }
            for host in hosts where state.mirrors[host.id] == nil {
                state.mirrors[host.id] = RemoteHostMirror(hostId: host.id)
            }
            state.hosts = hosts
            changes.append(.hosts(hosts))
            state.sink?.remoteFeed(changes)
        }
    }

    /// One event of the hub; events of hosts that are not configured (any more) are dropped.
    func receive(_ event: RemoteHostEvent) {
        let date = now()
        let nowMs = Clock.milliseconds(date)
        let todayStartMs = Clock.milliseconds(todayStart(date))
        state.withLock { state in
            guard state.mirrors[event.hostId] != nil else { return }
            let changes = state.mirrors[event.hostId]!.apply(event, nowMs: nowMs, todayStartMs: todayStartMs)
            if !changes.isEmpty { state.sink?.remoteFeed(changes) }
        }
    }

    /// Makes `sink` the receiver of every later change and returns the current state as seeded changes.
    func attach(_ sink: any RemoteFeedSink) -> [RemoteFeedChange] {
        state.withLock { state in
            state.sink = sink
            var changes: [RemoteFeedChange] = [.hosts(state.hosts)]
            for host in state.hosts {
                changes += state.mirrors[host.id]?.replay() ?? []
            }
            return changes
        }
    }

    func detach(_ sink: any RemoteFeedSink) {
        state.withLock { state in
            if state.sink === sink { state.sink = nil }
        }
    }

    /// The mirror of one host (tests and diagnostics).
    func mirror(_ hostId: String) -> RemoteHostMirror? {
        state.withLock { $0.mirrors[hostId] }
    }
}
