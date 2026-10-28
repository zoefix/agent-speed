using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Settings;

/// <summary>The live value's unit (DESIGN 9): token speed or the API-equivalent cost per second.</summary>
public enum LiveUnit
{
    Tokens,
    Cost,
}

/// <summary>The cost settings of DESIGN 9 and NETWORK 3 (settings value <c>cost</c>). Immutable; use <c>with</c> to change.</summary>
public sealed record CostSettings
{
    public static CostSettings Default { get; } = new();

    /// <summary>The display currency: an ISO 4217 code of <c>currencies.json</c>.</summary>
    public string Currency { get; init; } = Core.Money.Currency.UsdCode;

    /// <summary>The unit of the taskbar widget and the dashboard hero; orbs choose their own.</summary>
    public LiveUnit LiveUnit { get; init; } = LiveUnit.Tokens;

    /// <summary>Sources used through a subscription (METRICS 9.4): left out of displayed costs unless included.</summary>
    public SourceSet Subscriptions { get; init; } = SourceSet.Empty;

    public bool IncludeSubscriptions { get; init; }

    /// <summary>Sync the price table daily (NETWORK 3, <c>prices.autoSync</c>).</summary>
    public bool SyncPrices { get; init; } = true;

    /// <summary>The usage history window's own include-subscriptions choice; null starts from <see cref="IncludeSubscriptions"/>.</summary>
    public bool? HistoryIncludeSubscriptions { get; init; }

    /// <summary>The sources whose costs are shown (METRICS 9.4): every source unless marked and not included.</summary>
    public SourceSet Billable => IncludeSubscriptions ? SourceSet.Everything : SourceSet.Everything.Except(Subscriptions);

    public bool IsSubscription(SourceKind source) => Subscriptions.Contains(source);

    public CostSettings WithSubscription(SourceKind source, bool marked) =>
        this with { Subscriptions = marked ? Subscriptions.With(source) : Subscriptions.Without(source) };

    public CostSettings Validated() => this with
    {
        Currency = CurrencyTable.Bundled.CurrencyOrUsd(Currency?.Trim().ToUpperInvariant() ?? string.Empty).Code,
        LiveUnit = Enum.IsDefined(LiveUnit) ? LiveUnit : LiveUnit.Tokens,
        Subscriptions = Subscriptions.Intersect(SourceSet.Everything),
    };

    /// <summary>Source keys as in METRICS 1 (<c>codex</c>, <c>claudeCode</c>, …), in source order.</summary>
    public IEnumerable<string> SubscriptionKeys => Subscriptions.Select(OrbSources.KeyOf);

    public static SourceSet ParseSubscriptions(IEnumerable<string?> keys)
    {
        var set = SourceSet.Empty;
        foreach (var key in keys)
        {
            if (OrbSources.TryParseKey(key, out var kind))
            {
                set = set.With(kind);
            }
        }

        return set;
    }
}
