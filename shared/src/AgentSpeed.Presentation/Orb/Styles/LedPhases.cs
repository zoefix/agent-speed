using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The <c>led</c> style's accumulated speed phases (DESIGN 2.1): each advances by its rate per second times dt, only on rendered
/// frames and with dt clamped to 1/15 s, so a static orb never jumps when it renders again. <see cref="Clock"/> times the silk
/// ripple and the response-wave pattern. The values handed to the shader wrap at periods of their functions so float
/// precision never runs out.
/// </summary>
public struct LedPhases
{
    public const double MaxStep = 1.0 / 15;

    /// <summary>Every silk fold frequency is a multiple of 0.01 rad per phase unit: the folds repeat after 2π × 100.</summary>
    public const double SilkPeriod = 2 * Math.PI * 100;

    /// <summary>The 3D simplex noise repeats every 867 along z (289 lattice cells after skewing).</summary>
    public const double NoisePeriod = 867;

    /// <summary>The light azimuth (× 0.35) and glide (× 0.8) repeat after 2π × 20.</summary>
    public const double LightPeriod = 2 * Math.PI * 20;

    /// <summary>The corona samples its noise at 0.35, 0.5 and 1 × its phase: all repeat after 20 noise periods.</summary>
    public const double CoronaPeriod = NoisePeriod * 20;

    /// <summary>The warp streaks are hashed per bin, not periodic: a rare wrap reshuffles them unnoticed.</summary>
    public const double WarpPeriod = 4096;

    /// <summary>The ripple (× 2.6, × 16) and wave (× 8) patterns repeat after 10π.</summary>
    public const double ClockPeriod = 10 * Math.PI;

    /// <summary>φsilk, unwrapped (double): its folds and its noise repeat with incommensurate periods.</summary>
    public double Silk;
    public double Light;
    public double Flow;
    public double Warp;
    public double Corona;
    public double Clock;

    public static double SilkRate(double h, double o) => 0.12 + 1.4 * h + 2.2 * o;

    public static double LightRate(double h, double o) => 0.05 + 0.5 * h + 1.2 * o;

    public static double FlowRate(double h, double o) => 0.35 + 1.9 * h + 1.6 * o;

    public static double WarpRate(double h, double o) => 0.2 + 1.1 * h + 1.2 * o;

    public static double CoronaRate(double h, double o) => 0.5 + 1.7 * h + 1.8 * o;

    /// <summary>The step a rendered frame advances by: dt clamped to [0, 1/15 s].</summary>
    public static double Step(double dt) => double.IsFinite(dt) ? Math.Clamp(dt, 0, MaxStep) : 0;

    /// <param name="silkScale">The silk's share of its rate: 1, 0.5 at Soft, 0 when frozen (Off, Reduce Motion, thumbnails).</param>
    /// <param name="motion">The other phases move (Standard and Max).</param>
    public void Advance(double dt, double h, double o, double silkScale, bool motion)
    {
        dt = Step(dt);
        if (dt <= 0)
        {
            return;
        }

        if (silkScale > 0)
        {
            Silk += SilkRate(h, o) * silkScale * dt;
            Light = Wrap(Light + LightRate(h, o) * silkScale * dt, LightPeriod);
            Clock = Wrap(Clock + dt, ClockPeriod);
        }

        if (motion)
        {
            Flow = Wrap(Flow + FlowRate(h, o) * dt, 1);
            Warp = Wrap(Warp + WarpRate(h, o) * dt, WarpPeriod);
            Corona = Wrap(Corona + CoronaRate(h, o) * dt, CoronaPeriod);
        }
    }

    /// <summary>The silk phase for the fold sines.</summary>
    public readonly double SilkFolds => Silk % SilkPeriod;

    /// <summary>The silk's noise z, <c>0.07 φsilk</c>.</summary>
    public readonly double SilkNoise => Silk * 0.07 % NoisePeriod;

    private static double Wrap(double value, double period) => value >= period ? value % period : value;
}

/// <summary>
/// What the <c>led</c> style draws at an effects level (DESIGN 2.1, effects-level table), after Reduce Motion, high contrast
/// and the size LOD.
/// </summary>
/// <param name="Silk">The silk core is drawn.</param>
/// <param name="SilkRate">Share of the silk's speed: 1; 0.5 at Soft; 0 (frozen) at Off, with Reduce Motion and in thumbnails.</param>
/// <param name="Ripples">Fine ripple packets past the redline (Standard and Max).</param>
/// <param name="SilkWave">The response pulse displaces the cloth (Soft and up, not with Reduce Motion).</param>
/// <param name="Motion">Gauge energy flow, tip flare, corona, warp streaks, sparks, embers and electric arcs (Standard and Max).</param>
/// <param name="Halo">Corona and warp streaks (Motion, not in thumbnails).</param>
/// <param name="Lightning">Digit lightning (Standard and Max, never with Reduce Motion).</param>
/// <param name="ChromaticSplit">The redline's chromatic split of the value (Standard and Max).</param>
/// <param name="Level"><c>lvl</c>: 1, or 1.6 at Max.</param>
/// <param name="CountScale">LOD: 0.5 below S 120 (sparks, embers and bolts), else 1.</param>
public readonly record struct LedEffectGates(
    bool Silk,
    double SilkRate,
    bool Ripples,
    bool SilkWave,
    bool Motion,
    bool Halo,
    bool Lightning,
    bool ChromaticSplit,
    double Level,
    double CountScale)
{
    public const double MaxLevel = 1.6;

    public static LedEffectGates For(EffectsPolicy effects, double size)
    {
        var level = effects.Level;
        var thumbnail = size < 64;
        var standard = level >= EffectsIntensity.Standard;
        var soft = level >= EffectsIntensity.Soft;
        var frozen = effects.ReduceMotion || thumbnail || !soft;
        var silkRate = frozen ? 0 : standard ? 1 : 0.5;
        return new LedEffectGates(
            Silk: !effects.HighContrast,
            SilkRate: silkRate,
            Ripples: standard && !thumbnail,
            SilkWave: soft && !effects.ReduceMotion && !thumbnail,
            Motion: standard,
            Halo: standard && !thumbnail,
            Lightning: standard && !effects.ReduceMotion,
            ChromaticSplit: standard,
            Level: level == EffectsIntensity.Max ? MaxLevel : 1,
            CountScale: size < 120 ? 0.5 : 1);
    }
}
