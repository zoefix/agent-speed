using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The <c>led</c> style (DESIGN 2a.1): its instrument state (gauge heat, comet, shimmer, edge glow) lives in the shared
/// <see cref="OrbScene"/> as it always has; the floating orb on the GPU adds the v1.4 <see cref="Effects"/> (DESIGN 2.1), the
/// WPF fallback renderer keeps the v1.3 sparks and embers of the scene.
/// </summary>
public sealed class LedModel : IOrbStyleModel
{
    private readonly OrbScene _scene;

    public LedModel(OrbScene scene, LedEffects? effects = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Effects = effects;
    }

    public OrbStyle Style => OrbStyle.Led;

    /// <summary>The silk core, corona, lightning and their particles; null for the v1.3 look.</summary>
    public LedEffects? Effects { get; }

    public bool Small { get; private set; }

    public bool Thumbnail { get; private set; }

    /// <summary>Idle: static apart from the 30 fps sweep; cruise and boost (h &lt; 0.45) 30 fps; overdrive and redline 60 fps.</summary>
    public FrameClass Demand
    {
        get
        {
            var scene = _scene;
            var effects = scene.Effects;
            if (Effects is { Crackling: true })
            {
                return FrameClass.Fps60;
            }

            var lingering = scene.Particles.Count > 0 || Effects is { Lingering: true }
                || scene.CometOpacity > 0.005 && scene.Tier == SpeedTier.Idle;
            if (scene.Tier == SpeedTier.Idle)
            {
                return scene.IdleSweep is not null || lingering ? FrameClass.Fps30 : FrameClass.None;
            }

            if (!effects.AuraBreathing && !effects.Comet && !lingering)
            {
                return FrameClass.None;
            }

            if (scene.Heat >= 0.45 || scene.Tier >= SpeedTier.Overdrive)
            {
                return effects.Level >= Settings.EffectsIntensity.Soft ? FrameClass.Fps60 : FrameClass.Fps30;
            }

            return FrameClass.Fps30;
        }
    }

    public void Update(in OrbFrame frame)
    {
        Small = frame.Small;
        Thumbnail = frame.Thumbnail;
        _scene.ParticleRateScale = frame.Small ? 0.5 : 1;
        Effects?.Update(frame);
    }

    public void Snap(in OrbFrame frame)
    {
        Update(frame);
        _scene.Particles.Clear();
        Effects?.Snap();
    }

    public void Pulse(double heat, double strength)
    {
    }

    public void Peak() => Effects?.Peak();
}
