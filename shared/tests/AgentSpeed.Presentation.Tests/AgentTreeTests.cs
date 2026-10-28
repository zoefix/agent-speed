using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Layout.TreeGuide;

namespace AgentSpeed.Presentation.Tests;

public class AgentTreeTests
{
    [Fact]
    public void RootsAreFlushLeftEvenWhenTheyAreSubagents()
    {
        var layout = AgentTree.Arrange([Agent("a", subagent: true), Agent("b", parent: "gone", subagent: true)], 6);

        Assert.All(layout.Rows, row =>
        {
            Assert.Equal(0, row.Depth);
            Assert.Equal(0, row.Indent);
            Assert.True(row.Guides.IsEmpty);
        });
    }

    [Fact]
    public void ChildrenAreIndentedByTreeDepthUpToTwoLevels()
    {
        var layout = AgentTree.Arrange(
            [Agent("r"), Agent("c", "r"), Agent("g", "c"), Agent("gg", "g"), Agent("ggg", "gg")], 6);

        Assert.Equal([0, 1, 2, 3, 4], layout.Rows.Select(r => r.Depth));
        Assert.Equal([0, 1, 2, 2, 2], layout.Rows.Select(r => r.Indent));
    }

    [Fact]
    public void ConnectorsRunFromEachParentToItsDirectChildren()
    {
        var layout = AgentTree.Arrange(
            [Agent("p"), Agent("c1", "p"), Agent("g1", "c1"), Agent("g2", "c1"), Agent("c2", "p"), Agent("q")], 6);

        Assert.Equal(
            [
                default,
                new TreeGuides(Branch, None, None),
                new TreeGuides(Through, Branch, None),
                new TreeGuides(Through, Last, None),
                new TreeGuides(Last, None, None),
                default,
            ],
            layout.Rows.Select(r => r.Guides));
    }

    [Fact]
    public void LevelsPastTheCapShareTheDeeperColumn()
    {
        var layout = AgentTree.Arrange(
            [Agent("r"), Agent("c", "r"), Agent("g", "c"), Agent("x", "g"), Agent("y", "x"), Agent("z", "g")], 6);

        Assert.Equal(
            [
                default,
                new TreeGuides(Last, None, None),
                new TreeGuides(None, Last, None),
                new TreeGuides(None, None, Branch),
                new TreeGuides(None, None, Branch),
                new TreeGuides(None, None, Last),
            ],
            layout.Rows.Select(r => r.Guides));
    }

    [Fact]
    public void RowLimitKeepsTheEngineOrderAndCountsTheRest()
    {
        var agents = Enumerable.Range(0, 9).Select(i => Agent("a" + i)).ToList();

        var layout = AgentTree.Arrange(agents, 6);

        Assert.Equal(agents.Take(6), layout.Rows.Select(r => r.Agent));
        Assert.Equal(3, layout.Hidden);
    }

    [Fact]
    public void RowLimitMayShowAParentWithoutItsChildren()
    {
        var layout = AgentTree.Arrange([Agent("a"), Agent("b"), Agent("p"), Agent("c", "p")], 3);

        Assert.Equal(["a", "b", "p"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal(1, layout.Hidden);
    }

    [Fact]
    public void RowLimitCutsTheDepthFirstOrderInsideAGroup()
    {
        var layout = AgentTree.Arrange(
            [Agent("a"), Agent("p"), Agent("c1", "p"), Agent("c2", "p"), Agent("c3", "p"), Agent("b"), Agent("q")], 4);

        Assert.Equal(["a", "p", "c1", "c2"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal(3, layout.Hidden);
        Assert.Equal(new TreeGuides(Last, None, None), layout.Rows[3].Guides);
    }

    [Fact]
    public void AnOversizedFirstGroupShowsItsFirstRowsDepthFirst()
    {
        var layout = AgentTree.Arrange(
            [
                Agent("r"), Agent("c1", "r"), Agent("g1", "c1"), Agent("g2", "c1"), Agent("c2", "r"), Agent("g3", "c2"),
                Agent("c3", "r"), Agent("c4", "r"), Agent("x"),
            ],
            6);

        Assert.Equal(["r", "c1", "g1", "g2", "c2", "g3"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal([0, 1, 2, 2, 1, 2], layout.Rows.Select(r => r.Depth));
        Assert.Equal(3, layout.Hidden);
        Assert.Equal(new TreeGuides(Last, None, None), layout.Rows[4].Guides);
        Assert.Equal(new TreeGuides(None, Last, None), layout.Rows[5].Guides);
    }

    [Fact]
    public void ShownRowsAlwaysIncludeEachParent()
    {
        var agents = new List<AgentStatus> { Agent("r0") };
        for (var i = 1; i < 20; i++)
        {
            agents.Add(Agent("r" + i, i % 3 == 0 ? null : "r" + (i - 1)));
        }

        for (var limit = 0; limit <= agents.Count; limit++)
        {
            var layout = AgentTree.Arrange(agents, limit);
            var shown = layout.Rows.Select(r => r.Agent.Id).ToHashSet();
            Assert.All(layout.Rows, row => Assert.True(row.Depth == 0 || shown.Contains(row.Agent.ParentId!)));
            Assert.Equal(agents.Count - layout.Rows.Count, layout.Hidden);
            Assert.Equal(Math.Min(limit, agents.Count), layout.Rows.Count);
        }
    }

    [Fact]
    public void ChildrenFollowTheirParentEvenWhenListedAfterAnotherRoot()
    {
        var layout = AgentTree.Arrange([Agent("p"), Agent("q"), Agent("c", "p")]);

        Assert.Equal(["p", "c", "q"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal([0, 1, 0], layout.Rows.Select(r => r.Depth));
        Assert.Equal(new TreeGuides(Last, None, None), layout.Rows[1].Guides);
    }

    [Fact]
    public void WithoutALimitEveryPlaceableAgentIsShown()
    {
        var agents = Enumerable.Range(0, 30).Select(i => Agent("a" + i, i % 2 == 1 ? "a" + (i - 1) : null)).ToList();

        var layout = AgentTree.Arrange(agents);

        Assert.Equal(agents, layout.Rows.Select(r => r.Agent));
        Assert.Equal(0, layout.Hidden);
    }

    [Fact]
    public void DuplicateIdsAreShownOnce()
    {
        var layout = AgentTree.Arrange([Agent("a"), Agent("a"), Agent("b")], 6);

        Assert.Equal(["a", "b"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal(1, layout.Hidden);
    }

    [Fact]
    public void ChildListedBeforeItsParentIsLeftOut()
    {
        var layout = AgentTree.Arrange([Agent("c", "p"), Agent("p"), Agent("d", "p")], 6);

        Assert.Equal(["p", "d"], layout.Rows.Select(r => r.Agent.Id));
        Assert.Equal([0, 1], layout.Rows.Select(r => r.Depth));
        Assert.Equal(1, layout.Hidden);
    }

    [Fact]
    public void SelfParentIsARoot()
    {
        var row = Assert.Single(AgentTree.Arrange([Agent("a", "a")], 6).Rows);

        Assert.Equal(0, row.Depth);
    }

    [Fact]
    public void EmptyListHasNoRows()
    {
        var layout = AgentTree.Arrange([], 6);

        Assert.Empty(layout.Rows);
        Assert.Equal(0, layout.Hidden);
    }

    [Fact]
    public void DisclosureTexts()
    {
        var table = Localizer.LoadEmbeddedTable();
        var zh = new Localizer(table, LanguageCodes.ZhHant);
        var en = new Localizer(table, LanguageCodes.En);

        Assert.Equal("+4 個 agent", DisplayText.ExpandAgents(4, zh));
        Assert.Equal("+2 more", DisplayText.ExpandAgents(2, en));
        Assert.Equal("顯示全部 10 個 agent", DisplayText.ExpandAgentsAccessibility(10, zh));
        Assert.Equal("Show all 10 agents", DisplayText.ExpandAgentsAccessibility(10, en));
        Assert.Equal("收合", zh["agents.collapse"]);
        Assert.Equal("Show less", en["agents.collapse"]);
    }

    private static AgentStatus Agent(string id, string? parent = null, bool subagent = false) => new(
        id, SourceKind.Codex, "p", null, AgentPhase.Generating, subagent || parent is not null, Rate.Zero, null,
        DateTimeOffset.UnixEpoch, parent);
}
