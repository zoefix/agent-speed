using AgentSpeed.History.Perf;
using AgentSpeed.Presentation.History.Perf;

namespace AgentSpeed.History;

/// <summary>
/// The 性能表現 section (DESIGN 4b, 3c; DESIGN 12): the daily performance records of the window's range pooled per source and
/// model (METRICS 6.11) — narrowed to one source when the window's picker selects one — with the selected row's trend.
/// </summary>
internal sealed partial class HistoryViewModel
{
    private PerfHistoryViewModel? _performance;

    public PerfHistoryViewModel Performance => _performance ??= new PerfHistoryViewModel(_loc);

    /// <param name="sort">The table's remembered sort, or null for the default.</param>
    /// <param name="changed">Persists a new sort.</param>
    public void RememberPerfSort(PerfSort? sort, Action<PerfSort> changed)
    {
        Performance.RememberSort(sort, changed);
        UpdatePerformance();
    }

    public void SetPerfAppearance(bool dark, bool highContrast, bool reduceMotion) => Performance.SetAppearance(dark, highContrast, reduceMotion);

    private void UpdatePerformance() => Performance.Update(_hub.PerfRecords, Range.Start, Range.End, Today, _source, IsAnalyzing);

    private void RelocalizePerformance() => _performance?.Relocalize(_loc);

    private void DisposePerformance() => _performance?.Dispose();
}
