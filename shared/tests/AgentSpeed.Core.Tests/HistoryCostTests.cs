using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Core.Tests;

/// <summary>Cost in the daily history and the live engine: day bindings, late pricing, back-fill and the store upgrade (METRICS 9.6, 9.7).</summary>
public sealed class HistoryCostTests : IDisposable
{
    /// <summary>2026-10-20T00:00Z; <see cref="ClaudeLines.T0"/> is 10:00 of that day.</summary>
    private const long DayStart = 1_792_454_400_000;

    private const string Day = "2026-10-20";

    private static readonly IReadOnlySet<SourceKind> Claude = new HashSet<SourceKind> { SourceKind.ClaudeCode };

    private readonly TempDirectory _temp = new();
    private readonly long _t = ClaudeLines.T0;

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private SourceLocations Locations => new(
        _temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"), []);

    [Fact]
    public void DaysKeepTheirTableAndUnknownModelsArePricedLater()
    {
        var (v1, v2) = Tables();
        WriteClaude("p/a.jsonl", ("r1", "claude-opus-5-5", 100), ("r2", "gpt-6.1-sol-mini", 500));

        var (_, first, _) = Launch(v1);
        var day = Assert.Single(first);
        Assert.Equal(1, day.PriceVersion);
        Assert.Equal(new CostTally(27_500, 1), day.Cost);
        var opusUnderV1 = day.Models.Single(m => m.Model == "claude-opus-5-5").Cost;
        Assert.Equal(new CostTally(27_500, 0), opusUnderV1);
        Assert.Equal(new CostTally(0, 1), day.Models.Single(m => m.Model == "gpt-6.1-sol-mini").Cost);

        var (bindings, copies) = HistoryStore.StoredPricing(StorePath);
        Assert.Equal([(Day, 1)], Pairs(bindings));
        Assert.Equal([1], copies.Keys.Order());
        Assert.Equal(v1.Data, copies[1]);

        var (unchanged, same, _) = Launch(v1);
        Assert.Equal(0, unchanged.FilesAnalyzed);
        Assert.Equal(Text(first), Text(same));

        var (repriced, second, book) = Launch(v1, v2);
        Assert.Equal(1, repriced.FilesAnalyzed);
        Assert.Equal(1, book.BoundVersion(Day));
        var after = Assert.Single(second);
        Assert.Equal(1, after.PriceVersion);
        Assert.Equal(new CostTally(27_500 + 3600, 0), after.Cost);
        var models = after.Models.ToDictionary(m => m.Model, m => m.Cost, StringComparer.Ordinal);
        Assert.Equal(opusUnderV1, models["claude-opus-5-5"]);
        Assert.Equal(new CostTally(3600, 0), models["gpt-6.1-sol-mini"]);
        Assert.Equal([(Day, 1)], Pairs(HistoryStore.StoredPricing(StorePath).Bindings));

        var (settled, _, _) = Launch(v1, v2);
        Assert.Equal(0, settled.FilesAnalyzed);
    }

    [Fact]
    public void OnlyFilesWithUnpricedResponsesAreAnalysedAgain()
    {
        var (v1, v2) = Tables();
        WriteClaude("p/a.jsonl", ("r1", "gpt-6.1-sol-mini", 500));
        WriteClaude("p/b.jsonl", ("r2", "claude-opus-5-5", 100));
        WriteClaude("p/c.jsonl", ("r3", "neko-model-f1097c63", 100));
        Assert.Equal(3, Launch(v1).Report.FilesAnalyzed);

        var (repriced, days, _) = Launch(v1, v2);
        Assert.Equal((2, 2), (repriced.FilesPlanned, repriced.FilesAnalyzed));
        Assert.Equal(new CostTally(3600 + 27_500, 1), Assert.Single(days).Cost);

        var settled = Launch(v1, v2).Report;
        Assert.Equal((0, 0), (settled.FilesPlanned, settled.FilesAnalyzed));
    }

    [Fact]
    public void AMissingTableCopyRebindsToTheCurrentTable()
    {
        var (v1, v2) = Tables();
        WriteClaude("p/a.jsonl", ("r1", "claude-opus-5-5", 100));
        Launch(v2);
        using var store = HistoryStore.Open(StorePath);
        store.SavePriceBindings(new Dictionary<string, int> { ["2026-10-19"] = 7 }, _ => null);
        var book = new PriceBook([v1, v2]);
        _ = new HistoryAnalyzer(store, pricing: book);
        Assert.Equal(2, book.BoundVersion("2026-10-19"));
        Assert.Equal(2, store.PriceBindings()["2026-10-19"]);
        Assert.Equal([2], store.PriceTables().Keys.Order());
    }

    [Fact]
    public void AStoredCopyKeepsItsDaysAndAnInvalidOneRebinds()
    {
        var (v1, v2) = Tables();
        WriteClaude("p/a.jsonl", ("r1", "claude-opus-5-5", 100));
        Launch(v1);

        var restored = new PriceBook([v2]);
        Assert.Empty(HistoryService.RestorePricing(restored, StorePath));
        Assert.Equal(1, restored.BoundVersion(Day));
        Assert.NotNull(restored.Table(1));
        Assert.Equal(2, restored.CurrentVersion);
        Assert.Equal(7500, restored.Cost("claude-opus-5-5", null, new TokenCounts(Input: 1000, Output: 100), _t, fast: false, Day));
        var (relaunch, days, _) = Launch(v2);
        Assert.Equal(0, relaunch.FilesAnalyzed);
        Assert.Equal(1, Assert.Single(days).PriceVersion);

        var none = new PriceBook([v2]);
        Assert.Empty(HistoryService.RestorePricing(none, _temp.Combine("none", "history.sqlite")));
        Assert.Null(none.BoundVersion(Day));

        using (var raw = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            raw.Execute("UPDATE price_tables SET body = x'7b7d'");
        }

        var repaired = new PriceBook([v2]);
        Assert.Equal([Day], HistoryService.RestorePricing(repaired, StorePath));
        Assert.Equal(2, repaired.BoundVersion(Day));
        using var store = HistoryStore.Open(StorePath);
        _ = new HistoryAnalyzer(store, pricing: repaired);
        Assert.Equal(2, store.PriceBindings()[Day]);
        Assert.Equal([2], store.PriceTables().Keys.Order());
    }

    [Fact]
    public void ContributionsWithoutCostDataStayUnavailableOrMakeTheDayPartial()
    {
        var old = new DayAggregate { Requests = 2, Agents = 1 };
        old.Models["m"] = new ModelTotals(2, 30, 30, 1000);
        var fresh = new DayAggregate();
        fresh.Add(new ParsedResponse("k", SourceKind.Codex, "a", "m", null, 0, new TokenCounts(Output: 1)), 40);

        var onlyOld = new DayAggregate();
        onlyOld.Merge(old);
        Assert.Null(onlyOld.Cost);
        Assert.Null(onlyOld.Models["m"].Cost);
        Assert.Null(onlyOld.ToUsage(new DayKey(Day, SourceKind.Codex), null, 1).PriceVersion);

        var mixed = new DayAggregate();
        mixed.Merge(old);
        mixed.Merge(fresh);
        Assert.Equal(new CostTally(40, 2), mixed.Cost);
        Assert.Equal(new CostTally(40, 2), mixed.Models["m"].Cost);
        Assert.Equal(1, mixed.ToUsage(new DayKey(Day, SourceKind.Codex), null, 1).PriceVersion);

        var reversed = new DayAggregate();
        reversed.Merge(fresh);
        reversed.Merge(old);
        Assert.Equal(new CostTally(40, 2), reversed.Cost);

        var unknown = new DayAggregate();
        unknown.Add(new ParsedResponse("u", SourceKind.Codex, "a", "m", null, 0, new TokenCounts(Output: 1)), null);
        Assert.Equal(new CostTally(0, 1), unknown.Cost);
        Assert.True(unknown.HasUnpriced);
        Assert.Null(CostMerge.Merged(null, 2, null, 3));
    }

    [Fact]
    public void AVersionTwoStoreMigratesWithoutCostForGoneFilesAndWithCostForPresentOnes()
    {
        var present = WriteClaude("p/a.jsonl", ("r1", "claude-opus-5-5", 100));
        var gone = _temp.Combine("claude", "projects", "p", "gone.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using (var old = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            old.Execute($"""
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE files (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, family TEXT NOT NULL, size INTEGER NOT NULL,
                    modified INTEGER NOT NULL, version INTEGER NOT NULL, keys BLOB NOT NULL);
                CREATE TABLE contributions (file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL,
                    requests INTEGER NOT NULL, input INTEGER NOT NULL, cached_input INTEGER NOT NULL,
                    cache_write_input INTEGER NOT NULL, output INTEGER NOT NULL, reasoning INTEGER NOT NULL,
                    measured_output INTEGER NOT NULL, measured_ms INTEGER NOT NULL, unfinalized INTEGER NOT NULL,
                    agents INTEGER NOT NULL, PRIMARY KEY (file_id, day, source)) WITHOUT ROWID;
                CREATE TABLE contribution_models (file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL,
                    model TEXT NOT NULL, requests INTEGER NOT NULL, output INTEGER NOT NULL, measured_output INTEGER NOT NULL DEFAULT 0,
                    measured_ms INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
                CREATE TABLE day_peaks (day TEXT PRIMARY KEY, peak REAL NOT NULL) WITHOUT ROWID;
                INSERT INTO meta VALUES ('timeZone', 'UTC');
                INSERT INTO files VALUES (1, '{gone}', 'Claude', 1, 1, 2, x'');
                INSERT INTO contributions VALUES (1, '2026-10-01', 'claudeCode', 2, 10, 0, 0, 30, 0, 30, 1000, 0, 1);
                INSERT INTO contribution_models VALUES (1, '2026-10-01', 'claudeCode', 'claude-opus-5-5', 2, 30, 30, 1000);
                INSERT INTO files VALUES (2, '{present}', 'Claude', 1, 1, 2, x'');
                INSERT INTO contributions VALUES (2, '{Day}', 'claudeCode', 1, 5000, 0, 0, 100, 0, 100, 2000, 0, 1);
                INSERT INTO contribution_models VALUES (2, '{Day}', 'claudeCode', 'claude-opus-5-5', 1, 100, 100, 2000);
                PRAGMA user_version = 2;
                """);
        }

        var (report, days, _) = Launch(PriceBook.Bundled);

        Assert.Equal(1, report.FilesAnalyzed);
        Assert.Equal(["2026-10-01", Day], days.Select(d => d.Day));
        Assert.Null(days[0].Cost);
        Assert.Null(days[0].PriceVersion);
        Assert.Null(Assert.Single(days[0].Models).Cost);
        Assert.Equal(2, days[0].Totals.Requests);
        Assert.Equal(1, days[1].Totals.Requests);
        Assert.True(days[1].Cost is { CostMicroUsd: > 0, UnpricedRequests: 0 }, $"cost {days[1].Cost}");
        Assert.Equal(days[1].Cost, Assert.Single(days[1].Models).Cost);
        Assert.Equal(PriceBook.Bundled.Version, days[1].PriceVersion);
        Assert.Equal([(Day, PriceBook.Bundled.Version)], Pairs(HistoryStore.StoredPricing(StorePath).Bindings));
        using var check = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(HistoryStore.SchemaVersion, check.ScalarInt64("PRAGMA user_version"));
    }

    // Engine

    [Fact]
    public void TheEngineRepricesTodaysUnpricedResponsesWhenANewTableArrives()
    {
        var (v1, v2) = Tables();
        var book = new PriceBook([v1]);
        var engine = new SpeedEngine(DayStart, book, TimeZoneInfo.Utc);
        const long end = 1_792_490_405_000;
        engine.Upsert(Response("a", SourceKind.Codex, "gpt-6.1-sol-mini", end - 4000, end, new TokenCounts(Input: 5000, Output: 500)),
            isCompletion: true, seeded: true);

        var before = engine.Compute(end + 1000, DayStart, observeLive: true);
        Assert.Equal(new CostTally(0, 1), before.TodayCost);
        Assert.Equal(new CostTally(0, 1), before.SourceTodayCost[(int)SourceKind.Codex]);
        Assert.Equal(["gpt-6.1-sol-mini"], before.SourceUnpricedModels[(int)SourceKind.Codex]);
        Assert.Equal(0, before.LiveCost.MicroUsdPerSecond);

        book.Add(v2);
        var after = engine.Compute(end + 1500, DayStart, observeLive: true);
        Assert.Equal(new CostTally(3600, 0), after.TodayCost);
        Assert.Equal(new CostTally(3600, 0), after.SourceTodayCost[(int)SourceKind.Codex]);
        Assert.Empty(after.SourceUnpricedModels[(int)SourceKind.Codex]);
        Assert.True(after.LiveCost.MicroUsdPerSecond > 0);
        Assert.True(after.SourceLiveCost[(int)SourceKind.Codex].KnownOutput > 0);
        Assert.Equal(1, book.BoundVersion(Day));
    }

    [Fact]
    public void TheEngineRepricesUnpricedResponsesOlderThanItsRetention()
    {
        var (v1, v2) = Tables();
        var book = new PriceBook([v1]);
        var engine = new SpeedEngine(DayStart, book, TimeZoneInfo.Utc);
        var usage = new TokenCounts(Input: 5000, Output: 500);
        const long early = DayStart + 3_600_000;
        engine.Upsert(Response("early", SourceKind.Codex, "gpt-6.1-sol-mini", early - 4000, early, usage), isCompletion: true, seeded: true);
        engine.Compute(early + 1000, DayStart, observeLive: true);

        var late = early + 2 * SpeedEngine.RetentionMs;
        engine.Upsert(Response("late", SourceKind.Codex, "gpt-6.1-sol-mini", late - 4000, late, usage), isCompletion: true, seeded: true);
        Assert.Equal(new CostTally(0, 2), engine.Compute(late + 1000, DayStart, observeLive: true).TodayCost);

        book.Add(v2);
        Assert.Equal(new CostTally(2 * 3600, 0), engine.Compute(late + 1500, DayStart, observeLive: true).TodayCost);
    }

    [Fact]
    public void TheEnginePricesWithTheDaysBoundTable()
    {
        var (v1, v2) = Tables();
        var book = new PriceBook([v1, v2], new Dictionary<string, int> { [Day] = 1 });
        var engine = new SpeedEngine(DayStart, book, TimeZoneInfo.Utc);
        var usage = new TokenCounts(Input: 1000, Output: 100);
        const long bound = 1_792_490_405_000;
        const long next = bound + 86_400_000;

        engine.Upsert(Response("old", SourceKind.ClaudeCode, "claude-opus-5-5", bound - 2000, bound, usage), isCompletion: true, seeded: true);
        Assert.Equal(new CostTally(7500, 0), engine.Compute(bound + 1000, DayStart, observeLive: true).TodayCost);

        engine.Upsert(Response("new", SourceKind.ClaudeCode, "claude-opus-5-5", next - 2000, next, usage), isCompletion: true, seeded: true);
        var nextDay = engine.Compute(next + 1000, DayStart + 86_400_000, observeLive: true);
        Assert.Equal(new CostTally(6000, 0), nextDay.TodayCost);
        Assert.Equal([("2026-10-21", 2)], Pairs(book.TakeNewBindings()));
        Assert.Equal(1, book.BoundVersion(Day));
    }

    [Fact]
    public void CostReadingsHonourSubscriptionsAndPartialPrices()
    {
        var snapshot = SpeedSnapshot.Empty with
        {
            Sources =
            [
                Status(SourceKind.Codex, live: 100, cost: 2000, known: 100, today: new CostTally(9000), requests: 3),
                Status(SourceKind.ClaudeCode, live: 50, cost: 0, known: 0, today: new CostTally(UnpricedRequests: 2), requests: 2),
            ],
        };

        var all = OrbSubset.Cost(snapshot, null, SourceSet.Everything);
        Assert.Equal(2000, all.MicroUsdPerSecond);
        Assert.Equal(100.0 / 150.0, all.KnownShare, 12);
        Assert.Equal(150, all.LiveOutput);
        Assert.Equal(CostState.Partial, all.State);
        Assert.Equal(new CostTally(9000, 2), all.Today);
        Assert.Equal(5, all.TodayRequests);
        Assert.Equal(["mystery-claudeCode"], all.UnpricedModels);
        Assert.Empty(all.Excluded);

        Assert.Equal(CostState.NothingPriced, OrbSubset.Cost(snapshot, SourceSet.Of(SourceKind.ClaudeCode), SourceSet.Everything).State);

        var billableCodex = OrbSubset.Cost(snapshot, null, SourceSet.Of(SourceKind.Codex));
        Assert.Equal(CostState.Priced, billableCodex.State);
        Assert.Equal([SourceKind.ClaudeCode], billableCodex.Excluded);
        Assert.Equal(new CostTally(9000), billableCodex.Today);

        var subscriptionOnly = OrbSubset.Cost(snapshot, SourceSet.Of(SourceKind.ClaudeCode), SourceSet.Of(SourceKind.Codex));
        Assert.Equal(CostState.SubscriptionOnly, subscriptionOnly.State);
        Assert.Equal(0, subscriptionOnly.MicroUsdPerSecond);
    }

    private static (PriceTable V1, PriceTable V2) Tables() =>
        (PriceTableTests.Fixture("prices/v1.json"), PriceTableTests.Fixture("prices/v2.json"));

    private static ParsedResponse Response(string key, SourceKind source, string model, long startMs, long endMs, TokenCounts tokens) =>
        new(key, source, "x", model, startMs, endMs, tokens);

    private static SourceStatus Status(SourceKind kind, double live, double cost, double known, CostTally today, long requests) =>
        new(kind, true, "/r", true, 1, new Rate(live, live), new TokenTotals(requests), 0)
        {
            LiveCost = new LiveCost(cost, known),
            TodayCost = today,
            UnpricedModels = today.UnpricedRequests > 0 ? ["mystery-" + SourceNames.Of(kind)] : [],
        };

    private static List<(string Day, int Version)> Pairs(IReadOnlyDictionary<string, int> bindings) =>
        [.. bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => (b.Key, b.Value))];

    private static string Text(IEnumerable<DailyUsage> days) => new JsonArray([.. days.Select(HistoryJson.Row)]).ToJsonString();

    /// <summary>A new launch: a fresh book with <paramref name="tables"/>, restored from the store by the analyzer.</summary>
    private (HistoryReport Report, List<DailyUsage> Days, PriceBook Book) Launch(params PriceTable[] tables)
    {
        var book = new PriceBook(tables);
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store, pricing: book);
        var report = analyzer.Run(Locations, Claude, TimeZoneInfo.Utc, CancellationToken.None);
        return (report, analyzer.Days(Claude), book);
    }

    /// <summary>A Claude transcript with one response per <c>(id, model, output)</c> and 5000 input tokens, ending at T0 + 10 s × index + 4 s.</summary>
    private string WriteClaude(string relative, params (string Id, string Model, long Output)[] responses)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < responses.Length; i++)
        {
            var (id, model, output) = responses[i];
            lines.Append(ClaudeLines.UserText(_t + i * 10_000, "q")).Append('\n');
            lines.Append(ClaudeLines.Assistant(_t + i * 10_000 + 4000, id, output, model: model, input: 5000)).Append('\n');
        }

        return _temp.WriteFile("claude/projects/" + relative, lines.ToString());
    }
}
