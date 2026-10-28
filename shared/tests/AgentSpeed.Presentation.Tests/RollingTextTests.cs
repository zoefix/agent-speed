using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests;

public class RollingTextTests
{
    [Fact]
    public void StartsEmpty()
    {
        var text = new RollingText();
        Assert.Equal(string.Empty, text.Current);
        Assert.Equal(0, text.SlotCount);
        Assert.False(text.IsAnimating(0));
        Assert.Equal(new RollingSlot(null, null, 1, 1), text.Slot(0, 0));
    }

    [Fact]
    public void SlotsAreAlignedFromTheRight()
    {
        var text = new RollingText();
        text.Set("99", 1, 0, animate: false);
        text.Set("100", 1, 10, animate: true);

        Assert.Equal(3, text.SlotCount);
        var units = text.Slot(0, 10);
        Assert.Equal('0', units.Current);
        Assert.Equal('9', units.Previous);
        var hundreds = text.Slot(2, 10);
        Assert.Equal('1', hundreds.Current);
        Assert.Null(hundreds.Previous);
        Assert.True(hundreds.IsChanging);
        Assert.Null(text.Slot(3, 10).Current);
    }

    [Fact]
    public void UnchangedCharactersDoNotRoll()
    {
        var text = new RollingText();
        text.Set("128", 1, 0, animate: false);
        text.Set("138", 1, 5, animate: true);

        var units = text.Slot(0, 5);
        Assert.Equal(1, units.Progress);
        Assert.False(units.IsChanging);

        var tens = text.Slot(1, 5);
        Assert.Equal('3', tens.Current);
        Assert.Equal('2', tens.Previous);
        Assert.True(tens.IsChanging);

        Assert.False(text.Slot(2, 5).IsChanging);
    }

    [Fact]
    public void SlotsRollRightMostFirstWithStagger()
    {
        var text = new RollingText();
        text.Set("111", 1, 0, animate: false);
        text.Set("222", 1, 1, animate: true);

        Assert.Equal(0, text.Slot(0, 1).Progress);
        Assert.Equal(0.5, text.Slot(0, 1 + RollingText.Duration / 2).Progress, 9);
        Assert.True(text.Slot(1, 1 + RollingText.Duration / 2).Progress < text.Slot(0, 1 + RollingText.Duration / 2).Progress);

        Assert.Equal(1, text.Slot(0, 1 + RollingText.Duration).Progress, 9);
        Assert.True(text.Slot(2, 1 + RollingText.Duration).IsChanging);
        Assert.False(text.Slot(2, 1 + RollingText.Duration + 2 * RollingText.Stagger + 1e-9).IsChanging);
    }

    [Fact]
    public void IsAnimatingCoversTheLastStaggeredSlot()
    {
        var text = new RollingText();
        text.Set("1", 1, 0, animate: false);
        text.Set("1.5", 1, 2, animate: true);

        Assert.True(text.IsAnimating(2));
        Assert.True(text.IsAnimating(2 + RollingText.Duration + 2 * RollingText.Stagger - 1e-6));
        Assert.False(text.IsAnimating(2 + RollingText.Duration + 2 * RollingText.Stagger + 1e-9));
    }

    [Fact]
    public void RollsAreShortForTwoHertzUpdates()
    {
        Assert.InRange(RollingText.Duration, 0.18, 0.22);
        Assert.Equal(1, RollingText.Ease(1), 9);
        Assert.True(RollingText.Ease(0.5) > 0.5);
    }

    [Fact]
    public void OnlyChangedDigitsRollAndRightMostStartsFirst()
    {
        var text = new RollingText();
        text.Set("596", 1, 0, animate: false);
        text.Set("601", 1, 10, animate: true);

        Assert.True(text.Slot(0, 10).IsChanging);
        Assert.True(text.Slot(1, 10).IsChanging);
        Assert.True(text.Slot(2, 10).IsChanging);
        Assert.True(text.Slot(2, 10.05).Progress < text.Slot(0, 10.05).Progress);

        text.Set("611", 1, 11, animate: true);
        Assert.False(text.Slot(0, 11).IsChanging);
        Assert.True(text.Slot(1, 11).IsChanging);
        Assert.False(text.Slot(2, 11).IsChanging);
        Assert.Equal(0, text.Slot(1, 11).Progress);
    }

    [Fact]
    public void RetargetMidRollContinuesFromTheCurrentPosition()
    {
        var text = new RollingText();
        text.Set("10", 1, 0, animate: false);
        text.Set("20", 1, 1, animate: true);

        var late = 1 + RollingText.Duration * 0.8;
        var before = text.Slot(1, late);
        var eased = RollingText.Ease(before.Progress);
        var incomingY = before.CurrentPosition(eased);
        var incomingAlpha = before.CurrentOpacity(eased);

        text.Set("30", 1, late, animate: true);
        var after = text.Slot(1, late);
        Assert.Equal('3', after.Current);
        Assert.Equal('2', after.Previous);
        Assert.Equal(incomingY, after.PreviousPosition(0), 9);
        Assert.Equal(incomingAlpha, after.PreviousOpacity(0), 9);

        var early = late + RollingText.Duration * 0.1;
        var midway = text.Slot(1, early);
        var e2 = RollingText.Ease(midway.Progress);
        var outgoingY = midway.PreviousPosition(e2);
        var outgoingAlpha = midway.PreviousOpacity(e2);
        text.Set("40", 1, early, animate: true);
        var retargeted = text.Slot(1, early);
        Assert.Equal('2', retargeted.Previous);
        Assert.Equal('4', retargeted.Current);
        Assert.Equal(outgoingY, retargeted.PreviousPosition(0), 9);
        Assert.Equal(outgoingAlpha, retargeted.PreviousOpacity(0), 9);
    }

    [Fact]
    public void FinishedRollRestsAtZeroOffset()
    {
        var text = new RollingText();
        text.Set("1", 1, 0, animate: false);
        text.Set("2", -1, 0, animate: true);
        var slot = text.Slot(0, 5);
        Assert.False(slot.IsChanging);
        Assert.Equal(0, slot.CurrentPosition(1));
        Assert.Equal(1, slot.CurrentOpacity(1));
        Assert.Equal(1, slot.PreviousPosition(1));
    }

    [Fact]
    public void NonAnimatedSetSettlesImmediately()
    {
        var text = new RollingText();
        text.Set("42", 1, 0, animate: false);
        text.Set("43", -1, 0, animate: false);
        Assert.Equal("43", text.Current);
        Assert.False(text.IsAnimating(0));
        Assert.False(text.Slot(0, 0).IsChanging);
        Assert.Equal(-1, text.Slot(0, 0).Direction);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, -1)]
    public void DirectionIsNormalized(int direction, int expected)
    {
        var text = new RollingText();
        text.Set("1", direction, 0, animate: true);
        Assert.Equal(expected, text.Slot(0, 0).Direction);
    }

    [Fact]
    public void SettingTheSameTextDoesNotRestart()
    {
        var text = new RollingText();
        text.Set("7", 1, 0, animate: true);
        text.Set("7", 1, 100, animate: true);
        Assert.Equal(1, text.Slot(0, RollingText.Duration).Progress);
        Assert.False(text.IsAnimating(100));
    }

    [Fact]
    public void NullTextThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new RollingText().Set(null!, 1, 0, animate: true));
    }
}
