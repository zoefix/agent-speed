using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.Tests;

/// <summary>The unpriced facts of the daily history (METRICS 9.10): the side table, its validity check and the derived fallback.</summary>
public sealed class UnpricedHistoryTests : IDisposable
{
    private const string Day = "2026-10-20";

    private static readonly IReadOnlySet<SourceKind> Claude = new HashSet<SourceKind> { SourceKind.ClaudeCode };
    private static readonly IReadOnlySet<SourceKind> All = new HashSet<SourceKind>(Enum.GetValues<SourceKind>());

    private readonly TempDirectory _temp = new();
    private readonly long _t = ClaudeLines.T0;

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private SourceLocations Locations => new(
        _temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"), []);

    [Fact]
    public void TheAnalysisVersionIsNine() => Assert.Equal(9, HistoryAnalyzer.AnalysisVersion);

    [Fact]
    public void EveryFixtureDayReadsBackItsFactsFromTheSideTable()
    {
        var copy = FixtureCopy.WithModificationTimes(_temp.Combine("fixtures"));
        var locations = new SourceLocations(
            Path.Combine(copy, "codex", "sessions"),
            Path.Combine(copy, "claude", "projects"),
            Path.Combine(copy, "dsh", "sessions"),
            Path.Combine(copy, "opencode"),
            [Path.Combine(copy, "gemini", "tmp")],
            Omp.OmpPaths.Roots(name => name == "PI_CONFIG_DIR" ? "omp" : null, macOS: false, copy),
            Antigravity.AntigravityPaths.Stores(Path.Combine(copy, "antigravity"), null),
            WorkBuddyRoot: SourceLocations.WorkBuddyRootOf(Path.Combine(copy, "workbuddy", ".workbuddy")),
            WorkBuddyAiRoot: SourceLocations.WorkBuddyRootOf(Path.Combine(copy, "workbuddy", ".workbuddy-ai")),
            PiRoots: PiFixture.RootsIn(copy),
            OpenClawRoots: OpenClawFixture.StateFoldersIn(copy),
            HermesRoot: HermesFixture.HomeIn(copy),
            DroidRoot: DroidFixture.RootIn(copy));
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "history-expected.json")))!;
        var pricing = document["prices"]!;
        var tables = pricing["tables"]!.AsArray()
            .Select(t => PriceTable.Load(File.ReadAllBytes(FixtureLocator.PathOf(t!.GetValue<string>()))).Table!)
            .ToList();
        var bindings = pricing["bindings"]!.AsObject().ToDictionary(b => b.Key, b => b.Value!.GetValue<int>());
        var options = new HistoryOptions { HermesZone = HermesFixture.Zone };
        using (var store = HistoryStore.Open(StorePath))
        {
            new HistoryAnalyzer(store, options, new PriceBook(tables, bindings)).Run(locations, All, TimeZoneInfo.Utc, CancellationToken.None);
        }

        using var reopened = HistoryStore.Open(StorePath);
        var days = new HistoryAnalyzer(reopened, options, new PriceBook(tables, bindings)).Days();
        var expected = document["days"]!;
        var actual = new JsonArray([.. days.Select(HistoryJson.Row)]);
        Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
        Assert.All(days, d => Assert.Equal((0L, 0L), (d.Unpriced.NoCostData, d.Unpriced.Derived)));
        Assert.All(days, d => Assert.Equal(d.Cost?.UnpricedRequests ?? 0, UnpricedFacts.Sum(d.Unpriced.Facts)));
    }

    [Fact]
    public void SideRowsKeepProviderAndFastAndStaleOnesFallBackToTheModels()
    {
        WriteClaude("p/a.jsonl", ("r1", "neko-model-f1097c63", 100), ("r2", "neko-model-f1097c63", 50), ("r3", "claude-opus-5-5", 10));
        var first = Assert.Single(Launch().Days);
        Assert.Equal([new UnpricedCount(new UnpricedFact("neko-model-f1097c63", null, false), 2)], first.Unpriced.Facts);
        Assert.Equal(1, Scalar("SELECT count(*) FROM contribution_unpriced"));
        Assert.Equal(HistoryAnalyzer.AnalysisVersion, Scalar("SELECT version FROM contribution_unpriced"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM contribution_unpriced WHERE provider <> ''"));

        Execute("UPDATE contribution_unpriced SET provider = 'custom', fast = 1");
        var valid = Assert.Single(Launch().Days);
        Assert.Equal([new UnpricedCount(new UnpricedFact("neko-model-f1097c63", "custom", true), 2)], valid.Unpriced.Facts);
        Assert.Equal(0, valid.Unpriced.Derived);

        // An older build analysed the file again: its version no longer matches the side rows.
        Execute("UPDATE files SET version = 6");
        Execute("INSERT INTO contribution_unpriced VALUES (999, '2026-10-20', 'claudeCode', 'orphan', '', 0, 5, 7)");
        using (var store = HistoryStore.Open(StorePath))
        {
            var derived = Assert.Single(new HistoryAnalyzer(store, pricing: new PriceBook()).Days(Claude));
            Assert.Equal([new UnpricedCount(new UnpricedFact("neko-model-f1097c63", null, false), 2)], derived.Unpriced.Facts);
            Assert.Equal(2, derived.Unpriced.Derived);
        }

        // This build analyses it again (the version differs) and writes fresh rows.
        var again = Launch();
        Assert.Equal(1, again.Report.FilesAnalyzed);
        Assert.Equal(0, Assert.Single(again.Days).Unpriced.Derived);
        Assert.Equal(1, Scalar($"SELECT count(*) FROM contribution_unpriced WHERE version = {HistoryAnalyzer.AnalysisVersion} AND file_id <> 999"));
    }

    [Fact]
    public void ContributionsOfGoneLogsWithoutSideRowsAreDerivedAndThoseWithoutCostDataAreNoCostData()
    {
        WriteClaude("p/a.jsonl", ("r1", "claude-opus-5-5", 100));
        Launch();
        Execute($"""
            INSERT INTO files VALUES (50, '/gone/a.jsonl', 'Claude', 1, 1, 6, x'');
            INSERT INTO contributions VALUES (50, '{Day}', 'claudeCode', 5, 10, 0, 0, 30, 0, 30, 1000, 0, 1, 7, 4);
            INSERT INTO contribution_models VALUES (50, '{Day}', 'claudeCode', 'b-model', 2, 10, 10, 500, 0, 2);
            INSERT INTO contribution_models VALUES (50, '{Day}', 'claudeCode', 'a-model', 3, 20, 20, 500, 7, 1);
            INSERT INTO files VALUES (51, '/gone/b.jsonl', 'Claude', 1, 1, 2, x'');
            INSERT INTO contributions VALUES (51, '{Day}', 'claudeCode', 8, 10, 0, 0, 30, 0, 30, 1000, 0, 1, NULL, NULL);
            """);

        var day = Assert.Single(Launch().Days);
        Assert.Equal(
            [
                new UnpricedCount(new UnpricedFact(string.Empty, null, false), 1),
                new UnpricedCount(new UnpricedFact("a-model", null, false), 1),
                new UnpricedCount(new UnpricedFact("b-model", null, false), 2),
            ],
            day.Unpriced.Facts);
        Assert.Equal((8L, 4L), (day.Unpriced.NoCostData, day.Unpriced.Derived));
    }

    [Fact]
    public void TheSideTableIsCreatedOnEveryOpenWithoutChangingTheSchemaVersion()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using (HistoryStore.Open(StorePath))
        {
        }

        Execute("DROP TABLE contribution_unpriced");
        using (HistoryStore.Open(StorePath))
        {
        }

        Assert.Equal(1, Scalar("SELECT count(*) FROM sqlite_master WHERE name = 'contribution_unpriced'"));
        Assert.Equal(HistoryStore.SchemaVersion, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TheEngineFramesCarryTodaysFactsAndRepricingMovesThem()
    {
        const long dayStart = 1_792_454_400_000;
        const long end = 1_792_490_405_000;
        var (v1, v2) = (PriceTableTests.Fixture("prices/v1.json"), PriceTableTests.Fixture("prices/v2.json"));
        var book = new PriceBook([v1]);
        var engine = new Engine.SpeedEngine(dayStart, book, TimeZoneInfo.Utc);
        engine.Upsert(new Parsing.ParsedResponse("a", SourceKind.Codex, "x", "gpt-6.1-sol-mini", end - 4000, end,
            new Parsing.TokenCounts(Input: 5000, Output: 500), Provider: "openai"), isCompletion: true, seeded: true);
        engine.Upsert(new Parsing.ParsedResponse("b", SourceKind.Codex, "x", "neko-model-1", end - 3000, end + 1,
            new Parsing.TokenCounts(Input: 50, Output: 5), Provider: "custom", RequestCount: 2), isCompletion: true, seeded: true);

        var before = engine.Compute(end + 1000, dayStart, observeLive: true);
        Assert.Equal(
            [
                new UnpricedCount(new UnpricedFact("gpt-6.1-sol-mini", "openai", false), 1),
                new UnpricedCount(new UnpricedFact("neko-model-1", "custom", false), 2),
            ],
            before.SourceUnpricedFacts[(int)SourceKind.Codex]);
        Assert.Empty(before.SourceUnpricedFacts[(int)SourceKind.ClaudeCode]);

        book.Add(v2);
        var after = engine.Compute(end + 1500, dayStart, observeLive: true);
        Assert.Equal([new UnpricedCount(new UnpricedFact("neko-model-1", "custom", false), 2)], after.SourceUnpricedFacts[(int)SourceKind.Codex]);
        Assert.Equal(2, after.SourceTodayCost[(int)SourceKind.Codex].UnpricedRequests);
    }

    private (HistoryReport Report, List<DailyUsage> Days) Launch()
    {
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store, pricing: new PriceBook());
        var report = analyzer.Run(Locations, Claude, TimeZoneInfo.Utc, CancellationToken.None);
        return (report, analyzer.Days(Claude));
    }

    private void Execute(string sql)
    {
        using var raw = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate);
        raw.Execute(sql);
    }

    private long? Scalar(string sql)
    {
        using var raw = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly);
        return raw.ScalarInt64(sql);
    }

    private void WriteClaude(string relative, params (string Id, string Model, long Output)[] responses)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < responses.Length; i++)
        {
            var (id, model, output) = responses[i];
            lines.Append(ClaudeLines.UserText(_t + i * 10_000, "q")).Append('\n');
            lines.Append(ClaudeLines.Assistant(_t + i * 10_000 + 4000, id, output, model: model, input: 5000)).Append('\n');
        }

        _temp.WriteFile("claude/projects/" + relative, lines.ToString());
    }
}
