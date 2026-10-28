using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The floating <c>led</c>'s v1.4 effect state (DESIGN 2.1): phases, gates, particles, arcs and digit lightning.</summary>
public class LedEffectsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0)]
    [InlineData(1, 0.4)]
    [InlineData(1, 1)]
    public void PhasesAdvanceAtTheirRates(double h, double o)
    {
        var phases = new LedPhases();
        for (var i = 0; i < 60; i++)
        {
            phases.Advance(1 / 60.0, h, o, 1, motion: true);
        }

        Assert.Equal(0.12 + 1.4 * h + 2.2 * o, phases.Silk, 9);
        Assert.Equal(0.05 + 0.5 * h + 1.2 * o, phases.Light, 9);
        Assert.Equal((0.35 + 1.9 * h + 1.6 * o) % 1, phases.Flow, 9);
        Assert.Equal(0.2 + 1.1 * h + 1.2 * o, phases.Warp, 9);
        Assert.Equal(0.5 + 1.7 * h + 1.8 * o, phases.Corona, 9);
        Assert.Equal(1, phases.Clock, 9);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0.2)]
    [InlineData(1.0 / 15)]
    public void AFrameAdvancesTheClockByAtMostAFifteenthOfASecond(double dt)
    {
        var phases = new LedPhases();
        phases.Advance(dt, 1, 1, 1, motion: true);
        Assert.Equal(LedPhases.SilkRate(1, 1) / 15, phases.Silk, 9);
        Assert.Equal(LedPhases.CoronaRate(1, 1) / 15, phases.Corona, 9);
        Assert.Equal(1.0 / 15, phases.Clock, 9);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NoTimeOrBadTimeLeavesThePhasesAlone(double dt)
    {
        var phases = new LedPhases();
        phases.Advance(dt, 1, 1, 1, motion: true);
        Assert.Equal(default, phases);
    }

    [Fact]
    public void SoftRunsTheSilkAtHalfRateAndFreezesTheRest()
    {
        var soft = new LedPhases();
        soft.Advance(0.05, 0.7, 0.2, 0.5, motion: false);
        Assert.Equal(LedPhases.SilkRate(0.7, 0.2) * 0.025, soft.Silk, 9);
        Assert.Equal(LedPhases.LightRate(0.7, 0.2) * 0.025, soft.Light, 9);
        Assert.Equal(0, soft.Flow);
        Assert.Equal(0, soft.Warp);
        Assert.Equal(0, soft.Corona);

        var frozen = new LedPhases();
        frozen.Advance(0.05, 0.7, 0.2, 0, motion: false);
        Assert.Equal(default, frozen);
    }

    [Fact]
    public void ShaderPhasesWrapWithoutLosingTheirFunctions()
    {
        var phases = new LedPhases { Silk = LedPhases.SilkPeriod * 3 + 1.25 };
        Assert.Equal(1.25, phases.SilkFolds, 6);
        Assert.Equal((LedPhases.SilkPeriod * 3 + 1.25) * 0.07 % LedPhases.NoisePeriod, phases.SilkNoise, 6);

        // The fold sines repeat after the period: every frequency is a multiple of 0.01.
        foreach (var k in new[] { 0.07, 0.3, 0.17, 0.2, 0.15, 0.1, 0.6, 0.35, 0.25, 0.8, 0.4 })
        {
            Assert.Equal(Math.Sin(k * 2), Math.Sin(k * (2 + LedPhases.SilkPeriod)), 6);
        }

        foreach (var k in new[] { 0.35, 0.8 })
        {
            Assert.Equal(Math.Sin(k * 3), Math.Sin(k * (3 + LedPhases.LightPeriod)), 6);
        }

        foreach (var k in new[] { 2.6, 16, 8 })
        {
            Assert.Equal(Math.Sin(k * 0.4), Math.Sin(k * (0.4 + LedPhases.ClockPeriod)), 6);
        }

        foreach (var k in new[] { 0.35, 0.5, 1 })
        {
            var periods = k * LedPhases.CoronaPeriod / LedPhases.NoisePeriod;
            Assert.Equal(Math.Round(periods), periods, 6);
        }
    }

    [Fact]
    public void LevelsGateTheEffects()
    {
        var off = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Off), 148);
        Assert.True(off.Silk);
        Assert.Equal(0, off.SilkRate);
        Assert.False(off.SilkWave || off.Ripples || off.Motion || off.Halo || off.Lightning || off.ChromaticSplit);

        var soft = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Soft), 148);
        Assert.Equal(0.5, soft.SilkRate);
        Assert.True(soft.SilkWave);
        Assert.False(soft.Ripples || soft.Motion || soft.Halo || soft.Lightning || soft.ChromaticSplit);
        Assert.Equal(1, soft.Level);

        var standard = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Standard), 148);
        Assert.Equal(1, standard.SilkRate);
        Assert.True(standard.Ripples && standard.SilkWave && standard.Motion && standard.Halo && standard.Lightning && standard.ChromaticSplit);
        Assert.Equal(1, standard.Level);
        Assert.Equal(1, standard.CountScale);

        var max = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Max), 148);
        Assert.Equal(1.6, max.Level);
        Assert.True(max.Lightning && max.Halo);
    }

    [Fact]
    public void ReduceMotionCapsAtSoftAndFreezesTheSilk()
    {
        var gates = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Max, ReduceMotion: true), 148);
        Assert.True(gates.Silk);
        Assert.Equal(0, gates.SilkRate);
        Assert.False(gates.SilkWave || gates.Ripples || gates.Motion || gates.Halo || gates.Lightning || gates.ChromaticSplit);
        Assert.Equal(1, gates.Level);
    }

    [Fact]
    public void HighContrastDrawsNoSilk()
    {
        var gates = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Max, HighContrast: true), 148);
        Assert.False(gates.Silk);
        Assert.False(gates.Motion || gates.Lightning);
    }

    [Theory]
    [InlineData(48, 0, false, 0.5)]
    [InlineData(96, 1, true, 0.5)]
    [InlineData(119, 1, true, 0.5)]
    [InlineData(120, 1, true, 1)]
    [InlineData(240, 1, true, 1)]
    public void SmallOrbsHalveTheCountsAndThumbnailsKeepAStaticSilk(double size, double silkRate, bool halo, double countScale)
    {
        var gates = LedEffectGates.For(new EffectsPolicy(EffectsIntensity.Standard), size);
        Assert.True(gates.Silk);
        Assert.Equal(silkRate, gates.SilkRate);
        Assert.Equal(halo, gates.Halo);
        Assert.Equal(countScale, gates.CountScale);
    }

    [Fact]
    public void SparkAndEmberRatesFollowThePrototype()
    {
        Assert.Equal(0, LedParticles.SparksPerSecond(0.44, 1, 1));
        Assert.Equal(120 * 1.6, LedParticles.SparksPerSecond(1, 1, 1), 9);
        Assert.Equal(120 * 0.5 * 1.6 * 1.6 * 0.5, LedParticles.SparksPerSecond(0.725, 1.6, 0.5), 9);
        Assert.Equal(0, LedParticles.EmbersPerSecond(false, 1, 1, 1));
        Assert.Equal(90 * 0.3 * 1.4, LedParticles.EmbersPerSecond(true, 0.1, 1, 1), 9);
        Assert.Equal(90 * 0.8 * 1.6 * 1.4 * 0.5, LedParticles.EmbersPerSecond(true, 0.8, 1.6, 0.5), 9);
    }

    [Fact]
    public void ThePoolNeverHoldsMoreThan640()
    {
        var particles = new LedParticles(3);
        for (var i = 0; i < 6; i++)
        {
            particles.Burst(LedParticles.BurstCount);
        }

        Assert.Equal(LedParticles.Capacity, particles.Count);
        for (var i = 0; i < 120; i++)
        {
            particles.Step(1 / 60.0, i / 60.0, 1, 1, 1000, 1000);
            Assert.InRange(particles.Count, 0, LedParticles.Capacity);
        }
    }

    [Fact]
    public void ParticlesDieByTheEdgeOfTheWindow()
    {
        var particles = new LedParticles(5);
        for (var i = 0; i < 300; i++)
        {
            particles.Step(1 / 60.0, i / 60.0, 1, 1, LedParticles.SparksPerSecond(1, 1.6, 1), LedParticles.EmbersPerSecond(true, 1, 1.6, 1));
            if (i % 50 == 0)
            {
                particles.Burst(LedParticles.BurstCount);
            }

            foreach (var p in particles.Particles)
            {
                Assert.True(p.X * p.X + p.Y * p.Y < LedParticles.Bound * LedParticles.Bound);
                Assert.InRange(p.Age, 0, p.Life);
            }
        }
    }

    [Fact]
    public void SparksLeaveTheLitArcAtTheGaugeRadius()
    {
        var particles = new LedParticles(9);
        particles.Step(0.05, 0, 0.8, 0, 200, 0);
        Assert.True(particles.Count >= 9);
        foreach (var p in particles.Particles)
        {
            Assert.Equal(LedParticleKind.Spark, p.Kind);
            Assert.InRange(p.Size, 1.2f, 2.8f);
            Assert.InRange(p.Heat, 0.1f, 0.9f + 1e-6f);
        }
    }

    [Fact]
    public void TheBurstIsAThirdGoldStreaksFromTheRim()
    {
        var particles = new LedParticles(11);
        particles.Burst(LedParticles.BurstCount);
        Assert.Equal(LedParticles.BurstCount, particles.Count);
        var gold = 0;
        foreach (var p in particles.Particles)
        {
            Assert.Equal(0.48, Math.Sqrt(p.X * p.X + p.Y * p.Y), 4);
            gold += p.Kind == LedParticleKind.Gold ? 1 : 0;
            Assert.True(p.Streak);
        }

        Assert.InRange(gold, 30, 70);
    }

    [Fact]
    public void ArcsSpawnOnlyWhileActiveAtTheirInterval()
    {
        Assert.Equal(0.28, LedArcs.Interval(0, 1), 9);
        Assert.Equal(0.08 / 1.6, LedArcs.Interval(1, 1.6), 9);
        var arcs = new LedArcs(7);
        for (var i = 0; i < 60; i++)
        {
            arcs.Step(1 / 60.0, false, 1, 1, 1);
        }

        Assert.Equal(0, arcs.Spawned);
        for (var i = 0; i < 600; i++)
        {
            arcs.Step(1 / 60.0, true, 1, 0, 1);
        }

        // One every 0.28 × (0.5…1.5) s: about 36 in 10 s.
        Assert.InRange(arcs.Spawned, 28, 46);
        Assert.InRange(arcs.Count, 0, LedArcs.Capacity);
    }

    [Fact]
    public void ArcPathsRunFromTheGaugeToJustOutsideTheRim()
    {
        var arcs = new LedArcs(13);
        for (var frame = 0; frame < 300; frame++)
        {
            arcs.Step(1 / 60.0, true, 1, 1, 1.6);
            for (var i = 0; i < arcs.Count; i++)
            {
                var path = arcs.Path(i);
                Assert.Equal(LedArcs.PointCount, path.Length);
                Assert.Equal(0.41, Radius(path[0]), 4);
                Assert.InRange(Radius(path[^1]), 0.5 - 1e-4, 0.6 + 1e-4);
                for (var k = 1; k < path.Length - 1; k++)
                {
                    // Interior points move along the diagonal by at most half the jitter.
                    var radius = Radius(path[k]);
                    Assert.InRange(radius, 0.41 - LedArcs.Jitter, 0.6 + LedArcs.Jitter);
                }

                Assert.InRange(arcs.Fade(i), 0, 1);
            }
        }
    }

    [Fact]
    public void LightningRulesFollowThePrototype()
    {
        Assert.Equal(0.3, LedLightning.Chance(0, 0), 9);
        Assert.Equal(1, LedLightning.Chance(2, 0.5), 9);
        Assert.Equal(0.55 * 0.6, LedLightning.Power(0, 0, 0, 1), 9);
        Assert.Equal((0.55 + 0.66) * 1.8 * 1.6, LedLightning.Power(3, 1, 1, 1.6), 9);
        Assert.Equal(2, LedLightning.Significance("1.50k", 0));
        Assert.Equal(3, LedLightning.Significance("2600", 0));
        Assert.Equal(0, LedLightning.Significance("1.50k", 3));
        Assert.Equal(1, LedLightning.BoltsPerDigit(0.5, false, true, 1));
        Assert.Equal(2, LedLightning.BoltsPerDigit(0.6, false, true, 1));
        Assert.Equal(2, LedLightning.BoltsPerDigit(0.6, true, false, 1));
        Assert.Equal(3, LedLightning.BoltsPerDigit(0.6, true, true, 1));
        Assert.Equal(1, LedLightning.BoltsPerDigit(0.5, false, true, 0.5));
        Assert.Equal(2, LedLightning.BoltsPerDigit(0.6, true, true, 0.5));
        Assert.Equal(16, LedLightning.BoltCap(1));
        Assert.Equal(8, LedLightning.BoltCap(0.5));
    }

    [Fact]
    public void OnlyChangedDigitsCrackleAndALengthChangeChangesEveryDigit()
    {
        var lightning = new LedLightning(1);
        lightning.Trigger("1.60k", "1.50k", Cells("1.60k"), 10, 0.5, 0, false, 1, 1, force: true);
        Assert.Equal(1, lightning.ZapCount);
        Assert.Equal(Cells("1.60k")[2].X, lightning.Zaps[0].X, 9);

        lightning = new LedLightning(1);
        lightning.Trigger("1.00k", "999", Cells("1.00k"), 10, 0.5, 0, false, 1, 1, force: true);
        Assert.Equal(3, lightning.ZapCount);

        lightning = new LedLightning(1);
        lightning.Trigger("–", "999", Cells("–"), 10, 0.5, 0, false, 1, 1, force: true);
        Assert.Equal(0, lightning.ZapCount);
    }

    [Fact]
    public void ADigitRestsForItsCooldown()
    {
        // h = 1 and two digits to the right: the chance is 1.
        var lightning = new LedLightning(2);
        lightning.Trigger("170", "160", Cells("170"), 10, 1, 0, false, 1, 1);
        Assert.Equal(1, lightning.ZapCount);
        lightning.Trigger("180", "170", Cells("180"), 10.1, 1, 0, false, 1, 1);
        Assert.Equal(1, lightning.ZapCount);
        lightning.Trigger("190", "180", Cells("190"), 10.11 + 1e-9, 1, 0, false, 1, 1);
        Assert.Equal(2, lightning.ZapCount);
    }

    [Fact]
    public void TheChanceMatchesItsFormula()
    {
        // The last digit (no digits to its right) at h = 0: 30 %.
        var hits = 0;
        var lightning = new LedLightning(17);
        for (var i = 0; i < 4000; i++)
        {
            var before = lightning.ZapCount;
            lightning.Trigger("7", "6", Cells("7"), i, 0, 0, false, 1, 1);
            hits += lightning.ZapCount > before || lightning.ZapCount == LedLightning.MaxZaps ? 1 : 0;
            lightning.Step(1);
        }

        Assert.InRange(hits / 4000.0, 0.27, 0.33);
    }

    [Theory]
    [InlineData(1.0, 16)]
    [InlineData(0.5, 8)]
    public void NoMoreThanTheCapOfBoltsLive(double countScale, int cap)
    {
        var lightning = new LedLightning(5);
        var text = "8888888";
        for (var i = 0; i < 40; i++)
        {
            lightning.Trigger(text, "1111111", Cells(text), i * 0.2, 1, 1, true, 1.6, countScale, force: true);
            Assert.InRange(lightning.BoltCount, 0, cap);
            lightning.Step(1 / 60.0);
        }

        Assert.True(lightning.Spawned >= cap);
    }

    [Fact]
    public void BoltPathsHaveTheirSizesAndForks()
    {
        var lightning = new LedLightning(23);
        lightning.Trigger("2.60k", "1.50k", Cells("2.60k"), 5, 1, 1, true, 1.6, 1, force: true);
        Assert.True(lightning.BoltCount > 0);
        var ringBolts = 0;
        for (var b = 0; b < lightning.BoltCount; b++)
        {
            var main = lightning.MainPath(b);
            Assert.Equal(33, main.Length);
            Assert.InRange(lightning.ForkCount(b), 1, 2);
            for (var f = 0; f < lightning.ForkCount(b); f++)
            {
                var fork = lightning.ForkPath(b, f);
                Assert.Equal(17, fork.Length);
                Assert.Contains(fork[0], main.ToArray());
            }

            ringBolts += Math.Abs(Radius(main[^1]) - LedLightning.RingRadius) < 1e-4 ? 1 : 0;
            Assert.InRange(lightning.Width(b), 0.0032 * 0.7, 0.0032 * (0.7 + 0.5 * 5));
            Assert.InRange(lightning.Alpha(b), 0, 1);
        }

        Assert.True(ringBolts > 0, "at the redline some bolts reach the LED ring");
    }

    [Fact]
    public void BoltsAndFlashesDieWithinTheirLife()
    {
        var lightning = new LedLightning(29);
        lightning.Trigger("5.55k", "4.44k", Cells("5.55k"), 0, 1, 1, true, 1.6, 1, force: true);
        Assert.True(lightning.BoltCount > 0 && lightning.ZapCount > 0);
        for (var i = 0; i < 40; i++)
        {
            lightning.Step(1 / 60.0);
        }

        Assert.Equal(0, lightning.BoltCount);
        Assert.Equal(0, lightning.ZapCount);
    }

    [Fact]
    public void TheEffectsCrackleOnlyWhenTheReadoutChanges()
    {
        var effects = new LedEffects(3);
        var frame = Frame(EffectsIntensity.Standard);
        effects.Update(frame);
        effects.NoteReadout("1.50k", 1500, Cells("1.50k"), 1);
        Assert.Equal(0, effects.Lightning.ZapCount);
        effects.NoteReadout("1.50k", 1500, Cells("1.50k"), 1.2);
        Assert.Equal(0, effects.Lightning.ZapCount);
        effects.NoteReadout("9.99k", 9990, Cells("9.99k"), 1.4);
        Assert.True(effects.Lightning.ZapCount > 0);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Soft, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void LightningNeedsStandard(EffectsIntensity level, bool crackles)
    {
        var effects = new LedEffects(3);
        effects.Update(Frame(level));
        effects.NoteReadout("1.50k", 1500, Cells("1.50k"), 1);
        effects.ForceLightning();
        effects.NoteReadout("1.50k", 1500, Cells("1.50k"), 2);
        Assert.Equal(crackles, effects.Lightning.ZapCount > 0);
    }

    [Fact]
    public void ReduceMotionHasNoLightningNorParticles()
    {
        var effects = new LedEffects(3);
        var frame = Frame(EffectsIntensity.Max, reduceMotion: true);
        for (var i = 0; i < 120; i++)
        {
            effects.Update(frame with { Time = 100 + i / 60.0 });
        }

        effects.ForceLightning();
        effects.NoteReadout("2.60k", 2600, Cells("2.60k"), 102);
        effects.Peak();
        Assert.Equal(0, effects.Lightning.ZapCount);
        Assert.Equal(0, effects.Particles.Count);
        Assert.Equal(0, effects.Arcs.Count);
        Assert.Equal(0, effects.Phases.Silk);
    }

    [Fact]
    public void MaxRedlineFillsTheEffectsAndSnapClearsThem()
    {
        var effects = new LedEffects(3);
        for (var i = 0; i < 120; i++)
        {
            effects.Update(Frame(EffectsIntensity.Max) with { Time = 100 + i / 60.0 });
        }

        effects.Peak();
        Assert.True(effects.Particles.Count > LedParticles.BurstCount);
        Assert.True(effects.Arcs.Spawned > 0);
        Assert.True(effects.Lingering);
        effects.Snap();
        Assert.False(effects.Lingering || effects.Crackling);
    }

    private static OrbFrame Frame(EffectsIntensity level, bool reduceMotion = false) =>
        new(100, 1 / 60.0, 1, 0.3, 1, 0.3, SpeedTier.Redline, 148, new EffectsPolicy(level, reduceMotion), true);

    /// <summary>Cells of a value like the readout lays them out: 0.075 S per digit, centered.</summary>
    private static LedDigitCell[] Cells(string text)
    {
        var cells = new LedDigitCell[text.Length];
        var x = -0.0375 * (text.Length - 1);
        for (var i = 0; i < text.Length; i++)
        {
            cells[i] = new LedDigitCell(x + i * 0.075, -0.073, 0.075, 0.286);
        }

        return cells;
    }

    private static double Radius(LedPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
}
