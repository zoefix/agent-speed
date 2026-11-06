import Foundation
import Network

/// Calls `onAvailable` when the network path becomes usable after it was not (REMOTE 4.4: retry at once when the
/// Mac's network comes back).
///
/// `@unchecked Sendable`: `wasSatisfied` is only touched on `queue`.
final class RemoteNetworkWatcher: @unchecked Sendable {
    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "app.agentspeed.remote.network", qos: .utility)
    private let onAvailable: @Sendable () -> Void
    private var wasSatisfied: Bool?

    init(onAvailable: @escaping @Sendable () -> Void) {
        self.onAvailable = onAvailable
    }

    func start() {
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let satisfied = path.status == .satisfied
            if satisfied, self.wasSatisfied == false { self.onAvailable() }
            self.wasSatisfied = satisfied
        }
        monitor.start(queue: queue)
    }

    func stop() {
        monitor.cancel()
    }
}
