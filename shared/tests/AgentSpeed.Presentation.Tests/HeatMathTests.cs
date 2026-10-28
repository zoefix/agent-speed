using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Tests;

public class HeatMathTests
{
    private const double R = HeatMath.ReferenceRedline;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(R, 1)]
    [InlineData(2 * R, 1)]
    [InlineData(100 * R, 1)]
    [InlineData(-5, 0)]
    [InlineData(-R, 0)]
    public void HeatAtReferencePoints(double value, double expected)
    {
        Assert.Equal(expected, HeatMath.Heat(value, R), 12);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(399)]
    public void HeatFollowsPowerCurveBelowRedline(double value)
    {
        Assert.Equal(Math.Pow(value / R, 0.6), HeatMath.Heat(value, R), 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void HeatOfNonFiniteValueIsZero(double value)
    {
        Assert.Equal(0, HeatMath.Heat(value, R));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(double.NaN)]
    public void HeatWithInvalidRedlineIsZero(double redline)
    {
        Assert.Equal(0, HeatMath.Heat(200, redline));
    }

    [Fact]
    public void HeatIsMonotonicAndBounded()
    {
        var previous = 0.0;
        for (var v = 0.0; v <= 3 * R; v += 0.25)
        {
            var h = HeatMath.Heat(v, R);
            Assert.InRange(h, 0, 1);
            Assert.True(h >= previous, $"Heat decreased at v={v}");
            previous = h;
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(R / 2, 0)]
    [InlineData(R, 0)]
    [InlineData(1.5 * R, 0.5)]
    [InlineData(2 * R, 1)]
    [InlineData(10 * R, 1)]
    [InlineData(-R, 0)]
    public void OverdriveIsClampedExcessOverRedline(double value, double expected)
    {
        Assert.Equal(expected, HeatMath.Overdrive(value, R), 12);
    }

    [Fact]
    public void OverdriveOfInvalidInputIsZero()
    {
        Assert.Equal(0, HeatMath.Overdrive(double.NaN, R));
        Assert.Equal(0, HeatMath.Overdrive(double.PositiveInfinity, R));
        Assert.Equal(0, HeatMath.Overdrive(1000, 0));
        Assert.Equal(0, HeatMath.Overdrive(1000, double.NaN));
    }

    [Theory]
    [InlineData(400, 0.5, 60, 150, 300)]
    [InlineData(1000, 0.5, 150, 375, 750)]
    [InlineData(100, 0.5, 15, 37.5, 75)]
    public void ThresholdsFollowDesign(double redline, double cruise, double boost, double overdrive, double red)
    {
        Assert.Equal(cruise, HeatMath.Threshold(SpeedTier.Cruise, redline), 12);
        Assert.Equal(boost, HeatMath.Threshold(SpeedTier.Boost, redline), 12);
        Assert.Equal(overdrive, HeatMath.Threshold(SpeedTier.Overdrive, redline), 12);
        Assert.Equal(red, HeatMath.Threshold(SpeedTier.Redline, redline), 12);
        Assert.Equal(0, HeatMath.Threshold(SpeedTier.Idle, redline));
    }

    [Fact]
    public void HeatAtThresholdMatchesHeatOfThreshold()
    {
        Assert.Equal(Math.Pow(0.75, 0.6), HeatMath.HeatAtThreshold(SpeedTier.Redline, R), 12);
        Assert.Equal(Math.Pow(0.375, 0.6), HeatMath.HeatAtThreshold(SpeedTier.Overdrive, R), 12);
        Assert.Equal(Math.Pow(0.15, 0.6), HeatMath.HeatAtThreshold(SpeedTier.Boost, R), 12);
        Assert.Equal(Math.Pow(0.5 / R, 0.6), HeatMath.HeatAtThreshold(SpeedTier.Cruise, R), 12);
    }

    [Theory]
    [InlineData(0, SpeedTier.Idle)]
    [InlineData(0.49, SpeedTier.Idle)]
    [InlineData(0.5, SpeedTier.Cruise)]
    [InlineData(59.999, SpeedTier.Cruise)]
    [InlineData(60, SpeedTier.Boost)]
    [InlineData(149.999, SpeedTier.Boost)]
    [InlineData(150, SpeedTier.Overdrive)]
    [InlineData(299.999, SpeedTier.Overdrive)]
    [InlineData(300, SpeedTier.Redline)]
    [InlineData(5000, SpeedTier.Redline)]
    [InlineData(-1, SpeedTier.Idle)]
    [InlineData(double.NaN, SpeedTier.Idle)]
    [InlineData(double.NegativeInfinity, SpeedTier.Idle)]
    public void RawTierBoundaries(double value, SpeedTier expected)
    {
        Assert.Equal(expected, HeatMath.RawTier(value, R));
    }

    [Fact]
    public void ActiveTiersAreOrderedAscending()
    {
        Assert.Equal([SpeedTier.Cruise, SpeedTier.Boost, SpeedTier.Overdrive, SpeedTier.Redline], HeatMath.ActiveTiers);
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(433, 450)]
    [InlineData(410, 400)]
    [InlineData(1000, 1000)]
    [InlineData(1990, 2000)]
    [InlineData(37, 50)]
    [InlineData(0, 50)]
    [InlineData(-500, 50)]
    [InlineData(9999, 2000)]
    [InlineData(2024, 2000)]
    [InlineData(double.NaN, 2000)]
    [InlineData(double.PositiveInfinity, 2000)]
    [InlineData(double.NegativeInfinity, 2000)]
    public void ClampRedlineStepsAndClamps(double input, double expected)
    {
        Assert.Equal(expected, HeatMath.ClampRedline(input));
    }

    [Fact]
    public void ClampRedlineAlwaysReturnsAStepInRange()
    {
        for (var input = -100.0; input <= 2500; input += 7.3)
        {
            var clamped = HeatMath.ClampRedline(input);
            Assert.InRange(clamped, HeatMath.MinRedline, HeatMath.MaxRedline);
            Assert.Equal(0, clamped % HeatMath.RedlineStep);
        }
    }

    [Fact]
    public void TotalRedlineSliderStepAlwaysMovesPastRounding()
    {
        const double step = 0.025;
        var up = HeatMath.MinRedlineTotal;
        var guard = 0;
        while (up < HeatMath.MaxRedlineTotal && guard++ < 1000)
        {
            var next = HeatMath.ClampRedlineTotal(Math.Pow(10, Math.Log10(up) + step));
            Assert.True(next > up, $"stuck at {up}");
            up = next;
        }

        Assert.Equal(HeatMath.MaxRedlineTotal, up);

        var down = HeatMath.MaxRedlineTotal;
        guard = 0;
        while (down > HeatMath.MinRedlineTotal && guard++ < 1000)
        {
            var next = HeatMath.ClampRedlineTotal(Math.Pow(10, Math.Log10(down) - step));
            Assert.True(next < down, $"stuck at {down}");
            down = next;
        }

        Assert.Equal(HeatMath.MinRedlineTotal, down);
    }
}
