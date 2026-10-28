using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Cost;

/// <summary>One line of the taskbar money block: label, number, unit.</summary>
/// <param name="Dim">Nothing priced or zero: the number takes the secondary color.</param>
public sealed record BarMoneyLine(string Label, string Number, string Unit, bool Dim);

/// <summary>
/// The taskbar money block (DESIGN 3, live value "tok/s + cost"): after a tok/s value, <c>bar.money.now</c> with the live
/// money rate of the billable sources over <c>bar.money.today</c> with today's billable total (the dashboard's 今日費用),
/// with the <c>≥</c> / <c>–</c> rules of section 9. The numbers share one right-aligned cell whose width grows at once
/// and shrinks only after the widget's delay.
/// </summary>
/// <param name="Spoken"><c>a11y.bar.money</c>: read after the value by UI Automation and the tooltip.</param>
public sealed record BarSecondary(BarMoneyLine Live, BarMoneyLine Today, string Spoken)
{
    /// <summary>
    /// Null unless the live value is "tok/s + cost" (<c>liveUnit</c> tokens with <c>barSecondary</c>) and the widget shows
    /// a value (not glyph only).
    /// </summary>
    public static BarSecondary? From(AppSettings settings, SpeedSnapshot snapshot, CostDisplay cost)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(cost);
        if (!settings.BarSecondary || settings.Cost.LiveUnit != LiveUnit.Tokens || settings.BarMode == BarDisplayMode.GlyphOnly)
        {
            return null;
        }

        var loc = cost.Localizer;
        var money = cost.Live(snapshot);
        var live = new BarMoneyLine(
            loc["bar.money.now"],
            money.Unknown ? CostDisplay.Dash : (money.AtLeast ? CostDisplay.AtLeastSign : string.Empty) + money.Number,
            money.Unit,
            money.Unknown || money.Value == 0);
        var summary = TodayCostSummary.From(snapshot, cost);
        var unknown = summary.Total.IsUnknown(summary.Requests);
        var atLeast = summary.Requests > 0 && summary.Total.UnpricedRequests > 0;
        var amount = cost.Amount(summary.Requests == 0 ? 0 : summary.Total.CostMicroUsd).Number;
        var today = new BarMoneyLine(
            loc["bar.money.today"],
            unknown ? CostDisplay.Dash : (atLeast ? CostDisplay.AtLeastSign : string.Empty) + amount,
            cost.Symbol,
            unknown || summary.Total.CostMicroUsd == 0);
        return new BarSecondary(live, today, loc.Format("a11y.bar.money", money.SpokenAmount, summary.Spoken));
    }
}
