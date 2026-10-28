using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 4c "Effects intensity", "Color and glow", "Accessibility and performance".</summary>
public class HeroStageEffectsTests
{
    private const double R = HeatMath.ReferenceRedline;

    private static int CountParticles(HeroStage stage, HeroParticleKind kind)
    {
        var count = 0;
        foreach (var p in stage.Particles.Items)
        {
            count += p.Alive && p.Kind == kind ? 1 : 0;
        }

        return count;
    }

    [Fact]
    public void NoSpeedLinesWhenOffOrParked()
    {
        var parked = new HeroDriver();
        parked.Run(1);
        Assert.Equal(0, parked.Stage.StreakCap);

        var off = new HeroDriver(options: new HeroOptions(EffectsIntensity.Off));
        off.Change(380, SpeedTitle.Hypersonic);
        off.Run(3);
        Assert.Equal(0, off.Stage.StreakCap);
        Assert.Equal(0, off.Stage.Streaks.AliveCount);
        Assert.Equal(3, off.Stage.StaticMarks);
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, 5)]
    [InlineData(EffectsIntensity.Standard, 9)]
    [InlineData(EffectsIntensity.Max, 14)]
    public void SpeedLineCapsFollowTheLevel(EffectsIntensity level, int cap)
    {
        var d = new HeroDriver(options: new HeroOptions(level));
        d.Change(380, SpeedTitle.Hypersonic);
        d.Run(4);
        Assert.Equal(cap, d.Stage.StreakCap);

        var seen = 0;
        d.Run(3, s =>
        {
            Assert.InRange(s.Streaks.AliveCount, 0, cap);
            seen = Math.Max(seen, s.Streaks.AliveCount);
        });
        Assert.True(seen > 0, "speed lines run while moving");
    }

    [Fact]
    public void SpeedLineCountScalesWithHeatAndSpeed()
    {
        var d = new HeroDriver();
        d.Change(5, SpeedTitle.Walk);
        d.Run(4);
        var s = d.Stage;
        var expected = HeroMath.RoundHalfUp((2 + 8 * s.Heat) * Math.Clamp(s.Ground / s.CurrentVehicle.Motion.SpeedLow, 0, 1));
        Assert.Equal(expected, s.StreakCap);
        Assert.InRange(s.StreakCap, 2, 4);
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void RocketSparksStreamFromStandardUp(EffectsIntensity level, bool sparks)
    {
        var d = new HeroDriver(options: new HeroOptions(level));
        d.Change(600, SpeedTitle.Rocket);
        var seen = 0;
        d.Run(3, s => seen = Math.Max(seen, CountParticles(s, HeroParticleKind.Spark)));
        Assert.Equal(sparks, seen > 0);
    }

    [Fact]
    public void MaxStreamsAboutOnePointSixTimesTheSparks()
    {
        static double MeanSparks(EffectsIntensity level)
        {
            var d = new HeroDriver(options: new HeroOptions(level));
            d.Change(600, SpeedTitle.Rocket);
            d.Run(2);
            var sum = 0.0;
            var frames = 0;
            d.Run(4, s =>
            {
                sum += CountParticles(s, HeroParticleKind.Spark);
                frames++;
            });
            return sum / frames;
        }

        var ratio = MeanSparks(EffectsIntensity.Max) / MeanSparks(EffectsIntensity.Standard);
        Assert.InRange(ratio, 1.35, 1.85);
    }

    [Fact]
    public void ParkedRocketStopsStreaming()
    {
        var d = new HeroDriver();
        d.Change(600, SpeedTitle.Rocket);
        d.Run(2);
        Assert.True(CountParticles(d.Stage, HeroParticleKind.Spark) > 0);
        d.Change(0, SpeedTitle.Idle);
        d.Run(1);
        Assert.Equal(0, CountParticles(d.Stage, HeroParticleKind.Spark));
    }

    [Fact]
    public void ParticlePoolNeverExceedsItsCapacity()
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Max));
        d.Change(800, SpeedTitle.Rocket);
        var max = 0;
        d.Run(20, s =>
        {
            Assert.InRange(s.Particles.AliveCount, 0, HeroParticlePool.Capacity);
            max = Math.Max(max, s.Particles.AliveCount);
        });

        Assert.Equal(64, HeroParticlePool.Capacity);
        Assert.Equal(64, d.Stage.Particles.Items.Length);
        Assert.Equal(16, d.Stage.Streaks.Items.Length);
        Assert.True(max > 20, $"a rocket at full overdrive keeps many sparks alive ({max})");
    }

    [Fact]
    public void CameraShakesOnlyAtRedlineFromStandard()
    {
        static double MaxShake(HeroOptions options, SpeedTier? tier)
        {
            var d = new HeroDriver(options: options) { Tier = tier };
            d.Change(380, SpeedTitle.Hypersonic);
            d.Run(3);
            var max = 0.0;
            d.Run(0.5, s => max = Math.Max(max, Math.Abs(s.Shake.X) + Math.Abs(s.Shake.Y)));
            return max;
        }

        Assert.Equal(0, MaxShake(new HeroOptions(EffectsIntensity.Standard), SpeedTier.Overdrive));
        Assert.Equal(0, MaxShake(new HeroOptions(EffectsIntensity.Soft), null));
        Assert.Equal(0, MaxShake(new HeroOptions(EffectsIntensity.Max, ReduceMotion: true), null));
        Assert.Equal(0, MaxShake(new HeroOptions(EffectsIntensity.Off), null));

        var standard = MaxShake(new HeroOptions(EffectsIntensity.Standard), null);
        Assert.InRange(standard, 0.05, 2 * 0.2 + 1e-9);
        var max = MaxShake(new HeroOptions(EffectsIntensity.Max), null);
        Assert.Equal(standard * 1.4, max, 9);
    }

    [Fact]
    public void ParkedStageDoesNotShake()
    {
        var d = new HeroDriver { Tier = SpeedTier.Redline };
        d.Run(1);
        Assert.Equal(default, d.Stage.Shake);
    }

    [Fact]
    public void SameSeedAndInputsReplayExactly()
    {
        var a = TourRun(7, 40);
        var b = TourRun(7, 40);

        Assert.Equal(a.Particles, b.Particles);
        Assert.Equal(a.Streaks, b.Streaks);
        Assert.Equal(a.Distance, b.Distance);
        Assert.True(a.Keys.Count >= 8, $"the tour visits most vehicles ({string.Join(", ", a.Keys)})");
        Assert.True(a.MostParticles > 6);
        Assert.True(a.MostStreaks > 3);

        var c = TourRun(8, 40);
        Assert.NotEqual(a.Streaks, c.Streaks);
        Assert.Equal(a.Distance, c.Distance);
    }

    [Fact]
    public void ParkedStageNeedsOnlyParkedFrames()
    {
        var d = new HeroDriver();
        Assert.Equal(FrameDemand.Medium, d.Stage.Demand);
        Assert.Null(d.Stage.Sweep);
    }

    [Theory]
    [InlineData(144, 72)]
    [InlineData(120, 60)]
    [InlineData(60, 60)]
    [InlineData(240, 60)]
    [InlineData(165, 165)]
    [InlineData(75, 75)]
    [InlineData(50, 50)]
    public void MovingFrameRateIsTheLargestDivisorOfTheRefreshAtLeast60(int refresh, double expected)
    {
        Assert.Equal(expected, HeroStage.MovingFrameRate(refresh));
    }

    [Fact]
    public void FrameDemandFollowsWhatMoves()
    {
        var d = new HeroDriver();
        var s = d.Stage;

        d.Change(10, SpeedTitle.Bicycle);
        Assert.Equal(FrameDemand.Full, s.Demand);
        d.Run(3);
        Assert.Equal(FrameDemand.Full, s.Demand);

        var parkedAt = d.Change(0, SpeedTitle.Idle);
        Assert.Equal(FrameDemand.Full, s.Demand);
        d.RunTo(parkedAt + 5);
        Assert.Equal(FrameDemand.Medium, s.Demand);
        Assert.Null(s.Sweep);

        d.RunTo(parkedAt + 6 + HeroStage.SweepDelay + 0.45);
        var own = HeroDriver.Some(s.Sweep);
        Assert.Equal(50, own.X, 6);
        Assert.Equal(1, own.Amount, 3);
        Assert.Equal(FrameDemand.Medium, s.Demand);

        d.RunTo(parkedAt + 6 + HeroStage.SweepDelay + 1.0);
        Assert.Null(s.Sweep);
        Assert.Equal(FrameDemand.Medium, s.Demand);
    }

    [Fact]
    public void SweepRunsInStepWithTheGaugeSweep()
    {
        var d = new HeroDriver();
        d.Run(2);
        var s = d.Stage;
        Assert.Null(s.Sweep);

        // The band starts 0.63 s into the gauge's sweep.
        var origin = d.Now;
        s.IdleSweepOrigin = origin;
        Assert.Null(s.Sweep);
        s.IdleSweepOrigin = origin - HeroStage.SweepDelay - 1e-9;
        Assert.Equal(-10, HeroDriver.Some(s.Sweep).X, 6);
        Assert.Equal(FrameDemand.Medium, s.Demand);

        d.RunTo(origin + 0.45);
        Assert.Equal(50, HeroDriver.Some(s.Sweep).X, 6);

        d.RunTo(origin + 0.95);
        Assert.Null(s.Sweep);
        Assert.Equal(FrameDemand.Medium, s.Demand);

        s.IdleSweepOrigin = d.Now - HeroStage.SweepDelay - 0.2;
        Assert.NotNull(s.Sweep);
        d.Change(10, SpeedTitle.Bicycle);
        Assert.Null(s.Sweep);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void StaticLayersRedrawOnlyOnChange(EffectsIntensity level, bool reduceMotion)
    {
        var d = new HeroDriver(options: new HeroOptions(level, ReduceMotion: reduceMotion));
        var s = d.Stage;
        Assert.Equal(FrameDemand.None, s.Demand);

        d.Change(10, SpeedTitle.Bicycle);
        Assert.Equal(FrameDemand.Full, s.Demand);
        d.Run(3);
        Assert.Equal(FrameDemand.None, s.Demand);

        s.IdleSweepOrigin = d.Now;
        Assert.Null(s.Sweep);

        d.Change(0, SpeedTitle.Idle);
        Assert.Equal(FrameDemand.Full, s.Demand);
        d.Run(3);
        Assert.Equal(FrameDemand.None, s.Demand);
        Assert.Null(s.Sweep);
    }

    [Theory]
    [InlineData(true, 0.55)]
    [InlineData(false, 0.5)]
    public void ParkedVehicleIsSlateAndBreathes(bool dark, double idle)
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Standard, Dark: dark));
        d.Run(1.3);
        var s = d.Stage;
        var paint = s.Paint();

        AssertColor(HeroPalette.Stroke(0, dark), paint.Color);
        Assert.Equal(idle * (0.86 + 0.14 * Math.Sin(2 * Math.PI * s.Time / 4.8)), paint.Presence, 12);
        Assert.Equal(dark, paint.Dark);
        Assert.False(paint.HighContrast);
        Assert.Equal(0, s.GlowOpacity);

        d.Run(1.2);
        Assert.NotEqual(paint.Presence, s.Paint().Presence);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    public void StaticParkedVehicleDoesNotBreathe(EffectsIntensity level, bool reduceMotion)
    {
        var d = new HeroDriver(options: new HeroOptions(level, ReduceMotion: reduceMotion));
        d.Run(1.3);
        Assert.Equal(0.55, d.Stage.Paint().Presence, 12);
    }

    [Theory]
    [InlineData(28, SpeedTitle.Car, true)]
    [InlineData(28, SpeedTitle.Car, false)]
    [InlineData(600, SpeedTitle.Rocket, true)]
    [InlineData(600, SpeedTitle.Rocket, false)]
    public void AwakeVehicleTakesTheQuantizedHeatColor(double value, SpeedTitle title, bool dark)
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Standard, Dark: dark));
        d.Change(value, title);
        d.Run(5);
        var s = d.Stage;
        Assert.Equal(1, s.Wake, 9);

        var h = Math.Round(s.Heat * 128) / 128;
        var o = Math.Round(s.Overdrive * 128) / 128;
        var paint = s.Paint();
        AssertColor(HeroPalette.Stroke(h, dark), paint.Color);
        AssertColor(HeroPalette.Hot(h, o, dark), paint.Hot);
        Assert.Equal(HeroPalette.Core(o, dark), paint.Core);
        Assert.Equal(1, paint.Presence, 9);
    }

    [Fact]
    public void WakeRecolorsFromSlateToHeat()
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car);
        d.Run(0.2);
        var s = d.Stage;
        Assert.InRange(s.Wake, 0.2, 0.9);

        var w = Math.Round(s.Wake * 128) / 128;
        var h = Math.Round(s.Heat * 128) / 128;
        AssertColor(OkLab.Mix(HeroPalette.Stroke(0, true), HeroPalette.Stroke(h, true), w), s.Paint().Color);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Standard, false)]
    [InlineData(EffectsIntensity.Soft, true)]
    [InlineData(EffectsIntensity.Max, true)]
    [InlineData(EffectsIntensity.Max, false)]
    [InlineData(EffectsIntensity.Off, true)]
    [InlineData(EffectsIntensity.Off, false)]
    public void GlowFollowsTheDesignFormula(EffectsIntensity level, bool dark)
    {
        var d = new HeroDriver(options: new HeroOptions(level, Dark: dark));
        Assert.Equal(0, d.Stage.GlowOpacity);

        d.Change(600, SpeedTitle.Rocket);
        d.Run(5);
        var s = d.Stage;
        var h = s.Heat;
        var o = s.Overdrive;
        Assert.Equal(1, h, 9);
        Assert.Equal(0.5, o, 6);

        var expected = level == EffectsIntensity.Off
            ? dark ? 0.30 : 0.12
            : dark ? 0.3 + 0.45 * h + 0.15 * o : 0.14 + 0.18 * h;
        Assert.Equal(expected, s.GlowOpacity, 9);
        Assert.Equal((1.0 + 1.4 * h + 0.6 * o) * (level == EffectsIntensity.Max ? 1.2 : 1), s.GlowSigma, 9);
    }

    [Fact]
    public void IncreasedContrastHalvesTheGlow()
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Standard, IncreasedContrast: true));
        d.Change(28, SpeedTitle.Car);
        d.Run(5);
        var s = d.Stage;
        Assert.Equal((0.3 + 0.45 * s.Heat + 0.15 * s.Overdrive) * 0.5, s.GlowOpacity, 9);
    }

    [Fact]
    public void LightStrokeIsLegibleOnThePopover()
    {
        var light = Rgba.FromHex("#E6E6E6");
        var dark = Rgba.FromHex("#3A3A3A");
        for (var i = 0; i <= 200; i++)
        {
            var h = i / 200.0;
            var stroke = HeroPalette.Stroke(h, false);
            Assert.True(Rgba.ContrastRatio(stroke, light) >= 4.5, $"h = {h}: {Rgba.ContrastRatio(stroke, light):F2}:1");
            var contrast = HeroPalette.Stroke(h, false, increasedContrast: true);
            Assert.True(Rgba.ContrastRatio(contrast, light) >= 7, $"h = {h}: {Rgba.ContrastRatio(contrast, light):F2}:1 with increased contrast");
            var darkContrast = HeroPalette.Stroke(h, true, increasedContrast: true);
            Assert.True(Rgba.ContrastRatio(darkContrast, dark) >= 4.5, $"h = {h}: {Rgba.ContrastRatio(darkContrast, dark):F2}:1 on dark");
            Assert.Equal(HeatPalette.Sample(h), HeroPalette.Stroke(h, true));
        }
    }

    [Fact]
    public void AccentsMixTowardWhiteHotAndAmber()
    {
        AssertColor(OkLab.Mix(HeatPalette.Sample(0.4), HeatPalette.WhiteHot, 0.65), HeroPalette.Hot(0.4, 0, true));
        AssertColor(OkLab.Mix(HeatPalette.Sample(0.4), HeatPalette.WhiteHot, 1.0), HeroPalette.Hot(0.4, 1, true));
        AssertColor(OkLab.Mix(HeroPalette.Stroke(0.4, false), HeroPalette.AmberHot, 0.45), HeroPalette.Hot(0.4, 0.7, false));
        AssertColor(HeatPalette.WhiteHot, HeroPalette.Core(0, true));
        AssertColor(Rgba.White, HeroPalette.Core(1, true));
        Assert.Equal(Rgba.FromHex("#FFB020"), HeroPalette.Core(0.5, false));
    }

    private static (HeroParticle[] Particles, HeroStreak[] Streaks, double Distance, HashSet<string> Keys, int MostParticles, int MostStreaks)
        TourRun(uint seed, double seconds)
    {
        var stage = new HeroStage(HeroDriver.Art, null, seed);
        var titles = new TitleTracker();
        var tiers = new TierTracker();
        var options = new HeroOptions(EffectsIntensity.Max);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var mostParticles = 0;
        var mostStreaks = 0;
        var frames = (int)Math.Round(seconds * 60);
        for (var i = 0; i <= frames; i++)
        {
            var now = i / 60.0;
            var value = HeroTour.Value(HeroTourScenario.Tour, now, R);
            titles.Update(value, R, now);
            tiers.Update(value, R, now);
            stage.Update(now, value, R, tiers.Displayed, titles.Displayed, options);
            keys.Add(stage.CurrentKey);
            mostParticles = Math.Max(mostParticles, stage.Particles.AliveCount);
            mostStreaks = Math.Max(mostStreaks, stage.Streaks.AliveCount);
        }

        return (stage.Particles.Items.ToArray(), stage.Streaks.Items.ToArray(), stage.Distance, keys, mostParticles, mostStreaks);
    }

    /// <summary>OKLab round trips are exact to about 1e-7; 1e-5 is still far below one 8-bit step.</summary>
    private static void AssertColor(Rgba expected, Rgba actual)
    {
        const double tolerance = 1e-5;
        Assert.True(
            Math.Abs(expected.R - actual.R) < tolerance && Math.Abs(expected.G - actual.G) < tolerance
            && Math.Abs(expected.B - actual.B) < tolerance && Math.Abs(expected.A - actual.A) < tolerance,
            $"expected {expected}, got {actual}");
    }
}
