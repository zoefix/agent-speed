using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Motion;

/// <summary>What the orb may draw for an intensity level, after Reduce Motion and high contrast caps.</summary>
public readonly record struct EffectsPolicy(EffectsIntensity Requested, bool ReduceMotion = false, bool HighContrast = false)
{
    public EffectsIntensity Level => HighContrast
        ? EffectsIntensity.Off
        : ReduceMotion && Requested > EffectsIntensity.Soft ? EffectsIntensity.Soft : Requested;

    public bool Aura => !HighContrast;

    public bool AuraBreathing => Level >= EffectsIntensity.Soft;

    public bool Comet => Level >= EffectsIntensity.Soft && !ReduceMotion;

    public bool Pulses => Level >= EffectsIntensity.Soft;

    public bool PulseExpands => !ReduceMotion;

    public bool ValueGlow => Level >= EffectsIntensity.Soft;

    public bool Particles => Level >= EffectsIntensity.Standard;

    public bool Skew => Level >= EffectsIntensity.Standard;

    public bool Jitter => Level >= EffectsIntensity.Standard;

    public bool Shimmer => Level >= EffectsIntensity.Standard;

    public bool HeatDistortion => Level == EffectsIntensity.Max;

    public bool RollingDigits => !ReduceMotion && Level >= EffectsIntensity.Soft;

    public bool SlideTransitions => !ReduceMotion;

    public bool HoverScale => !ReduceMotion;

    public bool IdleSweep => Level >= EffectsIntensity.Soft;

    public bool PeakBurst => Level >= EffectsIntensity.Soft;

    public double ParticleRate => Level == EffectsIntensity.Max ? 1.6 : 1;

    public double Bloom => Level switch
    {
        EffectsIntensity.Max => 1.5,
        EffectsIntensity.Off => 0.6,
        _ => 1,
    };

    public EffectsPolicy CappedAt(EffectsIntensity cap) =>
        this with { Requested = Requested > cap ? cap : Requested };
}
