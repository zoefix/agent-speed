using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.History.Perf;

/// <summary>One component cell of a row: the pooled value as the card writes it, or <c>–</c> with why it is absent.</summary>
/// <param name="Fill">Its sub-score on the log scale (the medium layout's bar); null when absent (a dotted stub).</param>
/// <param name="Value">Its sub-score, for sorting; null when absent.</param>
public sealed record PerfHistoryCell(PerfComponentId Id, string Text, bool Present, string Tooltip, double? Fill, string SubScore, double? Value);

/// <summary>
/// One row of 性能表現 (DESIGN 12, <i>Usage History: performance</i>): a source and model over the range — its pooled score
/// and meter, its usual value, its timed requests, its seven components — and the words a screen reader speaks for it.
/// </summary>
public sealed record PerfHistoryRow(
    SourceKind Source,
    string Model,
    string ModelText,
    string SourceName,
    int? Score,
    double ScoreFill,
    string ScoreText,
    string ScoreTooltip,
    int? Usual,
    string UsualText,
    string UsualTooltip,
    long Timed,
    string TimedText,
    IReadOnlyList<PerfHistoryCell> Cells,
    IReadOnlyList<PerfTrendPoint> Trend,
    double? Baseline)
{
    /// <summary>The row's identity, kept across ranges for the chart's selection.</summary>
    public (SourceKind Source, string Model) Key => (Source, Model);

    public bool HasScore => Score is not null;

    /// <summary>The medium layout's 構成 column for screen readers: <c>a11y.history.perf.parts</c> with every <c>label sub-score</c>.</summary>
    public string PartsSpoken { get; init; } = string.Empty;

    /// <summary>The narrow layout's line 3: the present components as <c>label value</c> joined with <c> · </c>.</summary>
    public string PresentLine { get; init; } = string.Empty;

    public static PerfHistoryRow Of(PerfRangeRow row, double? baseline, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(loc);
        var pooled = row.Pooled;
        var sourceName = loc[DisplayText.SourceKey(row.Source)];
        var score = pooled.Shown;
        var scoreTooltip = score is null ? loc.Format("history.perf.sample.hint", pooled.N, PerfFormula.PooledMinN) : string.Empty;
        var usual = baseline is { } b ? PerfFormula.Round(b) : (int?)null;
        var cells = new List<PerfHistoryCell>(7);
        foreach (var id in PerfText.CardOrder)
        {
            var component = pooled.Component(id);
            if (component.Measure is { } measure && component.Ratio is { } ratio)
            {
                var text = PerfText.Measure(id, measure, loc);
                cells.Add(new PerfHistoryCell(id, text, true, loc[PerfText.ComponentKey(id) + ".desc"], PerfScale.Fraction(1000 * ratio),
                    PerfText.SubScore(component.SubScore), component.SubScore));
            }
            else
            {
                var hint = component.Reason == PerfAbsence.NotRecorded
                    ? loc.Format("perf.reason.notRecorded.hint", sourceName)
                    : loc.Format("perf.reason.fewSamples.hint", PerfFormula.MinSamples);
                cells.Add(new PerfHistoryCell(id, PerfNumber.Dash, false, hint, null, PerfNumber.Dash, null));
            }
        }

        var parts = string.Join(loc["list.separator"], cells.Select(c => loc[PerfText.ComponentKey(c.Id)] + " " + c.SubScore));
        var present = string.Join(" · ", cells.Where(c => c.Present).Select(c => loc[PerfText.ColumnKey(c.Id)] + " " + c.Text));
        return new PerfHistoryRow(
            row.Source,
            row.Model,
            row.Model.Length == 0 ? loc["cost.noModelName"] : row.Model,
            sourceName,
            score,
            score is { } v ? PerfScale.Fraction(v) : 0,
            PerfNumber.Full(score),
            scoreTooltip,
            usual,
            PerfNumber.Full(usual),
            usual is null ? loc.Format("perf.usual.none.hint", PerfFormula.PooledMinN) : loc["history.perf.usual.hint"],
            pooled.N,
            PerfNumber.Grouped(pooled.N),
            cells,
            row.Trend,
            baseline)
        {
            PartsSpoken = loc.Format("a11y.history.perf.parts", parts),
            PresentLine = present,
        };
    }
}
