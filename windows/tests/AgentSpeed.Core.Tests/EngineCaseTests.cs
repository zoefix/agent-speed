using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class EngineCaseTests
{
    private const double Tolerance = 1e-6;

    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureLocator.FixturesDirectory, "engine-cases.json"))));

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in Cases())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EngineCaseMatches(string name)
    {
        var item = Cases().Single(c => c.GetProperty("name").GetString() == name);
        var todayStartMs = item.TryGetProperty("todayStartMs", out var today) && today.ValueKind != JsonValueKind.Null
            ? today.GetInt64()
            : 0;

        var engine = new SpeedEngine(todayStartMs);
        foreach (var agent in item.GetProperty("agents").EnumerateArray())
        {
            engine.UpdateAgent(new AgentInfo(
                agent.GetProperty("id").GetString()!,
                ParseSource(agent.GetProperty("source").GetString()!),
                null,
                OptionalString(agent, "model"),
                false,
                ParsePhase(agent.GetProperty("phase").GetString()!),
                OptionalLong(agent, "inFlightStartMs"),
                OptionalLong(agent, "lastLineMs"),
                OptionalString(agent, "parentId")));
        }

        foreach (var response in item.GetProperty("responses").EnumerateArray())
        {
            engine.Upsert(
                new ParsedResponse(
                    response.GetProperty("key").GetString()!,
                    ParseSource(response.GetProperty("source").GetString()!),
                    response.GetProperty("agentId").GetString()!,
                    OptionalString(response, "model"),
                    OptionalLong(response, "startMs"),
                    response.GetProperty("endMs").GetInt64(),
                    new TokenCounts(Input: response.GetProperty("input").GetInt64(), Output: response.GetProperty("output").GetInt64()),
                    !response.TryGetProperty("final", out var final) || final.GetBoolean(),
                    CostMicroUsd: OptionalLong(response, "costMicroUsd")),
                isCompletion: true,
                seeded: true);
        }

        var trackers = new Dictionary<SourceSet, SubsetPeakTracker>();
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            var frame = engine.Compute(nowMs, todayStartMs, observeLive: true);
            var at = $"[{name} @ {nowMs}]";

            AssertClose(query.GetProperty("liveOutput").GetDouble(), frame.Live.Output, at + " liveOutput");
            AssertClose(query.GetProperty("liveTotal").GetDouble(), frame.Live.Total, at + " liveTotal");
            AssertClose(query.GetProperty("estimatedShare").GetDouble(), frame.EstimatedShare, at + " estimatedShare");

            if (query.TryGetProperty("agentsOrder", out var order))
            {
                Assert.Equal(order.EnumerateArray().Select(e => e.GetString()!), frame.Agents.Select(a => a.Info.Id));
                var parents = query.GetProperty("agentParents").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
                Assert.Equal(parents, frame.Agents.Where(a => a.ParentId is not null).ToDictionary(a => a.Info.Id, a => a.ParentId));
            }

            if (query.TryGetProperty("liveCost", out var liveCost))
            {
                AssertClose(liveCost.GetDouble(), frame.LiveCost.MicroUsdPerSecond, at + " liveCost");
                AssertClose(query.GetProperty("liveCostKnownShare").GetDouble(), frame.LiveCost.KnownShare(frame.Live.Output),
                    at + " liveCostKnownShare");
                var sum = frame.SourceLiveCost.Aggregate(LiveCost.Zero, (a, b) => a + b);
                AssertClose(frame.LiveCost.MicroUsdPerSecond, sum.MicroUsdPerSecond, at + " liveCost per source");
            }

            if (query.TryGetProperty("todayCostMicroUsd", out var todayCost))
            {
                Assert.True(todayCost.GetInt64() == frame.TodayCost.CostMicroUsd, $"{at} todayCostMicroUsd: got {frame.TodayCost.CostMicroUsd}");
                var unpriced = query.GetProperty("todayUnpricedRequests").GetInt64();
                Assert.True(unpriced == frame.TodayCost.UnpricedRequests, $"{at} todayUnpricedRequests: got {frame.TodayCost.UnpricedRequests}");
            }

            if (query.TryGetProperty("todayRequests", out var requests))
            {
                Assert.True(requests.GetInt64() == frame.Today.Requests, $"{at} todayRequests: got {frame.Today.Requests}");
            }

            if (query.TryGetProperty("todayOutput", out var output))
            {
                Assert.True(output.GetInt64() == frame.Today.Output, $"{at} todayOutput: got {frame.Today.Output}");
            }

            if (query.TryGetProperty("todayInput", out var input))
            {
                Assert.True(input.GetInt64() == frame.Today.Input, $"{at} todayInput: got {frame.Today.Input}");
            }

            if (query.TryGetProperty("averageToday", out var average))
            {
                if (average.ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(frame.AverageToday);
                }
                else
                {
                    Assert.True(frame.AverageToday.HasValue, at + " averageToday is null");
                    AssertClose(average.GetDouble(), frame.AverageToday.GetValueOrDefault(), at + " averageToday");
                }
            }

            if (query.TryGetProperty("todayUnfinalized", out var unfinalized))
            {
                Assert.True(unfinalized.GetInt32() == frame.UnfinalizedToday, $"{at} todayUnfinalized: got {frame.UnfinalizedToday}");
            }

            if (query.TryGetProperty("peakTodayOutput", out var peak))
            {
                AssertClose(peak.GetDouble(), frame.PeakToday.Output, at + " peakTodayOutput");
            }

            if (query.TryGetProperty("subsets", out var subsets))
            {
                foreach (var subset in subsets.EnumerateArray())
                {
                    var set = SourceSet.From(subset.GetProperty("sources").EnumerateArray().Select(s => ParseSource(s.GetString()!)));
                    var label = $"{at} subset {set}";
                    var live = 0.0;
                    var cost = LiveCost.Zero;
                    foreach (var kind in set)
                    {
                        live += frame.SourceLive[(int)kind].Output;
                        cost += frame.SourceLiveCost[(int)kind];
                    }

                    var measured = engine.MeasuredPeakOutput(set);
                    var tracker = trackers.TryGetValue(set, out var known) ? known : trackers[set] = new SubsetPeakTracker();
                    tracker.Update(live, measured, todayStartMs, nowMs);
                    AssertClose(subset.GetProperty("liveOutput").GetDouble(), live, label + " liveOutput");
                    if (subset.TryGetProperty("peakTodayOutput", out var peakToday))
                    {
                        AssertClose(peakToday.GetDouble(), tracker.PeakTodayOutput, label + " peakTodayOutput");
                    }

                    if (subset.TryGetProperty("liveCost", out var subsetCost))
                    {
                        AssertClose(subsetCost.GetDouble(), cost.MicroUsdPerSecond, label + " liveCost");
                    }
                }
            }

            if (query.TryGetProperty("historyMeasuredOutput", out var history))
            {
                Assert.Equal(SpeedEngine.HistorySeconds, frame.History.Count);
                foreach (var bucket in history.EnumerateObject())
                {
                    var startMs = long.Parse(bucket.Name, CultureInfo.InvariantCulture);
                    var point = frame.History.SingleOrDefault(p => p.Time.ToUnixTimeMilliseconds() == startMs);
                    Assert.True(point is not null, $"{at} history bucket {startMs} missing");
                    AssertClose(bucket.Value.GetDouble(), point.Measured.Output, $"{at} history[{startMs}]");
                }
            }
        }
    }

    [Fact]
    public void EverySourceKeyParses()
    {
        string[] keys =
            ["codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae"];

        Assert.Equal(Enum.GetValues<SourceKind>(), keys.Select(ParseSource));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParseSource("cursor"));
    }

    internal static void AssertClose(double expected, double actual, string label) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"{label}: expected {expected}, got {actual}");

    /// <summary>Every METRICS 1 source key.</summary>
    internal static SourceKind ParseSource(string name) =>
        SourceNames.Parse(name) ?? throw new ArgumentOutOfRangeException(nameof(name), name, null);

    internal static AgentPhase ParsePhase(string name) => name switch
    {
        "generating" => AgentPhase.Generating,
        "tools" => AgentPhase.Tools,
        "idle" => AgentPhase.Idle,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static IEnumerable<JsonElement> Cases() => Document.Value.RootElement.GetProperty("cases").EnumerateArray();

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? OptionalLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}
