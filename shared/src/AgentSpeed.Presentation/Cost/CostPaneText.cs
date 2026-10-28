using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Cost;

/// <summary>The outcome of the last manual price check, shown inline next to <c>prices.check</c> (DESIGN 9).</summary>
public enum PriceCheckKind
{
    None,
    UpToDate,
    Updated,
    NewerSchema,
    Failed,
}

/// <summary>What the rate status needs from the exchange-rate controller.</summary>
/// <param name="FailureReason">Set for 6 s after a failed manual refresh.</param>
public readonly record struct RateStatusInput(bool NetworkEnabled, bool IsRefreshing, string? FailureReason, bool IsStale);

/// <summary>The rate status under the currency picker (DESIGN 9, Rate status); hidden for USD.</summary>
/// <param name="Line"><c>rates.status</c>, <c>rates.fallback</c> before the first fetch, or <c>rates.failed</c> after a failed refresh.</param>
/// <param name="ShowsAttribution">ExchangeRate-API's rates are in use, so its link is shown (NETWORK 4).</param>
/// <param name="ShowsNetworkOff">The master switch is off: <c>settings.network.offHint</c> explains the disabled button.</param>
public sealed record RateStatusView(
    bool IsShown,
    string Line,
    bool IsFailure,
    bool ShowsStale,
    bool ShowsAttribution,
    bool CanRefresh,
    string RefreshLabel,
    bool ShowsNetworkOff)
{
    public static RateStatusView For(CostDisplay display, RateStatusInput input)
    {
        ArgumentNullException.ThrowIfNull(display);
        var loc = display.Localizer;
        var failure = input.FailureReason;
        return new RateStatusView(
            !display.IsUsd,
            failure is null ? display.RateStatus : loc.Format("rates.failed", failure),
            failure is not null,
            input.IsStale && failure is null,
            display.Rates.NeedsAttribution,
            input.NetworkEnabled && !display.IsUsd && !input.IsRefreshing,
            input.IsRefreshing ? loc["rates.refreshing"] : loc["rates.refresh"],
            !input.NetworkEnabled);
    }
}

/// <summary>The current price table as Settings describes it (<c>prices.status</c>).</summary>
/// <param name="Updated">The table's <c>YYYY-MM-DD</c>.</param>
/// <param name="IsBundled">The table bundled with this build is current (<c>prices.bundled</c>).</param>
public readonly record struct PriceTableInfo(int Version, string Updated, int ModelCount, bool IsBundled)
{
    public static PriceTableInfo Of(PriceTable current, PriceTable bundled)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(bundled);
        return new PriceTableInfo(current.Version, current.Updated, current.Models.Count, current.Version == bundled.Version);
    }
}

/// <summary>What the price table group needs from the sync controller.</summary>
public readonly record struct PriceCheckInput(
    bool NetworkEnabled,
    bool IsChecking,
    PriceCheckKind LastCheck,
    int? LastCheckVersion,
    string? LastCheckReason,
    bool NewerSchemaOffered);

/// <summary>Settings → Cost → Price table (DESIGN 9, NETWORK 3).</summary>
/// <param name="LastSync"><c>prices.lastSync</c> with a relative time, or <c>prices.never</c>.</param>
/// <param name="CheckResult">The inline result of <c>prices.check</c>; empty when there is nothing to say.</param>
public sealed record PriceTableView(
    string Status,
    bool IsBundled,
    string LastSync,
    string CheckResult,
    bool CanCheck,
    bool CanToggleAutoSync,
    bool ShowsNetworkOff)
{
    public static PriceTableView For(
        PriceTableInfo table,
        long? lastSuccessMs,
        PriceCheckInput check,
        DateTimeOffset now,
        CostDisplay display,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        var loc = display.Localizer;
        return new PriceTableView(
            CostPaneText.PriceStatus(table, loc),
            table.IsBundled,
            CostPaneText.LastSync(lastSuccessMs, now, display, zone),
            CostPaneText.CheckResult(check, loc),
            check.NetworkEnabled && !check.IsChecking,
            check.NetworkEnabled,
            !check.NetworkEnabled);
    }
}

/// <summary>The texts of Settings → Cost (DESIGN 9) that are not a single string lookup.</summary>
public static class CostPaneText
{
    /// <summary>The picker button as accessibility reads it: <c>貨幣, TWD, 新臺幣</c>.</summary>
    public static string CurrencyButtonName(CostDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return $"{display.Localizer["settings.currency"]}, {display.Currency.Code}, {display.CurrencyName}";
    }

    /// <summary><c>settings.liveUnit.cost</c> with the currency's symbol: <c>Cost ($/s)</c>, <c>金額（NT$/s）</c>.</summary>
    public static string LiveUnitCost(CostDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return display.Localizer.Format("settings.liveUnit.cost", display.Symbol);
    }

    /// <summary><c>版本 12 · 2026/10/22 更新 · 85 個模型</c>.</summary>
    public static string PriceStatus(PriceTableInfo table, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("prices.status", table.Version, UpdatedDate(table.Updated, loc.Culture), table.ModelCount);
    }

    /// <summary>The table's <c>YYYY-MM-DD</c> in the language's numeric date format; the raw text when it does not parse.</summary>
    public static string UpdatedDate(string updated, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return DateOnly.TryParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? CostDisplay.NumericDate(day.ToDateTime(TimeOnly.MinValue), culture)
            : updated;
    }

    /// <summary><c>prices.lastSync</c> (<c>just now</c>, <c>5m ago</c>, a time or a date), or <c>prices.never</c>.</summary>
    public static string LastSync(long? lastSuccessMs, DateTimeOffset now, CostDisplay display, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        var loc = display.Localizer;
        if (lastSuccessMs is not { } ms)
        {
            return loc["prices.never"];
        }

        var when = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return loc.Format("prices.lastSync", display.RelativeTime(when, now, zone));
    }

    /// <summary>
    /// <c>prices.checking</c> while a round runs, then the last manual outcome (<c>prices.upToDate</c>,
    /// <c>prices.updatedTo</c>, <c>prices.newerSchema</c>, <c>prices.failed</c>); before any manual check,
    /// <c>prices.newerSchema</c> when the last round only offered a newer schema.
    /// </summary>
    public static string CheckResult(PriceCheckInput check, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (check.IsChecking)
        {
            return loc["prices.checking"];
        }

        return check.LastCheck switch
        {
            PriceCheckKind.UpToDate => loc["prices.upToDate"],
            PriceCheckKind.Updated => loc.Format("prices.updatedTo", check.LastCheckVersion ?? 0),
            PriceCheckKind.NewerSchema => loc["prices.newerSchema"],
            PriceCheckKind.Failed => loc.Format("prices.failed", check.LastCheckReason ?? string.Empty),
            _ => check.NewerSchemaOffered ? loc["prices.newerSchema"] : string.Empty,
        };
    }

    /// <summary>Today's models without a price over every source, each once, in source order.</summary>
    public static IReadOnlyList<string> UnpricedModels(IEnumerable<SourceStatus> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<string>();
        foreach (var source in sources)
        {
            foreach (var model in source.UnpricedModels)
            {
                if (seen.Add(model))
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    /// <summary><c>prices.unknownToday</c> listing every model; empty when every model of today has a price.</summary>
    public static string UnknownToday(IReadOnlyList<string> models, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(loc);
        return models.Count == 0 ? string.Empty : loc.Format("prices.unknownToday", string.Join(loc["list.separator"], models));
    }
}
