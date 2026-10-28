using System.Diagnostics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class OrbReactorModelTests
{
    private static double Sink;

    private static (ReactorModel Model, EnergyOrbDriver Driver) Create(double ratio = 0, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, double size = 148, bool dark = true, int seed = 7)
    {
        var model = new ReactorModel(seed);
        var driver = new EnergyOrbDriver(model) { Size = size, Dark = dark }.Speed(ratio).Level(level, reduceMotion);
        driver.Snap();
        return (model, driver);
    }

    private static double H(double ratio) => Math.Min(1, Math.Pow(ratio, 0.6));

    private static double O(double ratio) => Math.Clamp(ratio - 1, 0, 1);

    private static void AssertColor(Rgba expected, Rgba actual)
    {
        Assert.Equal(expected.R, actual.R, 6);
        Assert.Equal(expected.G, actual.G, 6);
        Assert.Equal(expected.B, actual.B, 6);
        Assert.Equal(expected.A, actual.A, 6);
    }

    private static (double A, double B, double C) Angles(ReactorModel model) =>
        (model.RotorADegrees, model.RotorBDegrees, model.RotorCDegrees);

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RotorSpeedsFollowHeatAndOverdrive(double ratio)
    {
        var (model, _) = Create(ratio);
        var hp = Math.Pow(H(ratio), 1.3);
        var o = O(ratio);
        Assert.Equal(OrbStyle.Reactor, model.Style);
        Assert.Equal(0.03 + 0.45 * hp + 0.35 * o, model.RotorASpeed, 9);
        Assert.Equal(-(0.02 + 0.30 * hp + 0.25 * o), model.RotorBSpeed, 9);
        Assert.Equal(0.05 + 1.1 * hp + 0.7 * o, model.RotorCSpeed, 9);
    }

    [Fact]
    public void RotorCTurnsAboutOnePointTwoAtTheRedlineAndOnePointNineAtFullOverdrive()
    {
        Assert.InRange(Create(1.0).Model.RotorCSpeed, 1.1, 1.25);
        Assert.InRange(Create(2.0).Model.RotorCSpeed, 1.8, 1.95);
    }

    [Fact]
    public void RotorsIntegrateTheirSpeeds()
    {
        var (model, driver) = Create(1.0, EffectsIntensity.Soft);
        var start = Angles(model);
        driver.Run(1);
        Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorADegrees, start.A + 360 * 0.48), 6);
        Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorBDegrees, start.B - 360 * 0.32), 6);
        Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorCDegrees, start.C + 360 * 1.15), 6);
    }

    [Fact]
    public void IdleRotorsStopWhereTheyWereWithMinimumArcsInSlate()
    {
        var (model, driver) = Create(0.5);
        driver.Run(1);
        driver.Speed(0);
        driver.Run(2);
        var rest = Angles(model);
        driver.Run(1);

        Assert.InRange(Math.Abs(EnergyOrbDriver.AngleDelta(model.RotorADegrees, rest.A)), 0, 1e-3);
        Assert.InRange(Math.Abs(EnergyOrbDriver.AngleDelta(model.RotorCDegrees, rest.C)), 0, 1e-3);
        Assert.Equal(20, model.ArcADegrees, 9);
        Assert.Equal(14, model.ArcCDegrees, 9);
        Assert.Equal(0, model.CoronaOpacity, 6);
        Assert.Equal(0.024 * 0.6, model.ArcAWidth, 9);
        Assert.Equal(0.38 * 0.55, model.ArcAHeadColor.A, 9);
        Assert.Equal(0.38 * 0.55 * 0.25, model.ArcATailColor.A, 9);
        Assert.Equal(0.38 * 0.5, model.ArcCHeadColor.A, 9);
        Assert.Equal(0, model.ArcAGlowColor.A, 9);
        Assert.Equal(0.07 * 2.2, model.RimColor.A, 9);
        foreach (var opacity in model.CoilOpacities)
        {
            Assert.Equal(0.25 * 0.38 * 0.8, opacity, 9);
        }

        Assert.Equal(FrameClass.None, model.Demand);
        Assert.Equal(0, model.RotorASpeed, 6);
    }

    [Fact]
    public void SignOfLifeRunsALightBandOnceAroundRotorB()
    {
        var (model, driver) = Create();
        driver.SignOfLifeAge = 0;
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.SignOfLifeAge = 0.45;
        driver.Step(1 / 60.0);

        var brightest = 0;
        for (var i = 1; i < model.CoilCount; i++)
        {
            brightest = model.CoilOpacities[i] > model.CoilOpacities[brightest] ? i : brightest;
        }

        Assert.Equal(12, brightest);
        Assert.Equal((0.25 + 0.6) * 0.38 * 0.8, model.CoilOpacities[12], 9);
        Assert.Equal(0, model.CoilHotness[12], 9);

        driver.SignOfLifeAge = 1.0;
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.None, model.Demand);
        Assert.Equal(0.25 * 0.38 * 0.8, model.CoilOpacities[12], 9);
    }

    [Theory]
    [InlineData(0, 20, 14)]
    [InlineData(0.2, 0, 0)]
    [InlineData(1.0, 110, 144)]
    [InlineData(1.5, 115, 154)]
    [InlineData(2.0, 120, 164)]
    public void ArcLengthsGrowAndRotorACloses(double ratio, double a, double c)
    {
        var (model, _) = Create(ratio);
        if (a == 0)
        {
            a = 20 + 90 * H(ratio);
            c = 14 + 130 * H(ratio);
        }

        Assert.Equal(a, model.ArcADegrees, 9);
        Assert.Equal(c, model.ArcCDegrees, 9);
        Assert.Equal((int)Math.Round(a), model.ArcABucket);
        Assert.Equal((int)Math.Round(c), model.ArcCBucket);
        Assert.True(model.ArcADegrees <= 120 && model.ArcCDegrees <= 175);
    }

    [Fact]
    public void PulseKicksTheRotorsAndFlaresTheCore()
    {
        var (model, driver) = Create(0.5);
        var (twin, twinDriver) = Create(0.5);
        var corona = model.CoronaOpacity;
        model.Pulse(0.8, 1);
        Assert.Equal(0.25, model.Kick, 9);

        driver.Step(1 / 60.0);
        twinDriver.Step(1 / 60.0);
        Assert.True(model.FlareVisible);
        Assert.InRange(model.CoronaOpacity - twin.CoronaOpacity, 0.2, 0.25);
        Assert.InRange(model.FlareRadius, 0.30, 0.34);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        Assert.True(corona > 0);

        var radius = model.FlareRadius;
        var growing = true;
        driver.Watch(0.25, () =>
        {
            growing &= model.FlareRadius > radius;
            radius = model.FlareRadius;
        });
        twinDriver.Run(0.25);
        Assert.True(growing);
        Assert.InRange(radius, 0.49, 0.5);
        driver.Run(0.1);
        twinDriver.Run(0.1);
        Assert.False(model.FlareVisible);
        Assert.Equal(0, model.FlareColor.A);

        driver.Run(2.633);
        twinDriver.Run(2.633);
        Assert.Equal(0.25 * Math.Exp(-3 / 0.5), model.Kick, 4);

        // ∫ k dt = 0.25 × 0.5 (1 − e^−6) turns, × 1, −0.6, 1.2 for the three rotors.
        var turns = 0.25 * 0.5 * (1 - Math.Exp(-6));
        Assert.InRange(EnergyOrbDriver.AngleDelta(model.RotorADegrees, twin.RotorADegrees) - 360 * turns, -0.01, 0.01);
        Assert.InRange(EnergyOrbDriver.AngleDelta(model.RotorBDegrees, twin.RotorBDegrees) + 0.6 * 360 * turns, -0.01, 0.01);
        Assert.InRange(EnergyOrbDriver.AngleDelta(model.RotorCDegrees, twin.RotorCDegrees) - 1.2 * 360 * turns, -0.01, 0.01);
    }

    [Fact]
    public void CoilEnergiesFollowTheBandFormula()
    {
        var (model, driver) = Create(0.5);
        var h = H(0.5);
        var start = model.BandPhase;
        driver.Run(1);
        Assert.Equal(2 * Math.PI * (0.15 + 1.4 * h), model.BandPhase - start, 6);
        Assert.Equal(24, model.CoilEnergies.Length);
        for (var i = 0; i < 24; i++)
        {
            var band = Math.Pow(0.5 + 0.5 * Math.Cos(3 * 2 * Math.PI * i / 24 - model.BandPhase), 2.2);
            var e = 0.18 + 0.82 * band * (0.35 + 0.65 * h);
            Assert.Equal(e, model.CoilEnergies[i], 9);
            Assert.Equal(Math.Clamp(e, 0, 1), model.CoilOpacities[i], 9);
            Assert.Equal(Math.Clamp((e - 0.75) * 3, 0, 1), model.CoilHotness[i], 9);
        }

        Assert.Equal(model.RotorBDegrees, model.CoilAngleDegrees(0), 9);
        Assert.Equal(ReactorModel.Normalize(model.RotorBDegrees + 15), model.CoilAngleDegrees(1), 9);
    }

    [Fact]
    public void OffHoldsStillRotorsUniformCoilsAndASteadyCorona()
    {
        var (model, driver) = Create(1.0, EffectsIntensity.Off);
        var start = Angles(model);
        var corona = model.CoronaOpacity;
        model.Pulse(0.9, 1);
        model.Peak();
        driver.Run(2);

        Assert.Equal(start, Angles(model));
        Assert.Equal(0, model.Kick);
        Assert.Equal(0, model.LockIntensity);
        Assert.Equal(corona, model.CoronaOpacity, 12);
        Assert.Equal(0.10 + 0.42, corona, 9);
        Assert.Empty(model.Bolts.ToArray());
        var first = model.CoilEnergies[0];
        foreach (var e in model.CoilEnergies)
        {
            Assert.Equal(first, e, 12);
        }

        Assert.Equal(110, model.ArcADegrees, 9);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void ReduceMotionFreezesTheRotorsAndCrossfadesTheLock()
    {
        var (model, driver) = Create(1.0, EffectsIntensity.Max, reduceMotion: true);
        var start = Angles(model);
        var band = model.BandPhase;
        model.Pulse(0.9, 1);
        driver.Run(1);
        Assert.Equal(start, Angles(model));
        Assert.Equal(band, model.BandPhase);
        Assert.Equal(0, model.Kick);
        Assert.Equal(0, model.EmittedBolts);
        Assert.Equal(FrameClass.None, model.Demand);

        model.Peak();
        var peak = 0.0;
        driver.Watch(0.25, () =>
        {
            peak = Math.Max(peak, model.LockIntensity);
            Assert.Equal(FrameClass.Fps30, model.Demand);
        });

        Assert.InRange(peak, 0.99, 1);
        Assert.Equal(start, Angles(model));
        driver.Run(0.1);
        Assert.Equal(0, model.LockIntensity);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, false, 1.0, true)]
    [InlineData(EffectsIntensity.Max, false, 2.0, true)]
    [InlineData(EffectsIntensity.Standard, false, 0.6, false)]
    [InlineData(EffectsIntensity.Soft, false, 2.0, false)]
    [InlineData(EffectsIntensity.Off, false, 2.0, false)]
    [InlineData(EffectsIntensity.Max, true, 2.0, false)]
    public void LightningStrikesOnlyAtTheRedlineStandardAndUp(EffectsIntensity level, bool reduceMotion, double ratio, bool strikes)
    {
        var (model, driver) = Create(ratio, level, reduceMotion);
        driver.Run(5);
        Assert.Equal(strikes, model.EmittedBolts > 0);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, 1.0, 1.5, 3)]
    [InlineData(EffectsIntensity.Standard, 2.0, 7.5, 3)]
    [InlineData(EffectsIntensity.Max, 1.0, 2.4, 4)]
    [InlineData(EffectsIntensity.Max, 2.0, 12, 4)]
    public void LightningRatesAndAliveCaps(EffectsIntensity level, double ratio, double rate, int cap)
    {
        var (model, driver) = Create(ratio, level);
        Assert.Equal(cap, model.BoltCap);
        var most = 0;
        var sparks = 0;
        driver.Watch(20, () =>
        {
            most = Math.Max(most, model.Bolts.Length);
            sparks = Math.Max(sparks, model.Sparks.Length);
        });

        Assert.InRange(model.EmittedBolts, (long)(rate * 20) - 2, (long)(rate * 20) + 1);
        Assert.InRange(most, 1, cap);
        Assert.InRange(sparks, ReactorModel.SparksPerBolt, model.SparkCap);
    }

    [Fact]
    public void AliveCapHoldsEvenWhenFramesAreLong()
    {
        foreach (var level in new[] { EffectsIntensity.Standard, EffectsIntensity.Max })
        {
            var (model, driver) = Create(2.0, level);
            var most = 0;
            driver.Watch(10, () => most = Math.Max(most, model.Bolts.Length), fps: 10);
            Assert.InRange(most, 1, model.BoltCap);
        }
    }

    [Fact]
    public void CoronaFlashesAreLimitedToThreePerSecond()
    {
        var (model, driver) = Create(2.0, EffectsIntensity.Max);
        var times = new List<double>();
        var seen = model.BoltFlashes;
        driver.Watch(20, () =>
        {
            for (; seen < model.BoltFlashes; seen++)
            {
                times.Add(driver.Time);
            }
        });

        Assert.True(model.EmittedBolts > 200);
        Assert.InRange(times.Count, 50, 63);
        for (var i = 0; i + 3 < times.Count; i++)
        {
            Assert.True(times[i + 3] - times[i] >= 1 - 1e-9, $"four flashes within {times[i + 3] - times[i]:F3} s");
        }
    }

    [Fact]
    public void BoltsAreJitteredNinePointPolylinesRefreshedEvery50Ms()
    {
        var (model, driver) = Create(2.0);
        var checkedBolts = 0;
        var versions = new Dictionary<int, (int Version, int Frame, ReactorPoint Middle)>();
        var frame = 0;
        driver.Watch(10, () =>
        {
            frame++;
            foreach (var bolt in model.Bolts)
            {
                var path = model.BoltPath(bolt);
                Assert.Equal(ReactorModel.BoltPointCount, path.Length);
                var r0 = Math.Sqrt(path[0].X * path[0].X + path[0].Y * path[0].Y);
                var r1 = Math.Sqrt(path[8].X * path[8].X + path[8].Y * path[8].Y);
                var theta0 = Math.Atan2(path[0].Y, path[0].X);
                var span = Math.IEEERemainder(Math.Atan2(path[8].Y, path[8].X) - theta0, 2 * Math.PI);
                if (bolt.Outward)
                {
                    Assert.Equal(0.437, r0, 9);
                    Assert.InRange(r1, 0.56, 0.66);
                    Assert.InRange(Math.Abs(span), 0, 0.3 + 1e-9);
                }
                else
                {
                    Assert.Equal(0.306, r0, 9);
                    Assert.Equal(0.425, r1, 9);
                    Assert.InRange(Math.Abs(span), 0, 0.5 + 1e-9);
                }

                for (var i = 1; i < 8; i++)
                {
                    var u = i / 8.0;
                    var theta = theta0 + span * u;
                    var r = r0 + (r1 - r0) * u;
                    var dx = path[i].X - r * Math.Cos(theta);
                    var dy = path[i].Y - r * Math.Sin(theta);
                    Assert.True(Math.Sqrt(dx * dx + dy * dy) <= 0.048 * Math.Sin(Math.PI * u) + 1e-9);
                }

                Assert.InRange(bolt.Alpha, 0, 1);
                if (versions.TryGetValue(bolt.PathOffset, out var last) && last.Frame == frame - 1)
                {
                    if (last.Version == bolt.Version)
                    {
                        Assert.Equal(last.Middle, path[4]);
                    }
                    else
                    {
                        Assert.NotEqual(last.Middle, path[4]);
                    }
                }

                versions[bolt.PathOffset] = (bolt.Version, frame, path[4]);
                checkedBolts++;
            }
        });

        Assert.True(checkedBolts > 100);
        Assert.InRange((double)model.OutwardBolts / model.EmittedBolts, 0.25, 0.55);

        var (calm, calmDriver) = Create(1.25);
        calmDriver.Run(10);
        Assert.True(calm.EmittedBolts > 10);
        Assert.Equal(0, calm.OutwardBolts);
    }

    [Fact]
    public void BoltsHoldFullBrightnessForAFifthOfTheirLifeThenFade()
    {
        var (model, driver) = Create(1.0);
        while (model.Bolts.Length == 0)
        {
            driver.Step(1 / 60.0);
        }

        var offset = model.Bolts[0].PathOffset;
        var alphas = new List<double> { model.Bolts[0].Alpha };
        while (true)
        {
            driver.Step(1 / 60.0);
            var found = false;
            foreach (var bolt in model.Bolts)
            {
                if (bolt.PathOffset == offset)
                {
                    alphas.Add(bolt.Alpha);
                    found = true;
                }
            }

            if (!found)
            {
                break;
            }
        }

        Assert.Equal(1, alphas[0]);
        Assert.InRange(alphas.Count, 5, 12);
        for (var i = 1; i < alphas.Count; i++)
        {
            Assert.True(alphas[i] <= alphas[i - 1]);
        }
    }

    [Fact]
    public void SparksFlyFromTheFarEndAndDie()
    {
        var (model, driver) = Create(1.0);
        while (model.Bolts.Length == 0)
        {
            driver.Step(1 / 60.0);
        }

        Assert.Equal(ReactorModel.SparksPerBolt, model.Sparks.Length);
        var path = model.BoltPath(model.Bolts[0]);
        foreach (var spark in model.Sparks)
        {
            var dx = spark.X - path[8].X;
            var dy = spark.Y - path[8].Y;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= 0.6 / 60 + 1e-9);
            Assert.InRange(spark.Alpha, 0, 1);
        }

        driver.Speed(0.6);
        driver.Run(0.42);
        Assert.Equal(0, model.Sparks.Length);
        Assert.Equal(0, model.Bolts.Length);
    }

    [Fact]
    public void NewPeakLocksEachRotorAtTwelveOClock()
    {
        var (model, driver) = Create(0.5);
        driver.Run(0.3);
        var from = Angles(model);
        var to = (A: ReactorModel.LockTarget(from.A, 120, true), B: ReactorModel.LockTarget(from.B, 15, false),
            C: ReactorModel.LockTarget(from.C, 180, true));
        Assert.InRange(to.A - from.A, 0, 120);
        Assert.InRange(from.B - to.B, 0, 15);
        Assert.InRange(to.C - from.C, 0, 180);

        Assert.Equal(0, (to.A + 90) % 120, 9);
        Assert.Equal(0, (to.B + 90) % 15, 9);
        Assert.Equal(0, (to.C + 90) % 180, 9);

        model.Peak();
        var age = 0.0;
        var overshoot = 0.0;
        var gold = 0.0;
        while (age + 1 / 60.0 < ReactorModel.LockEnd)
        {
            driver.Step(1 / 60.0);
            age += 1 / 60.0;
            Assert.Equal(FrameClass.Fps60, model.Demand);
            gold = Math.Max(gold, model.LockIntensity);
            overshoot = Math.Max(overshoot, EnergyOrbDriver.AngleDelta(model.RotorADegrees, from.A) / (to.A - from.A));
            if (age < 0.42 * 0.65 - 1e-9)
            {
                Assert.Equal(0, model.LockIntensity);
            }

            if (age >= 0.42)
            {
                // Holding the lock pose: arc heads at 12 o'clock, all three rotors flashing gold.
                Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorADegrees, to.A), 6);
                Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorBDegrees, to.B), 6);
                Assert.Equal(0, EnergyOrbDriver.AngleDelta(model.RotorCDegrees, to.C), 6);
                Assert.True(model.Locking);
                Assert.True(model.CoilEnergies[3] >= model.LockIntensity - 1e-12);
            }
        }

        Assert.InRange(gold, 0.95, 1);
        if (to.A - from.A > 5)
        {
            // easeOutBack with overshoot 1.2 swings about 5 % past the pose.
            Assert.InRange(overshoot, 1.03, 1.06);
        }

        driver.Step(1 / 60.0);
        Assert.False(model.Locking);
        Assert.Equal(0, model.LockIntensity);
        Assert.InRange(EnergyOrbDriver.AngleDelta(model.RotorCDegrees, to.C), 0.1, 360 * model.RotorCSpeed / 60 + 1e-6);
    }

    [Fact]
    public void LockTargetsLieAheadInTheDirectionOfTravel()
    {
        Assert.Equal(30, ReactorModel.LockTarget(10, 120, clockwise: true), 9);
        Assert.Equal(150, ReactorModel.LockTarget(31, 120, clockwise: true), 9);
        Assert.Equal(30, ReactorModel.LockTarget(30, 120, clockwise: true), 9);
        Assert.Equal(-90, ReactorModel.LockTarget(-80, 15, clockwise: false), 9);
        Assert.Equal(-105, ReactorModel.LockTarget(-91, 15, clockwise: false), 9);
        Assert.Equal(90, ReactorModel.LockTarget(-89, 180, clockwise: true), 9);
    }

    [Fact]
    public void CoronaBreathesWithHeatAndScalesAtMax()
    {
        var h = H(0.6);
        foreach (var (level, scale) in new[] { (EffectsIntensity.Standard, 1.0), (EffectsIntensity.Max, 1.2) })
        {
            var (model, driver) = Create(0.6, level);
            var low = 1.0;
            var high = 0.0;
            driver.Watch(3, () =>
            {
                low = Math.Min(low, model.CoronaOpacity);
                high = Math.Max(high, model.CoronaOpacity);
            });

            var baseline = (0.10 + 0.42 * h) * scale;
            Assert.InRange(low, baseline * 0.85 - 0.003, baseline * 0.85 + 0.003);
            Assert.InRange(high, baseline * 1.15 - 0.003, baseline * 1.15 + 0.003);
        }

        var (light, _) = Create(0.6, EffectsIntensity.Off, dark: false);
        Assert.Equal(0.06 + 0.2 * h, light.CoronaOpacity, 9);
    }

    [Fact]
    public void ColorsFollowHeatOverdriveAndAppearance()
    {
        var (dark, darkDriver) = Create(1.0);
        AssertColor(EnergyPalette.Hot(1, 0, true).WithAlpha(1), dark.ArcAHeadColor);
        AssertColor(HeatPalette.Sample(1).WithAlpha(0.25), dark.ArcATailColor);
        Assert.Equal(0.95, dark.ArcCHeadColor.A, 9);
        Assert.Equal(1.0, dark.RimColor.A, 9);
        Assert.Equal(0.7, dark.ArcAGlowColor.A, 9);
        Assert.Equal(0.015, dark.ArcAGlowSigma, 9);
        Assert.Equal(HeatPalette.Sample(0.92), dark.BoltColor);
        Assert.Equal(HeatPalette.WhiteHot, dark.BoltCoreColor);
        var key = dark.ColorKey;
        darkDriver.Run(1);
        Assert.Equal(key, dark.ColorKey);

        var (light, _) = Create(0.5, dark: false);
        var hq = Math.Round(H(0.5) * 128) / 128;
        Assert.Equal(0.5, light.ArcATailColor.A, 9);
        AssertColor(EnergyPalette.Label(hq, false).WithAlpha(0.5), light.ArcATailColor);
        Assert.Equal(0.6 + 0.4 * hq, light.RimColor.A, 9);
        Assert.Equal(0, light.ArcAGlowColor.A, 9);
        Assert.Equal(HeatPalette.LightSparkHot, light.BoltCoreColor);
        Assert.True(Rgba.ContrastRatio(light.HeatColor, Rgba.FromHex("#E6E6E6")) >= 4.5);

        var (max, _) = Create(1.0, EffectsIntensity.Max);
        Assert.Equal(0.015 * 1.3, max.ArcAGlowSigma, 9);

        var (flash, flashDriver) = Create(1.0);
        flashDriver.Flash = 1;
        flashDriver.Step(1 / 60.0);
        Assert.True(OkLab.FromRgb(flash.ArcAHeadColor).L > OkLab.FromRgb(dark.ArcAHeadColor).L);
        Assert.NotEqual(key, flash.ColorKey);
    }

    [Fact]
    public void SmallOrbsUseTwelveCoilsAndFourNotches()
    {
        var (small, _) = Create(0.5, size: 100);
        Assert.Equal(12, small.CoilCount);
        Assert.Equal(13, small.CoilArcDegrees);
        Assert.Equal(4, small.NotchCount);
        Assert.Equal(12, small.CoilOpacities.Length);
        Assert.Equal(small.UnlitColor.A * 2.6, small.NotchColor(1).A, 9);

        var (large, _) = Create(0.5);
        Assert.Equal(24, large.CoilCount);
        Assert.Equal(7.5, large.CoilArcDegrees);
        Assert.Equal(24, large.NotchCount);
        Assert.Equal(large.UnlitColor.A * 1.4, large.NotchColor(1).A, 9);
        Assert.Equal(large.UnlitColor.A * 2.6, large.NotchColor(6).A, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, false, 0, FrameClass.None)]
    [InlineData(EffectsIntensity.Standard, false, 0.05, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Standard, false, 0.2, FrameClass.Fps30)]
    [InlineData(EffectsIntensity.Standard, false, 0.5, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Standard, false, 1.0, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Soft, false, 1.0, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Max, false, 2.0, FrameClass.Fps60)]
    [InlineData(EffectsIntensity.Off, false, 1.0, FrameClass.None)]
    [InlineData(EffectsIntensity.Standard, true, 1.0, FrameClass.None)]
    public void DemandFollowsTheFrameTable(EffectsIntensity level, bool reduceMotion, double ratio, FrameClass expected)
    {
        var (model, driver) = Create(ratio, level, reduceMotion);
        driver.Run(0.5);
        Assert.Equal(expected, model.Demand);
    }

    [Fact]
    public void TransitionsRaiseTheDemand()
    {
        var (model, driver) = Create(0.05);
        model.Pulse(0.2, 1);
        driver.Run(1);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(1);
        Assert.Equal(FrameClass.Fps30, model.Demand);

        model.Peak();
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.Fps60, model.Demand);
        driver.Run(1);
        driver.Flash = 0.3;
        driver.Step(1 / 60.0);
        Assert.Equal(FrameClass.Fps60, model.Demand);

        var (off, offDriver) = Create(0.2, EffectsIntensity.Off);
        offDriver.Heat += 0.01;
        offDriver.Step(1 / 60.0);
        Assert.Equal(FrameClass.Fps30, off.Demand);
        offDriver.Step(1 / 60.0);
        Assert.Equal(FrameClass.None, off.Demand);
    }

    [Fact]
    public void SnapEndsTheLockAndClearsLightningWithoutCatchUp()
    {
        var (model, driver) = Create(2.0);
        driver.Run(2);
        model.Pulse(0.9, 1);
        var from = Angles(model);
        model.Peak();
        driver.Run(0.1);
        Assert.True(model.Locking);

        driver.Snap();
        Assert.False(model.Locking);
        Assert.Equal(0, model.LockIntensity);
        Assert.Equal(0, model.Kick);
        Assert.Equal(0, model.Bolts.Length);
        Assert.Equal(0, model.Sparks.Length);
        Assert.False(model.FlareVisible);
        Assert.Equal(ReactorModel.Normalize(ReactorModel.LockTarget(from.A, 120, true)), model.RotorADegrees, 9);
    }

    [Fact]
    public void RotationIsIndependentOfTheFrameRate()
    {
        var results = new List<(double A, double B, double C, double Band)>();
        foreach (var cadence in EnergyOrbDriver.Cadences)
        {
            var model = new ReactorModel(9);
            EnergyOrbDriver.Climb(new EnergyOrbDriver(model), cadence, SpeedTier.Overdrive);
            results.Add((model.RotorADegrees, model.RotorBDegrees, model.RotorCDegrees, model.BandPhase));
        }

        foreach (var result in results)
        {
            Assert.InRange(Math.Abs(EnergyOrbDriver.AngleDelta(result.A, results[0].A)), 0, 0.2);
            Assert.InRange(Math.Abs(EnergyOrbDriver.AngleDelta(result.B, results[0].B)), 0, 0.2);
            Assert.InRange(Math.Abs(EnergyOrbDriver.AngleDelta(result.C, results[0].C)), 0, 0.2);
            Assert.InRange(Math.Abs(result.Band - results[0].Band), 0, 0.005);
        }
    }

    [Fact]
    public void UpdatesAllocateNothing()
    {
        var (model, driver) = Create(2.0, EffectsIntensity.Max, size: 240);
        for (var i = 0; i < 1500; i++)
        {
            Exercise(model, driver, i);
        }

        // A per-frame allocation shows up in every round; a one-time runtime allocation (tiering) in one at most.
        var rounds = new long[5];
        for (var round = 0; round < rounds.Length; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1500; i++)
            {
                Exercise(model, driver, i);
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
        for (var i = 0; i < 300; i++)
        {
            Exercise(model, driver, i);
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Exercise(model, driver, i);
        }

        watch.Stop();
        Assert.True(watch.Elapsed.TotalMilliseconds < 250, $"1000 updates took {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static void Exercise(ReactorModel model, EnergyOrbDriver driver, int i)
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
        var sum = model.CoronaOpacity + model.RotorADegrees;
        foreach (var opacity in model.CoilOpacities)
        {
            sum += opacity;
        }

        foreach (var bolt in model.Bolts)
        {
            sum += model.BoltPath(bolt)[4].X;
        }

        foreach (var spark in model.Sparks)
        {
            sum += spark.Alpha;
        }

        Sink += sum;
    }
}
