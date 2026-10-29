using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Reporting;

/// <summary>
/// The performance score extension's side of live reporting (METRICS 11.10): which score extension the selected service's
/// reports carry, the watch the engine follows and the statistics ledger's rows that builds take. One window per controller,
/// so new runs, reconnections and transport changes keep the rows; a break — the watch turning off, or another service —
/// drops them.
/// </summary>
internal sealed partial class ReportController
{
    private readonly ReportPerfWindow _perf = new();

    /// <summary>
    /// The score extension of the selected service's reports (METRICS 11.10, Active and Legacy): <c>perfScore</c> when the
    /// known document advertises it, else the retired <c>taskScore</c> with the thinking speed only; either needs the document
    /// to list <c>models</c> with <c>maxModels</c> ≥ 1 and the user's models switch on.
    /// </summary>
    private ReportScoreExtension ScoreExtension =>
        Identity?.Discovery is { } document ? ScoreExtensionOf(document, Preferences.Sections) : ReportScoreExtension.None;

    private bool PerfScoreActive => ScoreExtension == ReportScoreExtension.Perf;

    private static ReportScoreExtension ScoreExtensionOf(ReportDiscovery document, IEnumerable<ReportSection> switches)
    {
        if (document.Limits.MaxModels < 1 || !document.Sections.Contains(ReportSection.Models) || !switches.Contains(ReportSection.Models))
        {
            return ReportScoreExtension.None;
        }

        return document.Extensions.PerfScore ? ReportScoreExtension.Perf
            : document.Extensions.TaskScore ? ReportScoreExtension.LegacyTask
            : ReportScoreExtension.None;
    }

    /// <summary>
    /// The watch (METRICS 11.10, Watch): reporting on for a confirmed and registered service, the network on, not paused, no
    /// error or stop that needs the user, <c>perfScore</c> active and seeding finished. Backoffs, waits, quiet and new runs
    /// leave it on. Evaluated after each tick's actions, so a leaving report takes the rows before the break empties them.
    /// </summary>
    private void FollowPerfWatch()
    {
        var on = IsReady() && NetworkEnabled && !IsPaused && PerfScoreActive && !(UsesWebSocket && _stream?.Scheduler.Stop is not null);
        if (_perf.Update(on, Identity?.Id))
        {
            _hub.PerfWatch = _perf.Watch;
            ReportLog.Write(on ? $"perf watch on (generation {_perf.Watch.Generation})" : "perf watch off");
        }
    }

    /// <summary>A build takes the statistics rows since the previous build; the engine learns how many it took (the 100 000 caps).</summary>
    private IReadOnlyDictionary<ReportModelKey, PerfSums> TakePerf(ReportSample sample)
    {
        var taken = _perf.Take(sample);
        _hub.PerfWatch = _perf.Watch;
        return taken;
    }
}
