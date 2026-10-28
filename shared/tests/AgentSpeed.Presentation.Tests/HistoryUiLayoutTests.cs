using AgentSpeed.Presentation.History;

namespace AgentSpeed.Presentation.Tests;

public class HistoryUiLayoutTests
{
    [Theory]
    [InlineData(836, new[] { 5 })]
    [InlineData(1400, new[] { 5 })]
    [InlineData(835.9, new[] { 3, 2 })]
    [InlineData(672, new[] { 3, 2 })]
    [InlineData(0, new[] { 3, 2 })]
    [InlineData(double.NaN, new[] { 5 })]
    [InlineData(double.PositiveInfinity, new[] { 5 })]
    public void FiveCardsShareOneRowOrWrapThreeAndTwo(double width, int[] rows)
    {
        Assert.Equal(rows, HistoryLayout.CardRows(5, width));
    }

    [Fact]
    public void TheDefaultWindowKeepsFiveCardsInOneRow()
    {
        // 920 DIP less the frame (16) and the page margins (48), with or without a 17 DIP scroll bar.
        Assert.Equal([5], HistoryLayout.CardRows(5, 920 - 16 - 48));
        Assert.Equal([5], HistoryLayout.CardRows(5, 920 - 16 - 48 - 17));
        Assert.Equal(836, HistoryLayout.FiveCardsMinWidth);
    }

    [Fact]
    public void NoCardsNeedNoRows()
    {
        Assert.Empty(HistoryLayout.CardRows(0, 900));
        Assert.Empty(HistoryLayout.CardRows(-1, 600));
    }

    [Fact]
    public void TooltipPrefersTheRightSide()
    {
        var (x, y) = HistoryLayout.TooltipPosition(100, 120, 100, 200, 120, 800, 260);
        Assert.Equal(132, x);
        Assert.Equal(40, y);
    }

    [Fact]
    public void TooltipFlipsLeftNearTheRightEdge()
    {
        var (x, _) = HistoryLayout.TooltipPosition(700, 720, 100, 200, 120, 800, 260);
        Assert.Equal(488, x);
    }

    [Fact]
    public void TooltipStaysInsideTheBounds()
    {
        var (x, y) = HistoryLayout.TooltipPosition(150, 170, -50, 280, 300, 320, 260);
        Assert.InRange(x, 0, 40);
        Assert.Equal(0, y);

        var (_, low) = HistoryLayout.TooltipPosition(10, 20, 900, 100, 120, 800, 260);
        Assert.Equal(140, low);
    }

    [Theory]
    [InlineData(0, 320, 32, 100, 2, 0, 12)]
    [InlineData(640, 320, 32, 100, 2, 18, 32)]
    [InlineData(-200, 320, 32, 100, 2, 0, 6)]
    [InlineData(5000, 320, 32, 100, 2, 99, 99)]
    public void RowWindowCoversTheViewportWithOverscan(double top, double height, double row, int count, int overscan, int first, int last)
    {
        Assert.Equal((first, last), HistoryLayout.RowWindow(top, height, row, count, overscan));
    }

    [Fact]
    public void RowWindowIsEmptyWithoutRows()
    {
        var (first, last) = HistoryLayout.RowWindow(0, 300, 32, 0, 4);
        Assert.True(first > last);
    }

    [Theory]
    [InlineData(1000, 32, 400, 2000, 816)]
    [InlineData(50, 32, 400, 2000, 0)]
    [InlineData(1990, 32, 400, 1800, 1800)]
    [InlineData(500, 32, 400, -5, 0)]
    public void CenteredOffsetClamps(double rowTop, double rowHeight, double viewport, double scrollable, double expected)
    {
        Assert.Equal(expected, HistoryLayout.CenteredOffset(rowTop, rowHeight, viewport, scrollable));
    }
}
