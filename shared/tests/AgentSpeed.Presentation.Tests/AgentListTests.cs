using AgentSpeed.Core;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class AgentListTests
{
    [Fact]
    public void LimitsMatchDesign()
    {
        Assert.Equal(6, AgentList.CollapsedRows);
        Assert.Equal(12, AgentList.ViewportRows);
    }

    [Fact]
    public void ShortListsOfferNoDisclosure()
    {
        var list = AgentList.Arrange(Roots(6));

        Assert.Equal(6, list.Collapsed.Rows.Count);
        Assert.Equal(0, list.Collapsed.Hidden);
        Assert.False(list.CanExpand);
        Assert.Equal(6, list.ExpandedViewportRows);
        Assert.False(list.ExpandedScrolls);
        Assert.Equal(6, list.Total);
    }

    [Fact]
    public void CollapsedShowsSixRowsAndExpandedShowsEveryAgent()
    {
        var agents = Roots(9);
        var list = AgentList.Arrange(agents);

        Assert.Equal(agents.Take(6), list.Collapsed.Rows.Select(r => r.Agent));
        Assert.Equal(3, list.Collapsed.Hidden);
        Assert.Equal(agents, list.Expanded.Rows.Select(r => r.Agent));
        Assert.Equal(0, list.Expanded.Hidden);
        Assert.True(list.CanExpand);
        Assert.Same(list.Collapsed, list.For(expanded: false));
        Assert.Same(list.Expanded, list.For(expanded: true));
    }

    [Fact]
    public void HiddenCountCountsAgentsNotGroups()
    {
        // Groups of 4 and 3 rows: six rows end inside the second group, leaving one of its agents hidden.
        var list = AgentList.Arrange(
            [Agent("p"), Agent("p1", "p"), Agent("p2", "p"), Agent("p3", "p"), Agent("q"), Agent("q1", "q"), Agent("q2", "q")]);

        Assert.Equal(["p", "p1", "p2", "p3", "q", "q1"], list.Collapsed.Rows.Select(r => r.Agent.Id));
        Assert.Equal(1, list.Collapsed.Hidden);
        Assert.True(list.CanExpand);
        Assert.Equal(7, list.Expanded.Rows.Count);
    }

    [Fact]
    public void FirstGroupLongerThanSixRowsStillShowsSixRows()
    {
        var agents = new List<AgentStatus> { Agent("r") };
        agents.AddRange(Enumerable.Range(0, 9).Select(i => Agent("c" + i, "r")));
        var list = AgentList.Arrange(agents);

        Assert.Equal(6, list.Collapsed.Rows.Count);
        Assert.Equal(4, list.Collapsed.Hidden);
        Assert.Equal("r", list.Collapsed.Rows[0].Agent.Id);
        Assert.Equal(10, list.ExpandedViewportRows);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(7, 7, false)]
    [InlineData(12, 12, false)]
    [InlineData(13, 12, true)]
    [InlineData(40, 12, true)]
    public void ExpandedViewportIsAtMostTwelveRows(int agents, int viewport, bool scrolls)
    {
        var list = AgentList.Arrange(Roots(agents));

        Assert.Equal(viewport, list.ExpandedViewportRows);
        Assert.Equal(viewport, list.ViewportRowsFor(expanded: true));
        Assert.Equal(Math.Min(agents, 6), list.ViewportRowsFor(expanded: false));
        Assert.Equal(scrolls, list.ExpandedScrolls);
        Assert.Equal(agents, list.Expanded.Rows.Count);
    }

    [Theory]
    [InlineData(3, 120, 120)]
    [InlineData(9, 240, 360)]
    [InlineData(12, 240, 480)]
    [InlineData(40, 240, 480)]
    public void ViewportHeightIsAWholeNumberOfRows(int agents, double collapsed, double expanded)
    {
        var list = AgentList.Arrange(Roots(agents));

        Assert.Equal(collapsed, list.ViewportHeightFor(expanded: false, rowHeight: 40));
        Assert.Equal(expanded, list.ViewportHeightFor(expanded: true, rowHeight: 40));
        Assert.Equal(0, list.ViewportHeightFor(expanded: true, rowHeight: 0));
        Assert.Equal(0, list.ViewportHeightFor(expanded: true, rowHeight: double.NaN));
    }

    [Fact]
    public void CollapsedRowsArePrefixOfExpandedRows()
    {
        var agents = new List<AgentStatus>();
        for (var i = 0; i < 25; i++)
        {
            agents.Add(Agent("a" + i, i % 4 == 0 ? null : "a" + (i - 1)));
        }

        var list = AgentList.Arrange(agents);

        Assert.Equal(
            list.Expanded.Rows.Take(list.Collapsed.Rows.Count).Select(r => r.Agent.Id),
            list.Collapsed.Rows.Select(r => r.Agent.Id));
        Assert.Equal(6, list.Collapsed.Rows.Count);
        Assert.Equal(19, list.Collapsed.Hidden);
    }

    [Fact]
    public void UnplaceableAgentsCountAsHiddenButDoNotOfferExpanding()
    {
        var list = AgentList.Arrange([Agent("c", "p"), Agent("p")]);

        Assert.Equal(["p"], list.Collapsed.Rows.Select(r => r.Agent.Id));
        Assert.Equal(1, list.Collapsed.Hidden);
        Assert.False(list.CanExpand);
        Assert.Equal(2, list.Total);
    }

    [Fact]
    public void EmptyList()
    {
        var list = AgentList.Arrange([]);

        Assert.Empty(list.Collapsed.Rows);
        Assert.Empty(list.Expanded.Rows);
        Assert.False(list.CanExpand);
        Assert.Equal(0, list.ExpandedViewportRows);
        Assert.Equal(AgentList.Empty.Total, list.Total);
        Assert.Throws<ArgumentNullException>(() => AgentList.Arrange(null!));
    }

    private static List<AgentStatus> Roots(int count) => Enumerable.Range(0, count).Select(i => Agent("a" + i)).ToList();

    private static AgentStatus Agent(string id, string? parent = null) => new(
        id, SourceKind.ClaudeCode, "p", null, AgentPhase.Generating, parent is not null, Rate.Zero, null,
        DateTimeOffset.UnixEpoch, parent);
}
