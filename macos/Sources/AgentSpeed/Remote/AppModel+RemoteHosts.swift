import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The remote hosts as the UI sees them (DESIGN 13): names, statuses, the pickers' choices and the edits of the
/// settings, with REMOTE 2.4 on removal.
extension AppModel {
    /// Whether any remote host is configured; every host label, picker and section waits for the first one.
    var hasRemoteHosts: Bool { !settings.remoteHosts.hosts.isEmpty }

    /// This Mac first, then every remote host in settings order with its display name; empty without remote hosts.
    var hostChoices: [OrbHostChoice] {
        let document = settings.remoteHosts
        guard !document.hosts.isEmpty else { return [] }
        let names = document.displayNames()
        return [OrbHostChoice(id: RemoteHost.localId, name: l10n("remote.local"))]
            + document.hosts.map { OrbHostChoice(id: $0.id, name: names[$0.id] ?? $0.automaticName) }
    }

    /// `local`, then the remote host ids in settings order.
    var hostOrder: [String] { [RemoteHost.localId] + settings.remoteHosts.hosts.map(\.id) }

    /// The name a remote host goes by (the settings' name, else the last one the monitor knew); nil for this Mac.
    func remoteHostName(_ id: String) -> String? {
        guard id != RemoteHost.localId else { return nil }
        return settings.remoteHosts.displayName(of: id) ?? snapshot.hosts.first { $0.id == id }?.name
    }

    /// The status the UI shows for `host`: the settings decide off, network off and an invalid destination at once;
    /// otherwise the connection's last status (connecting before its first one). A history batch beyond the bounds
    /// (REMOTE 8.3) is noted whatever the connection does.
    func remoteStatus(of host: RemoteHost) -> RemoteHostStatus {
        var status = connectionStatus(of: host)
        status.historyTooLarge = history.remoteTooLarge.contains(host.id)
        return status
    }

    private func connectionStatus(of host: RemoteHost) -> RemoteHostStatus {
        if !settings.networkEnabled { return RemoteHostStatus(status: .networkOff) }
        if !host.enabled { return RemoteHostStatus(status: .disabled) }
        if !host.isConnectable { return RemoteHostStatus(status: .invalidDestination) }
        guard let status = snapshot.hosts.first(where: { $0.id == host.id })?.remote,
              status.status != .disabled, status.status != .networkOff else {
            return RemoteHostStatus(status: .connecting)
        }
        return status
    }

    /// The dot of a host id in a picker: this Mac is always connected.
    func hostDotKind(_ id: String) -> RemoteDotKind {
        guard let host = settings.remoteHosts.host(id) else { return .connected }
        return RemoteDotKind(remoteStatus(of: host).status)
    }

    /// Today's main sessions on a host (the status line's agent count).
    func hostSessions(_ id: String) -> Int {
        snapshot.hosts.first { $0.id == id }?.sessions ?? 0
    }

    // MARK: Editing

    /// Appends a host; an enabled one connects at once. False at the limit.
    @discardableResult
    func addRemoteHost(_ host: RemoteHost) -> Bool {
        var document = settings.remoteHosts
        guard document.add(host) else { return false }
        settings.remoteHosts = document
        return true
    }

    /// Saves an edit; a host that is not connected tries again at once (REMOTE 4.4).
    func updateRemoteHost(_ host: RemoteHost) {
        var document = settings.remoteHosts
        guard document.update(host) else { return }
        settings.remoteHosts = document
        guard host.enabled, remoteStatus(of: host).status != .connected else { return }
        // After the coordinator applied the edit, so the retry reaches the new connection.
        DispatchQueue.main.async { [weak self] in self?.retryRemoteHost(host.id) }
    }

    func setRemoteHostEnabled(_ enabled: Bool, id: String) {
        guard var host = settings.remoteHosts.host(id), host.enabled != enabled else { return }
        host.enabled = enabled
        var document = settings.remoteHosts
        document.update(host)
        settings.remoteHosts = document
    }

    /// *Reconnect* under a host's replaced status line: its relay runs QUOTAS 5.2 again (REMOTE 11.3).
    func reconnectHostBridge(_ id: String) {
        remoteHosts?.coordinator.reapplyQuota(hostId: id)
    }

    /// *Retry now*.
    func retryRemoteHost(_ id: String) {
        remoteHosts?.coordinator.retryNow(hostId: id)
    }

    /// REMOTE 2.4: the connection stops, the host leaves every orb and board selection (an empty one becomes all), and
    /// its history goes only when asked.
    func removeRemoteHost(_ id: String, deleteHistory: Bool) {
        var document = settings.remoteHosts
        guard document.host(id) != nil else { return }
        document.remove(id)
        settings.remoteHosts = document
        orbs.removeHost(id)
        boards.removeHost(id)
        if deleteHistory { remoteHistory.deleteHistory(ofHost: id) }
    }
}
