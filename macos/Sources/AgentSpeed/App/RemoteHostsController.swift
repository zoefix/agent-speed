import AgentSpeedCore
import AppKit

/// Keeps the remote hosts (REMOTE) connected to the live monitor: the settings' hosts, the network master switch and
/// the current price table go to the coordinator whenever they change; wake from sleep, network and time zone changes
/// reach the hosts' connections.
@MainActor
final class RemoteHostsController {
    let coordinator: RemoteCoordinator
    private var loop: ObservationLoop?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []

    init(monitor: LiveMonitor) {
        coordinator = RemoteCoordinator(monitor: monitor)
    }

    func start(settings: AppSettings, prices: PriceSyncController) {
        let coordinator = coordinator
        loop = ObservationLoop { [weak settings, weak prices] in
            guard let settings, let prices else { return }
            // Reading the table status re-runs this when a newer table becomes current.
            _ = prices.table
            coordinator.update(hosts: settings.remoteHosts, networkEnabled: settings.networkEnabled,
                               priceTable: prices.book.current.data, quota: settings.remoteQuotaSwitches)
        }
        let workspace = NSWorkspace.shared.notificationCenter
        observers.append((workspace, workspace.addObserver(forName: NSWorkspace.didWakeNotification, object: nil,
                                                           queue: .main) { _ in
            MainActor.assumeIsolated { coordinator.noteWake() }
        }))
        let center = NotificationCenter.default
        observers.append((center, center.addObserver(forName: .NSSystemTimeZoneDidChange, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { coordinator.noteTimeZoneChange() }
        }))
        coordinator.startNetworkMonitoring()
    }

    func stop() {
        loop?.cancel()
        loop = nil
        for (center, observer) in observers { center.removeObserver(observer) }
        observers.removeAll()
        coordinator.stop()
    }
}
