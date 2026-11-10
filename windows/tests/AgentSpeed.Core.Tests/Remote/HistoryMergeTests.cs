using System.Text.Json.Nodes;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RemoteHistoryLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The hosts' rows in the daily and performance history (REMOTE 8.3).</summary>
public sealed class RemoteHistoryMergeTests : IDisposable
{
    private static readonly IReadOnlySet<SourceKind> All = new HashSet<SourceKind>(Enum.GetValues<SourceKind>());

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Every fixture day, encoded as the relay sends it and read back, is the row the local analysis gives (bar its host and
    /// price version, which the line does not carry).
    /// </summary>
    [Fact]
    public void ARemoteAggregateGivesTheLocalAnalysisRow()
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
            DroidRoot: DroidFixture.RootIn(copy))
        {
            QoderCliRoots = [QoderCliFixture.RootIn(copy)],
        };
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "history-expected.json")))!;
        var pricing = document["prices"]!;
        var tables = pricing["tables"]!.AsArray()
            .Select(t => Pricing.PriceTable.Load(File.ReadAllBytes(FixtureLocator.PathOf(t!.GetValue<string>()))).Table!)
            .ToList();
        var bindings = pricing["bindings"]!.AsObject().ToDictionary(b => b.Key, b => b.Value!.GetValue<int>());
        using var store = HistoryStore.Open(_temp.Combine("store", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store, new HistoryOptions { HermesZone = HermesFixture.Zone }, new Pricing.PriceBook(tables, bindings));
        analyzer.Run(locations, All, TimeZoneInfo.Utc, CancellationToken.None);

        var merged = new Dictionary<DayKey, DayAggregate>();
        foreach (var entry in analyzer.Entries.Values)
        {
            foreach (var (key, day) in entry.Days)
            {
                if (!merged.TryGetValue(key, out var total))
                {
                    merged[key] = total = new DayAggregate();
                }

                total.Merge(day);
            }
        }

        var local = analyzer.Days();
        Assert.Equal(merged.Count, local.Count);
        var checkedModes = false;
        var checkedUnpriced = false;
        foreach (var day in local)
        {
            var aggregate = merged[new DayKey(day.Day, day.Source)];
            // The relay counts a session's distinct agents itself (METRICS 8).
            aggregate.Agents = day.Agents;
            var decoded = RelayLineDecoder.StoredAggregate(L.LinuxJson(aggregate));
            Assert.NotNull(decoded);
            var remote = RemoteHistoryMerge.Usage(decoded, day.Day, day.Source, L.Host, day.Peak);
            Assert.Equal((L.Host, (int?)null), (remote.Host, remote.PriceVersion));
            Assert.Equal(L.Describe(day), L.Describe(remote with { Host = RemoteHost.LocalId, PriceVersion = day.PriceVersion }));
            checkedModes |= day.Modes.Any(m => m.Evidence != "unknown");
            checkedUnpriced |= day.Unpriced.Facts.Count > 0;
        }

        Assert.True(checkedModes && checkedUnpriced, "the fixtures cover mode usage and unpriced facts");
    }

    [Fact]
    public void NullCostsStayUnknown()
    {
        var aggregate = RelayLineDecoder.StoredAggregate(L.Aggregate(cost: null));
        Assert.NotNull(aggregate);
        var usage = RemoteHistoryMerge.Usage(aggregate, "2026-11-03", SourceKind.Codex, L.Host);
        Assert.Null(usage.Cost);
        Assert.All(usage.Models, m => Assert.Null(m.Cost));
        Assert.Equal(3, usage.Unpriced.NoCostData);
        Assert.Contains(usage.Modes, m => m is { Kind: ExecutionModeKind.ServiceTier, Mode: "fast", Evidence: "recorded" });
        Assert.Equal($"2026-11-03/codex/{L.Host}", usage.Id);
        Assert.False(usage.IsLocal);
    }

    [Fact]
    public void FactsThatDoNotAddUpAreDerivedFromTheModels()
    {
        var json = L.Aggregate().Replace("\"unpricedRequests\":0,\"models\"", "\"unpricedRequests\":2,\"models\"", StringComparison.Ordinal)
            .Replace("1234,0]", "1234,2]", StringComparison.Ordinal)
            .Replace("\"unpriced\":[]", "\"unpriced\":[[\"gpt-5.5\",\"openai\",true,false,1]]", StringComparison.Ordinal);
        var usage = RemoteHistoryMerge.Usage(RelayLineDecoder.StoredAggregate(json)!, "2026-11-03", SourceKind.Codex, L.Host);
        Assert.Equal(2, usage.Unpriced.Derived);
        Assert.Equal("gpt-5.5", Assert.Single(usage.Unpriced.Facts).Fact.Model);

        var exact = json.Replace("true,false,1]", "true,true,2]", StringComparison.Ordinal);
        var facts = RemoteHistoryMerge.Usage(RelayLineDecoder.StoredAggregate(exact)!, "2026-11-03", SourceKind.Codex, L.Host).Unpriced;
        Assert.Equal((0L, 0L), (facts.NoCostData, facts.Derived));
        Assert.Equal(new Unpriced.UnpricedFact("gpt-5.5", "openai", true, true), Assert.Single(facts.Facts).Fact);
    }

    [Fact]
    public void DaysAreSortedByDaySourceAndHost()
    {
        static DailyUsage Row(string day, SourceKind source, string host = RemoteHost.LocalId) =>
            new(day, source, new TokenTotals(1, 0, 0, 0, 1, 0), 0, 0, 0, 1, null, []) { Host = host };
        IReadOnlyList<DailyUsage> local = [Row("2026-11-02", SourceKind.ClaudeCode), Row("2026-11-02", SourceKind.Codex), Row("2026-11-03", SourceKind.Codex)];
        Assert.Same(local, RemoteHistoryMerge.Days(local, []));
        var merged = RemoteHistoryMerge.Days(local,
        [
            Row("2026-11-03", SourceKind.Codex, L.OtherHost), Row("2026-11-01", SourceKind.Codex, L.Host),
            Row("2026-11-03", SourceKind.Codex, L.Host), Row("2026-11-02", SourceKind.ClaudeCode, L.Host),
        ]);
        // Remote hosts of one day and source follow this computer in id order (`1F2E…` before `8C0D…`).
        Assert.Equal(
        [
            $"2026-11-01/codex/{L.Host}", "2026-11-02/claudeCode", $"2026-11-02/claudeCode/{L.Host}", "2026-11-02/codex",
            "2026-11-03/codex", $"2026-11-03/codex/{L.OtherHost}", $"2026-11-03/codex/{L.Host}",
        ], merged.Select(d => d.Id));
    }

    [Fact]
    public void PerformanceRecordsArePooledAcrossHosts()
    {
        var a = new PerfSums { N = 3, ToolLnSum = 1.5 };
        var b = new PerfSums { N = 4, ToolLnSum = 2.25 };
        IReadOnlyList<PerfRecord> local = [new PerfRecord("2026-11-02", SourceKind.Codex, "gpt-5.5", a)];
        Assert.Same(local, RemoteHistoryMerge.Perf(local, []));
        var pooled = RemoteHistoryMerge.Perf(local,
            [new PerfRecord("2026-11-02", SourceKind.Codex, "gpt-5.5", b), new PerfRecord("2026-11-01", SourceKind.Codex, "gpt-5.5", b)]);
        Assert.Equal(["2026-11-01", "2026-11-02"], pooled.Select(r => r.Day));
        Assert.Equal((7L, 3.75), (pooled[1].Sums.N, pooled[1].Sums.ToolLnSum));
        Assert.Equal((3L, 4L), (a.N, b.N));
    }
}
