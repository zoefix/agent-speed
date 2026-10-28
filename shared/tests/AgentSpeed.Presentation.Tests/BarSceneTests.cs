using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class BarSceneTests
{
    private static (BarScene Scene, FrameClock Clock) Create(double value, SpeedTier tier, bool reduceMotion = false)
    {
        var scene = new BarScene { ReduceMotion = reduceMotion, Redline = HeatMath.ReferenceRedline };
        var clock = new FrameClock();
        scene.SetTarget(value, tier);
        scene.Update(clock.Now);
        return (scene, clock);
    }

    [Fact]
    public void FirstUpdateDoesNotJump()
    {
        var (scene, _) = Create(200, SpeedTier.Overdrive);
        Assert.Equal(0, scene.Heat);
        Assert.Equal(200, scene.Value);
        Assert.Equal(SpeedTier.Overdrive, scene.Tier);
    }

    [Theory]
    [InlineData(60, SpeedTier.Boost)]
    [InlineData(200, SpeedTier.Overdrive)]
    [InlineData(800, SpeedTier.Redline)]
    public void HeatConvergesToTarget(double value, SpeedTier tier)
    {
        var (scene, clock) = Create(value, tier);
        clock.Advance(0.1, scene.Update);
        Assert.InRange(scene.Heat, 1e-6, HeatMath.Heat(value, scene.Redline) - 1e-3);
        clock.Advance(2, scene.Update);
        Assert.Equal(HeatMath.Heat(value, scene.Redline), scene.Heat, 3);
    }

    [Fact]
    public void RedlineSettingChangesTargetHeat()
    {
        var scene = new BarScene { Redline = 1000 };
        scene.SetTarget(500, SpeedTier.Overdrive);
        scene.Snap();
        Assert.Equal(HeatMath.Heat(500, 1000), scene.Heat, 12);
    }

    [Fact]
    public void GlowAppearsFromOverdrive()
    {
        var (boost, boostClock) = Create(100, SpeedTier.Boost);
        boostClock.Advance(2, boost.Update);
        Assert.Equal(0, boost.Glow, 6);

        var (overdrive, overdriveClock) = Create(200, SpeedTier.Overdrive);
        overdriveClock.Advance(2, overdrive.Update);
        Assert.Equal(1, overdrive.Glow, 3);
    }

    [Fact]
    public void FillFlickersBetween85And100PercentAtRedline()
    {
        var (scene, clock) = Create(400, SpeedTier.Redline);
        var min = double.MaxValue;
        var max = double.MinValue;
        clock.Advance(3, now =>
        {
            scene.Update(now);
            Assert.InRange(scene.FillOpacity, 0.85, 1);
            min = Math.Min(min, scene.FillOpacity);
            max = Math.Max(max, scene.FillOpacity);
        });

        Assert.True(min < 0.9, $"Flicker too shallow: min {min}");
        Assert.True(max > 0.95, $"Flicker too shallow: max {max}");
        Assert.True(scene.IsAnimating);
    }

    [Theory]
    [InlineData(0, SpeedTier.Idle)]
    [InlineData(10, SpeedTier.Cruise)]
    [InlineData(100, SpeedTier.Boost)]
    [InlineData(200, SpeedTier.Overdrive)]
    public void FillIsSolidBelowRedline(double value, SpeedTier tier)
    {
        var (scene, clock) = Create(value, tier);
        clock.Advance(2, now =>
        {
            scene.Update(now);
            Assert.Equal(1, scene.FillOpacity);
        });
    }

    [Fact]
    public void ReduceMotionSnapsAndStopsFlicker()
    {
        var (scene, clock) = Create(200, SpeedTier.Boost, reduceMotion: true);
        Assert.Equal(HeatMath.Heat(200, scene.Redline), scene.Heat);
        Assert.False(scene.IsAnimating);

        scene.SetTarget(800, SpeedTier.Redline);
        clock.AdvanceFrames(1, scene.Update);
        Assert.Equal(1, scene.Heat);
        Assert.Equal(1, scene.Glow);

        clock.Advance(1, now =>
        {
            scene.Update(now);
            Assert.Equal(1, scene.FillOpacity);
        });
        Assert.False(scene.IsAnimating);
    }

    [Theory]
    [InlineData(10, SpeedTier.Cruise)]
    [InlineData(100, SpeedTier.Boost)]
    [InlineData(200, SpeedTier.Overdrive)]
    public void StopsAnimatingOnceSettledBelowRedline(double value, SpeedTier tier)
    {
        var (scene, clock) = Create(value, tier);
        clock.AdvanceFrames(1, scene.Update);
        Assert.True(scene.IsAnimating);
        clock.Advance(3, scene.Update);
        Assert.False(scene.IsAnimating);
    }

    [Fact]
    public void SnapSettlesImmediately()
    {
        var scene = new BarScene { Redline = HeatMath.ReferenceRedline };
        scene.SetTarget(120, SpeedTier.Boost);
        Assert.True(scene.IsAnimating);
        scene.Snap();
        Assert.Equal(HeatMath.Heat(120, scene.Redline), scene.Heat);
        Assert.False(scene.IsAnimating);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-10)]
    public void InvalidValuesBecomeZero(double value)
    {
        var scene = new BarScene { Redline = HeatMath.ReferenceRedline };
        scene.SetTarget(value, SpeedTier.Idle);
        scene.Snap();
        Assert.Equal(0, scene.Value);
        Assert.Equal(0, scene.Heat);
    }

    [Fact]
    public void LongGapsAreClamped()
    {
        var (scene, _) = Create(400, SpeedTier.Redline);
        scene.Update(0.25);
        var afterQuarter = scene.Heat;
        var (other, _) = Create(400, SpeedTier.Redline);
        other.Update(60);
        Assert.Equal(afterQuarter, other.Heat, 12);
        Assert.True(afterQuarter < 1);
    }
}
