using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The floating <c>led</c> orb's v1.4 effect state (DESIGN 2.1): the speed phases, the sparks, embers and new-peak burst, the
/// electric arcs and the digit lightning, gated by the effects level. Advanced once per rendered frame by
/// <see cref="LedModel"/>; the draw builder reads it. The dashboard hero and the WPF fallback renderer have none.
/// </summary>
public sealed class LedEffects
{
    private LedPhases _phases;
    private string? _lastText;
    private bool _force;
    private double _heat;
    private double _overdrive;
    private bool _redline;
    private bool _unavailable;

    public LedEffects(uint seed)
    {
        Particles = new LedParticles(seed ^ 0x9E3779B9u);
        Arcs = new LedArcs(unchecked(seed * 31 + 7));
        Lightning = new LedLightning(unchecked(seed + 0x51ED27u));
    }

    public LedEffectGates Gates { get; private set; } = LedEffectGates.For(new Motion.EffectsPolicy(Settings.EffectsIntensity.Standard), 148);

    public LedPhases Phases => _phases;

    public LedParticles Particles { get; }

    public LedArcs Arcs { get; }

    public LedLightning Lightning { get; }

    /// <summary>Particles or arcs still alive.</summary>
    public bool Lingering => Particles.Count > 0 || Arcs.Count > 0;

    /// <summary>Lightning or its flashes still alive (a transition: 60 fps).</summary>
    public bool Crackling => Lightning.BoltCount > 0 || Lightning.ZapCount > 0;

    public void Update(in OrbFrame frame)
    {
        Gates = LedEffectGates.For(frame.Effects, frame.Size);
        _unavailable = frame.Unavailable;
        _heat = frame.Unavailable ? 0 : Math.Clamp(frame.Heat, 0, 1);
        _overdrive = frame.Unavailable ? 0 : Math.Clamp(frame.Overdrive, 0, 1);
        _redline = !frame.Unavailable && frame.Tier == SpeedTier.Redline;
        var gates = Gates;
        _phases.Advance(frame.Dt, _heat, _overdrive, gates.SilkRate, gates.Motion);

        var moving = gates.Motion && !frame.Unavailable && !frame.Thumbnail;
        var sparks = moving ? LedParticles.SparksPerSecond(_heat, gates.Level, gates.CountScale) : 0;
        var embers = moving ? LedParticles.EmbersPerSecond(_redline, _overdrive, gates.Level, gates.CountScale) : 0;
        Particles.Step(frame.Dt, frame.Time, _heat, _overdrive, sparks, embers);
        Arcs.Step(frame.Dt, moving && _redline, _heat, _overdrive, gates.Level);
        Lightning.Step(frame.Dt);
    }

    public void Snap()
    {
        Particles.Clear();
        Arcs.Clear();
        Lightning.Clear();
        _lastText = null;
    }

    /// <summary>The new-peak burst of 160 sparks (80 below S 120), Standard and Max.</summary>
    public void Peak()
    {
        if (Gates.Motion)
        {
            Particles.Burst((int)Math.Round(LedParticles.BurstCount * Gates.CountScale));
        }
    }

    /// <summary>Developer captures: the next readout crackles on every digit (the prototype's <c>zap=1</c>).</summary>
    public void ForceLightning() => _force = true;

    /// <summary>
    /// The readout drew <paramref name="text"/> in <paramref name="cells"/>: when the odometer changed since the last call,
    /// lightning crackles off the changed digits (Standard and Max, from 0.5 tok/s).
    /// </summary>
    public void NoteReadout(string text, double value, ReadOnlySpan<LedDigitCell> cells, double now)
    {
        if (_lastText is null && !_force)
        {
            _lastText = text;
            return;
        }

        if (text == _lastText && !_force)
        {
            return;
        }

        var previous = _force ? "\0" : _lastText!;
        var force = _force;
        _force = false;
        _lastText = text;
        if (!Gates.Lightning || _unavailable || value < 0.5 || cells.IsEmpty)
        {
            return;
        }

        Lightning.Trigger(text, previous, cells, now, _heat, _overdrive, _redline, Gates.Level, Gates.CountScale, force);
    }
}
