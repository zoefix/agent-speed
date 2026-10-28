using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>Builders for daily history records and summaries used by the history tests.</summary>
internal static class HistoryData
{
    public static readonly DateOnly Today = new(2026, 10, 21);

    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    public static Localizer For(string language) => new(Embedded, language);

    public static DateOnly D(int year, int month, int day) => new(year, month, day);

    public static DailyUsage Usage(
        string day,
        SourceKind source = SourceKind.Codex,
        long output = 1000,
        long requests = 10,
        long input = 50_000,
        long cached = 40_000,
        long measuredOutput = -1,
        long measuredMs = 10_000,
        double? peak = null,
        int agents = 1,
        long unfinalized = 0,
        long cacheWrite = 0,
        long reasoning = 0,
        params ModelUsage[] models) =>
        new(
            day,
            source,
            new TokenTotals(requests, input, cached, cacheWrite, output, reasoning),
            measuredOutput < 0 ? output : measuredOutput,
            measuredMs,
            unfinalized,
            agents,
            peak,
            models);

    public static DaySummary Summary(
        DateOnly day,
        long output = 1000,
        long requests = 10,
        long input = 50_000,
        long cached = 40_000,
        long measuredOutput = -1,
        long measuredMs = 10_000,
        double? peak = null,
        int agents = 1,
        IReadOnlyList<ModelUsage>? models = null,
        IReadOnlyList<SourceOutput>? sources = null) =>
        new(
            day,
            requests,
            input,
            cached,
            0,
            output,
            0,
            measuredOutput < 0 ? output : measuredOutput,
            measuredMs,
            0,
            agents,
            peak,
            models ?? [],
            sources ?? [new SourceOutput(SourceKind.Codex, output)]);

    public static string Day(DateOnly day) => HistoryRanges.FormatDay(day);

    /// <summary>The window's money in <paramref name="currency"/> at <paramref name="rate"/> units per US dollar.</summary>
    public static HistoryMoney Money(Localizer loc, string currency = Currency.UsdCode, decimal rate = 1m)
    {
        var rates = new Dictionary<string, decimal> { [Currency.UsdCode] = 1m };
        rates[currency] = currency == Currency.UsdCode ? 1m : rate;
        var display = new CostDisplay(loc, CurrencyTable.Bundled, new RatesInUse(null, new FallbackRates("2026-10-22", "test", rates)))
        {
            Settings = CostSettings.Default with { Currency = currency },
        };
        return new HistoryMoney(display);
    }

    /// <summary>New Taiwan dollars at 32.4 per US dollar, as in the macOS history checks.</summary>
    public static HistoryMoney Twd(Localizer loc) => Money(loc, "TWD", 32.4m);

    /// <summary>A source's day with one model carrying the stored cost; days with cost data are bound to price version 3.</summary>
    public static DailyUsage Priced(string day, SourceKind source, long requests, CostTally? cost, string model = "m") =>
        new(day, source, new TokenTotals(Requests: requests, Input: 100, Output: 10), 0, 0, 0, 1, null,
            [new ModelUsage(model, requests, 10) { Cost = cost }])
        {
            Cost = cost,
            PriceVersion = cost is null ? null : 3,
        };

    /// <summary>
    /// The cost records of the macOS history checks: a priced and a partly priced source (10-20), a priced source beside
    /// one without cost data (10-21), nothing priced (10-19) and no cost data at all (10-18).
    /// </summary>
    public static IReadOnlyList<DailyUsage> CostRecords { get; } =
    [
        Priced("2026-10-20", SourceKind.Codex, 4, new CostTally(4_000_000)),
        Priced("2026-10-20", SourceKind.ClaudeCode, 2, new CostTally(1_500_000, 1), "mystery"),
        Priced("2026-10-21", SourceKind.Codex, 3, new CostTally(2_250_000)),
        Priced("2026-10-21", SourceKind.ClaudeCode, 5, null),
        Priced("2026-10-19", SourceKind.Codex, 2, new CostTally(UnpricedRequests: 2)),
        Priced("2026-10-18", SourceKind.ClaudeCode, 1, null),
    ];

    /// <summary>The cost records by day for every source, with <paramref name="excluded"/> left out as subscriptions.</summary>
    public static IReadOnlyList<DaySummary> CostDays(SourceSet excluded = default) =>
        HistoryAggregator.Daily(CostRecords, null, excluded: excluded);

    public static DaySummary DayOf(IEnumerable<DaySummary> days, DateOnly day) => days.Single(d => d.Day == day);
}
