import AgentSpeedCore
import Foundation

/// One WebSocket transport session (METRICS 11.9) as the controller keeps it: the stream session (scheduler,
/// connection, run id), the open window and title tracker of the current run, and what the changed rule and the
/// reactions remember between ticks.
struct ReportStreamRun {
    let origin: String
    let session: ReportStreamSession
    var window: ReportWindow
    var title = ReportTitleTracker()
    /// The rounded values of the last built report (the changed rule); nil before the first.
    var lastBuilt: ReportStreamValues?
    /// The latest tick's activity, which a wake-up repeats.
    var lastActive = false
    /// After `signature_invalid` with a rotated key waiting: connect and sign with the pending key.
    var signsWithPending = false
    /// A discard or a build happened during the current step, so this tick's peaks start the new window.
    var windowStarted = false
    /// The session's delivered count at the last step (a delivery signed with the pending key promotes it).
    var delivered = 0
    /// The stop already turned into the shared problem it stands for.
    var handledStop: ReportStreamScheduler.Stop?
    /// When `quotas` goes out (REPORTING-EXTENSIONS 11.3); a new run id starts empty.
    var quotaCadence = QuotaReportCadence()

    @MainActor var scheduler: ReportStreamScheduler { session.scheduler }

    /// `seq_conflict`: a new window and title tracker for the new run id.
    mutating func startOver(_ sample: ReportSample) {
        window = ReportWindow(sample)
        title = ReportTitleTracker()
        lastBuilt = nil
        quotaCadence = QuotaReportCadence()
    }
}
