using AgentSpeed.Core;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History;

public enum SummaryMetric
{
    Output,
    Input,
    Requests,
    Speed,
    Cost,
}

/// <param name="Value">Compact value (METRICS 7).</param>
/// <param name="Unit">Unit drawn after the value (speed only).</param>
/// <param name="FullValue">Exact value for the tooltip.</param>
/// <param name="Secondary">Active days, busiest day or the change against the previous period or day.</param>
/// <param name="Change">The change shown in <paramref name="Secondary"/>, when it is one (colored green/red unless <see cref="NeutralChange"/>).</param>
/// <param name="Spark">One value per day of <see cref="HistoryRanges.SparkRange"/>; null where the line breaks.</param>
/// <param name="Highlight">Index in <paramref name="Spark"/> of the selected day, for a single-day range.</param>
public sealed record SummaryCard(
    SummaryMetric Metric,
    string Label,
    string Value,
    string Unit,
    string FullValue,
    string Secondary,
    double? Change,
    IReadOnlyList<double?> Spark,
    int? Highlight = null)
{
    /// <summary>A note added to the tooltip and read as the card's help text (<c>history.costFrozen</c>).</summary>
    public string? Note { get; init; }

    /// <summary>How accessibility reads the value (<c>12.34 美元</c>); null reads <see cref="Value"/> with its unit.</summary>
    public string? Spoken { get; init; }

    /// <summary>The change is shown without green or red (money is never colored, DESIGN 9).</summary>
    public bool NeutralChange { get; init; }
}

/// <summary>The summary cards (DESIGN 4b, item 2, and the cost card of DESIGN 9).</summary>
public static class HistorySummary
{
    public static string LabelKey(SummaryMetric metric) => metric switch
    {
        SummaryMetric.Output => "history.summary.output",
        SummaryMetric.Input => "history.summary.input",
        SummaryMetric.Requests => "history.summary.requests",
        SummaryMetric.Cost => "history.summary.cost",
        _ => "history.summary.speed",
    };

    /// <summary>The five cards, the cost card last.</summary>
    /// <param name="costLabelKey"><c>history.summary.cost</c>, or <c>history.summary.costApi</c> for a subscription source selected directly.</param>
    public static IReadOnlyList<SummaryCard> Build(IReadOnlyList<DaySummary> days, IReadOnlyList<DaySummary> previous, DayRange range,
        DateOnly today, Localizer loc, HistoryMoney money, string costLabelKey, IReadOnlyList<DaySummary>? sparkDays = null) =>
        [.. Build(days, previous, range, today, loc, sparkDays), CostCard(days, previous, range, loc, money, costLabelKey, sparkDays)];

    /// <summary>
    /// <c>費用（API 等值）</c>: the range's cost, its average per day with cost data (the change against the day before for a
    /// single day), and a sparkline of daily cost. Amounts convert with the one rate in use, so the card equals the sum of
    /// its days (METRICS 9.8).
    /// </summary>
    public static SummaryCard CostCard(IReadOnlyList<DaySummary> days, IReadOnlyList<DaySummary> previous, DayRange range, Localizer loc,
        HistoryMoney money, string costLabelKey = "history.summary.cost", IReadOnlyList<DaySummary>? sparkDays = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(money);
        var totals = HistoryAggregator.Totals(days);
        var cost = totals.Cost;
        var tag = loc["cost.subscription.tag"];
        var full = money.Text(cost, tag);
        var spoken = HistoryText.SpokenCost(cost, money, loc);
        var secondary = string.Empty;
        double? change = null;
        switch (cost.Kind)
        {
            case HistoryCostKind.Priced:
                full = (cost.IsPartial ? CostDisplay.AtLeastSign : string.Empty) + money.AmountWithUsd(cost.MicroUsd);
                if (cost.IsPartial)
                {
                    full += Environment.NewLine + loc.Format("history.unpricedDay", cost.Unpriced);
                }

                if (range.IsSingleDay)
                {
                    var before = HistoryAggregator.Totals(previous).Cost;
                    if (before.Tally is not null)
                    {
                        change = HistoryAggregator.Change(cost.MicroUsd, before.MicroUsd);
                        secondary = change is { } c ? HistoryText.VersusPreviousDay(c, loc) : string.Empty;
                    }
                }
                else if (totals.CostDays > 0)
                {
                    secondary = loc.Format("history.summary.costPerDay", money.Amount(cost.MicroUsd / totals.CostDays));
                }

                break;
            case HistoryCostKind.Unknown:
                full = loc.Format("history.unpricedDay", cost.Requests);
                break;
            case HistoryCostKind.Unavailable:
                full = loc["history.costUnavailable"];
                break;
            case HistoryCostKind.Subscription:
                full = loc.Format("cost.subscriptionExcluded", HistoryText.SourceList(cost.Excluded, loc));
                break;
        }

        var sparkRange = HistoryRanges.SparkRange(range);
        var spark = HistoryAggregator.Series(sparkDays ?? days, sparkRange,
            d => d.Cost.Tally is null && d.Cost.Requests > 0 ? null : money.Value(d.Cost.MicroUsd));
        return new SummaryCard(SummaryMetric.Cost, loc[costLabelKey], money.Text(cost, tag), string.Empty, full, secondary, change, spark,
            range.IsSingleDay ? sparkRange.IndexOf(range.Start) : null)
        {
            Note = loc["history.costFrozen"],
            Spoken = spoken,
            NeutralChange = true,
        };
    }

    /// <summary>The four token cards.</summary>
    /// <param name="days">Days with data in <paramref name="range"/>.</param>
    /// <param name="previous">Days with data in the equally long period before it (the day before a single day).</param>
    /// <param name="sparkDays">
    /// Days with data in <see cref="HistoryRanges.SparkRange"/>, for a single day whose sparklines show the week up to it;
    /// null uses <paramref name="days"/>.
    /// </param>
    public static IReadOnlyList<SummaryCard> Build(IReadOnlyList<DaySummary> days, IReadOnlyList<DaySummary> previous, DayRange range,
        DateOnly today, Localizer loc, IReadOnlyList<DaySummary>? sparkDays = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(loc);
        var totals = HistoryAggregator.Totals(days);
        var before = HistoryAggregator.Totals(previous);
        var unit = loc["unit.tokS"];
        var lines = range.IsSingleDay ? DayLines(totals, before, loc) : PeriodLines(days, totals, before, today, loc);

        var sparkRange = HistoryRanges.SparkRange(range);
        var spark = sparkDays ?? days;
        int? highlight = range.IsSingleDay ? sparkRange.IndexOf(range.Start) : null;

        return
        [
            new SummaryCard(SummaryMetric.Output, loc[LabelKey(SummaryMetric.Output)], SpeedFormat.Tokens(totals.Output), string.Empty,
                HistoryText.FullNumber(totals.Output, loc), lines.Output.Text, lines.Output.Change,
                HistoryAggregator.Series(spark, sparkRange, d => d.Output), highlight),
            new SummaryCard(SummaryMetric.Input, loc[LabelKey(SummaryMetric.Input)], SpeedFormat.Tokens(totals.Input), string.Empty,
                HistoryText.FullNumber(totals.Input, loc), lines.Input.Text, lines.Input.Change,
                HistoryAggregator.Series(spark, sparkRange, d => d.Input), highlight),
            new SummaryCard(SummaryMetric.Requests, loc[LabelKey(SummaryMetric.Requests)], SpeedFormat.Tokens(totals.Requests), string.Empty,
                HistoryText.FullNumber(totals.Requests, loc), lines.Requests.Text, lines.Requests.Change,
                HistoryAggregator.Series(spark, sparkRange, d => d.Requests), highlight),
            new SummaryCard(SummaryMetric.Speed, loc[LabelKey(SummaryMetric.Speed)],
                totals.AverageSpeed is { } speed ? SpeedFormat.Speed(speed) : "—", totals.AverageSpeed is null ? string.Empty : unit,
                HistoryText.Speed(totals.AverageSpeed, loc), lines.Speed.Text, lines.Speed.Change,
                HistoryAggregator.Series(spark, sparkRange, d => d.AverageSpeed, missing: null), highlight),
        ];
    }

    /// <summary>A range of days: the change against the previous period, the busiest day and the active days.</summary>
    private static CardLines PeriodLines(IReadOnlyList<DaySummary> days, HistoryTotals totals, HistoryTotals before, DateOnly today,
        Localizer loc)
    {
        var outputChange = HistoryAggregator.Change(totals.Output, before.Output);
        var outputSecondary = outputChange is { } oc
            ? HistoryText.VersusPrevious(oc, loc)
            : totals.BusiestDay is { Output: > 0 } busiest ? HistoryText.BusiestDay(busiest.Day, today, loc) : string.Empty;

        var busiestInput = days.Count == 0 ? null : days.MaxBy(d => (d.Input, d.Day.DayNumber));
        var inputSecondary = busiestInput is { Input: > 0 } bi ? HistoryText.BusiestDay(bi.Day, today, loc) : string.Empty;

        var speedChange = HistoryAggregator.Change(totals.AverageSpeed, before.AverageSpeed);
        return new CardLines(
            new CardLine(outputSecondary, outputChange),
            new CardLine(inputSecondary, null),
            new CardLine(totals.ActiveDays > 0 ? HistoryText.ActiveDays(totals.ActiveDays, loc) : string.Empty, null),
            new CardLine(speedChange is { } sc ? HistoryText.VersusPrevious(sc, loc) : string.Empty, speedChange));
    }

    /// <summary>A single day: every card compares with the day before (busiest and active days say nothing about one day).</summary>
    private static CardLines DayLines(HistoryTotals totals, HistoryTotals before, Localizer loc)
    {
        CardLine Versus(double? current, double? previous)
        {
            var change = HistoryAggregator.Change(current, previous);
            return new CardLine(change is { } c ? HistoryText.VersusPreviousDay(c, loc) : string.Empty, change);
        }

        return new CardLines(
            Versus(totals.Output, before.Output),
            Versus(totals.Input, before.Input),
            Versus(totals.Requests, before.Requests),
            Versus(totals.AverageSpeed, before.AverageSpeed));
    }

    private readonly record struct CardLine(string Text, double? Change);

    private readonly record struct CardLines(CardLine Output, CardLine Input, CardLine Requests, CardLine Speed);
}
