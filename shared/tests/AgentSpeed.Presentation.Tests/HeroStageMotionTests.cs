using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 4c "Speed model", "Idle, start-up and parking" (parked state, parking, snapping).</summary>
public class HeroStageMotionTests
{
    private const double R = HeatMath.ReferenceRedline;

    [Theory]
    [InlineData("sportsCar", "sportsCar")]
    [InlineData("bicycle", "bicycle")]
    [InlineData("rocket", "rocket")]
    [InlineData("walk", "walk")]
    [InlineData(null, "walk")]
    [InlineData("", "walk")]
    [InlineData("plane", "walk")]
    [InlineData("SportsCar", "walk")]
    public void StartsParkedWithThePersistedVehicle(string? key, string expected)
    {
        var art = HeroDriver.Art;
        var stage = new HeroStage(art, key);

        Assert.Equal(expected, stage.CurrentKey);
        Assert.True(stage.Parked);
        Assert.Equal(art.Vehicles[art.IndexOf(expected)].Motion.Rest, stage.Distance);
        Assert.Equal(1, stage.Stand);
        Assert.Equal(0, stage.Wake);
        Assert.Equal(0, stage.Ground);
        Assert.Equal(SpeedTitle.Idle, stage.Title);
        Assert.Null(stage.Previous);
        Assert.False(stage.StartupRunning);
        Assert.Equal(ActorTransition.Stay, stage.Current.Transition);
        Assert.Equal(0, stage.StreakCap);
    }

    [Fact]
    public void RequiresArt() => Assert.Throws<ArgumentNullException>(() => new HeroStage(null!));

    [Fact]
    public void StaysParkedWhileIdle()
    {
        var d = new HeroDriver("bicycle");
        var rest = d.Stage.Distance;
        d.Run(3, s =>
        {
            Assert.True(s.Parked);
            Assert.Equal(rest, s.Distance);
            Assert.Equal(0, s.Ground);
            Assert.Equal(0, s.GroundTarget);
            Assert.Equal(0, s.Particles.AliveCount);
        });
        Assert.Equal("bicycle", d.Stage.CurrentKey);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(150)]
    public void BandProgressSpansEachTitleGeometrically(double redline)
    {
        for (var i = 0; i < SpeedTitles.Ladder.Count - 1; i++)
        {
            var title = SpeedTitles.Ladder[i].Title;
            var lo = SpeedTitles.Threshold(title, redline);
            var hi = SpeedTitles.Threshold(title + 1, redline);

            Assert.Equal(0, HeroStage.BandProgress(title, lo, redline, 0), 9);
            Assert.Equal(1, HeroStage.BandProgress(title, hi, redline, 0), 9);
            Assert.Equal(0.5, HeroStage.BandProgress(title, Math.Sqrt(lo * hi), redline, 0), 9);
            Assert.Equal(0, HeroStage.BandProgress(title, lo / 2, redline, 0));
            Assert.Equal(1, HeroStage.BandProgress(title, hi * 3, redline, 0));
            Assert.Equal(0, HeroStage.BandProgress(title, 0, redline, 0));
        }
    }

    [Fact]
    public void RocketBandIsTheOverdrive()
    {
        Assert.Equal(0.3, HeroStage.BandProgress(SpeedTitle.Rocket, 400, R, 0.3));
        Assert.Equal(0, HeroStage.BandProgress(SpeedTitle.Rocket, 10_000, R, 0));
        Assert.Equal(1, HeroStage.BandProgress(SpeedTitle.Rocket, 400, R, 1.7));
        Assert.Equal(0, HeroStage.BandProgress(SpeedTitle.Rocket, 400, R, -0.2));
        Assert.Equal(0, HeroStage.BandProgress(SpeedTitle.Idle, 50, R, 0));
    }

    [Theory]
    [InlineData(28, SpeedTitle.Car)]
    [InlineData(5, SpeedTitle.Walk)]
    [InlineData(10, SpeedTitle.Bicycle)]
    [InlineData(330, SpeedTitle.Hypersonic)]
    [InlineData(600, SpeedTitle.Rocket)]
    public void HoldingAValueSetsTheGroundTargetFromTheBand(double value, SpeedTitle title)
    {
        var d = new HeroDriver();
        d.Change(value, title);
        d.Run(5);
        var s = d.Stage;
        var vehicle = s.CurrentVehicle;

        Assert.Equal(VehicleArt.KeyFor(title), s.CurrentKey);
        Assert.Equal(value, s.SmoothedValue, 6);

        Assert.Equal(HeroStage.BandProgress(title, value, R, HeatMath.Overdrive(value, R)), s.Band, 6);
        Assert.Equal(HeroStage.BandProgress(title, s.SmoothedValue, R, s.Overdrive), s.Band, 12);
        Assert.Equal(1, s.Coast);
        Assert.Equal(HeroMath.Lerp(vehicle.Motion.SpeedLow, vehicle.Motion.SpeedHigh, s.Band) * Math.Sqrt(s.Coast), s.GroundTarget, 9);
        Assert.InRange(s.GroundTarget, vehicle.Motion.SpeedLow, vehicle.Motion.SpeedHigh);
        Assert.Equal(s.GroundTarget, s.Ground, 3);
        Assert.Equal(0, s.Acceleration, 3);
    }

    [Fact]
    public void CarBandMatchesTheLogFormula()
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car);
        d.Run(5);

        Assert.Equal(Math.Log(28 / 24.0) / Math.Log(32 / 24.0), d.Stage.Band, 6);
        Assert.Equal(HeroMath.Lerp(184, 230, d.Stage.Band), d.Stage.GroundTarget, 9);
    }

    [Fact]
    public void DistanceIntegratesTheGroundSpeed()
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car);
        var start = d.Stage.Distance;
        var sum = 0.0;
        d.Run(3, s => sum += s.Ground * HeroDriver.FrameSeconds);

        Assert.True(sum > 300, "the car covers ground");
        Assert.Equal(start + sum, d.Stage.Distance, 6);
    }

    [Fact]
    public void GroundRisesThroughItsSpringWithoutOvershoot()
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car);
        var previous = 0.0;
        var target = 0.0;
        d.Run(4, s =>
        {
            Assert.True(s.Ground >= previous - 1e-9, "a critically damped launch never jerks back");
            previous = s.Ground;
            target = s.GroundTarget;
        });

        Assert.InRange(previous, 0, target + 1e-6);
    }

    [Theory]
    [InlineData(12, 0.5)]
    [InlineData(18, 0.75)]
    public void CoastingSlowsTheVehicleWhileTheTitleHolds(double value, double coast)
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car);
        d.Run(4);
        var full = d.Stage.GroundTarget;

        d.Change(value, SpeedTitle.Car);
        d.Run(4);
        var s = d.Stage;

        Assert.Equal(SpeedTitle.Car, s.Title);
        Assert.Equal(value, s.SmoothedValue, 6);
        Assert.Equal(coast, s.Coast, 6);
        Assert.Equal(0, s.Band);
        Assert.Equal(s.CurrentVehicle.Motion.SpeedLow * Math.Sqrt(coast), s.GroundTarget, 4);
        Assert.True(s.GroundTarget < full);
        Assert.Equal(s.GroundTarget, s.Ground, 3);
    }

    [Fact]
    public void ParkingCoastsToAStopAndKeepsTheCrankPosition()
    {
        var d = new HeroDriver();
        d.Change(10, SpeedTitle.Bicycle);
        d.Run(3);
        var s = d.Stage;
        Assert.True(s.Ground > 50);
        var rest = s.CurrentVehicle.Motion.Rest;

        d.Change(0, SpeedTitle.Idle);
        Assert.True(s.Parked);
        Assert.Equal(0, s.GroundTarget);
        Assert.Equal("bicycle", s.CurrentKey);

        var previous = s.Ground;
        d.Run(4, st =>
        {
            Assert.True(st.Ground <= previous + 1e-9);
            Assert.True(st.Ground >= -1e-6);
            previous = st.Ground;
        });

        Assert.True(s.Ground < 0.01);
        Assert.True(s.Wake < 0.01);
        Assert.Equal(0, s.StreakCap);

        var parked = s.Distance;
        Assert.True(parked > rest + 100, "the bicycle travelled before parking");
        d.Run(2);
        Assert.Equal(parked, s.Distance, 2);
        Assert.Equal(s.Distance, s.DistanceFor(s.CurrentVehicle));
        var crank = s.CurrentVehicle.Part("crankArm")?.Spin ?? throw new InvalidOperationException("The bicycle has no crank.");
        Assert.Equal(HeroMath.WrapRadians(s.Distance / crank.Radius), s.SpinAngle(s.CurrentVehicle, crank), 12);
    }

    [Fact]
    public void ParkedWalkerSettlesIntoTheStandingPose()
    {
        var d = new HeroDriver();
        d.Change(5, SpeedTitle.Walk);
        d.Run(3);
        Assert.True(d.Stage.Stand < 0.01);

        d.Change(0, SpeedTitle.Idle);
        d.Run(3);
        var s = d.Stage;
        Assert.True(s.Stand > 0.999);

        var pose = new RigPose();
        Assert.True(s.PoseRig(s.CurrentVehicle, StartupFx.None, pose));
        var ground = s.CurrentVehicle.GroundY ?? 79;
        var rig = s.CurrentVehicle.Walk ?? throw new InvalidOperationException("The walker has no walk rig.");
        var hip = pose[2][0];
        Assert.Equal(ground - 0.6 - 0.97 * (rig.Thigh + rig.Shin), hip.Y, 2);
        Assert.Equal(hip.X + 1.6, pose[3][2].X, 2);
        Assert.Equal(hip.X - 1.6, pose[0][2].X, 2);
        Assert.Equal(ground - 0.6, pose[3][2].Y, 2);
    }

    [Fact]
    public void VehiclesWithoutARigPoseNothing()
    {
        var d = new HeroDriver("car");
        var pose = new RigPose();
        Assert.False(d.Stage.PoseRig(d.Stage.CurrentVehicle, StartupFx.None, pose));
    }

    [Fact]
    public void SnapJumpsStraightToTheCurrentState()
    {
        var d = new HeroDriver();
        d.Change(28, SpeedTitle.Car, snap: true);
        var s = d.Stage;

        Assert.False(s.Parked);
        Assert.Equal("car", s.CurrentKey);
        Assert.Null(s.Previous);
        Assert.Equal(ActorTransition.Stay, s.Current.Transition);
        Assert.False(s.StartupRunning);
        Assert.Null(s.Flash);
        Assert.Equal(1, s.Wake);
        Assert.Equal(HeatMath.Heat(28, R), s.Heat);
        Assert.Equal(s.GroundTarget, s.Ground);
        Assert.True(s.GroundTarget > 184);
        Assert.Equal(0, s.Stand);
        Assert.Equal(1, s.Place(s.Current, false).Alpha);
        Assert.Equal(0, s.Particles.AliveCount);

        d.Change(0, SpeedTitle.Idle, snap: true);
        Assert.True(s.Parked);
        Assert.Equal(0, s.Wake);
        Assert.Equal(0, s.Ground);
        Assert.Equal(1, s.Stand);
        Assert.Equal("car", s.CurrentKey);
    }

    [Fact]
    public void SnapClearsARunningTransition()
    {
        var d = new HeroDriver();
        d.Change(5, SpeedTitle.Walk);
        d.Run(2);
        d.Change(10, SpeedTitle.Bicycle);
        Assert.NotNull(d.Stage.Previous);

        d.Change(28, SpeedTitle.Car, snap: true);
        Assert.Null(d.Stage.Previous);
        Assert.Null(d.Stage.Flash);
        Assert.Equal(ActorTransition.Stay, d.Stage.Current.Transition);
        var bursts = 0;
        foreach (var streak in d.Stage.Streaks.Items)
        {
            bursts += streak.Alive && streak.Burst ? 1 : 0;
        }

        Assert.Equal(0, bursts);
    }

    [Fact]
    public void OptionsCapTheLayer()
    {
        Assert.Equal(EffectsIntensity.Soft, new HeroOptions(EffectsIntensity.Max, ReduceMotion: true).Level);
        Assert.False(new HeroOptions(EffectsIntensity.Max, ReduceMotion: true).Animated);
        Assert.Equal(EffectsIntensity.Soft, new HeroOptions(EffectsIntensity.Standard, HighContrast: true).Level);
        Assert.Equal(EffectsIntensity.Off, new HeroOptions(EffectsIntensity.Off, ReduceMotion: true).Level);
        Assert.False(new HeroOptions(EffectsIntensity.Off).Animated);
        Assert.True(new HeroOptions(EffectsIntensity.Soft).Animated);
        Assert.Equal(EffectsIntensity.Max, new HeroOptions(EffectsIntensity.Max).Level);
        Assert.True(new HeroOptions(EffectsIntensity.Max).Animated);
    }

    [Fact]
    public void NonFiniteInputsAreTreatedAsIdle()
    {
        var d = new HeroDriver();
        d.Change(double.NaN, SpeedTitle.Walk);
        d.Run(1);
        Assert.Equal(0, d.Stage.Heat);
        Assert.True(double.IsFinite(d.Stage.Distance));
        Assert.True(double.IsFinite(d.Stage.Ground));

        d.Redline = double.NaN;
        d.Run(0.5);
        Assert.Equal(HeatMath.DefaultRedline, d.Stage.Redline);
    }

    [Fact]
    public void LurchSurgesAlongTheDirectionOfTravel()
    {
        var d = new HeroDriver();
        d.Change(500, SpeedTitle.Rocket, snap: true);
        d.Run(1);
        d.Change(800, SpeedTitle.Rocket);
        d.Run(0.2);
        var s = d.Stage;
        Assert.True(s.Acceleration > 10, $"the rocket accelerates ({s.Acceleration})");

        var placement = s.Place(s.Current, false);
        var motion = s.Motion(s.CurrentVehicle, placement, StartupFx.None);
        var heading = (s.CurrentVehicle.Heading ?? 0) * Math.PI / 180;
        var lurch = Math.Clamp(0.008 * s.Acceleration, -1.2, 1.2);
        var drift = s.CurrentVehicle.Motion.Float * Math.Sin(2 * Math.PI * 0.55 * s.Time);
        Assert.Equal(placement.X + lurch * Math.Cos(heading) - Math.Sin(heading) * drift, motion.OffsetX, 9);
        Assert.Equal(placement.Y + lurch * Math.Sin(heading) + Math.Cos(heading) * drift, motion.OffsetY, 9);
    }
}
