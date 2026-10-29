import AgentSpeedCore
import Foundation

/// The `perfScore` extension on the reporting side (METRICS 11.10): the watch, worked out at every tick and handed to
/// the live monitor as an epoch; the counted completions each snapshot brings, taken once; and the statistics ledger,
/// which every build with the extension active takes and a break empties after the leaving report of its tick.
extension ReportController {
    /// The service the watch is on for, nil while it is off: reporting on for a confirmed and registered service, the
    /// network on, not paused, nothing that needs the user (an error, a review, a stop), the extension active and the
    /// monitor's seeding done. Transient waits (backoff, `Retry-After`, quiet, a sleep gap, a new run or transport, a
    /// stale document) leave it on.
    var perfWatchKey: String? {
        guard monitorRunning, preferences.enabled, settings.networkEnabled, !preferences.isPaused, problem == nil,
              signer != nil, let identity, identity.isRegistered, identity.pin != nil, !needsReview,
              let document = identity.discovery, document.supportsThisClient,
              ReportPerfScoreRule.isActive(document, switches: preferences.sections),
              stream?.scheduler.stop == nil else { return nil }
        return identity.id
    }

    /// At the start of a tick: the snapshot's new completions of the current watch join the ledger (true when one
    /// did, the changed rule); a watch that moved to another service starts before anything is built for it.
    func beginPerfTick(_ sample: ReportSample) -> Bool {
        if let key = perfWatchKey, key != perfTracker.watchKey { updatePerfWatch() }
        return perfTracker.ingest(sample.facts.perf.completions)
    }

    /// The watch as it is now, after the tick's leaving report took the ledger: turning off (or to another service)
    /// is a break that empties it; a new epoch reaches the monitor.
    func updatePerfWatch() {
        guard perfTracker.watch(perfWatchKey) else { return }
        perfWatchHandler?(perfTracker.epoch)
    }

    /// A build takes the statistics rows into the body while the extension is active (also when the size rule drops
    /// `models`: those completions are lost, never resent).
    func takePerfStats(_ options: ReportOptions) -> ReportPerfStats {
        options.perfScoreActive ? perfTracker.take() : [:]
    }

    /// The `perfScore` (or legacy `taskScore`) objects the next report of the stream would carry, for the changed
    /// rule; nil while neither is active.
    func streamPerfScoreValues(_ window: ReportWindow, _ sample: ReportSample) -> ReportPerfScoreValues? {
        guard let document = identity?.discovery else { return nil }
        let options = ReportOptions(sections: preferences.sections.intersection(document.sections),
                                    maxModels: document.limits.maxModels, extensions: document.extensions ?? .none)
        var content = window.content(sample)
        content.perfStats = perfTracker.pending
        return ReportBody.perfScoreValues(content, sample, options)
    }
}
