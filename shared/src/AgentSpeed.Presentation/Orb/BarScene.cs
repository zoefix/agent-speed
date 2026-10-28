using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb;

/// <summary>Animation state of the taskbar gauge glyph (DESIGN section 3). Calm by design.</summary>
public sealed class BarScene
{
    public const int AnimatedFps = 30;

    private CriticalSpring _heat = new(0.6);
    private CriticalSpring _glow = new(0.4);
    private double _target;
    private double _lastUpdate = double.NaN;

    public double Redline { get; set; } = HeatMath.DefaultRedline;

    public bool ReduceMotion { get; set; }

    public double Value { get; private set; }

    public SpeedTier Tier { get; private set; } = SpeedTier.Idle;

    public double Heat => Math.Clamp(_heat.Value, 0, 1);

    /// <summary>Soft glow amount 0..1, present from overdrive upward.</summary>
    public double Glow => Math.Clamp(_glow.Value, 0, 1);

    /// <summary>Fill opacity; flickers gently between 85 % and 100 % at about 6 Hz at redline.</summary>
    public double FillOpacity { get; private set; } = 1;

    public bool IsAnimating =>
        !_heat.IsSettled(_target, 0.002) || !_glow.IsSettled(GlowTarget, 0.005) || Flickers;

    private bool Flickers => Tier == SpeedTier.Redline && !ReduceMotion;

    private double GlowTarget => Tier >= SpeedTier.Overdrive ? 1 : 0;

    public void SetTarget(double value, SpeedTier tier)
    {
        Value = double.IsFinite(value) && value > 0 ? value : 0;
        Tier = tier;
        _target = HeatMath.Heat(Value, Redline);
    }

    public void Snap()
    {
        _heat.Snap(_target);
        _glow.Snap(GlowTarget);
    }

    public void Update(double nowSeconds)
    {
        var dt = double.IsNaN(_lastUpdate) ? 0 : Math.Clamp(nowSeconds - _lastUpdate, 0, 0.25);
        _lastUpdate = nowSeconds;
        if (ReduceMotion)
        {
            Snap();
        }
        else
        {
            _heat.Step(_target, dt);
            _glow.Step(GlowTarget, dt);
        }

        if (Flickers)
        {
            var wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 6 * nowSeconds) * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.9 * nowSeconds));
            FillOpacity = 0.85 + 0.15 * wave;
        }
        else
        {
            FillOpacity = 1;
        }
    }
}
