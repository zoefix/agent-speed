using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleSettingsNavigationTests
{
    private const int Presets = 9;

    [Theory]
    [InlineData(559, 2)]
    [InlineData(560, 3)]
    [InlineData(900, 3)]
    [InlineData(0, 2)]
    public void ThreeColumnsFrom560(double width, int expected)
    {
        Assert.Equal(expected, TitleCardNavigation.Columns(width));
    }

    [Theory]
    [InlineData(0, TitleCardMove.Left, 0)]
    [InlineData(0, TitleCardMove.Right, 1)]
    [InlineData(8, TitleCardMove.Right, 9)]
    [InlineData(9, TitleCardMove.Left, 8)]
    [InlineData(11, TitleCardMove.Right, 11)]
    [InlineData(0, TitleCardMove.Up, 0)]
    [InlineData(1, TitleCardMove.Down, 4)]
    [InlineData(6, TitleCardMove.Down, 9)]
    [InlineData(7, TitleCardMove.Down, 10)]
    [InlineData(8, TitleCardMove.Down, 11)]
    [InlineData(9, TitleCardMove.Up, 6)]
    [InlineData(11, TitleCardMove.Up, 8)]
    [InlineData(10, TitleCardMove.Down, 10)]
    public void ThreeColumnsMoveRowMajorAcrossTheTwoGrids(int current, TitleCardMove move, int expected)
    {
        // Nine presets, two custom sets and the New card.
        Assert.Equal(expected, TitleCardNavigation.Move(current, move, Presets, 12, 3));
    }

    [Theory]
    [InlineData(6, TitleCardMove.Down, 8)]
    [InlineData(7, TitleCardMove.Down, 10)]
    [InlineData(8, TitleCardMove.Down, 9)]
    [InlineData(9, TitleCardMove.Up, 8)]
    [InlineData(10, TitleCardMove.Up, 8)]
    [InlineData(9, TitleCardMove.Down, 11)]
    [InlineData(12, TitleCardMove.Up, 10)]
    public void TwoColumnsKeepTheColumnAndClampToTheShortRow(int current, TitleCardMove move, int expected)
    {
        // Nine presets (the last row has one card), three custom sets and the New card.
        Assert.Equal(expected, TitleCardNavigation.Move(current, move, Presets, 13, 2));
    }

    [Fact]
    public void DownFromTheLastPresetRowStaysWithoutACustomGroup()
    {
        Assert.Equal(7, TitleCardNavigation.Move(7, TitleCardMove.Down, Presets, Presets, 3));
    }

    [Fact]
    public void DownIntoAShortCustomRowTakesItsLastCard()
    {
        // Only the New card below the presets.
        Assert.Equal(9, TitleCardNavigation.Move(8, TitleCardMove.Down, Presets, 10, 3));
    }

    [Fact]
    public void OutOfRangeInputIsClamped()
    {
        Assert.Equal(0, TitleCardNavigation.Move(5, TitleCardMove.Left, Presets, 0, 3));
        Assert.Equal(11, TitleCardNavigation.Move(40, TitleCardMove.Right, Presets, 12, 3));
        Assert.Equal(1, TitleCardNavigation.Move(0, TitleCardMove.Down, Presets, 12, 0));
    }
}
