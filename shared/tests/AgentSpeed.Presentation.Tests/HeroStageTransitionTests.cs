using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 4c "Title transitions" and "Start-up".</summary>
public class HeroStageTransitionTests
{
    private const double R = HeatMath.ReferenceRedline;

    /// <summary>A walker that has been walking for 3 s climbs to the bicycle; returns the time of the change.</summary>
    private static (HeroDriver Driver, double Change) Upgrade(HeroOptions? options = null, SpeedTitle to = SpeedTitle.Bicycle)
    {
        var d = new HeroDriver(options: options);
        d.Change(5, SpeedTitle.Walk);
        d.Run(3);
        var change = d.Change(SpeedTitles.Threshold(to, R) * 1.2, to);
        return (d, change);
    }

    private static int CountParticles(HeroStage stage, HeroParticleKind kind)
    {
        var count = 0;
        foreach (var p in stage.Particles.Items)
        {
            count += p.Alive && p.Kind == kind ? 1 : 0;
        }

        return count;
    }

    private static int CountBursts(HeroStage stage, double maxAge = double.MaxValue)
    {
        var count = 0;
        foreach (var s in stage.Streaks.Items)
        {
            count += s.Alive && s.Burst && s.Age <= maxAge ? 1 : 0;
        }

        return count;
    }

    [Fact]
    public void UpgradeSendsBothActorsUp()
    {
        var (d, _) = Upgrade();
        var s = d.Stage;
        var previous = HeroDriver.Some(s.Previous);

        Assert.Equal(ActorTransition.Up, previous.Transition);
        Assert.Equal(0, previous.Vehicle);
        Assert.Equal(0, previous.BaseOffset, 9);
        Assert.Equal(1, previous.BaseAlpha, 9);
        Assert.Equal(ActorTransition.Up, s.Current.Transition);
        Assert.Equal(1, s.Current.Vehicle);
        Assert.Equal(1, s.Current.Levels);
        Assert.Equal("bicycle", s.CurrentKey);
        Assert.Equal(SpeedTitle.Bicycle, s.Title);
        Assert.False(s.StartupRunning);
    }

    [Fact]
    public void OutgoingVehicleAcceleratesAwayAndFades()
    {
        var (d, t0) = Upgrade();
        var s = d.Stage;

        d.RunTo(t0 + 0.06);
        var early = s.Place(HeroDriver.Some(s.Previous), true);
        Assert.Equal(90 * HeroMath.EaseInCubic(0.06 / 0.34), early.Along, 6);
        Assert.Equal(1, early.Alpha, 9);

        d.RunTo(t0 + 0.17);
        var mid = s.Place(HeroDriver.Some(s.Previous), true);
        Assert.Equal(90 * 3 * 0.25 / 0.34, mid.Ghost, 6);

        d.RunTo(t0 + 0.21);
        Assert.Equal(0.5, s.Place(HeroDriver.Some(s.Previous), true).Alpha, 6);

        d.RunTo(t0 + 0.305);
        Assert.Equal(0, s.Place(HeroDriver.Some(s.Previous), true).Alpha, 9);

        d.RunTo(t0 + 0.34);
        var away = s.Place(HeroDriver.Some(s.Previous), true);
        Assert.Equal(90, away.Along, 6);
        Assert.Equal(90, away.X, 6);
        Assert.Equal(0, away.Y);
        Assert.Equal(0, away.Alpha, 9);

        d.RunTo(t0 + 0.75);
        Assert.NotNull(s.Previous);
        d.RunTo(t0 + 0.85);
        Assert.Null(s.Previous);
    }

    [Fact]
    public void IncomingVehicleSwoopsInOnItsSpring()
    {
        var (d, t0) = Upgrade();
        var s = d.Stage;

        var waiting = s.Place(s.Current, false);
        Assert.Equal(-62, waiting.Along);
        Assert.Equal(-62, waiting.X);
        Assert.Equal(0, waiting.Alpha);
        Assert.Equal(0, waiting.Ghost);

        d.RunTo(t0 + 0.11);
        Assert.Equal(-62, s.Place(s.Current, false).Along);
        Assert.Equal(0, s.Place(s.Current, false).Alpha);

        d.RunTo(t0 + 0.19);
        var swoop = s.Place(s.Current, false);
        Assert.Equal(HeroMath.SpringAt(0.07, -62, 0, 140, 0.5, 0.8), swoop.Along, 6);
        Assert.Equal(0.5, swoop.Alpha, 6);
        Assert.Equal(HeroMath.SpringVelocityAt(0.07, -62, 0, 140, 0.5, 0.8), swoop.Ghost, 6);
        Assert.True(swoop.Ghost > 40, "fast enough for motion ghosts");

        var max = double.MinValue;
        d.Run(1.0, st => max = Math.Max(max, st.Place(st.Current, false).Along));
        Assert.InRange(max, 0.3, 3);

        d.RunTo(t0 + 1.3);
        var settled = s.Place(s.Current, false);
        Assert.True(Math.Abs(settled.Along) < 0.05, $"settled at {settled.Along}");
        Assert.Equal(1, settled.Alpha);
    }

    [Fact]
    public void UpgradeFlashesTheIncomingVehicle()
    {
        var (d, t0) = Upgrade();
        var s = d.Stage;

        d.RunTo(t0 + 0.30);
        Assert.Null(s.Flash);

        d.RunTo(t0 + 0.40);
        var flash = HeroDriver.Some(s.Flash);
        var u = 0.08 / 0.35;
        Assert.Equal(s.CurrentVehicle.Bounds.Center, flash.Center);
        Assert.Equal(8 + 28 * HeroMath.EaseOutCubic(u), flash.Radius, 6);
        Assert.Equal(0.42 * (1 - u) * (1 - u), flash.Alpha, 6);

        d.RunTo(t0 + 0.9);
        Assert.Null(s.Flash);
    }

    [Fact]
    public void UpgradeFlashBoostsTheGlow()
    {
        var (d, t0) = Upgrade();
        var s = d.Stage;

        d.RunTo(t0 + 0.42);
        var baseGlow = (0.3 + 0.45 * s.Heat + 0.15 * s.Overdrive) * s.Wake;
        Assert.Equal(baseGlow * (1 + 0.6 * (1 - 0.1 / 0.4)), s.GlowOpacity, 6);

        d.RunTo(t0 + 0.9);
        Assert.Equal((0.3 + 0.45 * s.Heat + 0.15 * s.Overdrive) * s.Wake, s.GlowOpacity, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, true, SpeedTitle.Bicycle, 0.42)]
    [InlineData(EffectsIntensity.Standard, false, SpeedTitle.Bicycle, 0.26)]
    [InlineData(EffectsIntensity.Soft, true, SpeedTitle.Bicycle, 0.42 * 0.6)]
    [InlineData(EffectsIntensity.Soft, false, SpeedTitle.Bicycle, 0.26 * 0.6)]
    [InlineData(EffectsIntensity.Standard, true, SpeedTitle.EBike, 0.42 * 1.2)]
    [InlineData(EffectsIntensity.Max, true, SpeedTitle.Car, 0.42 * 1.6)]
    [InlineData(EffectsIntensity.Standard, true, SpeedTitle.Jetliner, 0.42 * 1.6)]
    public void FlashStrengthFollowsTheTitlesClimbed(EffectsIntensity level, bool dark, SpeedTitle to, double peak)
    {
        var (d, t0) = Upgrade(new HeroOptions(level, Dark: dark), to);
        Assert.Equal((int)to - (int)SpeedTitle.Walk, d.Stage.Current.Levels);

        d.RunTo(t0 + 0.32);
        Assert.Equal(peak, HeroDriver.Some(d.Stage.Flash).Alpha, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Standard)]
    [InlineData(EffectsIntensity.Max)]
    public void UpgradeAddsFourBurstSpeedLines(EffectsIntensity level)
    {
        var (d, _) = Upgrade(new HeroOptions(level));
        Assert.Equal(4, CountBursts(d.Stage, HeroDriver.FrameSeconds + 1e-9));
    }

    [Fact]
    public void JumpAcrossSeveralTitlesGoesStraightToTheFinalVehicle()
    {
        var (d, _) = Upgrade(to: SpeedTitle.Maglev);
        var s = d.Stage;
        Assert.Equal("maglev", s.CurrentKey);
        Assert.Equal(8, s.Current.Levels);
        Assert.Equal(0, HeroDriver.Some(s.Previous).Vehicle);
    }

    [Fact]
    public void DowngradeFallsBackAndGlidesIn()
    {
        var (d, _) = Upgrade();
        d.Run(2);
        var s = d.Stage;
        Assert.Null(s.Previous);

        var t0 = d.Change(3, SpeedTitle.Walk);
        var previous = HeroDriver.Some(s.Previous);
        Assert.Equal(ActorTransition.Down, previous.Transition);
        Assert.Equal(1, previous.Vehicle);
        Assert.Equal(0, previous.BaseOffset, 3);
        Assert.Equal(1, previous.BaseAlpha, 6);
        Assert.Equal(ActorTransition.Down, s.Current.Transition);
        Assert.Equal(0, s.Current.Vehicle);
        Assert.Equal(0, CountBursts(s, HeroDriver.FrameSeconds + 1e-9));

        var incoming = s.Place(s.Current, false);
        Assert.Equal(8, incoming.Along, 9);
        Assert.Equal(0, incoming.Alpha);

        d.RunTo(t0 + 0.4);
        Assert.Null(s.Flash);

        d.RunTo(t0 + 0.45);
        Assert.Equal(0, s.Place(HeroDriver.Some(s.Previous), true).Alpha, 9);

        d.RunTo(t0 + 0.55);
        Assert.Equal(-46, s.Place(HeroDriver.Some(s.Previous), true).Along, 3);
        Assert.Equal(8 * (1 - HeroMath.EaseOutCubic(0.45 / 0.55)), s.Place(s.Current, false).Along, 6);

        d.RunTo(t0 + 0.7);
        var arrived = s.Place(s.Current, false);
        Assert.Equal(0, arrived.Along, 9);
        Assert.Equal(1, arrived.Alpha, 9);
        Assert.Null(s.Flash);
    }

    [Fact]
    public void InterruptedTransitionContinuesFromWhereTheActorStands()
    {
        var (d, t0) = Upgrade();
        var s = d.Stage;
        d.RunTo(t0 + 0.2);

        d.Change(28, SpeedTitle.Car);
        var standing = s.Place(new HeroActor(1, t0, ActorTransition.Up), false);
        Assert.InRange(standing.Along, -62, -20);
        Assert.InRange(standing.Alpha, 0.1, 0.99);

        var previous = HeroDriver.Some(s.Previous);
        Assert.Equal(1, previous.Vehicle);
        Assert.Equal(ActorTransition.Up, previous.Transition);
        Assert.Equal(standing.Along, previous.BaseOffset, 12);
        Assert.Equal(standing.Alpha, previous.BaseAlpha, 12);
        var outgoing = s.Place(previous, true);
        Assert.Equal(standing.Along, outgoing.Along, 9);
        Assert.Equal(standing.Alpha, outgoing.Alpha, 9);
    }

    [Fact]
    public void OffCutsBetweenVehiclesAndFreezesTheWorld()
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Off));
        var walkRest = d.Stage.Distance;

        d.Change(10, SpeedTitle.Bicycle);
        var s = d.Stage;
        var previous = HeroDriver.Some(s.Previous);
        Assert.Equal(ActorTransition.Cut, previous.Transition);
        Assert.Equal(0, s.Place(previous, true).Alpha);
        Assert.Equal(ActorTransition.Stay, s.Current.Transition);
        Assert.Equal(1, s.Place(s.Current, false).Alpha);
        Assert.Equal(0, s.Place(s.Current, false).Along);
        Assert.False(s.StartupRunning);
        Assert.False(s.Animated);

        d.Run(2, st =>
        {
            Assert.Equal(st.GroundTarget, st.Ground);
            Assert.Equal(walkRest, st.Distance);
            Assert.Equal(0, st.Particles.AliveCount);
            Assert.Equal(0, st.Streaks.AliveCount);
            Assert.Equal(0, st.StreakCap);
            Assert.Equal(default, st.Shake);
        });
        Assert.Equal(s.CurrentVehicle.Motion.Rest, s.DistanceFor(s.CurrentVehicle));
        Assert.InRange(s.StaticMarks, 1, 3);

        var t1 = d.Change(28, SpeedTitle.Car);
        previous = HeroDriver.Some(s.Previous);
        Assert.Equal(ActorTransition.Cut, previous.Transition);
        Assert.Equal(0, s.Place(previous, true).Alpha);
        var current = s.Place(s.Current, false);
        Assert.Equal(1, current.Alpha);
        Assert.Equal(0, current.Along);
        d.RunTo(t1 + 0.4);
        Assert.Null(s.Flash);
        Assert.Equal(walkRest, s.Distance);
    }

    [Fact]
    public void OffStartUpOfTheSameVehicleStays()
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Off));
        d.Change(5, SpeedTitle.Walk);
        Assert.Null(d.Stage.Previous);
        Assert.Equal(ActorTransition.Stay, d.Stage.Current.Transition);
        Assert.False(d.Stage.StartupRunning);
    }

    [Fact]
    public void ReduceMotionCrossfadesAndHoldsTheRestPose()
    {
        var d = new HeroDriver(options: new HeroOptions(EffectsIntensity.Standard, ReduceMotion: true));
        var walkRest = d.Stage.Distance;
        var s = d.Stage;

        var t0 = d.Change(10, SpeedTitle.Bicycle);
        Assert.Equal(ActorTransition.Fade, HeroDriver.Some(s.Previous).Transition);
        Assert.Equal(ActorTransition.Fade, s.Current.Transition);
        Assert.False(s.StartupRunning);
        Assert.False(s.Animated);
        Assert.Equal(EffectsIntensity.Soft, s.Level);

        d.RunTo(t0 + 0.15);
        Assert.Equal(0.5, s.Place(HeroDriver.Some(s.Previous), true).Alpha, 6);
        Assert.Equal(0.5, s.Place(s.Current, false).Alpha, 6);

        d.RunTo(t0 + 0.3);
        Assert.Equal(0, s.Place(HeroDriver.Some(s.Previous), true).Alpha, 9);
        Assert.Equal(1, s.Place(s.Current, false).Alpha, 9);

        d.Run(1, st =>
        {
            Assert.Equal(st.GroundTarget, st.Ground);
            Assert.Equal(walkRest, st.Distance);
            Assert.Equal(0, st.Particles.AliveCount);
            Assert.Equal(0, st.Streaks.AliveCount);
            Assert.Equal(default, st.Shake);
        });
        Assert.True(s.Wake > 0.99, "a start-up still recolors");
        Assert.Equal(s.CurrentVehicle.Motion.Rest, s.DistanceFor(s.CurrentVehicle));

        var t1 = d.Change(28, SpeedTitle.Car);
        Assert.Equal(ActorTransition.Fade, HeroDriver.Some(s.Previous).Transition);
        Assert.Equal(ActorTransition.Fade, s.Current.Transition);
        d.RunTo(t1 + 0.4);
        Assert.Null(s.Flash);
        Assert.Equal(0, s.Streaks.AliveCount);
    }

    [Fact]
    public void SameVehicleStartsWhereItStands()
    {
        var d = new HeroDriver();
        var t0 = d.Change(5, SpeedTitle.Walk);
        var s = d.Stage;

        Assert.False(s.Parked);
        Assert.True(s.StartupRunning);
        Assert.Null(s.Previous);
        Assert.Equal(ActorTransition.Stay, s.Current.Transition);
        Assert.Equal(0, s.Current.Vehicle);
        Assert.Equal(1, s.Place(s.Current, false).Alpha);
        Assert.Equal(0, s.Startup(s.Current, false).Flame);

        var previous = s.Wake;
        d.Run(0.25, st =>
        {
            Assert.True(st.Wake >= previous);
            previous = st.Wake;
        });
        Assert.InRange(s.Wake, 0.6, 0.9);
        d.RunTo(t0 + 0.5);
        Assert.InRange(s.Wake, 0.98, 1);
        Assert.True(s.Stand < 0.1, "the walker takes its first step from standing");
        Assert.Equal(StartupFx.None, s.Startup(s.Current, false));
    }

    [Fact]
    public void WalkerStartUpKicksUpThreeDustPuffsAndThreeSpeedLines()
    {
        var d = new HeroDriver();
        var t0 = d.Change(5, SpeedTitle.Walk);
        d.RunTo(t0 + 0.15 + 0.04);
        Assert.Equal(0, CountParticles(d.Stage, HeroParticleKind.Puff));
        d.RunTo(t0 + 0.15 + 0.1);
        Assert.Equal(3, CountParticles(d.Stage, HeroParticleKind.Puff));
        d.RunTo(t0 + 0.15 + 0.26);
        Assert.Equal(3, CountBursts(d.Stage));
    }

    [Fact]
    public void AnotherVehicleFadesInOnTheSpot()
    {
        var d = new HeroDriver();
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        var s = d.Stage;

        var previous = HeroDriver.Some(s.Previous);
        Assert.Equal(ActorTransition.Wake, previous.Transition);
        Assert.Equal(0, previous.Vehicle);
        Assert.Equal(ActorTransition.Wake, s.Current.Transition);
        Assert.Equal(1, s.Current.Vehicle);
        Assert.True(s.StartupRunning);

        (double Tau, double Out, double In)[] expected =
        [
            (0.1, 0.5, 0),
            (0.12, 1 - HeroMath.Smooth(0.6), 0),
            (0.2, 0, HeroMath.Smooth(0.08 / 0.28)),
            (0.26, 0, 0.5),
            (0.40, 0, 1),
        ];
        foreach (var (tau, outgoing, incoming) in expected)
        {
            d.RunTo(t0 + tau);
            var o = s.Place(HeroDriver.Some(s.Previous), true);
            var i = s.Place(s.Current, false);
            Assert.Equal(outgoing, o.Alpha, 6);
            Assert.Equal(incoming, i.Alpha, 6);
            Assert.Equal(0, o.Along);
            Assert.Equal(0, i.Along);
        }
    }

    [Fact]
    public void BicycleRiderStandsOnThePedalsAfterTheDelay()
    {
        var d = new HeroDriver();
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        var max = 0.0;
        var at = 0.0;
        d.Run(1.2, st =>
        {
            var fx = st.Startup(st.Current, false);
            var tau = st.Time - t0 - 0.30;
            if (tau < -1e-9)
            {
                Assert.Equal(0, fx.HipLift);
                Assert.Equal(0, fx.Lurch);
                Assert.Equal(0, fx.Flame);
            }

            if (fx.HipLift > max)
            {
                max = fx.HipLift;
                at = tau;
            }
        });

        Assert.InRange(max, 1.27, 1.3 + 1e-9);
        Assert.InRange(at, 0.18 - 0.02, 0.18 + 0.02);
    }

    [Fact]
    public void BicycleStartUpChoreographyHitsItsPeaks()
    {
        var d = new HeroDriver();
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        var s = d.Stage;

        d.RunTo(t0 + 0.30 + 0.18);
        Assert.Equal(1.3, s.Startup(s.Current, false).HipLift, 6);
        Assert.Equal(StartupFx.None, s.Startup(s.Current, true));
        var pose = new RigPose();
        Assert.True(s.PoseRig(s.CurrentVehicle, s.Startup(s.Current, false), pose));
        Assert.Equal(0.6 * 1.3, pose.RiderOffset.X, 6);

        d.RunTo(t0 + 0.30 + 0.22);
        Assert.Equal(1.6, s.Startup(s.Current, false).Lurch, 6);
        d.RunTo(t0 + 0.30 + 0.6);
        Assert.Equal(0, s.Startup(s.Current, false).HipLift, 6);
        Assert.Equal(StartupFx.None, s.Startup(new HeroActor(0, t0, ActorTransition.Wake), false));
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Standard)]
    public void BicycleStartUpKicksUpSixDustPuffs(EffectsIntensity level)
    {
        var d = new HeroDriver(options: new HeroOptions(level));
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        d.RunTo(t0 + 0.30 + 0.04);
        Assert.Equal(0, CountParticles(d.Stage, HeroParticleKind.Puff));
        d.RunTo(t0 + 0.30 + 0.10);
        Assert.Equal(6, CountParticles(d.Stage, HeroParticleKind.Puff));
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, 6, 6)]
    [InlineData(EffectsIntensity.Standard, 6, 7)]
    [InlineData(EffectsIntensity.Max, 6, 7)]
    public void EBikeStartUpBurstsAreCappedAtSoft(EffectsIntensity level, int dust, int arcs)
    {
        var d = new HeroDriver(options: new HeroOptions(level));
        var t0 = d.Change(14, SpeedTitle.EBike);
        d.RunTo(t0 + 0.30 + 0.10);
        var s = d.Stage;

        Assert.Equal(arcs, CountParticles(s, HeroParticleKind.Arc));
        Assert.Equal(dust, CountParticles(s, HeroParticleKind.Puff));
        var delays = new List<double>();
        foreach (var p in s.Particles.Items)
        {
            if (p.Alive && p.Kind == HeroParticleKind.Arc)
            {
                delays.Add(p.Delay);
            }
        }

        Assert.Equal(Enumerable.Range(0, arcs).Select(i => i * 0.03), delays.Order());
    }

    [Fact]
    public void StartUpAddsThreeBurstSpeedLines()
    {
        var d = new HeroDriver();
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        d.RunTo(t0 + 0.30 + 0.09);
        Assert.Equal(0, CountBursts(d.Stage));
        d.RunTo(t0 + 0.30 + 0.26);
        Assert.Equal(3, CountBursts(d.Stage));
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Standard)]
    public void RocketIgnitionSpringsThePlumeAndBlowsSixRings(EffectsIntensity level)
    {
        var d = new HeroDriver(options: new HeroOptions(level));
        var t0 = d.Change(500, SpeedTitle.Rocket);
        var max = 0.0;
        d.Run(1.0, st =>
        {
            var tau = st.Time - t0 - 0.30;
            var fx = st.Startup(st.Current, false);
            if (tau < -1e-9)
            {
                Assert.Equal(0, fx.Flame);
            }
            else if (tau > 0.4 + 1e-9)
            {
                Assert.Equal(1, fx.Flame);
            }

            max = Math.Max(max, fx.Flame);
        });

        Assert.InRange(max, 1.1, 1.25);

        var again = new HeroDriver(options: new HeroOptions(level));
        var t1 = again.Change(500, SpeedTitle.Rocket);
        again.RunTo(t1 + 0.30);
        var ignition = again.Stage.Startup(again.Stage.Current, false);
        Assert.Equal(6, ignition.Lift, 9);
        Assert.Equal(0.8, ignition.Shake, 9);
        again.RunTo(t1 + 0.35);
        Assert.Equal(6, CountParticles(again.Stage, HeroParticleKind.Ring));
        again.RunTo(t1 + 0.30 + 0.9);
        Assert.Equal(0, again.Stage.Startup(again.Stage.Current, false).Lift, 6);
    }

    [Fact]
    public void FighterJetAfterburnerLightsOff()
    {
        var d = new HeroDriver();
        d.Change(220, SpeedTitle.FighterJet);
        var max = 0.0;
        d.Run(1.0, st => max = Math.Max(max, st.Startup(st.Current, false).Flame));
        Assert.InRange(max, 1.08, 1.3);

        var again = new HeroDriver();
        var t1 = again.Change(220, SpeedTitle.FighterJet);
        again.RunTo(t1 + 0.30 + 0.06);
        Assert.Equal(1, again.Stage.Startup(again.Stage.Current, false).Glow, 6);
        again.RunTo(t1 + 0.30 + 0.5);
        Assert.Equal(1, again.Stage.Startup(again.Stage.Current, false).Shock, 6);
    }

    [Fact]
    public void StartUpEndsAfterItsLifetime()
    {
        var d = new HeroDriver();
        var t0 = d.Change(10, SpeedTitle.Bicycle);
        d.RunTo(t0 + 0.30 + HeroStage.StartupLifetime - 0.05);
        Assert.True(d.Stage.StartupRunning);
        d.RunTo(t0 + 0.30 + HeroStage.StartupLifetime + 0.05);
        Assert.False(d.Stage.StartupRunning);
        Assert.Equal(StartupFx.None, d.Stage.Startup(d.Stage.Current, false));
    }
}
