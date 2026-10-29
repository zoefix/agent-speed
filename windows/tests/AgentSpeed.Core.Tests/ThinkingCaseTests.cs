using System.Text.Json;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The live thinking line (METRICS 6.9 <i>Live thinking</i>) against the <c>thinking</c> section of
/// <c>spec/fixtures/perf-score-cases.json</c>: the window sums of a set, of every agent and of every board group.
/// </summary>
public sealed class ThinkingCaseTests
{
    private static readonly Lazy<JsonElement> Section = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("perf-score-cases.json"))).RootElement.GetProperty("thinking"));

    public static TheoryData<string> WindowNames() => Names("windows");

    public static TheoryData<string> AgentWindowNames() => Names("agentWindows");

    public static TheoryData<string> GroupWindowNames() => Names("groupWindows");

    public static TheoryData<string> ModelWindowNames() => Names("modelWindows");

    [Theory]
    [MemberData(nameof(WindowNames))]
    public void WindowMatches(string name)
    {
        var item = Case("windows", name);
        var engine = Engine(item);
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            var frame = engine.Compute(nowMs, 0, observeLive: false);
            var members = query.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array
                ? SourceSet.From(sources.EnumerateArray().Select(s => EngineCaseTests.ParseSource(s.GetString()!)))
                : SourceSet.Everything;
            var sums = members.Aggregate(IntelligenceSums.Zero, (total, kind) => total + frame.SourceIntelligence[(int)kind]);
            AssertSums(query.GetProperty("expect"), sums, $"[{name} @ {nowMs} {members}]");
        }
    }

    [Theory]
    [MemberData(nameof(AgentWindowNames))]
    public void AgentWindowMatches(string name)
    {
        var item = Case("agentWindows", name);
        var engine = Engine(item);
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            var frame = engine.Compute(nowMs, 0, observeLive: false);
            var at = $"[{name} @ {nowMs}]";
            var set = frame.SourceIntelligence.Aggregate(IntelligenceSums.Zero, (total, sums) => total + sums);
            AssertSums(query.GetProperty("expect"), set, at);

            var expected = query.GetProperty("agents").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), frame.Agents.Select(a => a.Info.Id).Order(StringComparer.Ordinal));
            var added = frame.Agents.Aggregate(IntelligenceSums.Zero, (total, agent) => total + agent.Intelligence.Sums);
            AssertAdded(set, added, at + " agents'");
            foreach (var agent in frame.Agents)
            {
                AssertSums(expected[agent.Info.Id], agent.Intelligence.Sums, $"{at} {agent.Info.Id}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(GroupWindowNames))]
    public void GroupWindowMatches(string name)
    {
        var item = Case("groupWindows", name);
        var engine = Engine(item);
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            var frame = engine.Compute(nowMs, 0, observeLive: false);
            var at = $"[{name} @ {nowMs}]";
            var set = frame.SourceIntelligence.Aggregate(IntelligenceSums.Zero, (total, sums) => total + sums);
            AssertSums(query.GetProperty("expect"), set, at);

            // Groups as a board forms them: a root (no listed parent) and every listed descendant, root first, depth first.
            var statuses = frame.Agents.Select(a => new AgentStatus(a.Info.Id, a.Info.Source, a.Info.Project ?? string.Empty, a.Info.Model, a.Phase,
                a.Info.IsSubagent, a.Live, a.RecentSpeed, DateTimeOffset.UnixEpoch, a.ParentId) { Intelligence = a.Intelligence })
                .ToArray();
            var groups = Groups(statuses);
            var expected = query.GetProperty("groups").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), groups.Keys.Order(StringComparer.Ordinal));
            var added = IntelligenceSums.Zero;
            foreach (var (rootId, members) in groups)
            {
                var want = expected[rootId];
                Assert.Equal(want.GetProperty("members").EnumerateArray().Select(e => e.GetString()!), members.Select(m => m.Id));
                var intelligence = AgentIntelligence.Group(members[0], members.Skip(1).ToArray());
                added += intelligence.Sums;
                AssertSums(want, intelligence.Sums, $"{at} group {rootId}");
            }

            // The groups partition the agents, so their sums add up to the set's.
            AssertAdded(set, added, at + " groups'");
        }
    }

    /// <summary>
    /// The model sums live reporting sends per model (METRICS 6.9 <i>Live thinking</i>, 11.10): a response counts for its own
    /// model, an in-flight estimate for its agent's current model; keyed by the raw model, they add up to the set's.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModelWindowNames))]
    public void ModelWindowMatches(string name)
    {
        var item = Case("modelWindows", name);
        var engine = Engine(item);
        foreach (var query in item.GetProperty("queries").EnumerateArray())
        {
            var nowMs = query.GetProperty("nowMs").GetInt64();
            var frame = engine.Compute(nowMs, 0, observeLive: false);
            var at = $"[{name} @ {nowMs}]";
            var set = frame.SourceIntelligence.Aggregate(IntelligenceSums.Zero, (total, sums) => total + sums);
            AssertSums(query.GetProperty("expect"), set, at);

            var byModel = new Dictionary<string, IntelligenceSums>(StringComparer.Ordinal);
            foreach (var (key, sums) in frame.ModelIntelligence)
            {
                byModel[key.Model] = byModel.GetValueOrDefault(key.Model) + sums;
            }

            var expected = query.GetProperty("models").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), byModel.Keys.Order(StringComparer.Ordinal));
            AssertAdded(set, byModel.Values.Aggregate(IntelligenceSums.Zero, (total, sums) => total + sums), at + " models'");
            foreach (var (model, sums) in byModel)
            {
                AssertSums(expected[model], sums, $"{at} {model}");
            }

            // Reporting adds the raw models that resolve to one report id (METRICS 11.3).
            var reported = frame.Report.Perf;
            EngineCaseTests.AssertClose(set.LiveThinking, reported.Thinking, at + " reported thinking");
            EngineCaseTests.AssertClose(set.LiveThinking, reported.ModelThinking.Values.Sum(), at + " reported model thinking");
        }
    }

    [Fact]
    public void ASubAgentsOutputNeverCountsForItsParent()
    {
        var engine = new SpeedEngine(0, new PriceBook());
        engine.UpdateAgent(new AgentInfo("parent", SourceKind.ClaudeCode, null, "claude-opus-5-5", false, AgentPhase.Tools, null, 4000));
        engine.UpdateAgent(new AgentInfo("child", SourceKind.ClaudeCode, null, "claude-haiku-5", true, AgentPhase.Idle, null, 4000, "parent"));
        engine.Upsert(new ParsedResponse("r", SourceKind.ClaudeCode, "child", "claude-haiku-5", 1000, 4000,
            new TokenCounts(Input: 1000, Output: 300, Reasoning: 120), true), isCompletion: true, seeded: true);
        var frame = engine.Compute(4000, 0, observeLive: false);
        var parent = frame.Agents.Single(a => a.Info.Id == "parent");
        var child = frame.Agents.Single(a => a.Info.Id == "child");
        Assert.Equal(IntelligenceSums.Zero, parent.Intelligence.Sums);
        Assert.Equal(frame.SourceIntelligence[(int)SourceKind.ClaudeCode].Thinking, child.Intelligence.Sums.Thinking);
        Assert.True(child.Intelligence.Sums.IsActive);
    }

    [Fact]
    public void ReasoningTokensWithoutOutputAddNothing()
    {
        var sums = IntelligenceSums.Zero.Add(SourceKind.Codex, 0, 50);
        Assert.Equal(IntelligenceSums.Zero, sums);
        Assert.Equal(0, sums.ThinkingShare);
        Assert.Equal(0, sums.LiveThinking);
    }

    private static SpeedEngine Engine(JsonElement item)
    {
        var engine = new SpeedEngine(0, new PriceBook());
        foreach (var agent in item.GetProperty("agents").EnumerateArray())
        {
            engine.UpdateAgent(new AgentInfo(
                agent.GetProperty("id").GetString()!,
                EngineCaseTests.ParseSource(agent.GetProperty("source").GetString()!),
                null,
                Text(agent, "model"),
                Text(agent, "parentId") is not null,
                EngineCaseTests.ParsePhase(agent.GetProperty("phase").GetString()!),
                Number(agent, "inFlightStartMs"),
                Number(agent, "lastLineMs"),
                Text(agent, "parentId")) { Modes = Effort(Text(agent, "effort")) });
        }

        foreach (var response in item.GetProperty("responses").EnumerateArray())
        {
            var tokens = new TokenCounts(
                Input: response.GetProperty("input").GetInt64(),
                Output: response.GetProperty("output").GetInt64(),
                Reasoning: response.GetProperty("reasoning").GetInt64());
            engine.Upsert(
                new ParsedResponse(
                    response.GetProperty("key").GetString()!,
                    EngineCaseTests.ParseSource(response.GetProperty("source").GetString()!),
                    response.GetProperty("agentId").GetString()!,
                    Text(response, "model"),
                    Number(response, "startMs"),
                    response.GetProperty("endMs").GetInt64(),
                    tokens,
                    response.GetProperty("final").GetBoolean(),
                    Text(response, "provider")) { Modes = Effort(Text(response, "effort")) },
                isCompletion: true,
                seeded: true);
        }

        return engine;
    }

    /// <summary><c>liveThinking</c>, <c>thinkingShare</c> and <c>weight</c> (Σ w, tokens generating in the window).</summary>
    private static void AssertSums(JsonElement expect, IntelligenceSums sums, string at)
    {
        EngineCaseTests.AssertClose(expect.GetProperty("liveThinking").GetDouble(), sums.LiveThinking, at + " liveThinking");
        EngineCaseTests.AssertClose(expect.GetProperty("thinkingShare").GetDouble(), sums.ThinkingShare, at + " thinkingShare");
        EngineCaseTests.AssertClose(expect.GetProperty("weight").GetDouble(), sums.Output, at + " weight");
    }

    private static void AssertAdded(IntelligenceSums set, IntelligenceSums added, string label)
    {
        EngineCaseTests.AssertClose(set.Output, added.Output, label + " output");
        EngineCaseTests.AssertClose(set.Thinking, added.Thinking, label + " reasoning");
        EngineCaseTests.AssertClose(set.ThinkingBasis, added.ThinkingBasis, label + " reasoning basis");
    }

    // Root id → the root, then its descendants depth first in list order (a parent that is not listed makes a root).
    private static Dictionary<string, List<AgentStatus>> Groups(IReadOnlyList<AgentStatus> agents)
    {
        var ids = agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<AgentStatus>>(StringComparer.Ordinal);
        void Walk(AgentStatus agent, List<AgentStatus> members)
        {
            members.Add(agent);
            foreach (var child in agents.Where(a => a.ParentId == agent.Id))
            {
                Walk(child, members);
            }
        }

        foreach (var root in agents.Where(a => a.ParentId is null || !ids.Contains(a.ParentId)))
        {
            var members = new List<AgentStatus>();
            Walk(root, members);
            groups[root.Id] = members;
        }

        return groups;
    }

    private static ExecutionModes Effort(string? effort) =>
        ExecutionModes.Of(ExecutionModeNative.Effort(effort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request));

    private static TheoryData<string> Names(string group)
    {
        var data = new TheoryData<string>();
        foreach (var item in Section.Value.GetProperty(group).EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static JsonElement Case(string group, string name) =>
        Section.Value.GetProperty(group).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}
