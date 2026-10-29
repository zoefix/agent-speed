using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History.Perf;

/// <summary>What the section shows (DESIGN 12, <i>States</i>).</summary>
public enum PerfHistoryState
{
    /// <summary>The store upgrade re-analyses and the range has no records yet: placeholders.</summary>
    Analyzing,

    /// <summary>No timed request in the range for any source.</summary>
    Empty,

    Rows,
}

/// <summary>
/// The 性能表現 section of the Usage History window (DESIGN 12; METRICS 6.11 <i>History window</i>): every source and model of
/// the range with its pooled values, sorted as the window remembers, and the selected row's daily trend. Immutable: a new
/// range, source, sort or selection builds a new one.
/// </summary>
public sealed record PerfHistorySection(
    PerfHistoryState State,
    IReadOnlyList<PerfHistoryRow> Rows,
    PerfHistoryRow? Selected,
    PerfHistoryChart? Chart,
    PerfSort Sort)
{
    public static PerfHistorySection Build(
        IReadOnlyList<PerfRecord> records,
        DateOnly first,
        DateOnly last,
        SourceKind? source,
        DateOnly today,
        bool analyzing,
        PerfSort sort,
        SourceKindModel? selected,
        Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(loc);
        var from = Day(first);
        var to = Day(last);
        var todayText = Day(today);
        var range = PerfHistory.RangeOf(records, from, to, source);
        if (range.Count == 0)
        {
            return new PerfHistorySection(analyzing ? PerfHistoryState.Analyzing : PerfHistoryState.Empty, [], null, null, sort);
        }

        var history = new PerfHistory(records, todayText);
        var rows = range.Select(r => PerfHistoryRow.Of(r, history.Baseline(r.Source, r.Model), loc)).ToList();
        var sorted = sort.Apply(rows);

        // The default selection is the row with the most timed requests (the fullest trend; ties in the table's order).
        var chosen = selected is { } key ? sorted.FirstOrDefault(r => r.Source == key.Source && r.Model == key.Model) : null;
        chosen ??= sorted.Aggregate((best, r) => r.Timed > best.Timed ? r : best);

        IReadOnlyList<PerfChartDay> days;
        DateOnly? highlighted = null;
        if (first == last)
        {
            // A single-day range shows the 14 days ending on that day.
            var start = first.AddDays(-(PerfFormula.BaselineDays - 1));
            var trend = PerfHistory.RangeOf(records, Day(start), to, chosen.Source)
                .FirstOrDefault(r => r.Model == chosen.Model)?.Trend ?? chosen.Trend;
            days = PerfHistoryChart.DaysOf(trend, records, new SourceKindModel(chosen.Source, chosen.Model));
            highlighted = first;
        }
        else
        {
            days = PerfHistoryChart.DaysOf(chosen.Trend, records, new SourceKindModel(chosen.Source, chosen.Model));
        }

        return new PerfHistorySection(PerfHistoryState.Rows, sorted, chosen, PerfHistoryChart.Of(chosen, days, highlighted, loc), sort);
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
