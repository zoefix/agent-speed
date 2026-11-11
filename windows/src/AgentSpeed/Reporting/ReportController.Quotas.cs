using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Reporting;

/// <summary>
/// The <c>quotas</c> of reports (REPORTING-EXTENSIONS 11): behind the 額度 switch and the service's <c>quotaEstimate</c>,
/// sent per run when the cadence of 11.3 says so, on both transports; the preview shows the entries as they are now.
/// </summary>
internal sealed partial class ReportController
{
    /// <summary>
    /// The quota estimate's report entries at a unix time in ms for the service of an api origin (REPORTING-EXTENSIONS 11.2),
    /// already limited to the sources whose quota is read (11.1); thread-safe. Null means there are none.
    /// </summary>
    public Func<long, string, IReadOnlyList<QuotaReportEntry>>? QuotaEntries { get; set; }

    /// <summary>The 額度 switch (REPORTING-EXTENSIONS 11.1); turned off, the next report sends <c>quotas: []</c> once.</summary>
    public void SetShareQuota(bool on)
    {
        UpdatePreferences(p => p with { ShareQuota = on });
        ClearRejection();
        Notify();
    }

    /// <summary>
    /// The entries now for the service of <paramref name="document"/>, each account tagged for the origin of its <c>api</c>
    /// (where reports go; before discovery, the service URL the user typed).
    /// </summary>
    private IReadOnlyList<QuotaReportEntry> CurrentQuotaEntries(ReportDiscovery document) =>
        QuotaEntries?.Invoke(UnixMs, document.Api.Origin) ?? [];

    /// <summary>The body as sent with the run's <c>quotas</c>; the cadence counts on the monotonic clock.</summary>
    private (ReportJson Json, byte[] Data) FittedWithQuotas(ReportQuotaRun quotas, ReportDiscovery document, ReportEnvelope envelope,
        ReportWindowContent content, ReportSample sample, ReportOptions options) =>
        quotas.Fitted(envelope, content, sample, options, MonotonicMs, Preferences.ShareQuota, () => CurrentQuotaEntries(document));
}
