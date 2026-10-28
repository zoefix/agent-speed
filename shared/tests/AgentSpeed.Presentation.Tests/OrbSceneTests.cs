using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Particles;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class OrbSceneTests
{
    private const double R = HeatMath.ReferenceRedline;

    private sealed class Rig
    {
        public Rig(EffectsIntensity effects, bool reduceMotion, bool highContrast, int seed)
        {
            Scene = new OrbScene(seed) { Effects = new EffectsPolicy(effects, reduceMotion, highContrast), Redline = R };
            Scene.Update(Clock.Now);
        }

        public OrbScene Scene { get; }

        public FrameClock Clock { get; } = new();

        public void Run(double seconds) => Clock.Advance(seconds, Scene.Update);

        public void Frames(int frames) => Clock.AdvanceFrames(frames, Scene.Update);

        public void RunTo(double seconds) => Clock.AdvanceTo(seconds, Scene.Update);

        /// <summary>Runs frame by frame, calling <paramref name="check"/> after each.</summary>
        public void Watch(double seconds, Action<OrbScene> check)
        {
            Clock.Advance(seconds, now =>
            {
                Scene.Update(now);
                check(Scene);
            });
        }
    }

    private static Rig Create(
        EffectsIntensity effects = EffectsIntensity.Standard, bool reduceMotion = false, bool highContrast = false, int seed = 1) =>
        new(effects, reduceMotion, highContrast, seed);

    private static void Drive(Rig rig, double value, double seconds = 2)
    {
        rig.Scene.SetTarget(value, HeatMath.RawTier(value, rig.Scene.Redline));
        rig.Run(seconds);
    }

    [Theory]
    [InlineData(60, 400)]
    [InlineData(200, 400)]
    [InlineData(400, 400)]
    [InlineData(800, 400)]
    [InlineData(500, 1000)]
    public void HeatConvergesTowardTargetHeat(double value, double redline)
    {
        var rig = Create(seed: 2);
        rig.Scene.Redline = redline;
        rig.Scene.SetTarget(value, HeatMath.RawTier(value, redline));

        rig.Run(0.1);
        Assert.InRange(rig.Scene.Heat, 1e-6, HeatMath.Heat(value, redline) - 1e-3);

        rig.Run(1.9);
        Assert.Equal(HeatMath.Heat(value, redline), rig.Scene.Heat, 3);
        Assert.Equal(HeatMath.Overdrive(value, redline), rig.Scene.Overdrive, 3);
    }

    [Fact]
    public void HeatFallsBackToZero()
    {
        var rig = Create();
        Drive(rig, 300);
        Drive(rig, 0, 3);
        Assert.Equal(0, rig.Scene.Heat, 4);
        Assert.Equal(SpeedTier.Idle, rig.Scene.Tier);
    }

    [Fact]
    public void LongFrameGapsAdvanceOnlyOneFrame()
    {
        var rig = Create();
        rig.Scene.SetTarget(400, SpeedTier.Redline);
        rig.Scene.Update(100);
        Assert.InRange(rig.Scene.Heat, 0, 0.1);
        Assert.Equal(100, rig.Scene.Time);
    }

    [Fact]
    public void TierChangeStartsTransitionAndFlash()
    {
        var rig = Create();
        rig.Run(1);
        rig.Scene.SetTarget(100, SpeedTier.Boost);

        Assert.Equal(new TierTransition(SpeedTier.Idle, SpeedTier.Boost, 0), rig.Scene.Transition);
        Assert.Equal(1, rig.Scene.Flash);

        rig.Frames(12);
        Assert.Equal(0.2 / OrbScene.TierTransitionDuration, rig.Scene.Transition.Progress, 6);
        Assert.InRange(rig.Scene.Flash, 0.01, 0.99);

        rig.Frames(15);
        Assert.Equal(1, rig.Scene.Transition.Progress);
        Assert.Equal(0, rig.Scene.Flash);

        rig.Scene.SetTarget(120, SpeedTier.Boost);
        rig.Frames(1);
        Assert.Equal(1, rig.Scene.Transition.Progress);
        Assert.Equal(0, rig.Scene.Flash);

        rig.Scene.SetTarget(400, SpeedTier.Redline);
        Assert.Equal(SpeedTier.Boost, rig.Scene.Transition.From);
        Assert.Equal(SpeedTier.Redline, rig.Scene.Transition.To);
        Assert.Equal(1, rig.Scene.Flash);
    }

    [Fact]
    public void TitleChangeSlidesTheLabelWithoutFlashingTheGauge()
    {
        var rig = Create();
        rig.Run(1);
        rig.Scene.SetTarget(100, SpeedTier.Boost, SpeedTitle.Maglev);
        rig.Frames(30);
        Assert.Equal(new TitleTransition(SpeedTitle.Idle, SpeedTitle.Maglev, 1), rig.Scene.TitleTransition);
        Assert.Equal(0, rig.Scene.Flash);

        // A new title inside the same effect tier: the label slides, the gauge does not flash.
        rig.Scene.SetTarget(140, SpeedTier.Boost, SpeedTitle.Jetliner);
        Assert.Equal(new TitleTransition(SpeedTitle.Maglev, SpeedTitle.Jetliner, 0), rig.Scene.TitleTransition);
        Assert.Equal(0, rig.Scene.Flash);
        Assert.Equal(FrameDemand.Full, rig.Scene.Demand);
        rig.Frames(12);
        Assert.Equal(0.2 / OrbScene.TierTransitionDuration, rig.Scene.TitleTransition.Progress, 6);
        Assert.Equal(1, rig.Scene.Transition.Progress);
        rig.Frames(15);
        Assert.Equal(1, rig.Scene.TitleTransition.Progress);

        // An effect-tier change flashes, whether or not the title changes with it.
        rig.Scene.SetTarget(160, SpeedTier.Overdrive, SpeedTitle.Jetliner);
        Assert.Equal(1, rig.Scene.Flash);
        Assert.Equal(1, rig.Scene.TitleTransition.Progress);
        Assert.Equal(SpeedTitle.Jetliner, rig.Scene.Title);
    }

    [Fact]
    public void FlashDecaysMonotonically()
    {
        var rig = Create();
        rig.Scene.SetTarget(200, SpeedTier.Overdrive);
        var previous = rig.Scene.Flash;
        rig.Watch(0.5, scene =>
        {
            Assert.True(scene.Flash <= previous);
            previous = scene.Flash;
        });
        Assert.Equal(0, rig.Scene.Flash);
    }

    [Fact]
    public void PulsesCoalesceWithin150MillisecondsAndExpireAfter700()
    {
        var rig = Create();
        Drive(rig, 100, 0.5);

        Assert.True(rig.Scene.TriggerPulse(100, 1000));
        Assert.Equal(1, rig.Scene.PulseCount);

        rig.Frames(6);
        Assert.False(rig.Scene.TriggerPulse(120, 1000));
        Assert.Equal(1, rig.Scene.PulseCount);

        rig.Frames(4);
        Assert.True(rig.Scene.TriggerPulse(200, 1000));
        Assert.Equal(2, rig.Scene.PulseCount);

        var first = rig.Scene.Pulses.First();
        Assert.Equal(10 / 60.0 / OrbScene.PulseDuration, first.Progress, 6);
        Assert.Equal(HeatMath.Heat(100, R), first.Heat, 9);
        Assert.Equal(HeatMath.Heat(200, R), rig.Scene.Pulses.Last().Heat, 9);

        rig.Frames(29);
        Assert.Equal(2, rig.Scene.PulseCount);

        rig.Frames(4);
        Assert.Equal(1, rig.Scene.PulseCount);

        rig.Frames(10);
        Assert.Equal(0, rig.Scene.PulseCount);
        Assert.Empty(rig.Scene.Pulses);
    }

    [Fact]
    public void PulseProgressStaysInRange()
    {
        var rig = Create();
        rig.Scene.TriggerPulse(50, 500);
        rig.Watch(1, scene => Assert.All(scene.Pulses, p => Assert.InRange(p.Progress, 0, 1)));
    }

    [Theory]
    [InlineData(1, 0.3)]
    [InlineData(10, 1 / 3.0)]
    [InlineData(1000, 1)]
    [InlineData(1_000_000, 1.25)]
    [InlineData(0, 0.3)]
    public void PulseStrengthScalesWithLogOfOutput(long output, double strength)
    {
        var rig = Create();
        Assert.True(rig.Scene.TriggerPulse(100, output));
        Assert.Equal(strength, Assert.Single(rig.Scene.Pulses).Strength, 9);
    }

    [Fact]
    public void DisabledPulsesDoNothing()
    {
        var rig = Create();
        Drive(rig, 200);
        rig.Scene.PulsesEnabled = false;
        var aura = rig.Scene.AuraIntensity;
        Assert.False(rig.Scene.TriggerPulse(200, 5000));
        Assert.Equal(0, rig.Scene.PulseCount);
        Assert.Equal(aura, rig.Scene.AuraIntensity);

        var off = Create(EffectsIntensity.Off);
        Assert.False(off.Scene.TriggerPulse(200, 5000));
        Assert.Equal(0, off.Scene.PulseCount);
    }

    [Fact]
    public void PulseFlaresTheAura()
    {
        var rig = Create(EffectsIntensity.Soft);
        Drive(rig, 100);
        var before = rig.Scene.AuraIntensity;
        rig.Scene.TriggerPulse(100, 1000);
        Assert.True(rig.Scene.AuraIntensity > before);
    }

    [Fact]
    public void NewPeakSweepsTheGaugeFromZero()
    {
        var rig = Create();
        Drive(rig, 400);
        var heat = rig.Scene.Heat;
        Assert.Equal(1, heat, 3);

        rig.Scene.TriggerPeak();
        Assert.Equal(0, rig.Scene.GaugeHeat);
        Assert.Equal(0.0, rig.Scene.PeakRays);
        Assert.Equal(0.0, rig.Scene.PeakPillAge);

        var previous = 0.0;
        var midway = double.NaN;
        var frame = 0;
        rig.Watch(0.5, scene =>
        {
            frame++;
            Assert.True(scene.GaugeHeat >= previous - 1e-12, "Sweep must not go backwards");
            previous = scene.GaugeHeat;
            if (frame == 15)
            {
                midway = scene.GaugeHeat;
            }
        });

        Assert.InRange(midway, 0.1, heat - 0.01);
        rig.Frames(1);
        Assert.Equal(rig.Scene.Heat, rig.Scene.GaugeHeat);
        Assert.NotNull(rig.Scene.PeakRays);

        rig.RunTo(rig.Clock.Now + 0.45);
        Assert.Null(rig.Scene.PeakRays);
        Assert.NotNull(rig.Scene.PeakPillAge);

        rig.RunTo(rig.Clock.Now + 0.9);
        Assert.Null(rig.Scene.PeakPillAge);
    }

    [Fact]
    public void DisabledPeaksDoNothing()
    {
        var rig = Create();
        Drive(rig, 400);
        rig.Scene.PeaksEnabled = false;
        rig.Scene.TriggerPeak();
        Assert.Equal(rig.Scene.Heat, rig.Scene.GaugeHeat);
        Assert.Null(rig.Scene.PeakRays);
        Assert.Null(rig.Scene.PeakPillAge);
    }

    [Fact]
    public void PeakBurstNeedsEffects()
    {
        var rig = Create(EffectsIntensity.Off);
        Drive(rig, 400);
        rig.Scene.TriggerPeak();
        Assert.Equal(rig.Scene.Heat, rig.Scene.GaugeHeat);
        Assert.Null(rig.Scene.PeakRays);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard)]
    [InlineData(EffectsIntensity.Max)]
    public void SparksAppearFromHeatPoint45WithStandardEffects(EffectsIntensity effects)
    {
        var rig = Create(effects);
        Drive(rig, 400);
        Assert.True(rig.Scene.SparkRate > 0);
        Assert.True(rig.Scene.EmberRate > 0);
        Assert.True(rig.Scene.Particles.Count > 0);
        Assert.InRange(rig.Scene.Particles.Count, 1, ParticleSystem.DefaultCapacity);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void NoSparksBelowHeatPoint45(double value)
    {
        Assert.True(HeatMath.Heat(value, R) < 0.45);
        var rig = Create(EffectsIntensity.Max);
        rig.Scene.SetTarget(value, HeatMath.RawTier(value, R));
        rig.Watch(3, scene =>
        {
            Assert.Equal(0, scene.SparkRate);
            Assert.Equal(0, scene.Particles.Count);
        });
    }

    [Fact]
    public void SparksJustAboveHeatPoint45()
    {
        Assert.True(HeatMath.Heat(120, R) > 0.45);
        var rig = Create();
        Drive(rig, 120, 3);
        Assert.Equal(120 * ((HeatMath.Heat(120, R) - 0.45) / 0.55), rig.Scene.SparkRate, 2);
        Assert.Equal(0, rig.Scene.EmberRate);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Soft, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Max, true)]
    public void ParticlesNeverAppearWithOffSoftOrReduceMotion(EffectsIntensity effects, bool reduceMotion)
    {
        var rig = Create(effects, reduceMotion);
        rig.Scene.SetTarget(800, SpeedTier.Redline);
        rig.Watch(3, scene =>
        {
            Assert.Equal(0, scene.SparkRate);
            Assert.Equal(0, scene.EmberRate);
            Assert.Equal(0, scene.Particles.Count);
        });
    }

    [Fact]
    public void MaxEffectsMultiplyParticleRates()
    {
        var standard = Create(EffectsIntensity.Standard);
        var max = Create(EffectsIntensity.Max);
        Drive(standard, 800);
        Drive(max, 800);
        Assert.Equal(1.6 * standard.Scene.SparkRate, max.Scene.SparkRate, 6);
        Assert.Equal(1.6 * standard.Scene.EmberRate, max.Scene.EmberRate, 6);
        Assert.Equal(90, standard.Scene.EmberRate, 3);
    }

    [Fact]
    public void EmbersOnlyAtRedlineTier()
    {
        var rig = Create();
        rig.Scene.SetTarget(400, SpeedTier.Overdrive);
        rig.Run(2);
        Assert.Equal(0, rig.Scene.EmberRate);
        Assert.True(rig.Scene.SparkRate > 0);

        rig.Scene.SetTarget(400, SpeedTier.Redline);
        rig.Run(0.5);
        Assert.Equal(90 * 0.3, rig.Scene.EmberRate, 6);
        Assert.Contains(ParticleKind.Ember, rig.Scene.Particles.Particles.ToArray().Select(p => p.Kind));
    }

    [Fact]
    public void ParticlesStayInsideTheWindowBound()
    {
        var rig = Create(EffectsIntensity.Max);
        rig.Scene.SetTarget(800, SpeedTier.Redline);
        rig.Watch(5, scene =>
        {
            foreach (var p in scene.Particles.Particles)
            {
                Assert.True(p.X * p.X + p.Y * p.Y <= ParticleSystem.Bound * ParticleSystem.Bound + 1e-5);
            }
        });
    }

    [Fact]
    public void DemandIsNoneWhenIdleWithEffectsOffAfterSettling()
    {
        var rig = Create(EffectsIntensity.Off);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);

        rig.Scene.SetTarget(200, SpeedTier.Boost);
        rig.Frames(1);
        Assert.Equal(FrameDemand.Full, rig.Scene.Demand);

        rig.Run(2);
        rig.Scene.SetTarget(0, SpeedTier.Idle);
        rig.Frames(1);
        Assert.Equal(FrameDemand.Full, rig.Scene.Demand);

        rig.Run(3);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);
        Assert.Null(rig.Scene.IdleSweep);
        Assert.Null(rig.Scene.NextWake);

        rig.Run(10);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);
    }

    [Fact]
    public void DemandIsNoneForASteadyValueWithEffectsOff()
    {
        var rig = Create(EffectsIntensity.Off);
        Drive(rig, 150, 3);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);
    }

    [Fact]
    public void DemandIsFullWhileActiveWithEffects()
    {
        var rig = Create(EffectsIntensity.Soft);
        Drive(rig, 150, 3);
        Assert.Equal(FrameDemand.Full, rig.Scene.Demand);
    }

    [Fact]
    public void IdleSweepRunsEverySixSecondsAtSlowDemand()
    {
        var rig = Create(EffectsIntensity.Soft);
        Assert.Equal(6, rig.Scene.NextWake);

        rig.RunTo(3);
        Assert.Null(rig.Scene.IdleSweep);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);

        rig.RunTo(6.5);
        Assert.Equal(0.5 / OrbScene.IdleSweepDuration, rig.Scene.IdleSweep!.Value, 6);
        Assert.Equal(FrameDemand.Slow, rig.Scene.Demand);
        Assert.Equal(12, rig.Scene.NextWake!.Value, 6);

        rig.RunTo(8);
        Assert.Null(rig.Scene.IdleSweep);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);

        rig.RunTo(12.5);
        Assert.NotNull(rig.Scene.IdleSweep);
        Assert.Equal(FrameDemand.Slow, rig.Scene.Demand);
    }

    [Fact]
    public void IdleSweepStartsSixSecondsAfterGoingIdle()
    {
        var rig = Create(EffectsIntensity.Soft);
        Drive(rig, 100, 2);
        Assert.Null(rig.Scene.IdleSweep);
        Assert.Null(rig.Scene.NextWake);

        rig.Scene.SetTarget(0, SpeedTier.Idle);
        Assert.Equal(8, rig.Scene.NextWake!.Value, 6);

        rig.RunTo(7.9);
        Assert.Null(rig.Scene.IdleSweep);
        Assert.Equal(FrameDemand.None, rig.Scene.Demand);

        rig.RunTo(8.1);
        Assert.NotNull(rig.Scene.IdleSweep);
        Assert.Equal(FrameDemand.Slow, rig.Scene.Demand);
    }

    [Fact]
    public void NoIdleSweepWithEffectsOff()
    {
        var rig = Create(EffectsIntensity.Off);
        rig.Watch(14, scene =>
        {
            Assert.Null(scene.IdleSweep);
            Assert.Equal(FrameDemand.None, scene.Demand);
        });
    }

    [Fact]
    public void ReadoutShowsFormattedValueAfterThrottle()
    {
        var rig = Create();
        Assert.Equal("0", rig.Scene.Readout.Current);

        rig.Scene.SetTarget(128.4, SpeedTier.Boost);
        rig.Frames(1);
        Assert.Equal("128", rig.Scene.Readout.Current);
        Assert.Equal(128.4, rig.Scene.Value);

        rig.Scene.SetTarget(130, SpeedTier.Boost);
        rig.Frames(8);
        Assert.Equal("128", rig.Scene.Readout.Current);
        Assert.Equal(128.4, rig.Scene.Value);

        rig.Frames(2);
        Assert.Equal("130", rig.Scene.Readout.Current);
        Assert.Equal(130, rig.Scene.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3.25)]
    [InlineData(9.96)]
    [InlineData(57.5)]
    [InlineData(999.6)]
    [InlineData(1234)]
    [InlineData(45678)]
    [InlineData(double.NaN)]
    [InlineData(-3)]
    public void ReadoutTextMatchesSpeedFormat(double value)
    {
        var rig = Create();
        rig.Run(0.5);
        rig.Scene.SetTarget(value, SpeedTier.Cruise);
        rig.Run(OrbScene.ReadoutMinInterval + 0.05);
        Assert.Equal(SpeedFormat.Speed(value), rig.Scene.Readout.Current);
    }

    [Fact]
    public void ReadoutRollsInTheDirectionOfChange()
    {
        var rig = Create();
        rig.Scene.SetTarget(50, SpeedTier.Cruise);
        rig.Frames(1);
        Assert.Equal(1, rig.Scene.Readout.Slot(0, rig.Scene.Time).Direction);
        Assert.True(rig.Scene.Readout.IsAnimating(rig.Scene.Time));

        rig.Run(0.5);
        rig.Scene.SetTarget(20, SpeedTier.Cruise);
        rig.Frames(1);
        Assert.False(rig.Scene.Readout.Slot(0, rig.Scene.Time).IsChanging);
        Assert.Equal(-1, rig.Scene.Readout.Slot(1, rig.Scene.Time).Direction);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Max, true)]
    public void ReadoutDoesNotRollWithoutRollingDigits(EffectsIntensity effects, bool reduceMotion)
    {
        var rig = Create(effects, reduceMotion);
        rig.Scene.SetTarget(88, SpeedTier.Boost);
        rig.Frames(1);
        Assert.Equal("88", rig.Scene.Readout.Current);
        Assert.False(rig.Scene.Readout.IsAnimating(rig.Scene.Time));
        Assert.False(rig.Scene.Readout.Slot(0, rig.Scene.Time).IsChanging);
    }

    [Fact]
    public void SkewLeansForwardFromOverdrive()
    {
        var rig = Create();
        Drive(rig, 100);
        Assert.Equal(0, rig.Scene.SkewDegrees, 6);

        Drive(rig, 200);
        Assert.Equal(SpeedTier.Overdrive, rig.Scene.Tier);
        Assert.Equal(-6 * HeatMath.Heat(200, R), rig.Scene.SkewDegrees, 3);

        var off = Create(EffectsIntensity.Off);
        Drive(off, 200);
        Assert.Equal(0, off.Scene.SkewDegrees);
    }

    [Fact]
    public void JitterOnlyAtRedlineWithStandardEffects()
    {
        var rig = Create();
        rig.Scene.SetTarget(400, SpeedTier.Redline);
        var sawJitter = false;
        rig.Watch(1, scene =>
        {
            Assert.InRange(scene.JitterX, -0.6, 0.6);
            Assert.InRange(scene.JitterY, -0.6, 0.6);
            sawJitter |= scene.JitterX != 0 || scene.JitterY != 0;
        });
        Assert.True(sawJitter);

        Drive(rig, 200);
        Assert.Equal(0, rig.Scene.JitterX);
        Assert.Equal(0, rig.Scene.JitterY);

        foreach (var calm in new[] { Create(EffectsIntensity.Soft), Create(EffectsIntensity.Max, reduceMotion: true) })
        {
            calm.Scene.SetTarget(400, SpeedTier.Redline);
            calm.Watch(1, scene =>
            {
                Assert.Equal(0, scene.JitterX);
                Assert.Equal(0, scene.JitterY);
            });
        }
    }

    [Fact]
    public void ShimmerVariesOnlyAtRedline()
    {
        var rig = Create();
        rig.Scene.SetTarget(400, SpeedTier.Redline);
        var sawShimmer = false;
        rig.Watch(1, scene =>
        {
            foreach (var s in scene.Shimmer)
            {
                Assert.InRange(s, 0.75f, 1f);
                sawShimmer |= s < 0.99f;
            }
        });
        Assert.True(sawShimmer);

        Drive(rig, 200);
        foreach (var s in rig.Scene.Shimmer)
        {
            Assert.Equal(1f, s);
        }
    }

    [Fact]
    public void CometIsHiddenAtIdleAndVisibleWhenActive()
    {
        var rig = Create(EffectsIntensity.Soft);
        rig.Run(1);
        Assert.Equal(0, rig.Scene.CometOpacity);

        Drive(rig, 100);
        Assert.Equal(0.25 + 0.6 * rig.Scene.Heat, rig.Scene.CometOpacity, 3);
        var degrees = rig.Scene.CometDegrees;
        rig.Frames(10);
        Assert.NotEqual(degrees, rig.Scene.CometDegrees);

        var off = Create(EffectsIntensity.Off);
        Drive(off, 100);
        Assert.Equal(0, off.Scene.CometOpacity);
    }

    [Fact]
    public void AuraFollowsHeatAndEffects()
    {
        var rig = Create(EffectsIntensity.Off);
        Assert.Equal(0, rig.Scene.AuraIntensity);
        Drive(rig, 200);
        Assert.Equal(rig.Scene.Heat, rig.Scene.AuraIntensity, 9);

        var contrast = Create(EffectsIntensity.Max, highContrast: true);
        Drive(contrast, 800);
        Assert.Equal(0, contrast.Scene.AuraIntensity);

        var breathing = Create(EffectsIntensity.Soft);
        Drive(breathing, 200);
        var heat = breathing.Scene.Heat;
        breathing.Watch(4, scene => Assert.InRange(scene.AuraIntensity, heat * 0.85 - 1e-6, heat * 1.15 + 1e-6));
    }

    [Fact]
    public void EdgeGlowAppearsAtRedline()
    {
        var rig = Create();
        Drive(rig, 200);
        Assert.Equal(0, rig.Scene.EdgeGlow, 3);
        Drive(rig, 400);
        Assert.InRange(rig.Scene.EdgeGlow, 0.09, 0.51);
    }

    [Fact]
    public void AgentDotsCapAtSixWithOverflow()
    {
        var rig = Create();
        var sources = Enumerable.Repeat(SourceKind.ClaudeCode, 5).Concat(Enumerable.Repeat(SourceKind.Codex, 3)).ToArray();
        rig.Scene.SetAgents(sources);
        Assert.Equal(OrbScene.MaxAgentDots, rig.Scene.AgentDots.Count);
        Assert.Equal(2, rig.Scene.AgentOverflow);
        Assert.Equal(sources.Take(6), rig.Scene.AgentDots);

        rig.Scene.SetAgents([SourceKind.ClaudeDesktop]);
        Assert.Equal([SourceKind.ClaudeDesktop], rig.Scene.AgentDots);
        Assert.Equal(0, rig.Scene.AgentOverflow);

        rig.Scene.SetAgents([SourceKind.DeepSeek, SourceKind.Codex, SourceKind.DeepSeek]);
        Assert.Equal([SourceKind.DeepSeek, SourceKind.Codex, SourceKind.DeepSeek], rig.Scene.AgentDots);
        Assert.Equal(0, rig.Scene.AgentOverflow);

        Assert.Throws<ArgumentNullException>(() => rig.Scene.SetAgents(null!));
    }

    [Fact]
    public void HoverSpringsTowardOne()
    {
        var rig = Create();
        rig.Scene.SetHover(true);
        rig.Run(1.5);
        Assert.Equal(1, rig.Scene.Hover, 2);
        Assert.Equal(1, rig.Scene.HoverBlend, 3);

        rig.Scene.SetHover(false);
        rig.Run(1.5);
        Assert.Equal(0, rig.Scene.Hover, 2);
    }

    [Fact]
    public void HoverSnapsWithReduceMotion()
    {
        var rig = Create(EffectsIntensity.Soft, reduceMotion: true);
        rig.Scene.SetHover(true);
        Assert.Equal(1, rig.Scene.Hover);
    }

    [Fact]
    public void SameSeedProducesIdenticalScenes()
    {
        var a = Create(EffectsIntensity.Max, seed: 99);
        var b = Create(EffectsIntensity.Max, seed: 99);
        foreach (var rig in new[] { a, b })
        {
            rig.Scene.SetTarget(800, SpeedTier.Redline);
            rig.Run(2);
        }

        Assert.True(a.Scene.Particles.Count > 0);
        Assert.Equal(a.Scene.Particles.Particles.ToArray(), b.Scene.Particles.Particles.ToArray());
        Assert.Equal(a.Scene.Shimmer.ToArray(), b.Scene.Shimmer.ToArray());
        Assert.Equal(a.Scene.JitterX, b.Scene.JitterX);
        Assert.Equal(a.Scene.CometDegrees, b.Scene.CometDegrees);
    }

    [Fact]
    public void InvalidTargetValuesAreTreatedAsZero()
    {
        var rig = Create();
        rig.Scene.SetTarget(double.NaN, SpeedTier.Idle);
        rig.Run(1);
        Assert.Equal(0, rig.Scene.Heat);
        Assert.Equal("0", rig.Scene.Readout.Current);

        rig.Scene.SetTarget(double.PositiveInfinity, SpeedTier.Cruise);
        rig.Run(1);
        Assert.Equal(0, rig.Scene.Heat);
    }
}
