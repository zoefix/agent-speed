using AgentSpeed.Core;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History;

public enum HistoryColumn
{
    Date,
    Requests,
    Output,
    Input,
    Cost,

    /// <summary>Observed traffic (DESIGN 10); shown once traffic was ever measured.</summary>
    Traffic,
    CacheHit,
    Speed,
    Peak,
    Agents,
    Models,
}

/// <summary>How wide a column would like to be, how narrow it may get, and its share of spare width.</summary>
public readonly record struct ColumnSpec(double Natural, double Minimum, double Weight = 0);

public readonly record struct ColumnSpan(double X, double Width)
{
    public double Right => X + Width;
}

/// <summary>Column positions and the cell padding (each side) they were laid out with.</summary>
public sealed record TableFit(IReadOnlyList<ColumnSpan> Spans, double Padding);

/// <summary>Columns, sorting, cell text and column layout of the daily table (DESIGN 4b, item 4).</summary>
public static class HistoryTable
{
    public static IReadOnlyList<HistoryColumn> Columns { get; } = Enum.GetValues<HistoryColumn>();

    public static string HeaderKey(HistoryColumn column) => column switch
    {
        HistoryColumn.Date => "history.table.date",
        HistoryColumn.Requests => "history.table.requests",
        HistoryColumn.Output => "history.table.output",
        HistoryColumn.Input => "history.table.input",
        HistoryColumn.Cost => "history.table.cost",
        HistoryColumn.Traffic => "history.table.traffic",
        HistoryColumn.CacheHit => "history.table.cacheHit",
        HistoryColumn.Speed => "history.table.speed",
        HistoryColumn.Peak => "history.table.peak",
        HistoryColumn.Agents => "history.table.agents",
        _ => "history.table.models",
    };

    /// <summary>Numbers are right-aligned; the date and the model names are not.</summary>
    public static bool IsNumeric(HistoryColumn column) => column is not (HistoryColumn.Date or HistoryColumn.Models);

    /// <summary>The first click on a header sorts largest (or newest) first; model names sort A→Z.</summary>
    public static bool DefaultDescending(HistoryColumn column) => column != HistoryColumn.Models;

    /// <summary>
    /// Stable sort. Days without a value in the column (no measured speed, no peak, no models) always come last; ties
    /// keep the newest day first.
    /// </summary>
    public static IReadOnlyList<DaySummary> Sort(IEnumerable<DaySummary> rows, HistoryColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        if (column == HistoryColumn.Models)
        {
            var models = list.OrderBy(r => r.Models.Count == 0)
                .ThenBy(r => r.Models.Count == 0 ? string.Empty : r.Models[0].Model, descending ? DescendingText.Instance : StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Day);
            return models.ToArray();
        }

        var keyed = list.Select(r => (Row: r, Key: Key(r, column))).ToList();
        var present = keyed.Where(k => k.Key is not null);
        var ordered = descending
            ? present.OrderByDescending(k => k.Key!.Value).ThenByDescending(k => k.Row.Day)
            : present.OrderBy(k => k.Key!.Value).ThenByDescending(k => k.Row.Day);
        return ordered.Concat(keyed.Where(k => k.Key is null).OrderByDescending(k => k.Row.Day)).Select(k => k.Row).ToArray();
    }

    public static string Cell(DaySummary row, HistoryColumn column, DateOnly today, Localizer loc, HistoryMoney money)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(money);
        return column switch
        {
            HistoryColumn.Date => HistoryText.TableDate(row.Day, today, loc),
            HistoryColumn.Requests => HistoryText.FullNumber(row.Requests, loc),
            HistoryColumn.Output => SpeedFormat.Tokens(row.Output),
            HistoryColumn.Input => SpeedFormat.Tokens(row.Input),
            HistoryColumn.Cost => money.Text(row.Cost, loc["cost.subscription.tag"]),
            HistoryColumn.Traffic => HistoryTrafficText.Cell(row.Traffic, loc),
            HistoryColumn.CacheHit => HistoryText.Percent(row.CacheHitRatio),
            HistoryColumn.Speed => HistoryText.Speed(row.AverageSpeed, loc),
            HistoryColumn.Peak => row.Peak is { } peak ? HistoryText.Speed(peak, loc) : "—",
            HistoryColumn.Agents => HistoryText.FullNumber(row.Agents, loc),
            _ => HistoryText.TopModels(row.Models, loc),
        };
    }

    /// <summary>A cell as a screen reader reads it: the cost with the currency's name (DESIGN 9), every other cell as shown.</summary>
    public static string AccessibleCell(DaySummary row, HistoryColumn column, DateOnly today, Localizer loc, HistoryMoney money) => column switch
    {
        HistoryColumn.Cost => HistoryText.SpokenCost(row.Cost, money, loc),
        HistoryColumn.Traffic => HistoryTrafficText.AccessibleCell(row.Traffic, loc),
        _ => Cell(row, column, today, loc, money),
    };

    /// <summary>
    /// The cost cell's tooltip: the amount with US dollars beside another currency, and why a value is partial or missing
    /// (<c>history.unpricedDay</c>, <c>history.costUnavailable</c>, the excluded subscriptions); null when there is nothing to add.
    /// </summary>
    public static string? CostTooltip(HistoryCost cost, HistoryMoney money, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new List<string>(2);
        if (cost.Kind == HistoryCostKind.Priced)
        {
            lines.Add((cost.IsPartial ? CostDisplay.AtLeastSign : string.Empty) + money.AmountWithUsd(cost.MicroUsd));
        }

        if (HistoryText.CostNote(cost, loc) is { } note)
        {
            lines.Add(note);
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>The row as tab-separated cells (pastes into a spreadsheet as one row).</summary>
    public static string RowText(DaySummary row, DateOnly today, Localizer loc, HistoryMoney money) => RowText(row, today, loc, money, Columns);

    /// <summary>The given columns of the row as tab-separated cells.</summary>
    public static string RowText(DaySummary row, DateOnly today, Localizer loc, HistoryMoney money, IEnumerable<HistoryColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return string.Join('\t', columns.Select(c => Cell(row, c, today, loc, money)));
    }

    public static string HeaderText(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return string.Join('\t', Columns.Select(c => loc[HeaderKey(c)]));
    }

    /// <summary>
    /// Lays columns out left to right with <paramref name="gap"/> between them. Spare width goes to columns with a
    /// weight (in proportion); when space is short, columns give up width in proportion to how far they are above their
    /// minimum, and never go below it.
    /// </summary>
    public static IReadOnlyList<ColumnSpan> Layout(IReadOnlyList<ColumnSpec> specs, double width, double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var count = specs.Count;
        var spans = new ColumnSpan[count];
        if (count == 0)
        {
            return spans;
        }

        var widths = specs.Select(s => Math.Max(0, Math.Max(s.Natural, s.Minimum))).ToArray();
        var available = Math.Max(0, (double.IsFinite(width) ? width : 0) - gap * (count - 1));
        var natural = widths.Sum();
        if (available >= natural)
        {
            var extra = available - natural;
            var weights = specs.Sum(s => Math.Max(0, s.Weight));
            for (var i = 0; i < count; i++)
            {
                widths[i] += weights > 0 ? extra * Math.Max(0, specs[i].Weight) / weights : i == count - 1 ? extra : 0;
            }
        }
        else
        {
            var slack = specs.Select((s, i) => Math.Max(0, widths[i] - Math.Max(0, s.Minimum))).ToArray();
            var totalSlack = slack.Sum();
            var shortfall = natural - available;
            var share = totalSlack > 0 ? Math.Min(1, shortfall / totalSlack) : 0;
            for (var i = 0; i < count; i++)
            {
                widths[i] -= slack[i] * share;
            }
        }

        var x = 0.0;
        for (var i = 0; i < count; i++)
        {
            spans[i] = new ColumnSpan(x, widths[i]);
            x += widths[i] + gap;
        }

        return spans;
    }

    /// <summary>
    /// Lays out columns given by their content widths (cell padding excluded): with <paramref name="padding"/> on each side
    /// while every column fits at its minimum, else with the padding tightened toward <paramref name="minimumPadding"/>,
    /// and only then with every column below its minimum (scaled alike), so a narrow window never clips the table. Columns
    /// with no width (hidden) get no padding.
    /// </summary>
    public static TableFit Fit(IReadOnlyList<ColumnSpec> content, double width, double padding, double minimumPadding)
    {
        ArgumentNullException.ThrowIfNull(content);
        var available = Math.Max(0, double.IsFinite(width) ? width : 0);
        var shown = content.Count(IsShown);
        var minimums = content.Where(IsShown).Sum(c => Math.Max(0, c.Minimum));
        var used = padding;
        if (shown > 0 && minimums + 2 * padding * shown > available)
        {
            used = Math.Clamp((available - minimums) / (2 * shown), Math.Min(minimumPadding, padding), padding);
        }

        var specs = content.Select(c => IsShown(c)
            ? new ColumnSpec(Math.Max(c.Natural, c.Minimum) + 2 * used, Math.Max(0, c.Minimum) + 2 * used, c.Weight)
            : new ColumnSpec(0, 0)).ToArray();
        var padded = specs.Sum(s => s.Minimum);
        if (padded <= available + 0.001 || padded <= 0)
        {
            return new TableFit(Layout(specs, available), used);
        }

        var scale = available / padded;
        var spans = new ColumnSpan[specs.Length];
        var x = 0.0;
        for (var i = 0; i < specs.Length; i++)
        {
            spans[i] = new ColumnSpan(x, specs[i].Minimum * scale);
            x += spans[i].Width;
        }

        return new TableFit(spans, used);

        static bool IsShown(ColumnSpec spec) => Math.Max(spec.Natural, spec.Minimum) > 0;
    }

    private static double? Key(DaySummary row, HistoryColumn column) => column switch
    {
        HistoryColumn.Date => row.Day.DayNumber,
        HistoryColumn.Requests => row.Requests,
        HistoryColumn.Output => row.Output,
        HistoryColumn.Input => row.Input,
        HistoryColumn.Cost => row.Cost.SortValue,
        HistoryColumn.Traffic => row.Traffic?.Shown.Sum,
        HistoryColumn.CacheHit => row.CacheHitRatio,
        HistoryColumn.Speed => row.AverageSpeed,
        HistoryColumn.Peak => row.Peak,
        HistoryColumn.Agents => row.Agents,
        _ => null,
    };

    private sealed class DescendingText : IComparer<string>
    {
        public static DescendingText Instance { get; } = new();

        public int Compare(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Compare(y, x);
    }
}
