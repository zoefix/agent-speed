using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>
/// The words a board's tick carries (DESIGN 2c Content): the speed title in the General title theme, the footer's
/// <c>boards.today</c> and <c>boards.peak</c>, today's cost of the board's sources (DESIGN 9), and the live metrics of the hero
/// and of each row (DESIGN 2c, Live metrics). Without a cost display, costs read in US dollars and every source counts.
/// </summary>
public sealed class BoardTexts
{
    private readonly TitleTheme _theme;

    public BoardTexts(Localizer localizer, TitleTheme theme, CostDisplay? cost = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(theme);
        Localizer = localizer;
        _theme = theme;
        Cost = cost;
    }

    public Localizer Localizer { get; }

    public CostDisplay? Cost { get; }

    public string Title(SpeedTitle title) => _theme.Label(title, Localizer);

    /// <summary>The title as accessibility reads it (not uppercased).</summary>
    public string SpokenTitle(SpeedTitle title) => _theme.Text(title, Localizer);

    public string Today(long output) => Localizer.Format("boards.today", SpeedFormat.Tokens(output));

    public string Peak(double peak) => Localizer.Format("boards.peak", SpeedFormat.Speed(peak));

    /// <summary>The sources whose costs show (METRICS 9.4).</summary>
    public SourceSet Billable => Cost?.Billable ?? SourceSet.Everything;

    /// <summary>
    /// The hero's 性能, 思考 and 費用 (DESIGN 2c, Live metrics: Values): the board's performance score (<paramref name="score"/>, its own
    /// form whatever the window holds), METRICS 7 of the thinking rate (<c>–</c> while nothing generates); the cost of the billable
    /// sources in the section 9 rate form, <c>≥</c> when partly priced, <c>–</c> when nothing is, the subscription tag when every
    /// source is excluded, a real 0 otherwise.
    /// </summary>
    public BoardHeroReadouts Hero(IntelligenceSums sums, PerfSetReading score, CostReading cost)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(cost);
        var money = cost.State switch
        {
            CostState.SubscriptionOnly => Tag(),
            CostState.NothingPriced => Unknown(),
            _ => Money(cost.MicroUsdPerSecond, cost.State == CostState.Partial),
        };

        if (!sums.IsActive)
        {
            return new BoardHeroReadouts(score, BoardHeroReadouts.Dash, true, false, money);
        }

        var thinking = sums.LiveThinking;
        return new BoardHeroReadouts(score, SpeedFormat.Speed(thinking), false, ThinkingZeroHold.ReadsZero(thinking), money);
    }

    /// <summary>
    /// A row's 性能 cell (DESIGN 2c, Ranking rows): a group row's highest member score (METRICS 6.9, <i>Sets and groups</i>), a sub
    /// row's own value, with <c>a11y.agent.intel</c> and the score words while the column shows. It names no cost: 費用 is the
    /// board's only live cost.
    /// </summary>
    public BoardRowReadout? Row(BoardEntry entry, BoardReadouts readouts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PerfSetReading score;
        switch (entry.Kind)
        {
            case BoardEntryKind.Group:
                var group = entry.Group;
                score = PerfSetReading.Of(group.Intelligence.Score, [group.Root, .. group.Subs]);
                break;
            case BoardEntryKind.Sub when entry.Agent is { } agent:
                score = PerfSetReading.Of(agent.Intelligence.Score, [agent]);
                break;
            case BoardEntryKind.Sub:
                score = PerfSetReading.None;
                break;
            default:
                return null;
        }

        // The 性能 words follow the speed only while the column shows.
        var spoken = readouts.Intelligence && score.Words(Localizer) is { } words ? Localizer.Format("a11y.agent.intel", words) : string.Empty;
        return new BoardRowReadout(score, spoken);
    }

    private BoardMoney Money(double microUsdPerSecond, bool partial)
    {
        var formatted = Cost is { } display ? display.RateMoney(microUsdPerSecond)
            : MoneyFormat.Format(MoneyFormat.Rate(microUsdPerSecond, 1m), Usd, Localizer.LanguageCode, MoneyForm.Rate);
        var number = formatted.Number;
        var zero = number == "0";
        var atLeast = partial && !zero;
        var spoken = atLeast ? Localizer.Format("a11y.orb.costAtLeast", number) : number;
        return new BoardMoney(number, Localizer.Format("cost.unitPerSecond", formatted.Symbol), atLeast, zero ? BoardMoneyState.Zero : BoardMoneyState.Value,
            spoken, CurrencyName);
    }

    // The unit stays known (the sub-gauge sizes its unit cell with it) though neither form draws it.
    private BoardMoney Unknown() => new(CostDisplay.Dash, UnitText, false, BoardMoneyState.Unknown, Localizer["cost.unknown"], CurrencyName);

    private BoardMoney Tag() => new(Localizer["cost.subscription.tag"], UnitText, false, BoardMoneyState.Tag, Localizer["cost.subscription.tag"],
        CurrencyName);

    private string UnitText => Localizer.Format("cost.unitPerSecond", Cost?.Symbol ?? Usd.SymbolFor(Localizer.LanguageCode));

    private string CurrencyName => Cost?.CurrencyName ?? Localizer["currency." + Currency.UsdCode];

    private static Currency Usd => CurrencyTable.Bundled.CurrencyOrUsd(Currency.UsdCode);

    /// <summary>
    /// Today's billable cost of the sources (every enabled source for null), as the dashboard writes it (<c>≥</c> when some
    /// responses have no price, <c>–</c> when none has); null without a cost display.
    /// </summary>
    public string? TodayCost(SpeedSnapshot snapshot, SourceSet? sources)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Cost is not { } cost)
        {
            return null;
        }

        var total = CostTally.Zero;
        long requests = 0;
        int available = 0, excluded = 0;
        foreach (var status in snapshot.Sources)
        {
            if (sources is { } members && !members.Contains(status.Kind) || OrbSubset.IsUnavailable(status))
            {
                continue;
            }

            available++;
            if (!cost.Billable.Contains(status.Kind))
            {
                excluded++;
                continue;
            }

            if (!status.Enabled || status.Today.Requests <= 0)
            {
                continue;
            }

            total += status.TodayCost;
            requests += status.Today.Requests;
        }

        // Every source of the board excluded as a subscription: the tag, as on the sub-gauge (DESIGN 2c, Live metrics: Values).
        if (available > 0 && excluded == available)
        {
            return Localizer["cost.subscription.tag"];
        }

        return requests == 0 ? cost.AmountText(0) : cost.TallyText(total, requests);
    }
}
