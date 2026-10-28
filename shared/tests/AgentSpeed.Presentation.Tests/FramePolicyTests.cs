using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests;

public class FramePolicyTests
{
    [Theory]
    [InlineData(FrameClass.Fps60, 144, 2)]
    [InlineData(FrameClass.Fps30, 144, 5)]
    [InlineData(FrameClass.Fps15, 144, 10)]
    [InlineData(FrameClass.Fps24, 144, 6)]
    [InlineData(FrameClass.Fps60, 120, 2)]
    [InlineData(FrameClass.Fps30, 120, 4)]
    [InlineData(FrameClass.Fps60, 60, 1)]
    [InlineData(FrameClass.Fps30, 60, 2)]
    [InlineData(FrameClass.Fps15, 60, 4)]
    [InlineData(FrameClass.Fps24, 60, 3)]
    [InlineData(FrameClass.Fps60, 75, 2)]
    [InlineData(FrameClass.Fps60, 240, 4)]
    [InlineData(FrameClass.None, 144, 0)]
    public void DivisorsFollowTheDisplayRate(FrameClass frameClass, double hz, int expected) =>
        Assert.Equal(expected, FramePolicy.Divisor(frameClass, hz));

    [Fact]
    public void NeverAboveSeventyTwoFramesPerSecond()
    {
        for (var hz = 30.0; hz <= 360; hz += 1)
        {
            var divisor = FramePolicy.Divisor(FrameClass.Fps60, hz);
            Assert.True(hz / divisor <= 72.0001, $"{hz} Hz → {hz / divisor}");
        }
    }

    [Fact]
    public void FourSixtyFpsOrbsDropToThirtyExceptTheFocusedOne()
    {
        Assert.Equal(FrameClass.Fps60, FramePolicy.Crowded(FrameClass.Fps60, 3, focused: false));
        Assert.Equal(FrameClass.Fps30, FramePolicy.Crowded(FrameClass.Fps60, 4, focused: false));
        Assert.Equal(FrameClass.Fps60, FramePolicy.Crowded(FrameClass.Fps60, 4, focused: true));
        Assert.Equal(FrameClass.Fps30, FramePolicy.Crowded(FrameClass.Fps30, 8, focused: false));
    }
}
