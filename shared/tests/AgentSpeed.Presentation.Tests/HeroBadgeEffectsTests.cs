using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Hero.Badge;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 4c, Hero title badge: effects levels, accessibility, pools, colors and the vehicle ↔ badge crossfade.</summary>
public class HeroBadgeEffectsTests
{
    private static BadgeDriver Hot(EffectsIntensity level, bool reduceMotion = false, bool highContrast = false, int title = 13)
    {
        var d = new BadgeDriver(new HeroOptions(level, reduceMotion, highContrast), title);
        d.Heat = 1;
        d.Overdrive = 0.6;
        d.Tier = SpeedTier.Redline;
        d.Run(3);
        return d;
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, 4, 5)]
    [InlineData(EffectsIntensity.Standard, 8, 9)]
    [InlineData(EffectsIntensity.Max, 12, 14)]
    public void MotesAndStreaksAreCappedPerLevel(EffectsIntensity level, double motes, int streaks)
    {
        var d = Hot(level);
        var s = d.Stage;

        Assert.Equal(motes, s.MoteCount, 9);
        Assert.Equal(streaks, s.StreakCount);
        Assert.Equal(level, s.Fx);
        var maxAlive = 0;
        for (var i = 0; i < 300; i++)
        {
            d.Run(1.0 / 60);
            maxAlive = Math.Max(maxAlive, s.AliveStreaks);
        }

        Assert.InRange(maxAlive, 1, BadgeStage.StreakCapacity);
    }

    [Fact]
    public void MoteCountFollowsHeatAndTheLastOneFadesIn()
    {
        var d = new BadgeDriver(level: 4);
        d.Heat = 0.25;
        d.Run(2);
        var s = d.Stage;

        Assert.Equal(2 + 6 * 0.25, s.MoteCount, 9);
        Assert.Equal(1, s.MoteVisibility(2), 9);
        Assert.Equal(0.5, s.MoteVisibility(3), 9);
        Assert.Equal(0, s.MoteVisibility(4), 9);
        Assert.Equal(8 + 40 * 0.25, s.MoteTrailDegrees, 9);

        // Fill order 0, 6, 3, 9: two motes sit opposite each other, four at right angles.
        var first = s.MoteHeadDegrees(0);
        Assert.Equal(180, s.MoteHeadDegrees(1) - first, 6);
        Assert.Equal(90, s.MoteHeadDegrees(2) - first, 6);
        Assert.Equal(270, s.MoteHeadDegrees(3) - first, 6);
    }

    [Fact]
    public void MotesTurnAtTheSpringDrivenSpeed()
    {
        var d = new BadgeDriver(level: 6);
        d.Run(3);
        var s = d.Stage;

        Assert.Equal(BadgeStage.OmegaTarget(d.Heat, d.Overdrive), s.Omega, 3);
        Assert.Equal(BadgeStage.GroundTarget(d.Heat, d.Overdrive), s.Ground, 2);
        Assert.Equal(2 * Math.PI * (0.06 + 0.9 * d.Heat + 0.5 * d.Overdrive), BadgeStage.OmegaTarget(d.Heat, d.Overdrive), 12);
        Assert.Equal(60 + 840 * d.Heat + 300 * d.Overdrive, BadgeStage.GroundTarget(d.Heat, d.Overdrive), 12);
    }

    [Fact]
    public void StreaksAreBornOnlyInTheBandsAboveAndBelowTheTitle()
    {
        var d = Hot(EffectsIntensity.Max);
        var seen = 0;
        for (var i = 0; i < 600; i++)
        {
            d.Run(1.0 / 60);
            foreach (var streak in d.Stage.Streaks)
            {
                if (!streak.Alive)
                {
                    continue;
                }

                seen++;
                Assert.True(streak.Y is >= 16 and <= 33 or >= 67 and <= 84, $"y {streak.Y}");
                Assert.True(streak.Alpha > 0);
            }
        }

        Assert.True(seen > 100);
    }

    [Fact]
    public void EmbersNeedStandardAndTheOverdriveTier()
    {
        var soft = Hot(EffectsIntensity.Soft);
        Assert.Equal(0, soft.Stage.AliveParticles);

        var standard = Hot(EffectsIntensity.Standard);
        var embers = 0;
        foreach (var p in standard.Stage.Particles)
        {
            embers += p.Alive && p.Ember ? 1 : 0;
        }

        Assert.InRange(embers, 10, BadgeStage.ParticleCapacity);
        foreach (var p in standard.Stage.Particles)
        {
            if (p.Alive && p.Ember)
            {
                Assert.True(p.VelocityY < 0);
                Assert.InRange(p.Life, 0.6, 1.1);
            }
        }

        var boost = new BadgeDriver(level: 9);
        boost.Run(3);
        Assert.True(boost.Tier < SpeedTier.Overdrive);
        Assert.Equal(0, boost.Stage.AliveParticles);
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, 6)]
    [InlineData(EffectsIntensity.Standard, 10)]
    [InlineData(EffectsIntensity.Max, 10)]
    public void LevelUpSparkBurstsPerLevel(EffectsIntensity level, int sparks)
    {
        var d = new BadgeDriver(new HeroOptions(level), 5);
        d.Run(1);
        d.Change(6);

        Assert.Equal(sparks, d.Stage.AliveParticles);
        foreach (var p in d.Stage.Particles)
        {
            if (p.Alive)
            {
                Assert.False(p.Ember);
                Assert.True(p.Age < 0, "sparks appear at τ = 0.08");
            }
        }
    }

    [Fact]
    public void SheenAndShakeNeedStandard()
    {
        var soft = Hot(EffectsIntensity.Soft);
        Assert.Equal(default, soft.Stage.Shake);
        Assert.Null(FindSheen(soft));

        var standard = Hot(EffectsIntensity.Standard);
        Assert.NotEqual(default, standard.Stage.Shake);
        var sheen = Assert.IsType<BadgeBand>(FindSheen(standard));
        Assert.Equal(18, sheen.HalfWidth);
        Assert.Equal(-0.45, sheen.Slope);
        Assert.Equal(0.4, sheen.Alpha);

        // Shake amplitude A = 0.2 + 0.5 o (× 1.4 at Max): never more than A per axis.
        var max = Hot(EffectsIntensity.Max);
        var amplitude = (0.2 + 0.5 * 0.6) * 1.4;
        for (var i = 0; i < 120; i++)
        {
            max.Run(1.0 / 60);
            Assert.InRange(max.Stage.Shake.X, -amplitude, amplitude);
            Assert.InRange(max.Stage.Shake.Y, -amplitude, amplitude);
        }
    }

    private static BadgeBand? FindSheen(BadgeDriver d)
    {
        for (var i = 0; i < 400; i++)
        {
            d.Run(1.0 / 60);
            if (d.Stage.Sheen(40) is { } band)
            {
                return band;
            }
        }

        return null;
    }

    [Fact]
    public void OffIsStaticAndCuts()
    {
        var d = new BadgeDriver(new HeroOptions(EffectsIntensity.Off), 5);
        d.Run(1);
        var s = d.Stage;
        var phase = s.MotePhase;
        d.Change(9);

        Assert.Null(s.Titles.Second);
        Assert.Equal("NINE", s.Titles.First.Text);
        Assert.Equal(1, s.PipLit(9));
        Assert.Equal(0, s.AliveParticles);
        Assert.Null(s.Flash);
        Assert.Null(s.Shockwave);
        Assert.Equal(0, s.MoteCount);
        Assert.Equal(0, s.StreakCount);
        Assert.Equal(0, s.HaloPhase);
        d.Run(1);
        Assert.Equal(phase, s.MotePhase);

        var glow = s.Glow(0);
        Assert.Equal(0.30, glow.Strength, 9);
        d.Labels = BadgeDriver.Ladder.Select(label => label + "!").ToArray();
        d.Run(1.0 / 60);
        Assert.Null(s.RenameFrom);
        Assert.Equal("NINE!", s.Titles.First.Text);
    }

    [Fact]
    public void ReduceMotionCapsAtSoftAndRemovesMotion()
    {
        var d = Hot(EffectsIntensity.Max, reduceMotion: true, title: 12);
        var s = d.Stage;

        Assert.Equal(EffectsIntensity.Soft, s.Fx);
        Assert.False(s.Moving);
        Assert.Equal(0, s.MoteCount);
        Assert.Equal(0, s.StreakCount);
        Assert.Equal(0, s.AliveParticles);
        Assert.Equal(default, s.Shake);
        Assert.Null(FindSheen(d));

        var start = d.Change(10);
        Assert.Equal(0, s.AliveParticles);
        Assert.Equal(start, s.Pips[11].Pop);
        Assert.Equal(start, s.Pips[10].Pop);
        d.RunTo(start, 0.15);
        var titles = s.Titles;
        var q = HeroMath.Smooth((s.Time - start) / 0.3);
        Assert.Equal(1 - q, titles.First.Alpha, 9);
        Assert.Equal(q, titles.Second!.Value.Alpha, 9);
        Assert.Equal(1, titles.First.Scale);
        Assert.Equal(1, titles.Second!.Value.Scale);
        Assert.Equal(0, titles.First.Dy);
        Assert.Null(s.Flash);
        Assert.Null(s.Shockwave);
        Assert.Equal(1 - (s.Time - start) / 0.3, s.PipLit(12), 9);
        Assert.Equal(s.PipLit(12), s.PipLit(11), 9);

        // Idle is static: no breathing, no light band.
        var idle = new BadgeDriver(new HeroOptions(EffectsIntensity.Standard, ReduceMotion: true), 0);
        idle.SweepOrigin = 0;
        idle.Run(1);
        Assert.Equal(0.55, idle.Stage.IdleAlpha, 9);
        Assert.Null(idle.Stage.IdleBand);
        Assert.Equal(360, idle.Stage.OuterRingSweep);
    }

    [Fact]
    public void HighContrastDropsGlowHaloMotesStreaksAndParticles()
    {
        var d = Hot(EffectsIntensity.Max, highContrast: true, title: 12);
        var s = d.Stage;

        Assert.False(s.Decorated);
        Assert.Equal(0, s.MoteCount);
        Assert.Equal(0, s.StreakCount);
        Assert.Equal(0, s.AliveParticles);
        Assert.Equal(0, s.HaloAlpha);
        Assert.Equal(0, s.Glow(0.8).Strength);
        Assert.Equal(1, s.IdleAlpha);
        Assert.Equal(BadgeFrames.None, s.Demand);
        d.Change(13);
        Assert.Equal(0, s.AliveParticles);
        Assert.Null(s.Flash);
    }

    [Fact]
    public void IncreaseContrastHalvesTheGlowAndDropsTheHalo()
    {
        var plain = new BadgeDriver(level: 8);
        plain.Run(1);
        var increased = new BadgeDriver(new HeroOptions(EffectsIntensity.Standard, IncreasedContrast: true), 8);
        increased.Run(1);

        Assert.True(plain.Stage.HaloAlpha > 0);
        Assert.Equal(0, increased.Stage.HaloAlpha);
        Assert.Equal(plain.Stage.Glow(0).Strength * 0.5, increased.Stage.Glow(0).Strength, 9);
    }

    [Fact]
    public void GlowFollowsTheSpec()
    {
        var d = Hot(EffectsIntensity.Max);
        var glow = d.Stage.Glow(0.8);
        Assert.Equal((1.0 + 1.6 + 0.6 * 0.6) * 1.2, glow.Sigma, 9);
        Assert.Equal((0.30 + 0.45 + 0.15 * 0.6) * 1.8, glow.Strength, 9);

        var light = new BadgeDriver(new HeroOptions(EffectsIntensity.Standard, Dark: false), 6);
        light.Run(1);
        Assert.Equal(0.16 + 0.22 * light.Heat, light.Stage.Glow(0).Strength, 9);
        Assert.Equal(0.7 * (0.86 + 0.14 * Math.Sin(light.Stage.BreathPhase)), light.Stage.IdleAlpha, 9);
    }

    [Fact]
    public void SameSeedReplaysExactly()
    {
        BadgeDriver Run()
        {
            var d = Hot(EffectsIntensity.Max);
            d.Change(12);
            d.Run(0.5);
            d.Change(13);
            d.Run(0.5);
            return d;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a.Stage.Streaks.ToArray(), b.Stage.Streaks.ToArray());
        Assert.Equal(a.Stage.Particles.ToArray(), b.Stage.Particles.ToArray());
        Assert.Equal(a.Stage.MotePhase, b.Stage.MotePhase);
    }

    [Fact]
    public void SteppingAndReadingAFrameNeverAllocate()
    {
        var d = Hot(EffectsIntensity.Max);
        d.Change(12);
        var s = d.Stage;
        var palette = new BadgePalette();
        AllocationProbe.AssertNone(() =>
        {
            for (var i = 0; i < 120; i++)
            {
                d.Run(1.0 / 60);
                _ = s.Titles;
                _ = s.Demand;
                _ = s.Shake;
                _ = s.Flash;
                _ = s.Shockwave;
                _ = s.Sheen(40);
                _ = s.IdleBand;
                _ = s.Glow(0.2);
                for (var k = 1; k <= BadgeStage.PipCount; k++)
                {
                    _ = s.PipRadiusOf(k);
                    _ = palette.Pip(k, BadgeDriver.R, true);
                }

                for (var slot = 0; slot < BadgeStage.MoteSlots; slot++)
                {
                    _ = s.MoteVisibility(slot) + s.MoteHeadDegrees(slot);
                }

                _ = palette.Resolve(s.Input.Heat, s.Input.Overdrive, s.Wake, true, false);
            }
        });
    }

    [Fact]
    public void PaletteFollowsTheVehicleColors()
    {
        var palette = new BadgePalette();
        var dark = palette.Resolve(0.5, 0, 1, true, false);
        Assert.Equal(HeroPalette.Stroke(0.5, true).ToHex(), dark.Color.ToHex());
        Assert.Equal(HeroPalette.Hot(0.5, 0, true).ToHex(), dark.Hot.ToHex());
        Assert.Equal(HeatPalette.WhiteHot, dark.Core);
        Assert.Equal(0.12, dark.Unlit.A, 9);

        var parked = palette.Resolve(0.5, 0, 0, true, false);
        Assert.Equal(HeroPalette.Stroke(0, true).ToHex(), parked.Color.ToHex());
        Assert.Equal(HeatPalette.Sample(0), parked.IdleFill);

        var light = palette.Resolve(0.5, 0, 1, false, true);
        Assert.Equal(HeroPalette.AmberHot, light.Core);
        Assert.Equal(0.3, light.Unlit.A, 9);
        Assert.True(Rgba.ContrastRatio(light.IdleFill, Rgba.FromHex("#E6E6E6")) >= 4.5);

        // Lit pips in light mode reach at least 3:1; dark pips are the heat of their threshold.
        for (var k = 1; k <= BadgeStage.PipCount; k++)
        {
            Assert.True(Rgba.ContrastRatio(palette.Pip(k, BadgeDriver.R, false), Rgba.FromHex("#E6E6E6")) >= 3);
            Assert.Equal(HeatPalette.Sample(SpeedTitles.HeatAt((SpeedTitle)k, BadgeDriver.R)), palette.Pip(k, BadgeDriver.R, true));
        }
    }

    [Fact]
    public void ArtCrossfadesBetweenVehicleAndBadge()
    {
        var art = new HeroArtMix();
        Assert.True(art.Update(false, 10, false));
        Assert.Equal(0, art.BadgeMix(10));
        Assert.False(art.IsFading(10));
        Assert.False(art.Update(false, 11, false));

        Assert.True(art.Update(true, 20, false));
        Assert.True(art.IsFading(20.1));
        Assert.Equal(HeroMath.Smooth(0.5), art.BadgeMix(20.15), 9);
        Assert.Equal(1, art.BadgeMix(20.3), 9);
        Assert.False(art.IsFading(20.31));

        // Switching back mid-fade continues from what is on screen.
        Assert.True(art.Update(true, 30, false) == false);
        art.Update(false, 30, false);
        art.Update(true, 30.15, false);
        var from = art.BadgeMix(30.15);
        Assert.Equal(1 - HeroMath.Smooth(0.5), from, 9);
        Assert.Equal(1, art.BadgeMix(30.45), 9);

        // Reduce Motion cuts.
        art.Update(false, 40, true);
        Assert.Equal(0, art.BadgeMix(40));
        Assert.False(art.IsFading(40));

        // The first theme seen is shown at once.
        var badge = new HeroArtMix();
        badge.Update(true, 5, false);
        Assert.Equal(1, badge.BadgeMix(5));
    }
}
