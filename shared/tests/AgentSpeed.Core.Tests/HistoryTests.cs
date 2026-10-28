using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Tests;

public sealed class HistoryTests : IDisposable
{
    private static readonly IReadOnlySet<SourceKind> All = new HashSet<SourceKind>(Enum.GetValues<SourceKind>());

    private readonly TempDirectory _temp = new();
    private readonly long _t = ClaudeLines.T0;

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private SourceLocations Locations => new(
        _temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"), [_temp.Combine("gemini", "tmp")]);

    [Fact]
    public void HistoryOverAllFixturesMatchesTheExpectation()
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
            WorkBuddyAiRoot: SourceLocations.WorkBuddyRootOf(Path.Combine(copy, "workbuddy", ".workbuddy-ai")));
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "history-expected.json")))!;
        var pricing = document["prices"]!;
        var tables = pricing["tables"]!.AsArray()
            .Select(t => Pricing.PriceTable.Load(File.ReadAllBytes(FixtureLocator.PathOf(t!.GetValue<string>()))).Table!)
            .ToList();
        var bindings = pricing["bindings"]!.AsObject().ToDictionary(b => b.Key, b => b.Value!.GetValue<int>());
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store, pricing: new Pricing.PriceBook(tables, bindings));

        var report = analyzer.Run(locations, All, TimeZoneInfo.Utc, CancellationToken.None);

        var expected = document["days"]!;
        var actual = new JsonArray([.. analyzer.Days().Select(HistoryJson.Row)]);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"expected {expected.ToJsonString()}\nactual   {actual.ToJsonString()}");
        Assert.Equal((false, 0), (report.Cancelled, report.FilesUnreadable));
        Assert.Equal(2, report.SessionsAnalyzed);
    }

    [Fact]
    public void UnchangedFilesAreSkippedAndTheStoreSurvivesReopening()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        WriteClaude("p/b.jsonl", _t + 86_400_000, "msg_b", 50);
        List<DailyUsage> first;
        using (var store = HistoryStore.Open(StorePath))
        {
            var analyzer = new HistoryAnalyzer(store);
            Assert.Equal(2, Run(analyzer).FilesAnalyzed);
            first = analyzer.Days();
            Assert.Equal(0, Run(analyzer).FilesAnalyzed);
            Assert.Equal(Text(first), Text(analyzer.Days()));
        }

        using (var store = HistoryStore.Open(StorePath))
        {
            var reopened = new HistoryAnalyzer(store);
            Assert.Equal(Text(first), Text(reopened.Days()));
            var report = Run(reopened);
            Assert.Equal((2, 0, 0), (report.FilesDiscovered, report.FilesPlanned, report.FilesAnalyzed));
        }

        Assert.Equal([150L, 100L, 50L], [first.Sum(d => d.Totals.Output), first[0].Totals.Output, first[1].Totals.Output]);
    }

    [Fact]
    public void ChangedFilesAreAnalysedAgainAndDeletedFilesKeepTheirDays()
    {
        var a = WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var b = WriteClaude("p/b.jsonl", _t + 86_400_000, "msg_b", 50);
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        Run(analyzer);

        File.AppendAllText(a, ClaudeLines.UserText(_t + 10_000, "more") + "\n" + ClaudeLines.Assistant(_t + 12_000, "msg_a2", 7) + "\n");
        var changed = Run(analyzer);
        Assert.Equal((1, 1), (changed.FilesPlanned, changed.FilesAnalyzed));
        Assert.Equal(107, analyzer.Days()[0].Totals.Output);
        Assert.Equal(2, analyzer.Days()[0].Totals.Requests);

        File.Delete(b);
        Assert.Equal(0, Run(analyzer).FilesAnalyzed);
        var days = analyzer.Days();
        Assert.Equal(2, days.Count);
        Assert.Equal(50, days[1].Totals.Output);
    }

    [Fact]
    public void AVersionBumpRecomputesEverythingButKeepsDaysWithoutLogs()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        var b = WriteClaude("p/b.jsonl", _t + 86_400_000, "msg_b", 50);
        using (var store = HistoryStore.Open(StorePath))
        {
            Run(new HistoryAnalyzer(store));
        }

        File.Delete(b);
        using (var store = HistoryStore.Open(StorePath))
        {
            var bumped = new HistoryAnalyzer(store, new HistoryOptions { Version = HistoryAnalyzer.AnalysisVersion + 1 });
            var report = Run(bumped);
            Assert.Equal((1, 1), (report.FilesPlanned, report.FilesAnalyzed));
            Assert.Equal([100L, 50L], bumped.Days().Select(d => d.Totals.Output));
            Assert.Equal(0, Run(bumped).FilesAnalyzed);
        }
    }

    [Fact]
    public void KeysOwnedByUnchangedFilesAreNeverCountedTwice()
    {
        var original = WriteClaude("p/a.jsonl", _t, "msg_x", 100);
        File.SetLastWriteTimeUtc(original, DateTime.UnixEpoch.AddMilliseconds(_t + 60_000));
        var fork = _temp.WriteFile("claude/projects/p/b.jsonl",
            ClaudeLines.UserText(_t, "go") + "\n" + ClaudeLines.Assistant(_t + 2000, "msg_x", 100) + "\n"
            + ClaudeLines.UserText(_t + 5000, "next") + "\n" + ClaudeLines.Assistant(_t + 7000, "msg_y", 30) + "\n");
        File.SetLastWriteTimeUtc(fork, DateTime.UnixEpoch.AddMilliseconds(_t + 120_000));
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        Run(analyzer);
        Assert.Equal((2L, 130L, 2), Totals(analyzer));

        File.AppendAllText(fork, ClaudeLines.UserText(_t + 9000, "more") + "\n" + ClaudeLines.Assistant(_t + 11_000, "msg_z", 5) + "\n");
        Assert.Equal(1, Run(analyzer).FilesAnalyzed);
        Assert.Equal((3L, 135L, 2), Totals(analyzer));

        File.AppendAllText(original, ClaudeLines.UserText(_t + 20_000, "again") + "\n" + ClaudeLines.Assistant(_t + 21_000, "msg_w", 1) + "\n");
        Assert.Equal(1, Run(analyzer).FilesAnalyzed);
        Assert.Equal((4L, 136L, 2), Totals(analyzer));
    }

    [Fact]
    public void ATimeZoneChangeRelabelsEveryDay()
    {
        WriteClaude("p/a.jsonl", ClaudeLines.T0 - (ClaudeLines.T0 % 86_400_000) + 82_800_000, "msg_late", 10);
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        analyzer.Run(Locations, All, TimeZoneInfo.Utc, CancellationToken.None);
        var utcDay = Assert.Single(analyzer.Days()).Day;

        var east = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");
        Assert.Equal(1, analyzer.Run(Locations, All, east, CancellationToken.None).FilesAnalyzed);
        var eastDay = Assert.Single(analyzer.Days()).Day;
        Assert.Equal(DateOnly.Parse(utcDay, System.Globalization.CultureInfo.InvariantCulture).AddDays(1),
            DateOnly.Parse(eastDay, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LivePeaksAreRepeatedOnEverySourceRowOfTheirDay()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        _temp.WriteFile("codex/sessions/2026/10/20/rollout-a.jsonl", string.Join('\n',
            CodexLines.SessionMeta(_t), CodexLines.TurnContext(_t), CodexLines.UserMessage(_t + 100),
            CodexLines.UsageRecord(_t + 2000, "resp_1", CodexLines.Usage(10, 0, 20, 0))) + "\n");
        using (var store = HistoryStore.Open(StorePath))
        {
            var analyzer = new HistoryAnalyzer(store);
            Run(analyzer);
            var day = analyzer.Days()[0].Day;
            Assert.True(analyzer.RecordPeaks(new Dictionary<string, double> { [day] = 123.5 }));
            Assert.False(analyzer.RecordPeaks(new Dictionary<string, double> { [day] = 100 }));
            Assert.All(analyzer.Days(), d => Assert.Equal(123.5, d.Peak));
            Assert.All(analyzer.Days(livePeaks: new Dictionary<string, double> { [day] = 150 }), d => Assert.Equal(150, d.Peak));
        }

        using (var store = HistoryStore.Open(StorePath))
        {
            var days = new HistoryAnalyzer(store).Days();
            Assert.Equal(2, days.Count);
            Assert.All(days, d => Assert.Equal(123.5, d.Peak));
            Assert.Equal([SourceKind.ClaudeCode, SourceKind.Codex], days.Select(d => d.Source));
        }
    }

    [Fact]
    public void DisabledSourcesAreNeitherReadNorPublished()
    {
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        var report = analyzer.Run(Locations, new HashSet<SourceKind> { SourceKind.Codex }, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(0, report.FilesDiscovered);
        Run(analyzer);
        Assert.Empty(analyzer.Days(new HashSet<SourceKind> { SourceKind.Codex }));
        Assert.Single(analyzer.Days(All));
    }

    [Fact]
    public void ACorruptStoreIsRecreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, new string('x', 4096));
        WriteClaude("p/a.jsonl", _t, "msg_a", 100);
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        Assert.Equal(1, Run(analyzer).FilesAnalyzed);
    }

    [Fact]
    public void CodexArchivedSessionsAreIncluded()
    {
        _temp.WriteFile("codex/archived_sessions/rollout-old.jsonl", string.Join('\n',
            CodexLines.SessionMeta(_t), CodexLines.TurnContext(_t), CodexLines.UserMessage(_t + 100),
            CodexLines.UsageRecord(_t + 2000, "resp_archived", CodexLines.Usage(10, 0, 20, 0))) + "\n");
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        Run(analyzer);
        var day = Assert.Single(analyzer.Days());
        Assert.Equal((SourceKind.Codex, 20L), (day.Source, day.Totals.Output));
    }

    private HistoryReport Run(HistoryAnalyzer analyzer) => analyzer.Run(Locations, All, TimeZoneInfo.Utc, CancellationToken.None);

    private static (long Requests, long Output, int Agents) Totals(HistoryAnalyzer analyzer)
    {
        var day = Assert.Single(analyzer.Days());
        return (day.Totals.Requests, day.Totals.Output, day.Agents);
    }

    private static string Text(IEnumerable<DailyUsage> days) => new JsonArray([.. days.Select(HistoryJson.Row)]).ToJsonString();

    private string WriteClaude(string relative, long ms, string id, long output) =>
        _temp.WriteFile("claude/projects/" + relative,
            ClaudeLines.UserText(ms, "go") + "\n" + ClaudeLines.Assistant(ms + 2000, id, output) + "\n");
}

/// <summary>The row shape of <c>history-expected.json</c> and the probes' <c>--dump-history</c>.</summary>
internal static class HistoryJson
{
    public static JsonNode Row(DailyUsage day)
    {
        var models = new JsonObject();
        foreach (var model in day.Models.OrderBy(m => m.Model, StringComparer.Ordinal))
        {
            models[model.Model] = new JsonObject
            {
                ["requests"] = model.Requests,
                ["output"] = model.Output,
                ["measuredOutput"] = model.MeasuredOutput,
                ["measuredMs"] = model.MeasuredMs,
                ["costMicroUsd"] = model.Cost?.CostMicroUsd,
                ["unpricedRequests"] = model.Cost?.UnpricedRequests,
            };
        }

        return new JsonObject
        {
            ["date"] = day.Day,
            ["source"] = SourceNames.Of(day.Source),
            ["requests"] = day.Totals.Requests,
            ["input"] = day.Totals.Input,
            ["cachedInput"] = day.Totals.CachedInput,
            ["cacheWriteInput"] = day.Totals.CacheWriteInput,
            ["output"] = day.Totals.Output,
            ["reasoning"] = day.Totals.Reasoning,
            ["measuredOutput"] = day.MeasuredOutput,
            ["measuredMs"] = day.MeasuredMs,
            ["unfinalized"] = day.Unfinalized,
            ["costMicroUsd"] = day.Cost?.CostMicroUsd,
            ["unpricedRequests"] = day.Cost?.UnpricedRequests,
            ["priceVersion"] = day.PriceVersion,
            ["agents"] = day.Agents,
            ["models"] = models,
        };
    }
}

/// <summary>A copy of spec/fixtures whose modification times follow parser-expected.json <c>mtimes</c>.</summary>
internal static class FixtureCopy
{
    public static string WithModificationTimes(string destination)
    {
        CopyDirectory(FixtureLocator.FixturesDirectory, destination);
        foreach (var entry in ParserFixtureTests.Expected.GetProperty("mtimes").EnumerateObject())
        {
            var path = Path.Combine(destination, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(entry.Value.GetInt64()));
        }

        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
