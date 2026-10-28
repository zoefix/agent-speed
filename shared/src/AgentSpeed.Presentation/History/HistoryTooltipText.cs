using AgentSpeed.Core;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History;

/// <summary>A line of the chart's hover card.</summary>
/// <param name="Source">The source of an indented per-source line (drawn with its color dot).</param>
/// <param name="Strong">The value the chart shows (output, or cost for the cost metric).</param>
/// <param name="IsNote">A small explanatory line under the one before it (the label is the whole text).</param>
public sealed record TooltipRow(string Label, string Value, SourceKind? Source = null, bool Strong = false, bool IsNote = false);

/// <summary>One of a day's top models: name, average speed (empty when not shown), share of the output and cost.</summary>
public sealed record TooltipModelRow(string Name, double Share, string Percent, string Speed, string Cost);

public sealed record TooltipContent(
    string Title,
    string Badge,
    IReadOnlyList<TooltipRow> Rows,
    string Note,
    string ModelsTitle,
    IReadOnlyList<TooltipModelRow> Models);

/// <summary>
/// The chart's hover card (DESIGN 4b item 3, DESIGN 9): the chart's metric first with its split by source, the day's other
/// figures, always a cost line, and the top three models with their share and cost.
/// </summary>
public static class HistoryTooltipText
{
    /// <param name="order">The chart's stacking order, for the per-source lines.</param>
    /// <param name="allSources">Every source is shown (per-source lines only then).</param>
    public static TooltipContent Build(DaySummary day, IReadOnlyList<SourceKind> order, bool allSources, HistoryChartMetric metric,
        DateOnly today, HistoryMoney money, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(loc);
        var cost = metric == HistoryChartMetric.Cost;
        var rows = new List<TooltipRow>();
        if (cost)
        {
            AddCost(rows, day, money, loc, strong: true);
            AddSources(rows, day, order, allSources, metric, money, loc);
            rows.Add(new TooltipRow(loc["history.table.output"], SpeedFormat.Tokens(day.Output)));
        }
        else
        {
            rows.Add(new TooltipRow(loc["history.table.output"], SpeedFormat.Tokens(day.Output), Strong: true));
            AddSources(rows, day, order, allSources, metric, money, loc);
        }

        rows.Add(new TooltipRow(loc["history.table.input"], SpeedFormat.Tokens(day.Input)));
        rows.Add(new TooltipRow(loc["history.table.requests"], HistoryText.FullNumber(day.Requests, loc)));
        rows.Add(new TooltipRow(loc["history.table.speed"], HistoryText.Speed(day.AverageSpeed, loc)));
        if (day.Peak is { } peak)
        {
            rows.Add(new TooltipRow(loc["history.table.peak"], HistoryText.Speed(peak, loc)));
        }

        if (!cost)
        {
            AddCost(rows, day, money, loc, strong: false);
        }

        foreach (var mode in ExecutionModeText.VisibleUsage(day.Modes))
            rows.Add(new TooltipRow(mode.Model, ExecutionModeText.Usage(mode, loc), mode.Source));

        var models = day.Models.Take(3)
            .Select(m =>
            {
                var share = day.Output > 0 ? (double)m.Output / day.Output : 0;
                var speed = !cost && m.AverageSpeed is { } value && double.IsFinite(value) && value > 0 ? HistoryText.Speed(value, loc) : string.Empty;
                return new TooltipModelRow(m.Model, share, HistoryText.Percent(share), speed,
                    HistoryText.ModelCostText(day.CostOf(m.Model), money, loc));
            })
            .ToArray();
        return new TooltipContent(
            HistoryText.LongDate(day.Day, loc),
            day.Day == today ? loc["history.inProgress"] : string.Empty,
            rows,
            day.Unfinalized > 0 ? loc.Format("history.unfinalized", day.Unfinalized) : string.Empty,
            loc["history.table.models"],
            models);
    }

    /// <summary>
    /// The traffic chart's hover card (DESIGN 10, Usage history): the day's pair, the tools' part when tools are included,
    /// this computer's total and how long a partial day was recorded; before measuring began only
    /// <c>history.traffic.notRecorded</c>.
    /// </summary>
    public static TooltipContent BuildTraffic(DateOnly day, DayTraffic? traffic, DateOnly today, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var rows = new List<TooltipRow>(4);
        if (traffic is null)
        {
            rows.Add(new TooltipRow(loc["history.traffic.notRecorded"], string.Empty, IsNote: true));
        }
        else
        {
            rows.Add(new TooltipRow(loc["history.table.traffic"], HistoryTrafficText.Pair(traffic.Shown, loc), Strong: true));
            rows.AddRange(HistoryTrafficText.Notes(traffic, loc).Select(note => new TooltipRow(note, string.Empty, IsNote: true)));
        }

        return new TooltipContent(HistoryText.LongDate(day, loc), day == today ? loc["history.inProgress"] : string.Empty, rows,
            string.Empty, string.Empty, []);
    }

    /// <summary>A traffic day as one sentence for screen readers: the date, then its traffic or that it was not recorded.</summary>
    public static string AccessibleTraffic(DateOnly day, DayTraffic? traffic, DateOnly today, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var title = day == today ? $"{HistoryText.LongDate(day, loc)} {loc["history.inProgress"]}" : HistoryText.LongDate(day, loc);
        var parts = new List<string> { title };
        if (traffic is null)
        {
            parts.Add(loc["history.traffic.notRecorded"]);
        }
        else
        {
            parts.Add(HistoryText.Labelled(loc["history.table.traffic"], HistoryTrafficText.Spoken(traffic.Shown, loc), loc));
            parts.AddRange(HistoryTrafficText.Notes(traffic, loc));
        }

        return HistoryText.JoinParts(parts, loc);
    }

    /// <summary>The day's cost, always present: the amount (with US dollars beside another currency), <c>–</c> or the tag, and why.</summary>
    private static void AddCost(List<TooltipRow> rows, DaySummary day, HistoryMoney money, Localizer loc, bool strong)
    {
        var value = day.Cost.Kind == HistoryCostKind.Priced
            ? (day.Cost.IsPartial ? CostDisplay.AtLeastSign : string.Empty) + money.AmountWithUsd(day.Cost.MicroUsd)
            : money.Text(day.Cost, loc["cost.subscription.tag"]);
        rows.Add(new TooltipRow(loc["history.table.cost"], value, Strong: strong));
        if (HistoryText.CostNote(day.Cost, loc) is { } note)
        {
            rows.Add(new TooltipRow(note, string.Empty, IsNote: true));
        }
    }

    /// <summary>The chart metric per source, in stacking order, when more than one source has some.</summary>
    private static void AddSources(List<TooltipRow> rows, DaySummary day, IReadOnlyList<SourceKind> order, bool allSources,
        HistoryChartMetric metric, HistoryMoney money, Localizer loc)
    {
        if (!allSources)
        {
            return;
        }

        var cost = metric == HistoryChartMetric.Cost;
        var parts = order
            .Select(source => (Source: source, Value: cost ? day.CostMicroUsdOf(source) : day.OutputOf(source)))
            .Where(p => p.Value > 0)
            .ToArray();
        if (parts.Length < 2)
        {
            return;
        }

        foreach (var (source, value) in parts)
        {
            rows.Add(new TooltipRow(loc[DisplayText.SourceKey(source)], cost ? money.Amount(value) : SpeedFormat.Tokens(value), source));
        }
    }
}
