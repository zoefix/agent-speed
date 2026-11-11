using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Core.Reporting;

/// <summary>
/// The <c>quotas</c> of one run's reports on either transport (REPORTING-EXTENSIONS 11.3): a built body carries them when
/// the cadence says so; a body too large leaves them out first and counts as unsent; switching the 額度 switch off sends
/// <c>[]</c> once. A service that does not advertise <c>quotaEstimate</c> gets none and the run starts over. A new
/// <c>run.id</c> starts empty (<see cref="NewRun"/>). Not thread-safe.
/// </summary>
public sealed class ReportQuotaRun
{
    private readonly QuotaReportCadence _cadence = new();

    public void NewRun() => _cadence.NewRun();

    /// <summary>
    /// The body as sent with this report's <c>quotas</c>, recording what it carried. <paramref name="entries"/> is asked
    /// only while the switch is on and the service advertises the extension.
    /// </summary>
    /// <param name="nowMs">The run's clock, consistent within the run.</param>
    /// <param name="switchOn">The user's 額度 switch (<c>report.section.quota</c>).</param>
    public (ReportJson Json, byte[] Data) Fitted(ReportEnvelope envelope, ReportWindowContent content, ReportSample sample, ReportOptions options,
        long nowMs, bool switchOn, Func<IReadOnlyList<QuotaReportEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entries);
        if (!options.Extensions.QuotaEstimate)
        {
            _cadence.NewRun();
            return ReportBody.Fitted(envelope, content, sample, options with { Quotas = null });
        }

        var pending = _cadence.Pending(nowMs, switchOn ? entries() : [], switchOn, envelope.Activity == ReportActivity.Leaving);
        var fitted = ReportBody.Fitted(envelope, content, sample, options with { Quotas = pending });
        _cadence.Commit(nowMs, fitted.Json["quotas"] is null ? null : pending, switchOn);
        return fitted;
    }
}
