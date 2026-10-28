using System.Diagnostics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests;

public class OrbNeedleModelTests(ITestOutputHelper output)
{
    private const double Dt = 1.0 / 60;
    private const double Peg = NeedleGeometry.Peg;

    private static (NeedleModel Model, OrbStyleDriver Driver) Create(
        EffectsIntensity level = EffectsIntensity.Standard, double size = 148, bool dark = true, bool reduceMotion = false,
        bool highContrast = false, uint seed = 3)
    {
        var model = new NeedleModel(seed);
        return (model, new OrbStyleDriver(model, level, size, dark, reduceMotion, highContrast));
    }

    private static double Dial(double n) => OrbStyleDriver.ValueForDial(n);

    private static void SnapAt(OrbStyleDriver driver, double value)
    {
        driver.Value = value;
        driver.Snap();
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.4)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void NeedleSettlesOnTheRawHeat(double n)
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, 0);
        driver.Value = Dial(n);
        driver.Run(6);
        Assert.Equal(n, model.SpringDial, 3);
        Assert.Equal(n, model.NeedleDial, 3);
    }

    [Theory]
    [InlineData(500, 0.25)]
    [InlineData(800, 1.0)]
    public void OverdrivePressesTheNeedleIntoThePeg(double value, double overdrive)
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.5));
        driver.Value = value;
        driver.Run(4);
        Assert.Equal(overdrive, driver.Frame.OverdriveRaw, 6);
        Assert.InRange(model.SpringDial, NeedleModel.PinnedAt, Peg);
        Assert.True(model.Pinned);
        Assert.True(model.NeedleDial <= Peg);
    }

    [Fact]
    public void IdleTargetIsZero()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.6));
        driver.Value = 0.3;
        driver.Run(6);
        Assert.Equal(SpeedTier.Idle, driver.Frame.Tier);
        Assert.Equal(0, model.SpringDial, 3);
        Assert.Equal(0, model.LitTickCount);
        Assert.False(model.TraceVisible);
    }

    [Fact]
    public void StepOvershootsAboutTwelvePercent()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, 0);
        driver.Value = Dial(0.5);
        var peak = 0.0;
        for (var i = 0; i < 180; i++)
        {
            driver.Step(Dt);
            peak = Math.Max(peak, model.SpringDial);
        }

        var overshoot = (peak - 0.5) / 0.5;
        Assert.InRange(overshoot, 0.11, 0.14);
    }

    [Fact]
    public void PegStopsTheNeedleAndBouncesIt()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.5));
        driver.Value = 800;
        var touched = false;
        var dipAfterTouch = Peg;
        for (var i = 0; i < 240; i++)
        {
            driver.Step(Dt);
            Assert.True(model.SpringDial <= Peg + 1e-12);
            Assert.True(model.NeedleDial <= Peg + 1e-12);
            // The contact happens between frames; the frame after it is already on its way back.
            if (model.SpringDial >= Peg - 0.005)
            {
                touched = true;
            }
            else if (touched)
            {
                dipAfterTouch = Math.Min(dipAfterTouch, model.SpringDial);
            }
        }

        Assert.True(touched);
        Assert.True(dipAfterTouch < Peg - 0.003, $"no bounce: {dipAfterTouch}");
        Assert.InRange(model.SpringDial, NeedleModel.PinnedAt, Peg);
    }

    [Fact]
    public void ZeroStopHoldsTheReturningNeedle()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, 800);
        driver.Value = 0;
        var low = double.MaxValue;
        for (var i = 0; i < 300; i++)
        {
            driver.Step(Dt);
            low = Math.Min(low, model.SpringDial);
            Assert.True(model.NeedleDial >= NeedleGeometry.ZeroStop - 1e-12);
        }

        Assert.InRange(low, NeedleGeometry.ZeroStop, -0.006);
        Assert.Equal(0, model.SpringDial, 3);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void OffAndReduceMotionFollowTheSmoothedHeat(EffectsIntensity level, bool reduceMotion)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion);
        SnapAt(driver, Dial(0.2));
        driver.Value = Dial(0.7);
        for (var i = 0; i < 180; i++)
        {
            driver.Step(Dt);
            Assert.Equal(driver.Frame.Heat, model.NeedleDial, 9);
            Assert.False(model.WakeVisible);
        }

        driver.Value = 800;
        driver.Run(4);
        Assert.Equal(Peg, model.NeedleDial, 9);
        Assert.False(model.Pinned);
        Assert.Equal(0, model.Sparks.Length);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, 0)]
    [InlineData(EffectsIntensity.Soft, 0)]
    [InlineData(EffectsIntensity.Standard, 1.0)]
    [InlineData(EffectsIntensity.Max, 1.4)]
    public void TrembleAmplitudeFollowsTheSpec(EffectsIntensity level, double scale)
    {
        var (model, driver) = Create(level);
        SnapAt(driver, Dial(0.9));
        var amplitude = StyleMath.SmoothStep(0.55, 1, 0.9) * 0.3 * scale / 270;
        var deviation = 0.0;
        for (var i = 0; i < 300; i++)
        {
            driver.Step(Dt);
            deviation = Math.Max(deviation, Math.Abs(model.NeedleDial - model.SpringDial));
        }

        Assert.True(deviation <= amplitude + 1e-12, $"{deviation} > {amplitude}");
        if (scale > 0)
        {
            Assert.True(deviation >= 0.6 * amplitude, $"{deviation} < 0.6 × {amplitude}");
        }
    }

    [Fact]
    public void PinnedNeedleChattersBelowThePeg()
    {
        var (model, driver) = Create();
        SnapAt(driver, 800);
        var low = Peg;
        for (var i = 0; i < 60; i++)
        {
            driver.Step(Dt);
            Assert.True(model.NeedleDial <= Peg + 1e-12);
            low = Math.Min(low, model.NeedleDial);
        }

        Assert.InRange(Peg - low, 0.002, 1.3 / 270 + 1e-5);
    }

    [Fact]
    public void WakeTrailsAFastNeedleUpToItsCap()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.1));
        driver.Value = 800;
        var longest = 0.0;
        var seen = false;
        for (var i = 0; i < 120; i++)
        {
            driver.Step(Dt);
            var speed = Math.Abs(model.AngularVelocity);
            Assert.Equal(speed > NeedleModel.WakeMinSpeed, model.WakeVisible);
            if (!model.WakeVisible)
            {
                continue;
            }

            seen = true;
            var length = Math.Abs(model.WakeHeadDegrees - model.WakeTailDegrees);
            Assert.Equal(Math.Min(55, 4 + 0.085 * speed), length, 9);
            Assert.Equal(model.NeedleDegrees, model.WakeHeadDegrees, 9);
            Assert.Equal(-Math.Sign(model.AngularVelocity), Math.Sign(model.WakeTailDegrees - model.WakeHeadDegrees));
            Assert.InRange(model.WakeHeadOpacity, 0, NeedleModel.WakeOpacity);
            longest = Math.Max(longest, length);
        }

        Assert.True(seen);
        Assert.Equal(55, longest, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false, false)]
    [InlineData(EffectsIntensity.Soft, true, false)]
    [InlineData(EffectsIntensity.Standard, false, true)]
    public void WakeNeedsSoftWithoutReduceMotionOrHighContrast(EffectsIntensity level, bool reduceMotion, bool highContrast)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion, highContrast: highContrast);
        SnapAt(driver, Dial(0.1));
        driver.Value = 800;
        for (var i = 0; i < 120; i++)
        {
            driver.Step(Dt);
            Assert.False(model.WakeVisible);
        }
    }

    [Theory]
    [InlineData(0.69, 0)]
    [InlineData(0.71, 1)]
    [InlineData(0.75, 2)]
    [InlineData(0.78, 3)]
    [InlineData(0.81, 4)]
    [InlineData(0.845, 5)]
    public void ShiftLightsLightAtTheirThresholds(double n, int expected)
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        driver.Tier = SpeedTier.Overdrive;
        SnapAt(driver, Dial(n));
        var lamps = model.Lamps;
        Assert.Equal(5, lamps.Length);
        for (var i = 0; i < lamps.Length; i++)
        {
            Assert.Equal(i < expected ? 1 : 0, lamps[i].Opacity);
            Assert.Equal(model.Palette.Lamp(i), lamps[i].Color);
            var (x, y) = StyleMath.Polar(0.33, -110 + 10 * i);
            Assert.Equal(x, lamps[i].X, 9);
            Assert.Equal(y, lamps[i].Y, 9);
        }

        Assert.False(model.LampsFlashing);
        Assert.Equal(0, model.LampFlashMix);
    }

    [Fact]
    public void RedlineFlashIsThreeHertz()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.9));
        Assert.Equal(SpeedTier.Redline, driver.Frame.Tier);
        var rising = 0;
        var previous = model.Lamps[0].Opacity;
        double low = 1, high = 0;
        for (var i = 0; i < 600; i++)
        {
            driver.Step(Dt);
            Assert.True(model.LampsFlashing);
            Assert.Equal(1, model.LampFlashMix);
            var opacity = model.Lamps[0].Opacity;
            foreach (var lamp in model.Lamps)
            {
                Assert.Equal(opacity, lamp.Opacity);
                Assert.Equal(model.Palette.LampFlash, lamp.Color);
            }

            if (previous < 0.64 && opacity >= 0.64)
            {
                rising++;
            }

            previous = opacity;
            low = Math.Min(low, opacity);
            high = Math.Max(high, opacity);
        }

        Assert.InRange(rising, 29, 30);
        Assert.Equal(0.28, low, 2);
        Assert.Equal(1, high, 2);
    }

    [Fact]
    public void ReduceMotionKeepsTheRedlineLampsSteady()
    {
        var (model, driver) = Create(reduceMotion: true);
        SnapAt(driver, Dial(0.9));
        for (var i = 0; i < 60; i++)
        {
            driver.Step(Dt);
            Assert.False(model.LampsFlashing);
            foreach (var lamp in model.Lamps)
            {
                Assert.Equal(1, lamp.Opacity);
                Assert.Equal(model.Palette.LampFlash, lamp.Color);
            }

            Assert.Equal(1, model.RedZoneOpacity);
        }
    }

    [Fact]
    public void OffKeepsLampsAndRedZoneSteady()
    {
        var (model, driver) = Create(EffectsIntensity.Off);
        SnapAt(driver, Dial(0.9));
        for (var i = 0; i < 60; i++)
        {
            driver.Step(Dt);
            Assert.Equal(0, model.LampFlashMix);
            foreach (var lamp in model.Lamps)
            {
                Assert.Equal(1, lamp.Opacity);
            }

            Assert.Equal(0.62, model.RedZoneOpacity);
            Assert.Equal(0, model.RedZoneGlowSigma);
        }
    }

    [Fact]
    public void LampsNeverBlinkFasterThanThreeHertzOnATour()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        driver.TrackTiers = true;
        SnapAt(driver, 0);
        var edges = new List<double>[5];
        for (var i = 0; i < 5; i++)
        {
            edges[i] = [];
        }

        var previous = new double[5];
        for (var frame = 0; frame < 60 * 62; frame++)
        {
            driver.Value = OrbStyleDriver.TourValue(driver.Time);
            driver.Step(Dt);
            for (var i = 0; i < 5; i++)
            {
                var opacity = model.Lamps[i].Opacity;
                if (previous[i] < 0.64 && opacity >= 0.64)
                {
                    edges[i].Add(driver.Time);
                }

                previous[i] = opacity;
            }
        }

        foreach (var list in edges)
        {
            Assert.NotEmpty(list);
            for (var j = 3; j < list.Count; j++)
            {
                Assert.True(list[j] - list[j - 3] >= 1 - 1e-6, $"four flashes within {list[j] - list[j - 3]:F3} s");
            }
        }
    }

    [Fact]
    public void FlashLimiterHoldsWhenTheModeKeepsChanging()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.9));
        var edges = new List<double>();
        var previous = model.Lamps[0].Opacity;
        for (var i = 0; i < 600; i++)
        {
            driver.Tier = i / 5 % 2 == 0 ? SpeedTier.Redline : SpeedTier.Overdrive;
            driver.Step(Dt);
            var opacity = model.Lamps[0].Opacity;
            if (previous < NeedleModel.LampLitLevel && opacity >= NeedleModel.LampLitLevel)
            {
                edges.Add(driver.Time);
            }

            previous = opacity;
        }

        Assert.True(edges.Count >= 20, $"{edges.Count} flashes");
        for (var j = 3; j < edges.Count; j++)
        {
            Assert.True(edges[j] - edges[j - 3] >= 1 - 1e-6, $"four flashes within {edges[j] - edges[j - 3]:F3} s");
        }
    }

    [Fact]
    public void AngularVelocityMatchesTheDisplayedMotion()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.3));
        driver.Value = Dial(0.6);
        driver.Run(0.2);
        model.Peak();
        var step = 1.0 / 480;
        var previousDial = model.NeedleDial;
        var previousSpeed = model.AngularVelocity;
        for (var i = 0; i < 600; i++)
        {
            driver.Step(step);
            var finite = (model.NeedleDial - previousDial) / step * 270;
            var mean = (model.AngularVelocity + previousSpeed) / 2;
            Assert.True(Math.Abs(finite - mean) < 4, $"frame {i}: {finite:F1} vs {mean:F1} °/s");
            previousDial = model.NeedleDial;
            previousSpeed = model.AngularVelocity;
        }
    }

    [Fact]
    public void UpdateStaysFarBelowTheFrameBudget()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 800);
        driver.Run(2);
        const int frames = 5000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            if (i % 10 == 0)
            {
                model.Pulse(1, 1);
            }

            driver.Step(i % 2 == 0 ? 1.0 / 30 : Dt);
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"needle Update at Max redline: {perFrame * 1000:F1} µs per frame, {model.Sparks.Length} sparks");
        Assert.True(perFrame < 0.25, $"{perFrame:F4} ms per frame");
    }

    [Fact]
    public void LampOnAThresholdDoesNotFlickerWithTheTremble()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        driver.Tier = SpeedTier.Overdrive;
        SnapAt(driver, Dial(0.77));
        var changes = 0;
        var previous = model.Lamps[2].Opacity;
        var lit = model.LitTickCount;
        for (var i = 0; i < 300; i++)
        {
            driver.Step(Dt);
            if (model.Lamps[2].Opacity != previous)
            {
                changes++;
            }

            previous = model.Lamps[2].Opacity;
            Assert.Equal(lit, model.LitTickCount);
        }

        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(0.0, 20)]
    [InlineData(1.0, 30)]
    public void RedZonePulsesAtTwoPlusOverdriveHertz(double overdrive, int cycles)
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, overdrive > 0 ? 800 : Dial(0.9));
        var peaks = 0;
        double a = model.RedZoneOpacity, b = a;
        double low = 1, high = 0;
        for (var i = 0; i < 600; i++)
        {
            driver.Step(Dt);
            var c = model.RedZoneOpacity;
            if (b > a && b >= c)
            {
                peaks++;
            }

            a = b;
            b = c;
            low = Math.Min(low, c);
            high = Math.Max(high, c);
        }

        Assert.InRange(peaks, cycles - 1, cycles + 1);
        Assert.Equal(0.8, low, 2);
        Assert.Equal(1.0, high, 2);
        Assert.Equal(0, model.RedZoneGlowSigma);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 1.0)]
    [InlineData(EffectsIntensity.Max, 1.3)]
    public void RedZoneGlowGrowsWithOverdrive(EffectsIntensity level, double scale)
    {
        var (model, driver) = Create(level);
        SnapAt(driver, Dial(0.9));
        Assert.Equal(0.025 * 0.6 * scale, model.RedZoneGlowSigma, 9);
        var key = model.RedZoneGlowKey;
        SnapAt(driver, 800);
        Assert.Equal(0.025 * 1.0 * scale, model.RedZoneGlowSigma, 9);
        Assert.NotEqual(key, model.RedZoneGlowKey);
        Assert.Equal(0.9 * model.RedZoneOpacity, model.RedZoneGlowOpacity, 9);

        SnapAt(driver, Dial(0.5));
        Assert.Equal(0, model.RedZoneGlowSigma);
        Assert.Equal(0.62, model.RedZoneOpacity);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 600, 60)]
    [InlineData(EffectsIntensity.Max, 600, 96)]
    public void PegSparksFlyAtTheirRate(EffectsIntensity level, double value, double rate)
    {
        var (model, driver) = Create(level);
        SnapAt(driver, value);
        driver.Run(1);
        double sum = 0;
        var frames = 0;
        for (var i = 0; i < 600; i++)
        {
            driver.Step(Dt);
            Assert.True(model.Pinned);
            sum += model.Sparks.Length;
            frames++;
        }

        // Little's law: live sparks = rate × mean life (0.385 s).
        var expected = rate * (NeedleSparks.MinLife + NeedleSparks.MaxLife) / 2;
        Assert.InRange(sum / frames, expected * 0.85, expected * 1.1);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, NeedleSparks.StandardCapacity)]
    [InlineData(EffectsIntensity.Max, NeedleSparks.MaxCapacity)]
    public void SparkPoolsAreCapped(EffectsIntensity level, int capacity)
    {
        var (model, driver) = Create(level);
        SnapAt(driver, 800);
        var most = 0;
        for (var i = 0; i < 1200; i++)
        {
            driver.Step(Dt);
            most = Math.Max(most, model.Sparks.Length);
            Assert.True(model.Sparks.Length <= capacity);
        }

        Assert.True(most > (level == EffectsIntensity.Max ? NeedleSparks.StandardCapacity : 30), $"most {most}");
    }

    [Fact]
    public void SparkPoolNeverGrowsPastItsCapacity()
    {
        var pool = new NeedleSparks();
        var random = new Hero.HeroRandom(3);
        pool.Step(0.05, 100_000, NeedleSparks.StandardCapacity, random);
        Assert.Equal(NeedleSparks.StandardCapacity, pool.Count);
        pool.Step(0.05, 100_000, 500, random);
        Assert.Equal(NeedleSparks.MaxCapacity, pool.Count);
        pool.Step(NeedleSparks.MaxLife + 0.01, 0, NeedleSparks.MaxCapacity, random);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void SparksAreBornAtThePegAsWhiteHotStreaks()
    {
        var (model, driver) = Create();
        SnapAt(driver, 800);
        driver.Step(Dt);
        Assert.NotEqual(0, model.Sparks.Length);
        var (cx, cy) = StyleMath.Polar(NeedleGeometry.ContactRadius, StyleMath.DialDegrees(NeedleGeometry.ContactDial));
        foreach (var spark in model.Sparks)
        {
            Assert.InRange(Math.Sqrt((spark.X - cx) * (spark.X - cx) + (spark.Y - cy) * (spark.Y - cy)), 0, 0.03);
            Assert.InRange(spark.Width, 0.4f, 1.6f);
            Assert.InRange(spark.Alpha, 0.85f, 1f);
            var length = Math.Sqrt(spark.StreakX * spark.StreakX + spark.StreakY * spark.StreakY);
            Assert.InRange(length, 0.025 * 0.1, 0.025 * 1.15);
            Assert.True(Math.Abs(spark.Color.R - HeatPalette.WhiteHot.R) < 0.1 && Math.Abs(spark.Color.B - HeatPalette.WhiteHot.B) < 0.15);
        }

        driver.Run(1);
        foreach (var spark in model.Sparks)
        {
            Assert.True(spark.Y > -0.1, "sparks fall under gravity and fly clockwise from the lower-right peg");
        }
    }

    [Fact]
    public void LightSparksAreTwentyPercentWider()
    {
        var (dark, darkDriver) = Create(seed: 9);
        var (light, lightDriver) = Create(dark: false, seed: 9);
        SnapAt(darkDriver, 800);
        SnapAt(lightDriver, 800);
        darkDriver.Run(0.5);
        lightDriver.Run(0.5);
        Assert.Equal(dark.Sparks.Length, light.Sparks.Length);
        for (var i = 0; i < dark.Sparks.Length; i++)
        {
            Assert.Equal(dark.Sparks[i].Width * 1.2f, light.Sparks[i].Width, 4);
        }
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void NoSparksBelowStandardOrWithReduceMotion(EffectsIntensity level, bool reduceMotion)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion);
        SnapAt(driver, 800);
        driver.Run(1);
        Assert.Equal(0, model.Sparks.Length);
    }

    [Fact]
    public void SparksStopWhenTheNeedleLeavesThePeg()
    {
        var (model, driver) = Create();
        SnapAt(driver, 800);
        driver.Run(0.5);
        Assert.NotEqual(0, model.Sparks.Length);
        driver.Value = Dial(0.7);
        driver.Run(0.8);
        Assert.False(model.Pinned);
        Assert.Equal(0, model.Sparks.Length);
    }

    [Fact]
    public void PulseFlaresTheTipFor035Seconds()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, Dial(0.4));
        var sigma = model.NeedleGlowSigma;
        Assert.Equal(0.0275 * 1.25, sigma, 9);
        var key = model.NeedleKey;
        model.Pulse(0.4, 1);
        driver.Step(Dt);
        Assert.Equal(1, model.Flare, 9);
        Assert.Equal(0.7, model.FlareSpotOpacity, 9);
        Assert.Equal((0.0275 + 0.0175) * 1.25, model.NeedleGlowSigma, 9);
        Assert.Equal(1, model.Spine.A, 9);
        Assert.NotEqual(key, model.NeedleKey);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(0.36);
        Assert.Equal(0, model.Flare);
        Assert.Equal(sigma, model.NeedleGlowSigma, 9);
        Assert.Equal(key, model.NeedleKey);
    }

    [Fact]
    public void OffHasNoFlare()
    {
        var (model, driver) = Create(EffectsIntensity.Off);
        SnapAt(driver, Dial(0.4));
        model.Pulse(0.4, 1);
        driver.Step(Dt);
        Assert.Equal(0, model.Flare);
    }

    [Fact]
    public void NewPeakSweepsToThePegAndBack()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        const double n0 = 0.4;
        SnapAt(driver, Dial(n0));
        model.Peak();
        for (var k = 0; k < 70; k++)
        {
            driver.Step(Dt);
            var tau = k * Dt;
            var g = StyleMath.SmoothStep(0, 0.38, tau) - StyleMath.SmoothStep(0.5, 1.0, tau);
            var expected = tau < 1 ? n0 + (Peg - n0) * g : n0;
            Assert.Equal(expected, model.NeedleDial, 6);
            if (k == 26)
            {
                Assert.Equal(1, model.LampFlashMix);
                foreach (var lamp in model.Lamps)
                {
                    Assert.Equal(1, lamp.Opacity);
                    Assert.Equal(model.Palette.LampFlash, lamp.Color);
                }

            }

            if (k == 10)
            {
                Assert.True(model.WakeVisible, "the sweep turns the needle fast enough for the wake");
                Assert.Equal(FrameClass.Fps60, model.Demand);
            }

            if (k == 48)
            {
                Assert.Equal(0, model.LampFlashMix);
                foreach (var lamp in model.Lamps)
                {
                    Assert.Equal(0, lamp.Opacity);
                }
            }
        }

        Assert.Null(model.PeakSweep);
    }

    [Fact]
    public void ReduceMotionHasNoGaugeSweep()
    {
        var (model, driver) = Create(reduceMotion: true);
        SnapAt(driver, Dial(0.4));
        model.Peak();
        for (var k = 0; k < 70; k++)
        {
            driver.Step(Dt);
            Assert.Equal(0.4, model.NeedleDial, 6);
            Assert.Null(model.PeakSweep);
        }
    }

    [Fact]
    public void TierFlashBrightensLitTicksAndTrace()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.4));
        var lit = model.Ticks[0];
        Assert.True(lit.Lit);
        Assert.Equal(0.95, lit.Color.A, 9);
        Assert.Equal(0.7, model.TraceOpacityEnd, 9);
        Assert.Equal(0.21, model.TraceOpacityStart, 9);
        var tickKey = model.TickKey;
        var traceKey = model.TraceKey;

        driver.Flash = 1;
        driver.Step(Dt);
        Assert.Equal(1, model.Ticks[0].Color.A, 9);
        Assert.Equal(0.875, model.TraceOpacityEnd, 9);
        Assert.NotEqual(tickKey, model.TickKey);
        Assert.NotEqual(traceKey, model.TraceKey);
        Assert.Equal(FrameClass.Fps60, model.Demand);
    }

    [Fact]
    public void LitTicksFollowTheNeedle()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.51));
        Assert.Equal(21, model.LitTickCount);
        var ticks = model.Ticks;
        for (var k = 0; k < ticks.Length; k++)
        {
            var tick = ticks[k];
            Assert.Equal(k < 21, tick.Lit);
            Assert.Equal(k == 20, tick.Leading);
            Assert.Equal(135 + 270 * k / 40.0, tick.Degrees, 9);
            Assert.Equal(k % 5 == 0, tick.Major);
            Assert.Equal(0.445, tick.OuterRadius, 9);
            Assert.Equal(tick.Major ? 0.445 - 0.062 : 0.445 - 0.03, tick.InnerRadius, 9);
            if (tick.Lit)
            {
                var color = tick.Leading ? model.Palette.LeadTick(k) : model.Palette.LitTick(k);
                Assert.Equal(color.WithAlpha(0.95), tick.Color);
                Assert.Equal(tick.Major ? 0.015 : 0, tick.GlowSigma);
            }
            else if (k >= 34)
            {
                Assert.Equal(tick.Major ? 0.85 : 0.6, tick.Color.A, 9);
                Assert.Equal(model.Palette.RedZone.R, tick.Color.R, 9);
            }
            else
            {
                Assert.Equal(tick.Major ? 0.5 : 0.24, tick.Color.A, 9);
            }
        }

        Assert.Equal(model.Palette.LitTick(20), StyleColors.Heat(0.5));
        Assert.Equal(model.Palette.LeadTick(20), OkLab.Mix(StyleColors.Heat(0.5), HeatPalette.WhiteHot, 0.75));
    }

    [Fact]
    public void TickKeyIsStableWhileNothingChanges()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 800);
        var key = model.TickKey;
        for (var i = 0; i < 120; i++)
        {
            driver.Step(Dt);
            Assert.Equal(key, model.TickKey);
        }
    }

    [Fact]
    public void IdleSignOfLifeSweepsTheGraduations()
    {
        var (model, driver) = Create();
        SnapAt(driver, 0);
        Assert.Equal(FrameClass.None, model.Demand);
        var key = model.TickKey;
        driver.SignOfLifeAge = 0.45;
        driver.Step(Dt);
        Assert.NotNull(model.SignOfLifeCenter);
        Assert.Equal(0.5, model.SignOfLifeCenter!.Value, 2);
        Assert.Equal(0.5 + 0.35, model.Ticks[20].Color.A, 2);
        Assert.Equal(0.5, model.Ticks[0].Color.A, 3);
        Assert.NotEqual(key, model.TickKey);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        Assert.Equal(model.Palette.NeedleIdle.WithAlpha(0.75), model.NeedleFill);

        driver.SignOfLifeAge = 0.95;
        driver.Step(Dt);
        Assert.Null(model.SignOfLifeCenter);
        Assert.Equal(FrameClass.None, model.Demand);

        driver.SignOfLifeAge = null;
        driver.Step(Dt);
        Assert.Equal(key, model.TickKey);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false, false)]
    [InlineData(EffectsIntensity.Standard, true, false)]
    [InlineData(EffectsIntensity.Standard, false, true)]
    public void SignOfLifeNeedsMotionAndAvailableSources(EffectsIntensity level, bool reduceMotion, bool unavailable)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion);
        driver.Unavailable = unavailable;
        SnapAt(driver, 0);
        driver.SignOfLifeAge = 0.45;
        driver.Step(Dt);
        Assert.Null(model.SignOfLifeCenter);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void LightSignOfLifeBoostIsTwentyFivePercent()
    {
        var (model, driver) = Create(dark: false);
        SnapAt(driver, 0);
        driver.SignOfLifeAge = 0.45;
        driver.Step(Dt);
        Assert.Equal(0.42 + 0.25, model.Ticks[20].Color.A, 2);
    }

    [Theory]
    [InlineData(148, true, true, true)]
    [InlineData(120, true, true, true)]
    [InlineData(110, false, false, true)]
    [InlineData(60, false, false, false)]
    public void LevelOfDetail(double size, bool minor, bool lamps, bool pins)
    {
        var (model, driver) = Create(size: size);
        SnapAt(driver, Dial(0.6));
        Assert.Equal(minor, model.ShowMinorTicks);
        Assert.Equal(lamps, model.ShowLamps);
        Assert.Equal(pins, model.ShowStopPins);
        Assert.Equal(pins, model.ShowHub);
        Assert.Equal(pins, model.TraceVisible);
        foreach (var tick in model.Ticks)
        {
            Assert.Equal(tick.Major || minor, tick.Visible);
        }
    }

    [Fact]
    public void ThumbnailIsStill()
    {
        var (model, driver) = Create(EffectsIntensity.Max, size: 60);
        SnapAt(driver, 800);
        driver.Run(1);
        Assert.Equal(Peg, model.NeedleDial, 9);
        Assert.Equal(0, model.Sparks.Length);
        Assert.Equal(FrameClass.None, model.Demand);
        Assert.Equal(0, model.NeedleGlowSigma);
        Assert.Equal(0, model.RedZoneGlowSigma);
        Assert.Equal(0.62, model.RedZoneOpacity);
        Assert.All(model.Ticks.ToArray(), tick => Assert.Equal(0, tick.GlowSigma));
    }

    [Fact]
    public void TraceRunsToTheNeedle()
    {
        var (model, driver) = Create(dark: false);
        SnapAt(driver, Dial(0.6));
        Assert.True(model.TraceVisible);
        Assert.Equal(model.NeedleDial, model.TraceEnd, 9);
        Assert.Equal(0.85, model.TraceOpacityEnd, 9);
        Assert.Equal(0.255, model.TraceOpacityStart, 9);
        Assert.Equal(StyleColors.Label(0.3, false), model.TraceColor(0.3));
        Assert.False(model.Palette.Additive);
    }

    [Fact]
    public void NeedleColorsFollowHeatAndOverdrive()
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.5));
        Assert.Equal(1, model.NeedleFill.A, 9);
        var h = Math.Round(0.5 * 255) / 255;
        var expected = OkLab.Mix(HeatPalette.Sample(h), HeatPalette.WhiteHot, 0.05);
        Assert.Equal(expected.R, model.NeedleFill.R, 2);
        Assert.Equal(expected.G, model.NeedleFill.G, 2);
        Assert.Equal(0.45 + 0.35 * h, model.NeedleGlowOpacity, 9);

        SnapAt(driver, 800);
        var hot = OkLab.Mix(HeatPalette.Sample(1), HeatPalette.WhiteHot, 0.5);
        Assert.Equal(hot.B, model.NeedleFill.B, 2);

        var (light, lightDriver) = Create(dark: false);
        SnapAt(lightDriver, Dial(0.5));
        Assert.Equal(StyleColors.Label(0.5, false).R, light.NeedleFill.R, 2);
        Assert.Equal(0, light.NeedleGlowSigma);
        Assert.True(light.NeedleShadowVisible);
    }

    [Fact]
    public void HighContrastDropsGlowsWakeAndSparks()
    {
        var (model, driver) = Create(EffectsIntensity.Max, highContrast: true);
        SnapAt(driver, 800);
        driver.Run(1);
        Assert.True(model.HighContrast);
        Assert.Equal(0, model.NeedleGlowSigma);
        Assert.Equal(0, model.RedZoneGlowSigma);
        Assert.All(model.Lamps.ToArray(), lamp => Assert.Equal(0, lamp.GlowSigma));
        Assert.All(model.Ticks.ToArray(), tick => Assert.Equal(0, tick.GlowSigma));
        Assert.Equal(0, model.Sparks.Length);
        Assert.False(model.WakeVisible);
        Assert.False(model.NeedleShadowVisible);
    }

    [Fact]
    public void DemandIdleIsStatic()
    {
        var (model, driver) = Create();
        SnapAt(driver, 0);
        driver.Run(1);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void DemandCruiseIsThirtyAndSixtyWhileTurningFast()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.1));
        Assert.Equal(SpeedTier.Cruise, driver.Frame.Tier);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Value = Dial(0.53);
        var sawSixty = false;
        for (var i = 0; i < 300; i++)
        {
            driver.Step(Dt);
            var fast = Math.Abs(model.AngularVelocity) > NeedleModel.FastTurn;
            Assert.Equal(fast ? FrameClass.Fps60 : FrameClass.Fps30, model.Demand);
            sawSixty |= fast;
        }

        Assert.True(sawSixty);
        Assert.Equal(FrameClass.Fps30, model.Demand);
    }

    [Theory]
    [InlineData(200, SpeedTier.Overdrive)]
    [InlineData(350, SpeedTier.Redline)]
    [InlineData(800, SpeedTier.Redline)]
    public void DemandOverdriveAndRedlineIsSixty(double value, SpeedTier tier)
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, value);
        driver.Run(1);
        Assert.Equal(tier, driver.Frame.Tier);
        Assert.Equal(FrameClass.Fps60, model.Demand);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Max, true)]
    public void DemandOffOrReduceMotionRendersOnlyWhileSettling(EffectsIntensity level, bool reduceMotion)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion);
        SnapAt(driver, 800);
        Assert.Equal(FrameClass.None, model.Demand);
        driver.Value = 100;
        driver.Step(Dt);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Run(3);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void DemandNewPeakIsSixtyForOneSecond()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.2));
        model.Peak();
        driver.Step(Dt);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(0.95);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(0.1);
        Assert.Equal(FrameClass.Fps30, model.Demand);
    }

    [Fact]
    public void EventsWakeAStaticOrbImmediately()
    {
        var (model, driver) = Create(EffectsIntensity.Off);
        SnapAt(driver, Dial(0.4));
        Assert.Equal(FrameClass.None, model.Demand);
        model.Pulse(0.4, 1);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Step(Dt);
        Assert.Equal(FrameClass.None, model.Demand);
        model.Peak();
        Assert.Equal(FrameClass.Fps60, model.Demand);
    }

    [Fact]
    public void DemandWhileReturningToIdleIsThirtyThenStatic()
    {
        var (model, driver) = Create();
        SnapAt(driver, Dial(0.3));
        driver.Value = 0;
        driver.Step(Dt);
        Assert.Equal(SpeedTier.Idle, driver.Frame.Tier);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Run(4);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void SnapJumpsWithoutCatchUp()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 800);
        driver.Run(1);
        model.Pulse(1, 1);
        model.Peak();
        driver.Step(Dt);
        Assert.NotEqual(0, model.Sparks.Length);
        Assert.NotNull(model.PeakSweep);

        driver.Value = Dial(0.3);
        driver.Snap();
        Assert.Equal(0.3, model.SpringDial, 9);
        Assert.Equal(0.3, model.NeedleDial, 9);
        Assert.Equal(0, model.AngularVelocity);
        Assert.Equal(0, model.Sparks.Length);
        Assert.Equal(0, model.Flare);
        Assert.Null(model.PeakSweep);
        Assert.False(model.WakeVisible);
        Assert.Equal(13, model.LitTickCount);

        driver.Step(Dt);
        Assert.Equal(0.3, model.NeedleDial, 6);
        Assert.Equal(FrameClass.Fps30, model.Demand);
    }

    [Fact]
    public void ResultsDoNotDependOnTheFrameRate()
    {
        var times = new[] { 0.3, 0.75, 1.0, 1.5, 2.25, 3.0, 4.2, 4.6, 5.5, 7.0 };
        var reference = Trace(times, 1.0 / 480);
        foreach (var dt in new[] { 1.0 / 30, 1.0 / 60, 1.0 / 72, 1.0 / 144 })
        {
            var run = Trace(times, dt);
            for (var i = 0; i < times.Length; i++)
            {
                Assert.True(Math.Abs(run[i] - reference[i]) < 0.006, $"dt {dt:F4} t {times[i]}: {run[i]:F5} vs {reference[i]:F5}");
            }
        }

        var irregular = Trace(times, 0, new Random(5));
        for (var i = 0; i < times.Length; i++)
        {
            Assert.True(Math.Abs(irregular[i] - reference[i]) < 0.012, $"irregular t {times[i]}: {irregular[i]:F5} vs {reference[i]:F5}");
        }
    }

    private static double[] Trace(double[] times, double dt, Random? jitter = null)
    {
        var (model, driver) = Create(EffectsIntensity.Soft);
        SnapAt(driver, Dial(0.2));
        var schedule = new (double At, double Value)[] { (0.5, Dial(0.8)), (2.0, 800), (4.0, Dial(0.3)), (6.0, 0) };
        var result = new double[times.Length];
        var next = 0;
        var sample = 0;
        while (sample < times.Length)
        {
            var target = Math.Min(times[sample], next < schedule.Length ? schedule[next].At : double.MaxValue);
            while (driver.Time < target - 1e-9)
            {
                var step = jitter is null ? dt : 1.0 / 90 + jitter.NextDouble() * (1.0 / 25 - 1.0 / 90);
                driver.Step(Math.Min(step, target - driver.Time));
            }

            if (next < schedule.Length && Math.Abs(driver.Time - schedule[next].At) < 1e-9)
            {
                driver.Value = schedule[next++].Value;
            }

            if (Math.Abs(driver.Time - times[sample]) < 1e-9)
            {
                result[sample++] = model.SpringDial;
            }
        }

        return result;
    }

    [Fact]
    public void SparkDensityDoesNotDependOnTheFrameRate()
    {
        double Mean(double dt)
        {
            var (model, driver) = Create(seed: 21);
            SnapAt(driver, 700);
            driver.Run(1, 1 / dt);
            double sum = 0;
            var n = (int)Math.Round(30 / dt);
            for (var i = 0; i < n; i++)
            {
                driver.Step(dt);
                sum += model.Sparks.Length;
            }

            return sum / n;
        }

        var at60 = Mean(1.0 / 60);
        Assert.InRange(Mean(1.0 / 30) / at60, 0.9, 1.1);
        Assert.InRange(Mean(1.0 / 72) / at60, 0.9, 1.1);
    }

    [Fact]
    public void SameSeedReplaysExactly()
    {
        static NeedleSpark[] Run(uint seed)
        {
            var (model, driver) = Create(EffectsIntensity.Max, seed: seed);
            SnapAt(driver, 800);
            driver.Run(1.3);
            return model.Sparks.ToArray();
        }

        Assert.Equal(Run(4), Run(4));
        Assert.NotEqual(Run(4), Run(5));
    }

    [Fact]
    public void UpdatesDoNotAllocate()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 0);
        Exercise(model, driver, 600);
        AllocationProbe.AssertNone(() => Exercise(model, driver, 1000));
    }

    private static void Exercise(NeedleModel model, OrbStyleDriver driver, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var phase = i % 600;
            driver.Value = phase < 100 ? 0 : phase < 200 ? 60 : phase < 400 ? 800 : 300;
            driver.SignOfLifeAge = phase < 50 ? phase / 60.0 : null;
            driver.Flash = phase % 150 < 20 ? 1 - phase % 150 / 20.0 : 0;
            if (i % 13 == 0)
            {
                model.Pulse(0.8, 1);
            }

            if (i % 250 == 0)
            {
                model.Peak();
            }

            driver.Step(i % 3 == 0 ? 1.0 / 30 : Dt);
            if (i % 997 == 0)
            {
                driver.Snap();
            }
        }
    }

    [Fact]
    public void LightLabelColorsReachFourAndAHalfToOne()
    {
        var body = Rgba.FromHex("#E7EBF3");
        for (var x = 0.0; x <= 1.0001; x += 0.02)
        {
            Assert.True(Rgba.ContrastRatio(StyleColors.Label(x, false), body) >= 4.4, $"label({x:F2})");
        }

        Assert.Equal(StyleColors.Heat(0.3), StyleColors.Label(0.3, true));
    }

    [Fact]
    public void StaticPaletteMatchesTheSpec()
    {
        var dark = NeedlePalette.DarkPalette;
        var light = NeedlePalette.LightPalette;
        Assert.Equal(Rgba.FromHex("#F43F5E"), dark.RedZone);
        Assert.Equal(Rgba.FromHex("#E11D48"), light.RedZone);
        Assert.Equal(0.8, light.RedZoneBaseOpacity);
        Assert.Equal(0.028, dark.DialBand.A, 9);
        Assert.Equal(0.035, light.DialBand.A, 9);
        Assert.Equal(Rgba.FromHex("#F59E0B"), light.LampFlash);
        Assert.Equal(OkLab.Darken(HeatPalette.Sample(0.7), 0.15), light.Lamp(0));
        Assert.Equal(HeatPalette.Sample(StyleMath.RedlineDial), dark.Lamp(4));
        Assert.Equal(0.8415, StyleMath.RedlineDial, 4);
        Assert.Equal(Rgba.FromHex("#9EA8BA"), dark.NeedleIdle);
        AssertColor(HeatPalette.WhiteHot, dark.Spark(0));
        AssertColor(HeatPalette.Sample(0.95), dark.Spark(1));
        AssertColor(HeatPalette.LightSparkHot, light.Spark(0));
        AssertColor(StyleColors.Label(0.95, false), light.Spark(1));
    }

    internal static void AssertColor(Rgba expected, Rgba actual, int precision = 6)
    {
        Assert.Equal(expected.R, actual.R, precision);
        Assert.Equal(expected.G, actual.G, precision);
        Assert.Equal(expected.B, actual.B, precision);
        Assert.Equal(expected.A, actual.A, precision);
    }
}

/// <summary>
/// Feeds an <see cref="IOrbStyleModel"/> the frames <see cref="OrbScene"/> would: raw heat from the value, smoothed with
/// the 0.6 s critically damped spring, and the raw tier (or a hysteresis tracker / an override).
/// </summary>
internal sealed class OrbStyleDriver
{
    private static readonly double[,] Tour =
    {
        { 0, 0 }, { 4, 3 }, { 7, 12 }, { 10, 30 }, { 13, 62 }, { 16, 110 }, { 19, 180 }, { 22, 260 }, { 25, 330 }, { 28, 430 },
        { 31, 580 }, { 34, 780 }, { 38, 620 }, { 41, 240 }, { 44, 60 }, { 47, 8 }, { 49, 0 }, { 62, 0 },
    };

    private readonly TierTracker _tracker = new();
    private CriticalSpring _heat = new(0.6);
    private CriticalSpring _overdrive = new(0.6);

    public OrbStyleDriver(IOrbStyleModel model, EffectsIntensity level = EffectsIntensity.Standard, double size = 148,
        bool dark = true, bool reduceMotion = false, bool highContrast = false)
    {
        Model = model;
        Effects = new EffectsPolicy(level, reduceMotion, highContrast);
        Size = size;
        Dark = dark;
    }

    public IOrbStyleModel Model { get; }

    public EffectsPolicy Effects { get; set; }

    public double Size { get; set; }

    public bool Dark { get; set; }

    public double Redline { get; set; } = HeatMath.ReferenceRedline;

    public double Time { get; private set; }

    public double Value { get; set; }

    /// <summary>Overrides the tier; null derives it from the value.</summary>
    public SpeedTier? Tier { get; set; }

    /// <summary>Derive the tier with the real hysteresis and 0.8 s hold.</summary>
    public bool TrackTiers { get; set; }

    public double Flash { get; set; }

    public double? SignOfLifeAge { get; set; }

    public bool Unavailable { get; set; }

    public OrbFrame Frame { get; private set; }

    public static double ValueForDial(double n, double redline = HeatMath.ReferenceRedline) => redline * Math.Pow(n, 1 / 0.6);

    /// <summary>The prototype's tour (engine.js <c>TOUR</c>), sampled every 0.5 s like the engine.</summary>
    public static double TourValue(double time)
    {
        var t = Math.Floor(time % 62 * 2) / 2;
        var rows = Tour.GetLength(0);
        var v = Tour[rows - 1, 1];
        for (var i = 0; i < rows - 1; i++)
        {
            if (t >= Tour[i, 0] && t < Tour[i + 1, 0])
            {
                var ramp = Math.Min(1.2, Tour[i + 1, 0] - Tour[i, 0]);
                var previous = i > 0 ? Tour[i - 1, 1] : Tour[i, 1];
                var current = Tour[i, 1];
                var u = Math.Min(1, (t - Tour[i, 0]) / ramp);
                var e = u * u * (3 - 2 * u);
                v = previous > 0 && current > 0
                    ? Math.Exp(Math.Log(previous) + (Math.Log(current) - Math.Log(previous)) * e)
                    : previous + (current - previous) * e;
                break;
            }
        }

        if (v <= 0)
        {
            return 0;
        }

        var noise = 0.035 * Math.Sin(t * 2.1) + 0.025 * Math.Sin(t * 5.3 + 1) + 0.015 * Math.Sin(t * 11.7 + 2);
        return v * (1 + noise);
    }

    public void Snap()
    {
        _heat.Snap(HeatMath.Heat(Value, Redline));
        _overdrive.Snap(HeatMath.Overdrive(Value, Redline));
        if (TrackTiers)
        {
            _tracker.Reset(HeatMath.RawTier(Value, Redline));
        }

        Frame = Build(0);
        Model.Snap(Frame);
    }

    public void Step(double dt)
    {
        Time += dt;
        Frame = Build(dt);
        Model.Update(Frame);
    }

    public void Run(double seconds, double fps = 60)
    {
        var frames = (int)Math.Round(seconds * fps);
        for (var i = 0; i < frames; i++)
        {
            Step(1 / fps);
        }
    }

    private OrbFrame Build(double dt)
    {
        var heatRaw = HeatMath.Heat(Value, Redline);
        var overdriveRaw = HeatMath.Overdrive(Value, Redline);
        _heat.Step(heatRaw, dt);
        _overdrive.Step(overdriveRaw, dt);
        SpeedTier tier;
        if (Tier is { } fixedTier)
        {
            tier = fixedTier;
        }
        else if (TrackTiers)
        {
            _tracker.Update(Value, Redline, Time);
            tier = _tracker.Displayed;
        }
        else
        {
            tier = HeatMath.RawTier(Value, Redline);
        }

        return new OrbFrame(Time, dt, Math.Clamp(_heat.Value, 0, 1), Math.Clamp(_overdrive.Value, 0, 1), heatRaw, overdriveRaw,
            tier, Size, Effects, Dark, Flash, SignOfLifeAge, Unavailable);
    }
}
