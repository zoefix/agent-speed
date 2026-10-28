using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// What every style model receives each frame from the orb's shared state (<see cref="OrbScene"/>): the smoothed and
/// raw heat and overdrive of section 1, the effect tier, the effects policy and the size. Times are seconds on the
/// shared clock, so several orbs breathe and sweep together.
/// </summary>
/// <param name="Time">Shared clock (seconds).</param>
/// <param name="Dt">Seconds since the model's previous update (0 on the first one; clamped by the caller to ≤ 0.1).</param>
/// <param name="Heat">Smoothed <c>h</c> (critically damped, settle 0.6 s).</param>
/// <param name="Overdrive">Smoothed <c>o</c>.</param>
/// <param name="HeatRaw"><c>h_raw = min(1, (v / R)^0.6)</c>, stepping with the 500 ms engine tick.</param>
/// <param name="OverdriveRaw"><c>o_raw</c>.</param>
/// <param name="Tier">The displayed effect tier (hysteresis and hold applied).</param>
/// <param name="Size">Diameter S in DIP.</param>
/// <param name="Effects">Effects level after Reduce Motion and high-contrast caps.</param>
/// <param name="Dark">Dark appearance (false: the light "porcelain" variant).</param>
/// <param name="Flash">Tier flash 0..1 (an effect-tier change brightens the primary lit element by 25 % for 0.4 s).</param>
/// <param name="SignOfLifeAge">Seconds since the current idle sign-of-life event started (every 6 s on the shared clock, the first one skipped when it would start less than 2 s after entering idle), or null when none is running. Each style decides its own duration (0.9 s; liquid 1.2 s).</param>
/// <param name="Unavailable">Every source of the orb is unavailable: the instrument rests, no sign of life.</param>
/// <param name="ReadoutShift">
/// How far (in S) the readout stack sits below its resting place while the traffic block shows above it (DESIGN 10,
/// Centering); particles fading behind the readout follow it.
/// </param>
public readonly record struct OrbFrame(
    double Time,
    double Dt,
    double Heat,
    double Overdrive,
    double HeatRaw,
    double OverdriveRaw,
    SpeedTier Tier,
    double Size,
    EffectsPolicy Effects,
    bool Dark,
    double Flash = 0,
    double? SignOfLifeAge = null,
    bool Unavailable = false,
    double ReadoutShift = 0)
{
    public bool Idle => Tier == SpeedTier.Idle;

    /// <summary>Level of detail: below 120 pt styles drop fine detail and halve some rates (DESIGN 2a, per style).</summary>
    public bool Small => Size < 120;

    /// <summary>Thumbnails (S &lt; 64) draw only the still instrument and the value.</summary>
    public bool Thumbnail => Size < 64;
}

/// <summary>
/// The style-specific animation state of one orb (DESIGN 2a). The shared readout, aura, body, agent dots, wave, rays
/// and pill live in <see cref="OrbScene"/>; a style model adds only its own instrument. Implementations are pure,
/// deterministic for a given seed, allocate nothing per frame (fixed pools sized from the style's maximum budget) and
/// expose read-only state for a renderer.
/// </summary>
public interface IOrbStyleModel
{
    OrbStyle Style { get; }

    /// <summary>Advances the model by <see cref="OrbFrame.Dt"/>.</summary>
    void Update(in OrbFrame frame);

    /// <summary>
    /// After a pause (hidden, occluded) or on creation: jump to the resting state for the frame's values without any
    /// catch-up animation (springs settled at their targets, particles cleared).
    /// </summary>
    void Snap(in OrbFrame frame);

    /// <summary>A <c>responseCompleted</c> of the orb's sources: the style's pulse accent.</summary>
    /// <param name="heat">Heat of that response's speed.</param>
    /// <param name="strength">Pulse strength 0.3..1.25 (log10 of the output, as the shared wave).</param>
    void Pulse(double heat, double strength);

    /// <summary>The orb's <c>newPeak_A</c>: the style's new-peak accent.</summary>
    void Peak();

    /// <summary>The frame class the style needs right now (DESIGN 2a frame table, its own column).</summary>
    FrameClass Demand { get; }
}
