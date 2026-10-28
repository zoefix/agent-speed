using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Hero.Badge;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Drives a <see cref="BadgeStage"/> at 60 fps the way the dashboard hero does.</summary>
internal sealed class BadgeDriver
{
    public const double R = HeatMath.DefaultRedline;
    public const double Frame = 1.0 / 60;

    public static readonly string[] Ladder =
        ["IDLE", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE", "TEN", "ELEVEN", "TWELVE", "THIRTEEN"];

    private long _frame;

    public BadgeDriver(HeroOptions? options = null, int level = 0, uint seed = 7)
    {
        Stage = new BadgeStage(seed);
        Options = options ?? new HeroOptions(EffectsIntensity.Standard);
        SetLevel(level);
        Step();
    }

    public BadgeStage Stage { get; }

    public HeroOptions Options { get; set; }

    public IReadOnlyList<string> Labels { get; set; } = Ladder;

    public int Level { get; private set; }

    public double ChangedAt { get; set; } = double.NegativeInfinity;

    public double Heat { get; set; }

    public double Overdrive { get; set; }

    public SpeedTier Tier { get; set; }

    public double SweepOrigin { get; set; } = double.NaN;

    public double Time => _frame / 60.0;

    public void Step() => Stage.Step(new BadgeInput(Time, Level, ChangedAt, Heat, Overdrive, Tier, R, Options, SweepOrigin), Labels);

    public void Run(double seconds)
    {
        var frames = (int)Math.Round(seconds * 60);
        for (var i = 0; i < frames; i++)
        {
            _frame++;
            Step();
        }
    }

    /// <summary>The title changes now (the label slide starts with it); returns the time of the change.</summary>
    public double Change(int level)
    {
        _frame++;
        SetLevel(level);
        ChangedAt = Time;
        Step();
        return ChangedAt;
    }

    /// <summary>The next steps see <paramref name="level"/> without moving the time of the change.</summary>
    public void Retitle(int level) => SetLevel(level);

    /// <summary>Runs until <paramref name="tau"/> seconds after <paramref name="start"/>.</summary>
    public void RunTo(double start, double tau)
    {
        while (Time < start + tau - 1e-9)
        {
            _frame++;
            Step();
        }
    }

    private void SetLevel(int level)
    {
        Level = level;
        var value = level == 0 ? 0 : SpeedTitles.Threshold((SpeedTitle)level, R) * 1.25;
        Heat = HeatMath.Heat(value, R);
        Overdrive = HeatMath.Overdrive(value, R);
        Tier = HeatMath.RawTier(value, R);
    }
}

/// <summary>DESIGN 4c, Hero title badge: transitions, rank pips, renames and frame demand.</summary>
public class HeroBadgeStageTests
{
    private static BadgeDriver Settled(int level, HeroOptions? options = null)
    {
        var d = new BadgeDriver(options, level);
        d.Run(2);
        return d;
    }

    [Fact]
    public void StartsSteadyAtItsFirstLevel()
    {
        var d = new BadgeDriver(level: 6);
        var s = d.Stage;

        Assert.Equal(6, s.Level);
        Assert.Null(s.Transition);
        Assert.Equal(1, s.Wake, 9);
        Assert.Equal("SIX", s.Text);
        for (var k = 1; k <= BadgeStage.PipCount; k++)
        {
            Assert.Equal(k <= 6 ? 1 : 0, s.PipLit(k));
        }

        var titles = s.Titles;
        Assert.Null(titles.Second);
        Assert.Equal(1, titles.First.Alpha);
        Assert.Equal(1, titles.First.Scale);
    }

    [Fact]
    public void LevelUpFollowsTheTransitionTable()
    {
        var d = Settled(5);
        var start = d.Change(6);
        var s = d.Stage;

        var transition = Assert.IsType<BadgeTransition>(s.Transition);
        Assert.Equal(BadgeTransitionKind.Up, transition.Kind);
        Assert.Equal(start, transition.Start);

        foreach (var tau in new[] { 0.05, 0.1, 0.15, 0.2, 0.3, 0.5 })
        {
            d.RunTo(start, tau);
            var t = s.Time - start;
            var titles = s.Titles;
            var incoming = Assert.IsType<BadgeTitleItem>(titles.Second);
            Assert.Equal("FIVE", titles.First.Text);
            Assert.Equal(1 - HeroMath.Smooth(t / 0.16), titles.First.Alpha, 9);
            Assert.Equal(1 + 0.12 * HeroMath.EaseOutCubic(t / 0.22), titles.First.Scale, 9);
            Assert.Equal("SIX", incoming.Text);
            Assert.Equal(HeroMath.Smooth((t - 0.08) / 0.12), incoming.Alpha, 9);
            Assert.Equal(BadgeStage.SpringTo1(t - 0.08, 0.72, 0.42, 0.6), incoming.Scale, 9);
        }

        // The incoming scale spring overshoots by about 4 %.
        var peak = 0.0;
        for (var t = 0.0; t < 0.6; t += 0.001)
        {
            peak = Math.Max(peak, BadgeStage.SpringTo1(t, 0.72, 0.42, 0.6));
        }

        Assert.InRange(peak, 1.02, 1.06);

        d.RunTo(start, 1.05);
        Assert.Null(s.Transition);
        Assert.Null(s.Titles.Second);
    }

    [Fact]
    public void LevelUpExtrasRunOnTime()
    {
        var d = Settled(5);
        var start = d.Change(6);
        var s = d.Stage;

        Assert.Null(s.Flash);
        Assert.Null(s.Shockwave);
        d.RunTo(start, 0.2);
        var flash = Assert.IsType<BadgeFlash>(s.Flash);
        var u = (s.Time - start - 0.06) / 0.35;
        Assert.Equal(0.42 * (1 - u) * (1 - u), flash.Alpha, 9);
        Assert.Equal(8 + 32 * HeroMath.EaseOutCubic(u), flash.Radius, 9);
        var ring = Assert.IsType<BadgeRing>(s.Shockwave);
        var p = (s.Time - start - 0.06) / 0.5;
        Assert.Equal(33 + 11 * HeroMath.EaseOutCubic(p), ring.Radius, 9);
        Assert.Equal(1.6 * (1 - p), ring.Width, 9);
        d.RunTo(start, 0.6);
        Assert.Null(s.Flash);
        Assert.Null(s.Shockwave);
    }

    [Fact]
    public void NewlyLitPipsPopFortyFiveMillisecondsApart()
    {
        var d = Settled(5);
        var start = d.Change(8);
        var s = d.Stage;

        Assert.Equal(start + 0.100, s.Pips[5].Pop, 9);
        Assert.Equal(start + 0.145, s.Pips[6].Pop, 9);
        Assert.Equal(start + 0.190, s.Pips[7].Pop, 9);
        Assert.Equal(0, s.PipLit(6));

        d.RunTo(start, 0.12);
        Assert.Equal(1, s.PipLit(6));
        Assert.Equal(0, s.PipLit(7));
        Assert.Equal(0, s.PipRadiusOf(8));
        Assert.InRange(s.PipRadiusOf(6), 0.01, BadgeStage.PipRadius);

        d.RunTo(start, 1);
        Assert.Equal(1, s.PipLit(8));
        Assert.Equal(BadgeStage.CurrentPipRadius, s.PipRadiusOf(8), 2);
        Assert.Equal(BadgeStage.PipRadius, s.PipRadiusOf(7), 2);
    }

    [Fact]
    public void JumpAcrossTitlesIsOneTransition()
    {
        var d = Settled(5);
        d.Change(8);
        var s = d.Stage;

        var transition = Assert.IsType<BadgeTransition>(s.Transition);
        Assert.Equal(5, transition.From);
        Assert.Equal(8, transition.To);
        Assert.Equal(3, transition.Climbed);
        Assert.Equal("FIVE", s.Titles.First.Text);
        Assert.Equal("EIGHT", s.Titles.Second?.Text);
        Assert.Equal(16, s.AliveParticles);

        // The pips catch up in sequence and the flash grows with the titles climbed.
        Assert.True(s.Pips[5].Pop < s.Pips[6].Pop && s.Pips[6].Pop < s.Pips[7].Pop);
        d.RunTo(s.Transition!.Value.Start, 0.2);
        var p = (s.Time - s.Transition!.Value.Start - 0.06) / 0.35;
        Assert.Equal(0.42 * (1 - p) * (1 - p) * 1.4, s.Flash!.Value.Alpha, 9);
    }

    [Fact]
    public void StartupPipsLightThirtyFiveMillisecondsApart()
    {
        var d = Settled(0);
        var start = d.Change(3);
        var s = d.Stage;

        var transition = Assert.IsType<BadgeTransition>(s.Transition);
        Assert.Equal(BadgeTransitionKind.Startup, transition.Kind);
        Assert.Equal(start + 0.120, s.Pips[0].Pop, 9);
        Assert.Equal(start + 0.155, s.Pips[1].Pop, 9);
        Assert.Equal(start + 0.190, s.Pips[2].Pop, 9);
        Assert.Equal(0, s.OuterRingSweep, 9);
        Assert.Equal(0, s.AliveParticles);

        d.RunTo(start, 0.2);
        var u = (s.Time - start) / 0.45;
        Assert.Equal(360 * HeroMath.EaseOutCubic(u), s.OuterRingSweep, 9);
        var titles = s.Titles;
        Assert.Equal("IDLE", titles.First.Text);
        Assert.Equal(0, titles.First.Level);
        Assert.Equal(HeroMath.Smooth((s.Time - start) / 0.25), titles.Second!.Value.Alpha, 9);
        var flash = Assert.IsType<BadgeFlash>(s.Flash);
        var p = (s.Time - start - 0.06) / 0.35;
        Assert.Equal(0.42 * (1 - p) * (1 - p) * Math.Min(1.6, 1 + 0.2 * 2) * 0.6, flash.Alpha, 9);
        Assert.Null(s.Shockwave);

        d.RunTo(start, 0.5);
        Assert.Equal(360, s.OuterRingSweep, 9);
    }

    [Fact]
    public void ParkingDarkensPipsFromTheTop()
    {
        var d = Settled(3);
        var start = d.Change(0);
        var s = d.Stage;

        Assert.Equal(BadgeTransitionKind.Park, s.Transition?.Kind);
        Assert.Equal(start, s.Pips[2].Pop, 9);
        Assert.Equal(start + 0.03, s.Pips[1].Pop, 9);
        Assert.Equal(start + 0.06, s.Pips[0].Pop, 9);

        d.RunTo(start, 0.2);
        Assert.True(s.PipLit(3) < s.PipLit(2) && s.PipLit(2) < s.PipLit(1));
        var titles = s.Titles;
        Assert.Equal("THREE", titles.First.Text);
        Assert.Equal(1 - HeroMath.Smooth((s.Time - start) / 0.4), titles.First.Alpha, 9);
        Assert.Equal(s.IdleAlpha * HeroMath.Smooth((s.Time - start) / 0.4), titles.Second!.Value.Alpha, 9);

        d.RunTo(start, 1.5);
        Assert.Equal(0, s.PipLit(1));
        Assert.True(s.Wake < 0.02);
        Assert.Equal(0, s.MoteCount);
    }

    [Fact]
    public void LevelDownSinksAndFadesThePipsTogether()
    {
        var d = Settled(8);
        var start = d.Change(6);
        var s = d.Stage;

        Assert.Equal(start, s.Pips[6].Pop, 9);
        Assert.Equal(start, s.Pips[7].Pop, 9);
        Assert.Equal(0, s.AliveParticles);

        d.RunTo(start, 0.175);
        Assert.Equal(1 - (s.Time - start) / 0.35, s.PipLit(7), 9);
        Assert.Equal(s.PipLit(7), s.PipLit(8), 9);
        var t = s.Time - start;
        var titles = s.Titles;
        Assert.Equal(5 * HeroMath.EaseInOutSine(t / 0.3), titles.First.Dy, 9);
        Assert.Equal(-3 * (1 - HeroMath.EaseOutCubic((t - 0.12) / 0.35)), titles.Second!.Value.Dy, 9);
        Assert.Null(s.Flash);
        Assert.Null(s.Shockwave);
    }

    [Fact]
    public void SameTextOnANewLevelDoesNotTransitionTheTitle()
    {
        var labels = (string[])BadgeDriver.Ladder.Clone();
        labels[7] = labels[6];
        var d = Settled(6);
        d.Labels = labels;
        d.Run(0.1);
        var start = d.Change(7);
        var s = d.Stage;

        Assert.NotNull(s.Transition);
        Assert.Null(s.RenameFrom);
        d.RunTo(start, 0.1);
        var titles = s.Titles;
        Assert.Null(titles.Second);
        Assert.Equal("SIX", titles.First.Text);
        Assert.Equal(1, titles.First.Alpha);
        Assert.Equal(1, titles.First.Scale);
        Assert.Equal(start + 0.1, s.Pips[6].Pop, 9);

        d.RunTo(start, 0.5);
        Assert.Equal(1, s.PipLit(7));
    }

    [Fact]
    public void ThemeChangeCrossfadesTheTitleWithoutEffects()
    {
        var d = Settled(4);
        var renamed = BadgeDriver.Ladder.Select(label => label.ToLowerInvariant()).ToArray();
        d.Labels = renamed;
        d.Run(1.0 / 60);
        var s = d.Stage;
        var start = s.RenameStart;

        Assert.Equal("FOUR", s.RenameFrom);
        Assert.Null(s.Transition);
        Assert.Equal(0, s.AliveParticles);
        d.RunTo(start, 0.125);
        var titles = s.Titles;
        var q = HeroMath.Smooth((s.Time - start) / 0.25);
        Assert.Equal("FOUR", titles.First.Text);
        Assert.Equal("four", titles.Second!.Value.Text);
        Assert.Equal(1 - q, titles.First.Alpha, 9);
        Assert.Equal(q, titles.Second!.Value.Alpha, 9);
        Assert.Equal(1, titles.Second!.Value.Scale);
        Assert.Null(s.Flash);

        d.RunTo(start, 0.3);
        Assert.Null(s.RenameFrom);
        Assert.Equal("four", s.Titles.First.Text);
        Assert.Null(s.Titles.Second);
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(0.25, true)]
    [InlineData(0.5, false)]
    public void ARecentTitleChangeStartsTheTransitionWhereItHappened(double age, bool kept)
    {
        var d = Settled(5);
        var stamp = d.Time;
        d.Run(age);
        d.ChangedAt = stamp;
        d.Retitle(6);
        d.Run(1.0 / 60);

        Assert.Equal(kept ? stamp : d.Time, d.Stage.Transition!.Value.Start, 9);
    }

    [Fact]
    public void AChangeDuringATransitionStartsFromTheTitleOnScreen()
    {
        var d = Settled(5);
        var first = d.Change(6);
        d.RunTo(first, 0.3);
        var second = d.Change(7);
        var transition = d.Stage.Transition!.Value;

        Assert.Equal(6, transition.From);
        Assert.Equal(7, transition.To);
        Assert.Equal(second, transition.Start);
        Assert.Equal("SIX", d.Stage.Titles.First.Text);
    }

    [Fact]
    public void FrameDemandFollowsWhatMoves()
    {
        var moving = Settled(6);
        Assert.Equal(BadgeFrames.Full, moving.Stage.Demand);

        var parked = Settled(0);
        parked.Run(1);
        Assert.Equal(BadgeFrames.Parked, parked.Stage.Demand);

        // The light band starts 0.63 s into the gauge's idle sweep and runs 0.9 s at 30 fps.
        parked.SweepOrigin = parked.Time - 0.63 - 0.3;
        parked.Run(1.0 / 60);
        Assert.Equal(BadgeFrames.Sweep, parked.Stage.Demand);
        Assert.NotNull(parked.Stage.IdleBand);
        parked.SweepOrigin = parked.Time - 3;
        parked.Run(1.0 / 60);
        Assert.Equal(BadgeFrames.Parked, parked.Stage.Demand);
        Assert.Null(parked.Stage.IdleBand);

        var off = Settled(6, new HeroOptions(EffectsIntensity.Off));
        Assert.Equal(BadgeFrames.None, off.Stage.Demand);
        off.Change(7);
        Assert.Equal(BadgeFrames.Full, off.Stage.Demand);
        off.Run(0.1);
        Assert.Equal(BadgeFrames.None, off.Stage.Demand);

        var reduced = Settled(0, new HeroOptions(EffectsIntensity.Standard, ReduceMotion: true));
        Assert.Equal(BadgeFrames.None, reduced.Stage.Demand);
        reduced.Change(4);
        Assert.Equal(BadgeFrames.Full, reduced.Stage.Demand);
        reduced.Run(2);
        Assert.Equal(BadgeFrames.None, reduced.Stage.Demand);

        var fresh = new BadgeStage();
        Assert.Equal(BadgeFrames.Full, fresh.Demand);
        Assert.Equal(Orb.FrameDemand.Medium, BadgeFrameRates.ToFrameDemand(BadgeFrames.Parked));
        Assert.Equal(Orb.FrameDemand.Full, BadgeFrameRates.ToFrameDemand(BadgeFrames.Sweep));
        Assert.Equal(Orb.FrameDemand.None, BadgeFrameRates.ToFrameDemand(BadgeFrames.None));
    }

    [Fact]
    public void IdleLightBandCrossesInStepWithTheGaugeSweep()
    {
        var d = Settled(0);
        var origin = d.Time;
        d.SweepOrigin = origin;
        d.Run(0.5);
        Assert.Null(d.Stage.IdleBand);

        d.RunTo(origin, 0.63 + 0.45);
        var band = Assert.IsType<BadgeBand>(d.Stage.IdleBand);
        var u = (d.Time - origin - 0.63) / 0.9;
        Assert.Equal(-10 + 120 * HeroMath.EaseInOutSine(u), band.X, 9);
        Assert.Equal(9, band.HalfWidth);
        Assert.Equal(0, band.Slope);
        Assert.Equal(0.75, band.Alpha);

        d.RunTo(origin, 1.6);
        Assert.Null(d.Stage.IdleBand);
    }

    [Fact]
    public void WithoutAGaugeSweepTheBandKeepsItsOwnCadence()
    {
        var d = Settled(0);
        var bands = 0;
        var wasOn = false;
        for (var i = 0; i < 60 * 13; i++)
        {
            d.Run(1.0 / 60);
            var on = d.Stage.IdleBand is not null;
            bands += on && !wasOn ? 1 : 0;
            wasOn = on;
        }

        Assert.Equal(2, bands);
    }

    [Fact]
    public void ResetStartsFromTheSteadyState()
    {
        var d = Settled(5);
        d.Change(9);
        Assert.NotNull(d.Stage.Transition);

        d.Stage.Reset();
        d.Run(1.0 / 60);
        Assert.Null(d.Stage.Transition);
        Assert.Equal(9, d.Stage.Level);
        Assert.Equal(1, d.Stage.PipLit(9));
        Assert.Equal(0, d.Stage.AliveParticles);
    }
}
