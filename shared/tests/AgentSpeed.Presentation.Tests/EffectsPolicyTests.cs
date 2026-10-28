using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class EffectsPolicyTests
{
    [Fact]
    public void OffDrawsStaticVisualsOnly()
    {
        var p = new EffectsPolicy(EffectsIntensity.Off);
        Assert.Equal(EffectsIntensity.Off, p.Level);
        Assert.True(p.Aura);
        Assert.False(p.AuraBreathing);
        Assert.False(p.Comet);
        Assert.False(p.Particles);
        Assert.False(p.Pulses);
        Assert.False(p.Jitter);
        Assert.False(p.Skew);
        Assert.False(p.Shimmer);
        Assert.False(p.HeatDistortion);
        Assert.False(p.RollingDigits);
        Assert.False(p.ValueGlow);
        Assert.False(p.IdleSweep);
        Assert.False(p.PeakBurst);
        Assert.Equal(1, p.ParticleRate);
        Assert.True(p.Bloom < 1);
    }

    [Fact]
    public void SoftAddsAuraCometAndPulsesButNoParticlesOrJitter()
    {
        var p = new EffectsPolicy(EffectsIntensity.Soft);
        Assert.Equal(EffectsIntensity.Soft, p.Level);
        Assert.True(p.Aura);
        Assert.True(p.AuraBreathing);
        Assert.True(p.Comet);
        Assert.True(p.Pulses);
        Assert.True(p.PulseExpands);
        Assert.True(p.RollingDigits);
        Assert.True(p.IdleSweep);
        Assert.True(p.PeakBurst);
        Assert.False(p.Particles);
        Assert.False(p.Jitter);
        Assert.False(p.Skew);
        Assert.False(p.HeatDistortion);
        Assert.Equal(1, p.Bloom);
    }

    [Fact]
    public void StandardAddsParticlesSkewAndJitter()
    {
        var p = new EffectsPolicy(EffectsIntensity.Standard);
        Assert.Equal(EffectsIntensity.Standard, p.Level);
        Assert.True(p.Comet);
        Assert.True(p.Pulses);
        Assert.True(p.Particles);
        Assert.True(p.Skew);
        Assert.True(p.Jitter);
        Assert.True(p.Shimmer);
        Assert.False(p.HeatDistortion);
        Assert.Equal(1, p.ParticleRate);
        Assert.Equal(1, p.Bloom);
    }

    [Fact]
    public void MaxBoostsParticlesAndBloomAndAddsDistortion()
    {
        var p = new EffectsPolicy(EffectsIntensity.Max);
        Assert.Equal(EffectsIntensity.Max, p.Level);
        Assert.True(p.Particles);
        Assert.True(p.Jitter);
        Assert.True(p.HeatDistortion);
        Assert.Equal(1.6, p.ParticleRate);
        Assert.True(p.Bloom > new EffectsPolicy(EffectsIntensity.Standard).Bloom);
    }

    [Theory]
    [InlineData(EffectsIntensity.Max, EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Standard, EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Soft, EffectsIntensity.Soft)]
    [InlineData(EffectsIntensity.Off, EffectsIntensity.Off)]
    public void ReduceMotionCapsAtSoft(EffectsIntensity requested, EffectsIntensity level)
    {
        var p = new EffectsPolicy(requested, ReduceMotion: true);
        Assert.Equal(level, p.Level);
        Assert.False(p.Particles);
        Assert.False(p.Jitter);
        Assert.False(p.Skew);
        Assert.False(p.HeatDistortion);
        Assert.False(p.Comet);
        Assert.False(p.RollingDigits);
        Assert.False(p.SlideTransitions);
        Assert.False(p.HoverScale);
        Assert.False(p.PulseExpands);
        Assert.Equal(1, p.ParticleRate);
    }

    [Fact]
    public void WithoutReduceMotionMovementIsAllowed()
    {
        var p = new EffectsPolicy(EffectsIntensity.Off);
        Assert.True(p.SlideTransitions);
        Assert.True(p.HoverScale);
        Assert.True(p.PulseExpands);
    }

    [Theory]
    [InlineData(EffectsIntensity.Max, false)]
    [InlineData(EffectsIntensity.Standard, true)]
    [InlineData(EffectsIntensity.Soft, false)]
    public void HighContrastTurnsEffectsOff(EffectsIntensity requested, bool reduceMotion)
    {
        var p = new EffectsPolicy(requested, reduceMotion, HighContrast: true);
        Assert.Equal(EffectsIntensity.Off, p.Level);
        Assert.False(p.Aura);
        Assert.False(p.AuraBreathing);
        Assert.False(p.Comet);
        Assert.False(p.Particles);
        Assert.False(p.Pulses);
    }

    [Fact]
    public void CappedAtLowersOnlyTheRequestedLevel()
    {
        var max = new EffectsPolicy(EffectsIntensity.Max, ReduceMotion: false, HighContrast: false);
        Assert.Equal(EffectsIntensity.Soft, max.CappedAt(EffectsIntensity.Soft).Level);
        Assert.Equal(EffectsIntensity.Soft, max.CappedAt(EffectsIntensity.Soft).Requested);
        Assert.Equal(EffectsIntensity.Off, new EffectsPolicy(EffectsIntensity.Off).CappedAt(EffectsIntensity.Soft).Level);
        Assert.Equal(EffectsIntensity.Max, max.CappedAt(EffectsIntensity.Max).Level);

        var reduced = new EffectsPolicy(EffectsIntensity.Max, ReduceMotion: true).CappedAt(EffectsIntensity.Standard);
        Assert.True(reduced.ReduceMotion);
        Assert.Equal(EffectsIntensity.Soft, reduced.Level);

        var contrast = new EffectsPolicy(EffectsIntensity.Max, HighContrast: true).CappedAt(EffectsIntensity.Soft);
        Assert.True(contrast.HighContrast);
        Assert.Equal(EffectsIntensity.Off, contrast.Level);
    }

    [Fact]
    public void LevelsAreMonotonic()
    {
        var levels = Enum.GetValues<EffectsIntensity>().Select(l => new EffectsPolicy(l)).ToArray();
        for (var i = 1; i < levels.Length; i++)
        {
            var lower = levels[i - 1];
            var higher = levels[i];
            Assert.True(!lower.Comet || higher.Comet);
            Assert.True(!lower.Particles || higher.Particles);
            Assert.True(!lower.Pulses || higher.Pulses);
            Assert.True(!lower.Jitter || higher.Jitter);
            Assert.True(higher.ParticleRate >= lower.ParticleRate);
            Assert.True(higher.Bloom >= lower.Bloom);
        }
    }
}
