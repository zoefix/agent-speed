using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.CodexLines;

namespace AgentSpeed.Core.Tests;

public sealed class AgentParentTests : IDisposable
{
    private const long Now = T0 + 3_600_000;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(@"C:\Users\me\.claude\projects\p\s1\subagents\agent-a.jsonl", @"C:\Users\me\.claude\projects\p\s1.jsonl")]
    [InlineData("/Users/me/.claude/projects/p/s1/subagents/agent-a.jsonl", "/Users/me/.claude/projects/p/s1.jsonl")]
    [InlineData(@"C:\Users\me\.claude\projects/p/s1\subagents/agent-a.jsonl", @"C:\Users\me\.claude\projects/p/s1.jsonl")]
    public void ClaudeSubagentsBelongToTheirSessionWithEitherSeparator(string child, string parent)
    {
        Assert.Equal(parent, ParentLinks.ClaudeParentPath(child));

        var parents = ParentLinks.Resolve([Claude(parent), Claude(child)], StringComparer.Ordinal);

        Assert.Equal(parent, Assert.Single(parents, p => p.Key == child).Value);
        Assert.False(parents.ContainsKey(parent));
    }

    [Theory]
    [InlineData("agent-a.jsonl")]
    [InlineData("/Users/me/.claude/projects/p/s1.jsonl")]
    [InlineData("/Users/me/.claude/projects/p/s1/agents/agent-a.jsonl")]
    public void OtherClaudePathsHaveNoParentPath(string path) => Assert.Null(ParentLinks.ClaudeParentPath(path));

    [Fact]
    public void ClaudeParentMustBeATrackedAgent()
    {
        var child = @"C:\p\s1\subagents\agent-a.jsonl";

        Assert.Empty(ParentLinks.Resolve([Claude(child), Claude(@"C:\p\s2.jsonl")], StringComparer.Ordinal));
    }

    [Fact]
    public void ClaudeParentIsReportedAsTrackedEvenWhenTheCaseDiffers()
    {
        var parents = ParentLinks.Resolve(
            [Claude(@"C:\P\S1.jsonl"), Claude(@"C:\p\s1\subagents\agent-a.jsonl")], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(@"C:\P\S1.jsonl", parents[@"C:\p\s1\subagents\agent-a.jsonl"]);
    }

    [Fact]
    public void CodexParentIsTheEarliestCreatedFileOfTheParentThread()
    {
        var parents = ParentLinks.Resolve(
            [
                Codex("resumed", "main", null, 2000),
                Codex("original", "main", null, 1000),
                Codex("worker", "w", "main", 3000),
                Codex("orphan", "o", "gone", 3000),
            ],
            StringComparer.Ordinal);

        Assert.Equal("original", parents["worker"]);
        Assert.Equal(["worker"], parents.Keys);
    }

    [Fact]
    public void SelfReferencesAndCyclesResolveToNull()
    {
        var parents = ParentLinks.Resolve(
            [
                Codex("self", "s", "s", 1000),
                Codex("a", "a", "b", 1000),
                Codex("b", "b", "a", 1000),
                Codex("below", "c", "a", 1000),
            ],
            StringComparer.Ordinal);

        Assert.Equal(new Dictionary<string, string> { ["b"] = "a", ["below"] = "a" }, parents);
    }

    [Fact]
    public void CodexParentThreadComesFromTheFirstSessionMeta()
    {
        var parser = new CodexParser("/s/rollout-worker.jsonl", new Deduper());
        Feed.Lines(parser, new RecordingSink(),
            SessionMeta(T0, "worker", extra: ""","parent_thread_id":"main","source":{"subagent":{"thread_spawn":{"parent_thread_id":"other"}}}"""),
            SessionMeta(T0 + 10, "main"));

        Assert.Equal("worker", parser.MetaThreadId);
        Assert.Equal("main", parser.ParentThreadId);
        Assert.Equal(T0, parser.CreatedMs);
        Assert.True(parser.IsSubagent);
    }

    [Fact]
    public void CodexParentThreadFallsBackToTheThreadSpawnSource()
    {
        var parser = new CodexParser("/s/rollout-worker.jsonl", new Deduper());
        Feed.Lines(parser, new RecordingSink(),
            SessionMeta(T0, "worker", extra: ""","source":{"subagent":{"thread_spawn":{"depth":1,"parent_thread_id":"main"}}}"""));

        Assert.Equal("main", parser.ParentThreadId);
    }

    [Theory]
    [InlineData(""","forked_from_id":"main" """)]
    [InlineData(""","source":"cli" """)]
    [InlineData(""","source":{"subagent":"review"} """)]
    [InlineData(""","parent_thread_id":"","source":{"subagent":{"thread_spawn":{"parent_thread_id":null}}} """)]
    public void CodexWithoutAParentThreadHasNoParent(string extra)
    {
        var parser = new CodexParser("/s/rollout-x.jsonl", new Deduper());
        Feed.Lines(parser, new RecordingSink(), SessionMeta(T0, "x", extra: extra));

        Assert.Null(parser.ParentThreadId);
    }

    [Fact]
    public void DshParentIsTheEarliestCreatedFileOfTheParentSession()
    {
        var parents = ParentLinks.Resolve(
            [
                Dsh("resumed", "main", null, 2000),
                Dsh("original", "main", null, 1000),
                Dsh("explorer", "sub", "main", 3000),
                Dsh("orphan", "o", "gone", 3000),
                Dsh("self", "me", "me", 3000),
            ],
            StringComparer.Ordinal);

        Assert.Equal(new Dictionary<string, string> { ["explorer"] = "original" }, parents);
    }

    [Fact]
    public void DshSessionsAndCodexThreadsNeverLinkToEachOther()
    {
        var parents = ParentLinks.Resolve(
            [Codex("codex-main", "main", null, 1000), Dsh("dsh-sub", "sub", "main", 2000),
                Dsh("dsh-main", "x", null, 1000), Codex("codex-sub", "y", "x", 2000)],
            StringComparer.Ordinal);

        Assert.Empty(parents);
    }

    [Fact]
    public void DshParentSessionComesFromTheFirstHeader()
    {
        var parser = new DshParser("/d/session-sub/session.v4.jsonl.zstd", new Deduper());
        Feed.Lines(parser, new RecordingSink(),
            DshLines.Session(T0, id: "sub", extra: ",\"parentSession\":\"main\""),
            DshLines.Session(T0 + 10, id: "other", extra: ",\"parentSession\":\"elsewhere\""));

        var link = AgentLink.Of(parser);

        Assert.Equal(new AgentLink(parser.AgentId, LinkKind.DshSession, "sub", "main", T0), link);
    }

    [Fact]
    public void MonitorListsDshSubagentsRightAfterTheirParents()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var todayStart = now - 7_200_000;
        var dshRoot = _temp.Combine("dsh", "sessions");
        var main = ZstdFrames.Write(Path.Combine(dshRoot, "--p--", "session-main", "session.v4.jsonl.zstd"),
            ZstdFrames.Lines(DshLines.Session(now - 3_000_000, id: "session-main", cwd: "/w/quasar")),
            ZstdFrames.Lines(DshLines.StepStart(1, now - 2_000_000), DshLines.TurnEnd(2, now - 1_900_000)));
        var sub = ZstdFrames.Write(Path.Combine(dshRoot, "--p--", "session-sub", "session.v4.jsonl.zstd"),
            ZstdFrames.Lines(DshLines.Session(now - 60_000, id: "session-sub", cwd: "/w/quasar",
                extra: ",\"parentSession\":\"session-main\",\"origin\":\"subagent\"")),
            ZstdFrames.Lines(DshLines.StepStart(3, now - 1000)));
        var core = new MonitorCore(MonitorConfiguration.Default, _temp.Combine("codex"), _temp.Combine("claude"), dshRoot, todayStart);

        core.Seed(now, todayStart, CancellationToken.None);
        var agents = core.BuildSnapshot(now, todayStart, MonitorState.Running, observeLive: false).Snapshot.Agents;

        Assert.Equal([main, sub], agents.Select(a => a.Id));
        Assert.Equal([null, main], agents.Select(a => a.ParentId));
        Assert.All(agents, a => Assert.Equal(SourceKind.DeepSeek, a.Source));
        Assert.True(agents[1].IsSubagent);
        Assert.Equal(AgentPhase.Generating, agents[1].Phase);
    }

    [Fact]
    public void InactiveParentIsListedRightBeforeItsRunningSubagent()
    {
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(Agent("main", AgentPhase.Generating, Now - 3_600_000));
        engine.UpdateAgent(Agent("worker", AgentPhase.Generating, Now - 1000, parentId: "main"));
        engine.UpdateAgent(Agent("other", AgentPhase.Tools, Now - 2000));
        engine.UpdateAgent(Agent("old", AgentPhase.Idle, Now - 3_600_000));

        var agents = engine.Compute(Now, 0, observeLive: false).Agents;

        Assert.Equal(["main", "worker", "other"], agents.Select(a => a.Info.Id));
        Assert.Equal(AgentPhase.Idle, agents[0].Phase);
        Assert.Null(agents[0].ParentId);
        Assert.Equal("main", agents[1].ParentId);
    }

    [Fact]
    public void AncestorsAreAddedUpTheWholeChain()
    {
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(Agent("root", AgentPhase.Idle, Now - 3_600_000));
        engine.UpdateAgent(Agent("middle", AgentPhase.Idle, Now - 3_000_000, parentId: "root"));
        engine.UpdateAgent(Agent("leaf", AgentPhase.Generating, Now - 1000, parentId: "middle"));

        var agents = engine.Compute(Now, 0, observeLive: false).Agents;

        Assert.Equal(["root", "middle", "leaf"], agents.Select(a => a.Info.Id));
        Assert.Equal([null, "root", "middle"], agents.Select(a => a.ParentId));
    }

    [Fact]
    public void SubagentWhoseParentIsUnknownIsARoot()
    {
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(Agent("worker", AgentPhase.Generating, Now - 1000, parentId: "gone", isSubagent: true));

        var agent = Assert.Single(engine.Compute(Now, 0, observeLive: false).Agents);

        Assert.Null(agent.ParentId);
        Assert.True(agent.Info.IsSubagent);
    }

    [Fact]
    public void BadParentCyclesNeverHideAgents()
    {
        var engine = new SpeedEngine(0);
        engine.UpdateAgent(Agent("a", AgentPhase.Generating, Now - 1000, parentId: "b"));
        engine.UpdateAgent(Agent("b", AgentPhase.Generating, Now - 2000, parentId: "a"));

        var agents = engine.Compute(Now, 0, observeLive: false).Agents;

        Assert.Equal(["a", "b"], agents.Select(a => a.Info.Id).Order(StringComparer.Ordinal));
        Assert.Single(agents, a => a.ParentId is null);
    }

    [Fact]
    public void MonitorListsRealSubagentsRightAfterTheirParents()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var todayStart = now - 7_200_000;
        var claudeRoot = _temp.Combine("claude", "projects");
        var codexRoot = _temp.Combine("codex", "sessions");
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(codexRoot);

        var session = Path.GetFullPath(_temp.WriteFile("claude/projects/p/s1.jsonl", ClaudeLines.UserText(now - 1_800_000, "plan") + "\n"));
        var subagent = Path.GetFullPath(_temp.WriteFile("claude/projects/p/s1/subagents/agent-a.jsonl", ClaudeLines.UserText(now - 1000, "go") + "\n"));
        var main = Path.GetFullPath(_temp.WriteFile("codex/sessions/rollout-main.jsonl",
            SessionMeta(now - 2_400_000, "main") + "\n" + UserMessage(now - 2_400_000) + "\n"));
        var worker = Path.GetFullPath(_temp.WriteFile("codex/sessions/rollout-worker.jsonl",
            SessionMeta(now - 5000, "worker", extra: ""","parent_thread_id":"main" """) + "\n" + UserMessage(now - 4000) + "\n"));
        var core = new MonitorCore(MonitorConfiguration.Default, codexRoot, claudeRoot, _temp.Combine("dsh"), todayStart);

        core.Seed(now, todayStart, CancellationToken.None);
        var agents = core.BuildSnapshot(now, todayStart, MonitorState.Running, observeLive: false).Snapshot.Agents.ToList();

        Assert.Equal(4, agents.Count);
        foreach (var (child, parent) in new[] { (subagent, session), (worker, main) })
        {
            var index = agents.FindIndex(a => a.Id == child);
            Assert.True(index > 0, child);
            Assert.Equal(parent, agents[index].ParentId);
            Assert.Equal(parent, agents[index - 1].Id);
            Assert.Null(agents[index - 1].ParentId);
        }
    }

    private static AgentLink Claude(string path) => new(path, LinkKind.Path, null, null, null);

    private static AgentLink Codex(string id, string thread, string? parentThread, long createdMs) =>
        new(id, LinkKind.CodexThread, thread, parentThread, createdMs);

    private static AgentLink Dsh(string id, string session, string? parentSession, long createdMs) =>
        new(id, LinkKind.DshSession, session, parentSession, createdMs);

    private static AgentInfo Agent(string id, AgentPhase phase, long lastLineMs, string? parentId = null, bool isSubagent = false) =>
        new(id, SourceKind.Codex, "p", "m", isSubagent, phase, null, lastLineMs, parentId);
}
