using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Reporting;

internal sealed partial class ReportController
{
    /// <summary>A built report: its body exactly as signed, the hash the <c>ack</c> must carry, and whether its message fits.</summary>
    private sealed record StreamReport(byte[] Body, string Sha256, bool Fits);

    /// <summary>
    /// One WebSocket transport session (METRICS 11.9): its scheduler and counters, the current run's id, window and title
    /// tracker, the connection and the reports awaiting an answer.
    /// </summary>
    private sealed class ReportStreamRun(string origin, ReportStreamScheduler scheduler, ReportWindow window)
    {
        public string Id { get; private set; } = ReportCanonical.NewUuid();

        public string Origin { get; } = origin;

        public ReportStreamScheduler Scheduler { get; } = scheduler;

        public ReportWindow Window { get; private set; } = window;

        public ReportTitleTracker Title { get; private set; } = new();

        public IReportStreamSocket? Socket { get; set; }

        /// <summary>Built reports not answered yet (at most <c>maxInFlight</c>).</summary>
        public Dictionary<int, StreamReport> Reports { get; } = [];

        /// <summary>The rounded values of the last built report (the changed rule); null before the first.</summary>
        public ReportStreamValues? LastBuilt { get; set; }

        /// <summary>The latest tick's activity, which a wake-up repeats.</summary>
        public bool LastActive { get; set; }

        /// <summary>After <c>signature_invalid</c> with a rotated key waiting: connect and sign with the pending key.</summary>
        public bool SigningWithPending { get; set; }

        /// <summary>The <c>serverTime</c> of the latest error, for the clock correction.</summary>
        public long? ServerTime { get; set; }

        /// <summary>When <c>quotas</c> goes out (REPORTING-EXTENSIONS 11.3); a new run id starts empty.</summary>
        public ReportQuotaRun Quotas { get; } = new();

        /// <summary><c>seq_conflict</c>: a new run id, window and title tracker; the scheduler restarted its sequence.</summary>
        public void NewRun(ReportSample sample)
        {
            Id = ReportCanonical.NewUuid();
            Window = new ReportWindow(sample);
            Title = new ReportTitleTracker();
            Reports.Clear();
            LastBuilt = null;
            Quotas.NewRun();
        }
    }
}
