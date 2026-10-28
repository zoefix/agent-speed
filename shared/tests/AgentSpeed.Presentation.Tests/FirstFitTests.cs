using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class FirstFitTests
{
    private static readonly double[] Privacy = [420, 130, 17];

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(420, 0)]
    [InlineData(419, 1)]
    [InlineData(130, 1)]
    [InlineData(129.9, 2)]
    [InlineData(17, 2)]
    public void TheFirstFormThatFitsIsShown(double available, int index)
    {
        Assert.Equal(index, FirstFit.Index(Privacy, available));
    }

    [Theory]
    [InlineData(16.9)]
    [InlineData(1)]
    [InlineData(0)]
    public void TheLastFormIsShownWhenNoneFits(double available)
    {
        Assert.Equal(2, FirstFit.Index(Privacy, available));
    }

    [Fact]
    public void LayoutRoundingNoiseStillFits()
    {
        Assert.Equal(0, FirstFit.Index([120.005, 60], 120));
        Assert.Equal(1, FirstFit.Index([120.5, 60], 120));
        Assert.True(FirstFit.Fits(120 + FirstFit.Tolerance, 120));
        Assert.False(FirstFit.Fits(120.02, 120));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void UnboundedRoomShowsTheFirstForm(double available)
    {
        Assert.Equal(0, FirstFit.Index(Privacy, available));
    }

    [Fact]
    public void FormsNotOfferedAreSkipped()
    {
        Assert.Equal(1, FirstFit.Index([double.NaN, 50, 20], 100));
        Assert.Equal(2, FirstFit.Index([double.NaN, 50, 20], 30));
        Assert.Equal(1, FirstFit.Index([80, 50, double.NaN], 10));
        Assert.False(FirstFit.Fits(double.NaN, double.PositiveInfinity));
    }

    [Fact]
    public void NoFormsShowNothing()
    {
        Assert.Equal(-1, FirstFit.Index([], 100));
        Assert.Equal(-1, FirstFit.Index([double.NaN, double.NaN], 100));
        Assert.Throws<ArgumentNullException>(() => FirstFit.Index(null!, 100));
    }

    [Fact]
    public void AnEmptyFormFitsNoRoom()
    {
        Assert.Equal(0, FirstFit.Index([0, 0], 0));
    }

    /// <summary>
    /// The known case of DESIGN 4b: five cards in a row in the default 920 DIP window, with a scroll bar, leave each title
    /// (920 − 16 − 48 − 17 + 12) ÷ 5 − 12 − 2 − 32 ≈ 124 DIP. 入力トークン（キャッシュ含む） is 15 full-width characters at 12 DIP
    /// (about 180 DIP); 入力トークン is 6 (about 72 DIP).
    /// </summary>
    [Fact]
    public void TheJapaneseInputTitleShortensInFiveCardsAtTheDefaultWidth()
    {
        const double title = (920 - 16 - 48 - 17 + 12) / 5.0 - 12 - 2 - 32;
        Assert.Equal(1, FirstFit.Index([15 * 12, 6 * 12], title));
    }
}
