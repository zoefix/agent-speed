import Foundation

/// The remote hosts' daily history on the worker's queue (REMOTE 8.2, 8.3). A long local analysis delays these
/// events; they are applied in order once it ends.
extension HistoryWorker {
    func remote(_ event: RemoteHostEvent) {
        queue.async { [self] in
            guard let analyzer = openAnalyzer() else { return }
            apply(remoteLedger.receive(event, database: analyzer.database))
        }
    }

    func setRemoteHosts(_ names: [String: String]) {
        queue.async { [self] in
            apply(remoteLedger.setHosts(names, database: openAnalyzer()?.database))
        }
    }

    func deleteRemoteHost(_ id: String) {
        queue.async { [self] in
            guard let analyzer = openAnalyzer() else { return }
            apply(remoteLedger.deleteHost(id, database: analyzer.database))
        }
    }

    /// After *Clear history*: every connected host is asked for all of its days (REMOTE 8.3).
    func resyncRemoteHosts() {
        for host in remoteLedger.connectedHosts { requestRemoteHistory(host, [:]) }
    }

    private func apply(_ effect: RemoteHistoryLedger.Effect) {
        switch effect {
        case .none:
            break
        case .changed:
            post(lastState, days: true)
            postPerf()
            postRemoteTooLarge()
        case .tooLargeChanged:
            postRemoteTooLarge()
        case .request(let host, let digests):
            requestRemoteHistory(host, digests)
        }
    }

    /// The hosts that show `historyTooLarge` (REMOTE 8.3).
    func postRemoteTooLarge() {
        let hosts = remoteLedger.tooLarge
        let publisher = remotePublisher
        DispatchQueue.main.async { [weak self] in
            guard let self, !isStopped else { return }
            MainActor.assumeIsolated { publisher(hosts) }
        }
    }
}
