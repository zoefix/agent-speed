using System.Globalization;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.History.Perf;

/// <summary>One day of the trend: its date, its pooled score (null: a gap) and its timed requests.</summary>
public sealed record PerfChartDay(DateOnly Day, int? Score, long Timed);

/// <summary>
/// The selected row's daily trend (DESIGN 12, <i>Trend</i>): one slot per day of the range (a single-day range: the 14 days
/// ending on it), the log y scale from 250 to <see cref="Top"/>, the 1000 line, the usual line and the words a screen
/// reader speaks.
/// </summary>
/// <param name="Highlighted">A single-day range: the chosen day's slot is tinted and its dot larger.</param>
public sealed record PerfHistoryChart(
    IReadOnlyList<PerfChartDay> Days,
    double Top,
    IReadOnlyList<int> Gridlines,
    double? Usual,
    DateOnly? Highlighted,
    string Title,
    string Spoken)
{
    public bool IsEmpty => Days.All(d => d.Score is null);

    /// <summary>The latest day with a score: its dot is the larger one.</summary>
    public int LatestIndex
    {
        get
        {
            for (var i = Days.Count - 1; i >= 0; i--)
            {
                if (Days[i].Score is not null)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>A score's height from the bottom (0) to the top (1) of the plot.</summary>
    public double Y(double score) => PerfScale.ChartFraction(score, Top);

    /// <param name="days">The trend's days in order with their pooled scores and counts.</param>
    /// <param name="usual">The row's baseline (the 14 days before today), drawn dashed; null without one.</param>
    public static PerfHistoryChart Of(PerfHistoryRow row, IReadOnlyList<PerfChartDay> days, DateOnly? highlighted, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(loc);
        var highest = days.Where(d => d.Score is not null).Select(d => (double)d.Score!.Value).DefaultIfEmpty(0).Max();
        var top = PerfScale.ChartTop(Math.Max(highest, row.Baseline ?? 0));
        var subject = row.ModelText + " · " + row.SourceName;
        var title = loc.Format("history.perf.trend", subject);
        if (highlighted is { } day)
        {
            title += " · " + loc.Format("history.perf.trend.days", HistoryText.Date(day, loc));
        }

        var scored = days.Where(d => d.Score is not null).Select(d => d.Score!.Value).ToList();
        var spoken = scored.Count == 0
            ? loc.Format("a11y.history.perf.chart.empty", subject)
            : loc.Format("a11y.history.perf.chart", subject, PerfNumber.Spoken(scored.Min()), PerfNumber.Spoken(scored.Max()),
                PerfNumber.Spoken(scored[^1]));
        return new PerfHistoryChart(days, top, PerfScale.Gridlines(top), row.Baseline, highlighted, title, spoken);
    }

    /// <summary>The trend of <paramref name="row"/> as stored with it (the range's days).</summary>
    public static IReadOnlyList<PerfChartDay> DaysOf(IReadOnlyList<PerfTrendPoint> trend, IReadOnlyList<PerfRecord> records, SourceKindModel key)
    {
        ArgumentNullException.ThrowIfNull(trend);
        ArgumentNullException.ThrowIfNull(records);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Source == key.Source && string.Equals(record.Model, key.Model, StringComparison.Ordinal))
            {
                counts[record.Day] = counts.GetValueOrDefault(record.Day) + record.Sums.N;
            }
        }

        return [.. trend.Select(p => new PerfChartDay(
            DateOnly.ParseExact(p.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            p.Score is { } s ? PerfFormula.Round(s) : null,
            counts.GetValueOrDefault(p.Day)))];
    }
}

/// <summary>A row's identity: a source and a raw model.</summary>
public readonly record struct SourceKindModel(AgentSpeed.Core.SourceKind Source, string Model);
