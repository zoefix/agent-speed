using System.Diagnostics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Feeds an orb style model the frames <c>OrbScene</c> would (DESIGN 2a), from a speed ratio v / R.</summary>
internal sealed class EnergyOrbDriver
{
    public EnergyOrbDriver(IOrbStyleModel model)
    {
        Model = model;
    }

    public IOrbStyleModel Model { get; }

    public double Time { get; set; } = 10;

    public double Heat { get; set; }

    public double Overdrive { get; set; }

    public SpeedTier Tier { get; set; } = SpeedTier.Idle;

    public double Size { get; set; } = 148;

    public bool Dark { get; set; } = true;

    public EffectsPolicy Effects { get; set; } = new(EffectsIntensity.Standard);

    public double Flash { get; set; }

    public double? SignOfLifeAge { get; set; }

    public bool Unavailable { get; set; }

    /// <summary>Sets heat, overdrive and tier for a speed of <paramref name="ratio"/> × the redline.</summary>
    public EnergyOrbDriver Speed(double ratio)
    {
        var v = ratio * HeatMath.ReferenceRedline;
        Heat = HeatMath.Heat(v, HeatMath.ReferenceRedline);
        Overdrive = HeatMath.Overdrive(v, HeatMath.ReferenceRedline);
        Tier = HeatMath.RawTier(v, HeatMath.ReferenceRedline);
        return this;
    }

    public EnergyOrbDriver Level(EffectsIntensity level, bool reduceMotion = false, bool highContrast = false)
    {
        Effects = new EffectsPolicy(level, reduceMotion, highContrast);
        return this;
    }

    public OrbFrame Frame(double dt) =>
        new(Time, dt, Heat, Overdrive, Heat, Overdrive, Tier, Size, Effects, Dark, Flash, SignOfLifeAge, Unavailable);

    public void Snap() => Model.Snap(Frame(0));

    public void Step(double dt)
    {
        Time += dt;
        Model.Update(Frame(dt));
    }

    public void Run(double seconds, double fps = 60)
    {
        var frames = (int)Math.Round(seconds * fps);
        for (var i = 0; i < frames; i++)
        {
            Step(1 / fps);
        }
    }

    /// <summary>Runs frame by frame, calling <paramref name="check"/> after each.</summary>
    public void Watch(double seconds, Action check, double fps = 60)
    {
        var frames = (int)Math.Round(seconds * fps);
        for (var i = 0; i < frames; i++)
        {
            Step(1 / fps);
            check();
        }
    }

    /// <summary>A climb from h 0 to 1 over 2 s, then 1 s held, at 30 fps, 72 fps or an irregular cadence.</summary>
    public static void Climb(EnergyOrbDriver driver, double[] cadence, SpeedTier tier)
    {
        driver.Tier = tier;
        driver.Heat = 0;
        driver.Snap();
        var t = 0.0;
        var i = 0;
        while (t < 3 - 1e-9)
        {
            var dt = Math.Min(cadence[i++ % cadence.Length], 3 - t);
            t += dt;
            driver.Heat = Math.Min(1, t / 2);
            driver.Step(dt);
        }
    }

    public static double[][] Cadences { get; } =
    [
        [1 / 30.0],
        [1 / 72.0],
        [1 / 30.0, 1 / 90.0, 1 / 45.0, 1 / 120.0, 1 / 60.0, 0.05],
    ];

    public static double AngleDelta(double a, double b)
    {
        var d = (a - b) % 360;
        if (d > 180)
        {
            d -= 360;
        }
        else if (d < -180)
        {
            d += 360;
        }

        return d;
    }
}

public class OrbLiquidModelTests
{
    private static double Sink;

    private static (LiquidModel Model, EnergyOrbDriver Driver) Create(double ratio = 0, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, double size = 148, bool dark = true, int seed = 3)
    {
        var model = new LiquidModel(seed);
        var driver = new EnergyOrbDriver(model) { Size = size, Dark = dark }.Speed(ratio).Level(level, reduceMotion);
        driver.Snap();
        return (model, driver);
    }

    private static double H(double ratio) => Math.Min(1, Math.Pow(ratio, 0.6));

    private static double O(double ratio) => Math.Clamp(ratio - 1, 0, 1);

    private static double Quantized(double value) => Math.Round(value * 128) / 128;

    [Fact]
    public void IdleIsASlatePoolAtEightPercentThatDrawsNoFrames()
    {
        var (model, driver) = Create();
        driver.Run(1);

        Assert.Equal(OrbStyle.Liquid, model.Style);
        Assert.Equal(0.08, model.Level, 9);
        Assert.Equal(0.455 - 0.91 * 0.08, model.MeanSurfaceY, 9);
        Assert.Equal(0.002, model.WaveAmplitude, 6);
        Assert.Equal(0, model.Sprites.Length);
        Assert.Equal(FrameClass.None, model.Demand);
        Assert.Equal(HeatPalette.Sample(0), model.LiquidColor);
        Assert.Equal(0.40, model.LiquidAlpha, 9);
        Assert.Equal(0.28, model.MeniscusColor.A, 6);
        Assert.Equal(0, model.ScrimOpacity, 6);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void LevelMapsHeatAndOverdrive(double ratio)
    {
        var expected = 0.08 + 0.74 * H(ratio) + 0.06 * O(ratio);
        var (model, driver) = Create(ratio);
        Assert.Equal(expected, model.Level, 9);
        Assert.Equal(expected, model.LevelTarget, 9);

        var (climbing, climb) = Create();
        climb.Speed(ratio);
        climb.Run(5);
        Assert.Equal(expected, climbing.Level, 3);
        driver.Run(1);
        Assert.Equal(expected, model.Level, 3);
    }

    [Fact]
    public void LevelSlowsWithAnUnderdampedSlosh()
    {
        var (model, driver) = Create(0.002);
        driver.Heat = 0;
        driver.Snap();
        Assert.Equal(0.08, model.Level, 9);

        driver.Heat = 1;
        var peak = 0.0;
        var peakAt = 0.0;
        driver.Watch(3, () =>
        {
            if (model.Level > peak)
            {
                peak = model.Level;
                peakAt = driver.Time - 10;
            }
        });

        // 0.75 Hz, damping 0.62: overshoot exp(−πζ/√(1−ζ²)) ≈ 8.4 % of the 0.74 step, peaking near 0.85 s.
        var overshoot = Math.Exp(-Math.PI * 0.62 / Math.Sqrt(1 - 0.62 * 0.62)) * 0.74;
        Assert.InRange(peak - 0.82, overshoot * 0.85, overshoot * 1.1);
        Assert.InRange(peakAt, 0.75, 0.95);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    public void OffAndReduceMotionEaseTheLevelCriticallyDamped(EffectsIntensity level, bool reduceMotion)
    {
        var (model, driver) = Create(0.002, level, reduceMotion);
        driver.Heat = 0;
        driver.Snap();
        driver.Heat = 1;
        var max = 0.0;
        driver.Watch(0.9, () => max = Math.Max(max, model.Level));

        Assert.True(max <= 0.82 + 1e-9);
        Assert.InRange(model.Level, 0.82 - 0.02, 0.82);
        driver.Run(0.8);
        Assert.Equal(0.82, model.Level, 3);
        Assert.Equal(0, model.WaveAmplitude, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, false, 0.2)]
    [InlineData(EffectsIntensity.Standard, false, 1.5)]
    [InlineData(EffectsIntensity.Max, false, 1.0)]
    [InlineData(EffectsIntensity.Soft, false, 0.2)]
    [InlineData(EffectsIntensity.Soft, false, 0)]
    [InlineData(EffectsIntensity.Standard, false, 0)]
    [InlineData(EffectsIntensity.Off, false, 0.5)]
    [InlineData(EffectsIntensity.Standard, true, 0.5)]
    public void WaveAmplitudeFollowsHeatEffectsAndIdle(EffectsIntensity level, bool reduceMotion, double ratio)
    {
        var (model, _) = Create(ratio, level, reduceMotion);
        var a = ratio == 0 ? 0.002 : 0.004 + 0.02 * H(ratio) + 0.018 * O(ratio);
        if (level == EffectsIntensity.Soft || reduceMotion)
        {
            a *= 0.6;
        }

        if (level == EffectsIntensity.Off || reduceMotion)
        {
            a = 0;
        }

        Assert.Equal(a, model.WaveAmplitude, 9);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void PhasesAccumulateAtTheSpecRates(double ratio)
    {
        var (model, driver) = Create(ratio, EffectsIntensity.Soft);
        var h = H(ratio);
        var o = O(ratio);
        var start = (model.Phase1, model.Phase2, model.Phase3, model.Phase4);
        driver.Run(1);

        Assert.Equal(2 * Math.PI * (0.25 + 0.5 * h + 0.3 * o), model.Phase1 - start.Phase1, 6);
        Assert.Equal(2 * Math.PI * (0.4 + 0.8 * h + 0.4 * o), model.Phase2 - start.Phase2, 6);
        Assert.Equal(2 * Math.PI * (0.9 + 1.6 * h), model.Phase3 - start.Phase3, 6);
        Assert.Equal(2 * Math.PI * 3.1, model.Phase4 - start.Phase4, 6);

        driver.Level(EffectsIntensity.Off);
        var frozen = model.Phase1;
        driver.Run(1);
        Assert.Equal(frozen, model.Phase1);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, false, 1.0, 148, 0.003)]
    [InlineData(EffectsIntensity.Standard, false, 2.0, 148, 0.006)]
    [InlineData(EffectsIntensity.Max, false, 1.5, 148, 0.0045)]
    [InlineData(EffectsIntensity.Standard, false, 2.0, 100, 0.003)]
    [InlineData(EffectsIntensity.Standard, false, 0.6, 148, 0)]
    [InlineData(EffectsIntensity.Soft, false, 2.0, 148, 0)]
    [InlineData(EffectsIntensity.Standard, true, 2.0, 148, 0)]
    public void BoilChopOnlyAtTheRedlineStandardAndUp(EffectsIntensity level, bool reduceMotion, double ratio, double size, double chop)
    {
        var (model, driver) = Create(ratio, level, reduceMotion, size);
        Assert.Equal(chop, model.ChopAmplitude, 9);
        Assert.Equal(chop > 0, model.Boiling);
        driver.Run(1);
        Assert.Equal(chop, model.ChopAmplitude, 6);
    }

    [Fact]
    public void RippleFollowsTheSpecFormula()
    {
        // Front at d = 0.42 τ; Gaussian width 0.075 S, wavelength 0.1 S, decay τ / 0.75.
        Assert.Equal(-0.02 * Math.Exp(-0.5 / 0.75), LiquidModel.RippleOffset(0.21, 0.5, 0.02), 12);
        var front = 0.05;
        var expected = -0.02 * Math.Exp(-0.5 / 0.75) * Math.Exp(-(front / 0.075) * (front / 0.075)) * Math.Cos(2 * Math.PI * front / 0.1);
        Assert.Equal(expected, LiquidModel.RippleOffset(0.21 + front, 0.5, 0.02), 12);
        Assert.Equal(0, LiquidModel.RippleOffset(0.21 + 0.025, 0.5, 0.02), 9);
        Assert.Equal(0, LiquidModel.RippleOffset(0.1, -0.1, 0.02));
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 0.2)]
    [InlineData(EffectsIntensity.Standard, 1.0)]
    [InlineData(EffectsIntensity.Standard, 1.5)]
    [InlineData(EffectsIntensity.Max, 1.0)]
    [InlineData(EffectsIntensity.Soft, 0.2)]
    [InlineData(EffectsIntensity.Soft, 1.0)]
    public void MotesRiseAtTheSpecRates(EffectsIntensity level, double ratio)
    {
        var (model, driver) = Create(ratio, level);
        var h = H(ratio);
        var o = O(ratio);
        var rate = level == EffectsIntensity.Soft
            ? 0.6 + 3 * h
            : (1.5 + 12 * h + (ratio >= 0.75 ? 6 + 10 * o : 0)) * (level == EffectsIntensity.Max ? 1.6 : 1);
        driver.Run(10);
        Assert.InRange(model.EmittedMotes, (long)(rate * 10 * 0.93), (long)Math.Ceiling(rate * 10));
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 148, 32)]
    [InlineData(EffectsIntensity.Standard, 100, 16)]
    [InlineData(EffectsIntensity.Max, 148, 48)]
    [InlineData(EffectsIntensity.Max, 100, 16)]
    [InlineData(EffectsIntensity.Soft, 148, 6)]
    public void MotePoolsAreCapped(EffectsIntensity level, double size, int cap)
    {
        var (model, driver) = Create(2.0, level, size: size);
        Assert.Equal(cap, model.MoteCap);
        var most = 0;
        driver.Watch(6, () => most = Math.Max(most, model.MoteCount));
        Assert.InRange(most, cap / 2, cap);
        Assert.True(model.Motes.Length <= LiquidModel.MotePool);
    }

    [Fact]
    public void MotesLookHotFromBoostUpAndFadeBehindTheReadout()
    {
        var (model, driver) = Create(1.0);
        driver.Run(2);
        Assert.Equal(Easing.SmoothStep(0.35, 0.9, Quantized(1)), model.MoteHot, 9);
        Assert.NotEqual(0, model.Motes.Length);
        foreach (var mote in model.Motes)
        {
            Assert.Equal(LiquidSpriteKind.Mote, mote.Kind);
            Assert.Equal(1, mote.Hot, 9);
            Assert.Equal(ReadoutZone.Fade(mote.X, mote.Y), mote.Alpha, 9);
            Assert.InRange(mote.Radius, 0.9 / 148 - 1e-12, 0.016 * 1.4 + 1e-12);
        }

        var (cool, coolDriver) = Create(0.05);
        coolDriver.Run(3);
        Assert.Equal(0, cool.MoteHot, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 2.0, 148, true)]
    [InlineData(EffectsIntensity.Max, 1.0, 148, true)]
    [InlineData(EffectsIntensity.Standard, 0.6, 148, false)]
    [InlineData(EffectsIntensity.Soft, 2.0, 148, false)]
    [InlineData(EffectsIntensity.Standard, 2.0, 100, false)]
    public void SteamRisesOnlyFromABoilingSurface(EffectsIntensity level, double ratio, double size, bool steams)
    {
        var (model, driver) = Create(ratio, level, size: size);
        var most = 0;
        driver.Watch(10, () => most = Math.Max(most, model.SteamCount));
        Assert.Equal(steams, model.EmittedSteam > 0);
        Assert.True(most <= LiquidModel.SteamPool);
        if (steams && level == EffectsIntensity.Standard)
        {
            Assert.InRange(model.EmittedSteam, 56, 61);
        }

        foreach (var wisp in model.Steam)
        {
            Assert.Equal(LiquidSpriteKind.Steam, wisp.Kind);
            Assert.True(wisp.Y < model.MeanSurfaceY);
        }
    }

    [Fact]
    public void AtMostThreeDropletsFallAndEachImpactRipples()
    {
        var (model, driver) = Create(0.5);
        for (var i = 0; i < 5; i++)
        {
            model.Pulse(0.7, 1);
        }

        driver.Step(1 / 60.0);
        Assert.Equal(3, model.DropletsInFlight);
        Assert.Equal(3, model.Droplets.Length);
        foreach (var droplet in model.Droplets)
        {
            Assert.Equal(LiquidSpriteKind.Droplet, droplet.Kind);
            Assert.InRange(droplet.Y, -0.414, -0.3 + 0.01);
            Assert.InRange(droplet.X, -0.21, 0.21);
            Assert.Equal(0.018, droplet.Radius, 9);
        }

        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(0.6);
        Assert.Equal(0, model.DropletsInFlight);
        Assert.Equal(3, model.RippleCount);
        Assert.Equal(0.022 * 1.2, model.RippleAmplitude(0), 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 6, 6)]
    [InlineData(EffectsIntensity.Max, 6, 6)]
    [InlineData(EffectsIntensity.Soft, 0, 2)]
    public void ImpactsSplashAndReleaseBubbles(EffectsIntensity level, int splashes, int bubbles)
    {
        var (model, driver) = Create(0.5, level);
        driver.Run(0.5);
        model.Pulse(0.6, 1);
        while (true)
        {
            var spawned = model.SpawnedBubbles - model.EmittedMotes;
            var splashed = model.SpawnedSplashes;
            driver.Step(1 / 60.0);
            if (model.DropletsInFlight == 0)
            {
                Assert.Equal(splashes, model.SpawnedSplashes - splashed);
                Assert.Equal(bubbles, model.SpawnedBubbles - model.EmittedMotes - spawned);
                Assert.Equal(1, model.RippleCount);
                break;
            }

            Assert.True(driver.Time < 20, "the droplet never landed");
        }
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    public void NoParticlesOrDropletsWhenStill(EffectsIntensity level, bool reduceMotion)
    {
        var (model, driver) = Create(2.0, level, reduceMotion);
        model.Pulse(0.9, 1);
        model.Peak();
        driver.Run(3);
        Assert.Equal(0, model.Sprites.Length);
        Assert.Equal(0, model.DropletsInFlight);
        Assert.Equal(0, model.EmittedMotes);
        Assert.False(model.CurrentsVisible);
    }

    [Fact]
    public void HighContrastIsStaticWithoutGlows()
    {
        var (model, driver) = Create(2.0);
        driver.Level(EffectsIntensity.Max, highContrast: true);
        driver.Snap();
        driver.Run(1);
        Assert.Equal(0, model.Sprites.Length);
        Assert.Equal(0, model.WaveAmplitude, 9);
        Assert.Equal(0, model.InnerGlowColor.A, 9);
        Assert.Equal(0, model.SpillColor.A, 9);
        Assert.Equal(0, model.MeniscusGlowColor.A, 9);
        Assert.Equal(0, model.CausticColor.A, 9);
        Assert.Equal(0, model.ScrimOpacity, 9);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void PopsGrowToTwoAndAHalfTimesInAQuarterSecond()
    {
        var (model, driver) = Create();
        driver.SignOfLifeAge = 0;
        var bubbleRadius = 0.0;
        var popFrames = 0;
        var firstPop = 0.0;
        var lastPop = 0.0;
        driver.Watch(1.2, () =>
        {
            driver.SignOfLifeAge += 1 / 60.0;
            if (model.Motes.Length == 1)
            {
                Assert.Equal(0, model.Motes[0].Hot);
                bubbleRadius = model.Motes[0].Radius;
            }

            if (model.Pops.Length == 1)
            {
                popFrames++;
                lastPop = model.Pops[0].Radius;
                if (popFrames == 1)
                {
                    firstPop = lastPop;
                }
            }
        });

        Assert.True(bubbleRadius > 0);
        Assert.Equal(bubbleRadius * (1 + 1.5 / 60 / 0.25), firstPop, 9);
        Assert.InRange(popFrames, 14, 16);
        Assert.InRange(lastPop, bubbleRadius * 2.3, bubbleRadius * 2.5 + 1e-12);
    }

    [Fact]
    public void SignOfLifeRaisesOneCoolBubbleAt30Fps()
    {
        var (model, driver) = Create();
        driver.SignOfLifeAge = 0;
        var most = 0;
        var demands = new HashSet<FrameClass>();
        driver.Watch(1.2, () =>
        {
            driver.SignOfLifeAge += 1 / 60.0;
            most = Math.Max(most, model.Motes.Length);
            demands.Add(model.Demand);
        });

        Assert.Equal(1, most);
        Assert.Equal(1, model.SpawnedBubbles);
        Assert.Equal(FrameClass.Fps30, Assert.Single(demands));
        Assert.Equal(0, model.Sprites.Length);
        driver.SignOfLifeAge = null;
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.None, model.Demand);

        var (still, stillDriver) = Create(level: EffectsIntensity.Off);
        stillDriver.SignOfLifeAge = 0;
        stillDriver.Run(1.2);
        Assert.Equal(0, still.SpawnedBubbles);
        Assert.Equal(FrameClass.None, still.Demand);
    }

    [Fact]
    public void NewPeakSurgesTheLevelAndRaisesAGoldBand()
    {
        var (model, driver) = Create(0.3);
        driver.Run(1);
        var target = model.LevelTarget;
        model.Peak();
        driver.Step(1 / 60.0);

        var gold = 0;
        foreach (var mote in model.Motes)
        {
            gold += mote.Kind == LiquidSpriteKind.GoldMote ? 1 : 0;
        }

        Assert.Equal(LiquidModel.GoldMoteCount, gold);
        Assert.InRange(model.GoldBandY, 0.4, 0.455);
        var surge = 0.0;
        var alpha = model.GoldBandAlpha;
        var fading = true;
        driver.Watch(0.85, () =>
        {
            surge = Math.Max(surge, model.Level - target);
            fading &= model.GoldBandAlpha < alpha && model.GoldBandAlpha > 0;
            alpha = model.GoldBandAlpha;
        });

        // Velocity + 0.9/s on a 0.9 Hz, damping 0.45 spring: about +0.09 of the diameter.
        Assert.InRange(surge, 0.08, 0.105);
        Assert.True(fading);
        Assert.InRange(model.GoldBandY, model.MeanSurfaceY - 0.01, model.MeanSurfaceY + 0.05);
        driver.Run(0.1);
        Assert.Equal(0, model.GoldBandAlpha);
    }

    [Fact]
    public void SoftPeaksWithoutSurgeAndReduceMotionCrossfadesTheBand()
    {
        var (soft, softDriver) = Create(0.3, EffectsIntensity.Soft);
        softDriver.Run(1);
        var target = soft.LevelTarget;
        soft.Peak();
        var surge = 0.0;
        softDriver.Watch(1, () => surge = Math.Max(surge, Math.Abs(soft.Level - target)));
        Assert.True(surge < 0.002);

        var (reduced, reducedDriver) = Create(0.3, EffectsIntensity.Standard, reduceMotion: true);
        reduced.Peak();
        var peak = 0.0;
        var y = double.NaN;
        reducedDriver.Watch(0.25, () =>
        {
            peak = Math.Max(peak, reduced.GoldBandAlpha);
            y = double.IsNaN(y) ? reduced.GoldBandY : y;
            Assert.Equal(y, reduced.GoldBandY, 9);
        });

        Assert.InRange(peak, 0.44, 0.45);
        Assert.Equal(FrameClass.Fps30, reduced.Demand);
        reducedDriver.Run(0.1);
        Assert.Equal(0, reduced.GoldBandAlpha);
        Assert.Equal(FrameClass.None, reduced.Demand);

        var (off, offDriver) = Create(0.3, EffectsIntensity.Off);
        off.Peak();
        offDriver.Step(1 / 60.0);
        Assert.Equal(0, off.GoldBandAlpha);
    }

    [Fact]
    public void SmallOrbsDropBackWaveCurrentsAndSteam()
    {
        var (small, driver) = Create(2.0, size: 100);
        Assert.False(small.BackWaveVisible);
        Assert.False(small.CurrentsVisible);
        driver.Run(3);
        Assert.Equal(0, small.EmittedSteam);

        var (large, _) = Create(2.0, size: 148);
        Assert.True(large.BackWaveVisible);
        Assert.True(large.CurrentsVisible);

        var (soft, _) = Create(0.5, EffectsIntensity.Soft);
        Assert.True(soft.CurrentsVisible);
        var (idle, _) = Create();
        Assert.False(idle.CurrentsVisible);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, false, 0, FrameClass.None)]
    [InlineData(EffectsIntensity.Standard, false, 0.05, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Standard, false, 0.2, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Standard, false, 0.5, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Standard, false, 1.0, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Max, false, 2.0, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Soft, false, 1.0, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Soft, false, 0.2, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Off, false, 1.0, FrameClass.None)]
    [InlineData(EffectsIntensity.Standard, true, 1.0, FrameClass.None)]
    public void DemandFollowsTheFrameTable(EffectsIntensity level, bool reduceMotion, double ratio, FrameClass expected)
    {
        var (model, driver) = Create(ratio, level, reduceMotion);
        driver.Run(0.5);
        Assert.Equal(expected, model.Demand);
    }

    [Fact]
    public void TransitionsAndValueChangesRaiseTheDemand()
    {
        var (model, driver) = Create(0.05);
        driver.Flash = 0.5;
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Flash = 0;
        driver.Run(2);
        Assert.Equal(FrameClass.Fps30, model.Demand);

        var (off, offDriver) = Create(0.2, EffectsIntensity.Off);
        Assert.Equal(FrameClass.None, off.Demand);
        offDriver.Speed(0.6);
        offDriver.Run(0.5);
        Assert.Equal(FrameClass.Fps30, off.Demand);
        offDriver.Run(2.5);
        Assert.Equal(FrameClass.None, off.Demand);
    }

    [Fact]
    public void SnapJumpsToRestWithoutCatchUp()
    {
        var (model, driver) = Create(2.0, EffectsIntensity.Max);
        for (var i = 0; i < 6; i++)
        {
            model.Pulse(0.9, 1);
            driver.Run(0.3);
        }

        model.Peak();
        driver.Run(0.2);
        Assert.NotEqual(0, model.Sprites.Length);

        driver.Speed(0.2);
        driver.Snap();
        Assert.Equal(0, model.Sprites.Length);
        Assert.Equal(0, model.RippleCount);
        Assert.Equal(0, model.DropletsInFlight);
        Assert.Equal(0, model.GoldBandAlpha);
        Assert.Equal(0.08 + 0.74 * H(0.2), model.Level, 9);
        Assert.Equal(0, model.ChopAmplitude, 9);
    }

    [Fact]
    public void MotionIsIndependentOfTheFrameRate()
    {
        var results = new List<(double Level, double Phase, double Amplitude)>();
        foreach (var cadence in EnergyOrbDriver.Cadences)
        {
            var model = new LiquidModel(5);
            EnergyOrbDriver.Climb(new EnergyOrbDriver(model), cadence, SpeedTier.Overdrive);
            results.Add((model.Level, model.Phase1, model.WaveAmplitude));
        }

        foreach (var result in results)
        {
            Assert.InRange(Math.Abs(result.Level - results[0].Level), 0, 0.001);
            Assert.InRange(Math.Abs(result.Phase - results[0].Phase), 0, 0.01);
            Assert.InRange(Math.Abs(result.Amplitude - results[0].Amplitude), 0, 0.0002);
        }
    }

    [Fact]
    public void SurfaceSamplesMatchTheSurfaceFunction()
    {
        var (model, driver) = Create(1.0);
        model.Pulse(0.8, 1);
        driver.Run(0.8);
        Span<double> ys = stackalloc double[33];
        model.FillSurface(ys);
        for (var i = 0; i < ys.Length; i++)
        {
            Assert.Equal(model.SurfaceY(LiquidGeometry.SampleX(i, ys.Length)), ys[i], 12);
        }

        Assert.Equal(-0.455, LiquidGeometry.SampleX(0, 33), 12);
        Assert.Equal(0.455, LiquidGeometry.SampleX(32, 33), 12);
        Assert.Equal(model.SurfaceY(0), model.ReadoutSplitY, 12);
        Assert.InRange(model.SurfaceY(0.1) - model.MeanSurfaceY, -0.06, 0.06);

        model.FillCurrent(1, ys);
        var depth = model.MeanSurfaceY + (0.455 - model.MeanSurfaceY) * 0.5;
        for (var i = 0; i < ys.Length; i++)
        {
            Assert.Equal(model.CurrentY(1, i / 32.0), ys[i], 12);
            Assert.InRange(ys[i], depth - 0.045 - 1e-9, depth + 0.045 + 1e-9);
        }

        var (still, _) = Create(1.0, EffectsIntensity.Off);
        Assert.Equal(still.MeanSurfaceY, still.SurfaceY(0.2), 12);
        Assert.Equal(still.SurfaceY(0.2) - 0.008, still.BackSurfaceY(0.2), 12);
    }

    [Fact]
    public void SurfaceVersionChangesOnlyWhileTheSurfaceMoves()
    {
        var (still, stillDriver) = Create(0.5, EffectsIntensity.Off);
        stillDriver.Run(0.2);
        var version = still.SurfaceVersion;
        stillDriver.Run(0.5);
        Assert.Equal(version, still.SurfaceVersion);

        var (moving, movingDriver) = Create(0.5);
        version = moving.SurfaceVersion;
        movingDriver.Step(1 / 60.0);
        Assert.NotEqual(version, moving.SurfaceVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ColorsFollowTheAppearance(bool dark)
    {
        var (model, driver) = Create(0.5, dark: dark);
        var h = Quantized(H(0.5));
        var heat = HeatPalette.Sample(h);
        var liquid = dark ? heat : OkLab.Mix(heat, Rgba.White, 0.12);
        var alpha = dark ? 0.46 + 0.10 * h : 0.80;
        Assert.Equal(liquid, model.LiquidColor);
        Assert.Equal(alpha, model.LiquidAlpha, 9);
        Assert.Equal(alpha, model.BodyTopColor.A, 9);
        Assert.Equal(Math.Min(1, alpha + 0.2), model.BodyBottomColor.A, 9);
        Assert.Equal(alpha * 0.55, model.BackWaveColor.A, 9);
        Assert.Equal((dark ? 0.16 : 0.10) * (0.4 + 0.6 * h), model.SpillColor.A, 9);
        Assert.Equal((dark ? 0.35 : 0.3) * (0.3 + 0.7 * h), model.CausticColor.A, 9);
        Assert.Equal(dark ? 1 : 0, model.ScrimOpacity, 9);
        Assert.Equal(0.008 + 0.004 * h, model.MeniscusWidth, 9);
        Assert.Equal(dark ? 0.9 : 0, model.MeniscusGlowColor.A, 9);
        Assert.Equal(dark ? 0.5 : 0.85, model.BubbleRingColor.A, 9);
        Assert.Equal(Math.Max(model.MeanSurfaceY + 0.08, 0.38), model.InnerGlowCenterY, 12);

        var key = model.ColorKey;
        driver.Run(0.5);
        Assert.Equal(key, model.ColorKey);
        driver.Speed(0.6);
        driver.Snap();
        Assert.NotEqual(key, model.ColorKey);

        var (idle, _) = Create(dark: dark);
        Assert.Equal(dark ? 0.40 : 0.55, idle.LiquidAlpha, 9);
        Assert.Equal(dark ? 0.28 : 0.5, idle.MeniscusColor.A, 9);
    }

    [Fact]
    public void UpdatesAllocateNothing()
    {
        var (model, driver) = Create(2.0, EffectsIntensity.Max, size: 240);
        Span<double> ys = stackalloc double[64];
        for (var i = 0; i < 1500; i++)
        {
            Exercise(model, driver, i, ys);
        }

        // A per-frame allocation shows up in every round; a one-time runtime allocation (tiering) in one at most.
        var rounds = new long[5];
        for (var round = 0; round < rounds.Length; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1500; i++)
            {
                Exercise(model, driver, i, ys);
            }

            rounds[round] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Contains(0L, rounds);
        Assert.All(rounds, bytes => Assert.True(bytes < 1024, $"{bytes} bytes allocated over 1500 updates"));
    }

    [Fact]
    public void MaxRedlineUpdatesStayWithinTheCpuBudget()
    {
        var (model, driver) = Create(2.0, EffectsIntensity.Max, size: 240);
        Span<double> ys = stackalloc double[64];
        for (var i = 0; i < 300; i++)
        {
            Exercise(model, driver, i, ys);
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Exercise(model, driver, i, ys);
        }

        watch.Stop();
        Assert.True(watch.Elapsed.TotalMilliseconds < 250, $"1000 updates took {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static void Exercise(LiquidModel model, EnergyOrbDriver driver, int i, Span<double> ys)
    {
        if (i % 20 == 0)
        {
            model.Pulse(0.9, 1);
        }

        if (i % 400 == 100)
        {
            model.Peak();
        }

        if (i % 700 == 650)
        {
            driver.Snap();
        }

        driver.Flash = i % 300 < 24 ? 1 - (i % 300) / 24.0 : 0;
        driver.Heat = 0.97 + 0.03 * Math.Sin(i * 0.05);
        driver.Step(i % 3 == 0 ? 1 / 72.0 : 1 / 60.0);
        model.FillSurface(ys);
        model.FillSurface(ys, back: true);
        model.FillCurrent(0, ys);
        var sum = 0.0;
        foreach (var sprite in model.Sprites)
        {
            sum += sprite.Alpha;
        }

        Sink += sum;
    }
}
