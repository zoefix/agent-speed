import AgentSpeedCore
import Foundation

/// The `quotas` of reports (REPORTING-EXTENSIONS 11): behind the 額度 switch and the service's `quotaEstimate`, sent
/// when the cadence of 11.3 says so; a report too large for them leaves them out and counts as unsent.
extension ReportController {
    /// The switch is on and the service's document advertises the extension.
    func sharesQuotas(_ document: ReportDiscovery?, on: Bool? = nil) -> Bool {
        (on ?? preferences.shareQuota) && document?.extensions?.quotaEstimate == true
    }

    /// The entries now, from the quota estimate; none without it.
    func currentQuotaEntries() -> [QuotaReportEntry] {
        quotaEntries?(ReportClock.unixMs) ?? []
    }

    /// The body as sent with this report's `quotas`, recording what it carried in `cadence`.
    func fittedWithQuotas(_ envelope: ReportEnvelope, _ content: ReportWindowContent, _ sample: ReportSample,
                          _ options: ReportOptions, document: ReportDiscovery, cadence: inout QuotaReportCadence,
                          leaving: Bool) -> Data {
        var options = options
        let on = sharesQuotas(document)
        let now = ReportClock.monotonicMs
        options.quotas = cadence.pending(nowMs: now, entries: on ? currentQuotaEntries() : [], on: on, leaving: leaving)
        let fitted = ReportBody.fitted(envelope, content, sample, options)
        cadence.commit(fitted.json["quotas"] == nil ? nil : options.quotas, nowMs: now, on: on)
        return fitted.data
    }
}
