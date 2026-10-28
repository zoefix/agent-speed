using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Cost;

/// <summary>The money unit as one surface shows it (DESIGN 9): the number, its unit, and the states around it.</summary>
/// <param name="Number">The digits (<c>0.0042</c>), <c>0</c> for a subscription-only set, <c>–</c> when nothing is priced.</param>
/// <param name="Unit"><c>US$/s</c>.</param>
/// <param name="Inline">The symbol in front of the number, for places without a unit line (<c>$0.0042</c>).</param>
/// <param name="AtLeast">Part of the live value has no known price: a small <c>≥</c> precedes the number.</param>
/// <param name="Unknown">Nothing is priced: the number is <c>–</c> in the idle value color.</param>
/// <param name="Value">micro-USD per second, for the odometer's direction.</param>
/// <param name="HoverLine">The hover line in money mode.</param>
/// <param name="SpokenAmount">The value as read by accessibility (<c>0.0042 美元</c>, wrapped in <c>a11y.orb.costAtLeast</c> when partial).</param>
public sealed record LiveMoney(
    string Number,
    string Unit,
    string Inline,
    bool AtLeast,
    bool Unknown,
    double Value,
    string HoverLine,
    string SpokenAmount)
{
    public static LiveMoney Placeholder { get; } = new("0", "$/s", "$0", false, false, 0, string.Empty, "0");

    /// <summary>The number with its <c>≥</c>, for plain text (taskbar tooltip, menus).</summary>
    public string NumberText => AtLeast ? "≥" + Number : Number;

    /// <summary>The inline form with its <c>≥</c>.</summary>
    public string InlineText => AtLeast ? "≥" + Inline : Inline;
}

/// <summary>
/// Formats costs in the display currency for the UI language with the rates in use (METRICS 9.8, DESIGN 9) and knows
/// which sources count (METRICS 9.4). The app updates <see cref="Settings"/> and <see cref="Rates"/>; every surface
/// formats through one instance and re-renders on <see cref="Changed"/>. UI thread only.
/// </summary>
public sealed class CostDisplay
{
    public const string Dash = "–";
    public const string AtLeastSign = "≥";

    private readonly Localizer _loc;
    private CostSettings _settings = CostSettings.Default;
    private RatesInUse _rates;

    public CostDisplay(Localizer localizer, CurrencyTable? currencies = null, RatesInUse? rates = null)
    {
        _loc = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Currencies = currencies ?? CurrencyTable.Bundled;
        _rates = rates ?? new RatesInUse(null, Currencies.FallbackRates);
        _loc.LanguageChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The currency, the rates, the subscription marks or the language changed.</summary>
    public event EventHandler? Changed;

    public CurrencyTable Currencies { get; }

    public Localizer Localizer => _loc;

    public CostSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _settings)
            {
                return;
            }

            _settings = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public RatesInUse Rates
    {
        get => _rates;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _rates))
            {
                return;
            }

            _rates = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Currency

    public Currency Currency => Currencies.CurrencyOrUsd(_settings.Currency);

    public bool IsUsd => Currency.Code == Currency.UsdCode;

    public string Language => _loc.LanguageCode;

    public string Symbol => Currency.SymbolFor(Language);

    /// <summary>Units of the display currency per US dollar.</summary>
    public decimal Rate => _rates.Rate(Currency.Code);

    /// <summary><c>US$/s</c>.</summary>
    public string UnitPerSecond => _loc.Format("cost.unitPerSecond", Symbol);

    public string CurrencyName => Name(Currency);

    /// <summary>The sources whose costs are shown (METRICS 9.4).</summary>
    public SourceSet Billable => _settings.Billable;

    public string Name(Currency currency) => _loc["currency." + currency.Code];

    /// <summary>The English name, for the picker's search (DESIGN 9).</summary>
    public string EnglishName(Currency currency) =>
        _loc.Table.TryGet("currency." + currency.Code, LanguageCodes.En, out var text) ? text : currency.Code;

    // Values

    public FormattedMoney RateMoney(double microUsdPerSecond) =>
        MoneyFormat.Format(MoneyFormat.Rate(microUsdPerSecond, Rate), Currency, Language, MoneyForm.Rate);

    public FormattedMoney Amount(long microUsd) =>
        MoneyFormat.Format(MoneyFormat.Amount(microUsd, Rate), Currency, Language, MoneyForm.Amount);

    /// <summary><c>$12.34</c>, <c>NT$1,235</c>.</summary>
    public string AmountText(long microUsd) => Amount(microUsd).Text;

    /// <summary>The amount in US dollars (tooltips show it next to another currency).</summary>
    public string UsdText(long microUsd) =>
        MoneyFormat.Format(MoneyFormat.Amount(microUsd, 1m), Currencies.CurrencyOrUsd(Currency.UsdCode), Language, MoneyForm.Amount).Text;

    /// <summary>Tooltips: <c>NT$1,235（US$37.94）</c> for a non-USD currency (English <c> (…)</c>), else the amount.</summary>
    public string AmountWithUsd(long microUsd)
    {
        var text = AmountText(microUsd);
        if (IsUsd)
        {
            return text;
        }

        var usd = UsdText(microUsd);
        return Language == LanguageCodes.En ? $"{text} ({usd})" : $"{text}（{usd}）";
    }

    /// <summary>A cost with <c>≥</c> when some responses are unpriced, <c>–</c> when nothing is (or no cost data exists).</summary>
    public string TallyText(CostTally? tally, long requests)
    {
        if (tally is not { } t || t.IsUnknown(requests))
        {
            return Dash;
        }

        var text = AmountText(t.CostMicroUsd);
        return t.UnpricedRequests > 0 ? AtLeastSign + text : text;
    }

    /// <summary><c>12.34 美元</c>: how accessibility reads an amount (DESIGN 9).</summary>
    public string Spoken(string number) => number == Dash ? _loc["cost.unknown"] : $"{number} {CurrencyName}";

    /// <summary>A tally as accessibility reads it: the amount with the currency's name, <c>a11y.orb.costAtLeast</c> when partial.</summary>
    public string SpokenTally(CostTally? tally, long requests)
    {
        if (tally is not { } t || t.IsUnknown(requests))
        {
            return _loc["cost.unknown"];
        }

        var amount = Spoken(Amount(t.CostMicroUsd).Number);
        return t.UnpricedRequests > 0 ? _loc.Format("a11y.orb.costAtLeast", amount) : amount;
    }

    // Live value

    /// <summary>The money unit of a cost reading (METRICS 9.5, DESIGN 9 Orb).</summary>
    public LiveMoney Live(CostReading reading, int agentCount)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var formatted = RateMoney(reading.MicroUsdPerSecond);
        var unknown = reading.State == CostState.NothingPriced;
        var number = reading.State switch
        {
            CostState.SubscriptionOnly => "0",
            CostState.NothingPriced => Dash,
            _ => formatted.Number,
        };
        var atLeast = reading.State == CostState.Partial && number != "0";
        var hover = reading.State switch
        {
            CostState.SubscriptionOnly => _loc["cost.subscriptionOnly"],
            CostState.NothingPriced => _loc.Format("cost.unknownModels", ModelList(reading.UnpricedModels)),
            CostState.Partial => _loc["cost.partial"],
            _ => _loc.Format("cost.today.summary", AmountText(reading.Today.CostMicroUsd), agentCount),
        };
        var spoken = Spoken(number);
        return new LiveMoney(
            number,
            UnitPerSecond,
            unknown ? Dash : MoneyFormat.WithSymbol(Symbol, number),
            atLeast,
            unknown,
            reading.State == CostState.SubscriptionOnly ? 0 : reading.MicroUsdPerSecond,
            hover,
            atLeast ? _loc.Format("a11y.orb.costAtLeast", spoken) : spoken);
    }

    /// <summary>The live money of a snapshot's billable sources (taskbar, dashboard hero).</summary>
    public LiveMoney Live(SpeedSnapshot snapshot, SourceSet? sources = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reading = OrbSubset.Cost(snapshot, sources, Billable);
        var agents = sources is { } set ? snapshot.Agents.Count(a => set.Contains(a.Source)) : snapshot.Agents.Count;
        return Live(reading, agents);
    }

    /// <summary>
    /// The taskbar value (DESIGN 3, 9): the number with the unit after it, or the symbol in front with the unit hidden;
    /// <c>≥</c> when partly priced, <c>–</c> when nothing is.
    /// </summary>
    public static (string Text, string Unit) BarValue(LiveMoney money, bool showUnit)
    {
        ArgumentNullException.ThrowIfNull(money);
        if (money.Unknown)
        {
            return (Dash, money.Unit);
        }

        return ((money.AtLeast ? AtLeastSign : string.Empty) + (showUnit ? money.Number : money.Inline), money.Unit);
    }

    /// <summary>
    /// How accessibility reads a live money value (DESIGN 9 Accessibility): <c>a11y.orb.cost</c> (or
    /// <c>a11y.orb.costNamed</c> with a <paramref name="name"/>), the amount wrapped in <c>a11y.orb.costAtLeast</c> when
    /// partial, <c>a11y.orb.costUnknown</c> when nothing is priced.
    /// </summary>
    public string LiveAccessibility(LiveMoney money, string title, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(money);
        if (money.Unknown)
        {
            var unknown = _loc.Format("a11y.orb.costUnknown", title);
            return name is null ? unknown : $"{name}: {unknown}";
        }

        var amount = money.AtLeast ? _loc.Format("a11y.orb.costAtLeast", money.Number) : money.Number;
        return name is null
            ? _loc.Format("a11y.orb.cost", amount, CurrencyName, title)
            : _loc.Format("a11y.orb.costNamed", name, amount, CurrencyName, title);
    }

    /// <summary>Up to two model names, then <c>+n</c>.</summary>
    public string ModelList(IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            return _loc["cost.unknown"];
        }

        var shown = string.Join(_loc["list.separator"], models.Take(2));
        return models.Count > 2 ? $"{shown} +{(models.Count - 2).ToString(CultureInfo.InvariantCulture)}" : shown;
    }

    // Rates

    /// <summary><c>US$1 = NT$32.55 · 2026/10/22 · open.er-api.com</c>, or <c>rates.fallback</c> before the first fetch.</summary>
    public string RateStatus
    {
        get
        {
            var date = RateDateText;
            if (_rates.Provider is not { } provider)
            {
                return _loc.Format("rates.fallback", date);
            }

            var usd = Currencies.CurrencyOrUsd(Currency.UsdCode).SymbolFor(Language);
            var value = MoneyFormat.WithSymbol(Symbol, MoneyFormat.Significant(Rate));
            return _loc.Format("rates.status", MoneyFormat.WithSymbol(usd, "1"), value, date, provider);
        }
    }

    /// <summary>The rate date in the language's numeric date format (rates are dated by UTC day).</summary>
    public string RateDateText => NumericDate(DateTimeOffset.FromUnixTimeMilliseconds(_rates.DateMs).UtcDateTime, _loc.Culture);

    /// <summary><c>以 2026/10/22 的匯率換算</c> for a non-USD currency.</summary>
    public string? HistoryNote => IsUsd ? null : _loc.Format("rates.historyNote", RateDateText);

    /// <summary>A date in the culture's short numeric order with two-digit month and day (<c>2026/10/22</c>, <c>10/22/2026</c>).</summary>
    public static string NumericDate(DateTime date, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var padded = System.Text.RegularExpressions.Regex.Replace(pattern, "(?<![Md])M(?!M)", "MM");
        padded = System.Text.RegularExpressions.Regex.Replace(padded, "(?<![dM])d(?!d)", "dd");
        return date.ToString(padded, culture);
    }

    /// <summary>A last-sync stamp like the update check's (<c>just now</c>, <c>5 minutes ago</c>, a time or a date).</summary>
    public string RelativeTime(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        UpdateText.RelativeTime(when, now, _loc, zone ?? TimeZoneInfo.Local);
}
