using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Cost;

namespace AgentSpeed.Presentation.History;

/// <summary>
/// What the daily chart and the single-day breakdown measure (DESIGN 9, Usage history: <c>history.chart.output</c> |
/// <c>history.chart.cost</c>).
/// </summary>
public enum HistoryChartMetric
{
    Output,
    Cost,

    /// <summary>Observed network traffic (DESIGN 10, Usage history): mirrored daily bars.</summary>
    Traffic,
}

public static class HistoryChartMetrics
{
    public static string TitleKey(HistoryChartMetric metric) => metric switch
    {
        HistoryChartMetric.Cost => "history.chart.cost",
        HistoryChartMetric.Traffic => "history.chart.traffic",
        _ => "history.chart.output",
    };
}

/// <summary>How costs count in the window (METRICS 9.4, DESIGN 9): the sources left out as subscriptions and the chart metric.</summary>
/// <param name="Marked">Sources marked as subscriptions in Settings.</param>
/// <param name="IncludeSubscriptions">The window's own <c>history.includeSubscriptions</c> choice.</param>
public readonly record struct HistoryCostOptions(
    SourceSet Marked = default,
    bool IncludeSubscriptions = false,
    HistoryChartMetric Metric = HistoryChartMetric.Output)
{
    /// <summary>At least one source is marked: the toolbar offers <c>history.includeSubscriptions</c>.</summary>
    public bool OffersSubscriptionToggle => !Marked.IsEmpty;

    /// <summary>
    /// Sources whose costs add nothing for <paramref name="source"/> (every source when null). A source selected directly
    /// always shows its own API-equivalent cost.
    /// </summary>
    public SourceSet Excluded(SourceKind? source) => source is null && !IncludeSubscriptions ? Marked : SourceSet.Empty;

    /// <summary><c>history.summary.costApi</c> for a subscription source selected directly, else <c>history.summary.cost</c>.</summary>
    public string CostLabelKey(SourceKind? source) =>
        source is { } one && Marked.Contains(one) ? "history.summary.costApi" : "history.summary.cost";
}

public enum HistoryCostKind
{
    /// <summary>A known amount; <see cref="HistoryCost.IsPartial"/> when some responses are unpriced (<c>≥</c>).</summary>
    Priced,

    /// <summary>Requests, but none with a known price (<c>–</c>).</summary>
    Unknown,

    /// <summary>Days kept from a store without cost data (<c>–</c>, <c>history.costUnavailable</c>).</summary>
    Unavailable,

    /// <summary>Every request came from sources left out as subscriptions (<c>cost.subscription.tag</c>).</summary>
    Subscription,
}

/// <summary>
/// A day's (or a range's, a source's, a bar's) cost as the window shows it, built from the stored contributions of the
/// counted sources (METRICS 9.7). <c>default</c> is nothing at all (a priced 0).
/// </summary>
/// <param name="Tally">Σ known cost of the counted sources; null without cost data.</param>
/// <param name="Requests">Requests of the counted sources.</param>
/// <param name="Excluded">Sources with usage that were left out as subscriptions.</param>
public readonly record struct HistoryCost(CostTally? Tally, long Requests, SourceSet Excluded = default)
{
    public static HistoryCost None => default;

    public HistoryCostKind Kind
    {
        get
        {
            if (Requests == 0 && !Excluded.IsEmpty)
            {
                return HistoryCostKind.Subscription;
            }

            if (Tally is not { } tally)
            {
                return Requests == 0 ? HistoryCostKind.Priced : HistoryCostKind.Unavailable;
            }

            return tally.IsUnknown(Requests) ? HistoryCostKind.Unknown : HistoryCostKind.Priced;
        }
    }

    /// <summary>A known amount with some responses left out (<c>≥</c>).</summary>
    public bool IsPartial => Kind == HistoryCostKind.Priced && Unpriced > 0;

    /// <summary>The known micro-USD that adds to totals, bars and sorting (0 for anything not priced).</summary>
    public long MicroUsd => Tally?.CostMicroUsd ?? 0;

    public long Unpriced => Tally?.UnpricedRequests ?? 0;

    /// <summary>For sorting: null when nothing priced can be compared.</summary>
    public double? SortValue => Kind == HistoryCostKind.Priced ? MicroUsd : null;

    /// <summary>Adds another day, source or model (the store's rule: a part without cost data counts its requests as unpriced).</summary>
    public HistoryCost Add(HistoryCost other) =>
        new(HistoryCostMath.Merged(Tally, Requests, other.Tally, other.Requests), Requests + other.Requests, Excluded.Union(other.Excluded));
}

/// <summary>A model's cost on a day over the counted sources (METRICS 9.7).</summary>
/// <param name="Cost">Null without cost data.</param>
/// <param name="Requests">Requests of the model from counted sources; 0 when only subscription sources used it.</param>
public readonly record struct ModelCost(CostTally? Cost, long Requests);

/// <summary>A model whose price is unknown, with the requests it left out of the range's cost.</summary>
/// <param name="Model">Empty for responses without a model name.</param>
public sealed record UnpricedModel(string Model, SourceKind Source, long Requests);

public static class HistoryCostMath
{
    /// <summary>
    /// The merge rule of the history store (METRICS 8, 9.7): all-null stays null; a null part beside known parts adds its
    /// requests as unpriced; an empty part changes nothing.
    /// </summary>
    public static CostTally? Merged(CostTally? a, long aRequests, CostTally? b, long bRequests)
    {
        if (aRequests == 0 && (a is null || a == CostTally.Zero))
        {
            return b;
        }

        if (bRequests == 0 && (b is null || b == CostTally.Zero))
        {
            return a;
        }

        return (a, b) switch
        {
            (null, null) => null,
            ({ } sum, null) => sum with { UnpricedRequests = sum.UnpricedRequests + Math.Max(0, bRequests) },
            (null, { } sum) => sum with { UnpricedRequests = sum.UnpricedRequests + Math.Max(0, aRequests) },
            ({ } x, { } y) => x + y,
        };
    }
}

/// <summary>
/// Money of the Usage History window in the display currency and UI language, through the app's one
/// <see cref="CostDisplay"/>. Every amount converts with the one rate in use, so a range equals the sum of its days
/// (METRICS 9.8).
/// </summary>
public sealed class HistoryMoney
{
    public HistoryMoney(CostDisplay display)
    {
        Display = display ?? throw new ArgumentNullException(nameof(display));
    }

    public CostDisplay Display { get; }

    public Currency Currency => Display.Currency;

    public bool IsUsd => Display.IsUsd;

    /// <summary>Units of the display currency per US dollar.</summary>
    public decimal Rate => Display.Rate;

    public string Symbol => Display.Symbol;

    /// <summary><c>$12.34</c>, <c>NT$1,235</c>.</summary>
    public string Amount(long microUsd) => Display.AmountText(microUsd);

    /// <summary>Tooltips: <c>NT$1,235（US$37.94）</c> for a non-USD currency.</summary>
    public string AmountWithUsd(long microUsd) => Display.AmountWithUsd(microUsd);

    /// <summary>The value in display units, for chart scales only (amounts are always formatted from decimals).</summary>
    public double Value(long microUsd) => (double)MoneyFormat.Amount(microUsd, Rate);

    /// <summary>How a cost reads in cards, cells and breakdowns: the amount (<c>≥</c> when partial), <c>–</c> or the tag.</summary>
    public string Text(HistoryCost cost, string subscriptionTag) => cost.Kind switch
    {
        HistoryCostKind.Priced => cost.IsPartial ? CostDisplay.AtLeastSign + Amount(cost.MicroUsd) : Amount(cost.MicroUsd),
        HistoryCostKind.Subscription => subscriptionTag,
        _ => CostDisplay.Dash,
    };

    /// <summary>The amount as accessibility reads it: <c>12.34 美元</c>.</summary>
    public string Spoken(long microUsd) => Display.Spoken(Display.Amount(microUsd).Number);

    /// <summary>An axis value with the symbol and as few digits as the step needs: <c>$0</c>, <c>$2.5</c>, <c>NT$1.5k</c>, <c>¥120</c>.</summary>
    public string AxisLabel(double value, double step)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return MoneyFormat.WithSymbol(Symbol, "0");
        }

        string number;
        if (value >= 1000)
        {
            number = TrimFraction(SpeedFormat.Tokens((long)Math.Round(value, MidpointRounding.AwayFromZero)));
        }
        else
        {
            var places = !double.IsFinite(step) || step <= 0 ? 2
                : step >= 1 ? (Math.Round(step) == step ? 0 : 1)
                : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9) + (NeedsExtraDigit(step) ? 1 : 0));
            number = TrimFraction(Math.Round(value, places, MidpointRounding.AwayFromZero)
                .ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }

        return MoneyFormat.WithSymbol(Symbol, number);
    }

    /// <summary><c>cost_twd</c> for a non-USD display currency (DESIGN 9 CSV); null for USD.</summary>
    public string? CsvColumn => IsUsd ? null : "cost_" + Currency.Code.ToLowerInvariant();

    /// <summary>The amount in the display currency with its digits + 2 decimals, at the rate in use.</summary>
    public string CsvAmount(long microUsd) => MoneyFormat.Csv(microUsd, Rate, Currency.Digits);

    /// <summary>Steps such as 0.25 or 0.025 need one digit more than their magnitude.</summary>
    private static bool NeedsExtraDigit(double step)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
        var mantissa = step / magnitude;
        return Math.Abs(mantissa - Math.Round(mantissa)) > 1e-9;
    }

    /// <summary><c>1.50k</c> → <c>1.5k</c>, <c>2.50</c> → <c>2.5</c>, <c>3.00</c> → <c>3</c>.</summary>
    private static string TrimFraction(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return text;
        }

        var end = dot + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        var digits = text[(dot + 1)..end].TrimEnd('0');
        return text[..dot] + (digits.Length > 0 ? "." + digits : string.Empty) + text[end..];
    }
}
