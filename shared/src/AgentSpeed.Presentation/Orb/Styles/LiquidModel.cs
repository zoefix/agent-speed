using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Animation state of the <c>liquid</c> style (DESIGN 2a.3): a glass sphere of glowing liquid whose level rises with
/// speed. Lengths are in units of S relative to the disc center (y down), angles in degrees clockwise from 3 o'clock,
/// colors are straight sRGB with their alpha included. The renderer rebuilds the surface strip each frame from
/// <see cref="FillSurface"/>, caches every brush by <see cref="ColorKey"/> and splats <see cref="Sprites"/>.
/// </summary>
public sealed partial class LiquidModel : IOrbStyleModel
{
    public const int MotePool = 80;
    public const int PopPool = 24;
    public const int SplashPool = 40;
    public const int SteamPool = 12;
    public const int MaxDroplets = 3;
    public const int MaxRipples = 4;
    public const int GoldMoteCount = 22;
    public const double RippleLifetime = 2.2;
    public const double PopDuration = 0.25;
    public const double PeakBandDuration = 0.9;
    public const double ReducedPeakDuration = 0.3;
    public const double SignOfLifeDuration = 1.2;

    private const double R = LiquidGeometry.GlassRadius;
    private const double Tau = 2 * Math.PI;
    private const double PhaseWrap = Tau * 10;
    private const double DriftWrap = Tau * 200;

    private readonly Body[] _motes = new Body[MotePool];
    private readonly Body[] _pops = new Body[PopPool];
    private readonly Body[] _splashes = new Body[SplashPool];
    private readonly Body[] _steam = new Body[SteamPool];
    private readonly Droplet[] _droplets = new Droplet[MaxDroplets];
    private readonly Ripple[] _ripples = new Ripple[MaxRipples];
    private readonly LiquidSprite[] _sprites = new LiquidSprite[MotePool + PopPool + SplashPool + SteamPool + MaxDroplets];
    private readonly double[] _currentPhases = new double[3];

    private XorShift32 _random;
    private SubSteppedSpring _level = new(0.75, 0.62, LiquidGeometry.IdleLevel);
    private SubSteppedSpring _surge = new(0.9, 0.45);
    private CriticalSpring _amplitude = new(0.8, 0.002);
    private CriticalSpring _chop = new(0.6);
    private CriticalSpring _activity = new(0.5);
    private EmissionDebt _moteDebt;
    private EmissionDebt _steamDebt;

    private int _moteCount;
    private int _popCount;
    private int _splashCount;
    private int _steamCount;
    private int _dropletCount;
    private int _rippleCount;
    private int _moteEnd;
    private int _popEnd;
    private int _splashEnd;
    private int _steamEnd;
    private int _spriteCount;

    private double _time;
    private double _size = 148;
    private bool _dark = true;
    private EffectsIntensity _effects = EffectsIntensity.Standard;
    private bool _reduceMotion;
    private bool _highContrast;
    private SpeedTier _tier = SpeedTier.Idle;
    private double _heat;
    private double _overdrive;
    private double _flash;
    private bool _unavailable;
    private double? _signOfLifeAge;
    private double _readoutShift;
    private double? _lastSignOfLifeAge;

    private double _f = LiquidGeometry.IdleLevel;
    private double _phase1;
    private double _phase2;
    private double _phase3;
    private double _phase4;
    private double _rate1;
    private double _rate2;
    private double _rate3;
    private double _drift;
    private bool _ratesPrimed;
    private double _lastLevelTarget = LiquidGeometry.IdleLevel;
    private bool _boiling;
    private double _peakStart = double.NegativeInfinity;
    private bool _peakReduced;
    private double _previousHeat;
    private double _previousOverdrive;
    private int _surfaceVersion;
    private long _colorKey = -1;

    public LiquidModel(int seed = 1)
    {
        _random = new XorShift32(seed);
        RefreshColors(force: true);
    }

    public OrbStyle Style => OrbStyle.Liquid;

    /// <summary>0 at idle (or with every source unavailable), 1 when active; a 0.5 s crossfade between the two looks.</summary>
    public double Activity => Math.Clamp(_activity.Value, 0, 1);

    /// <summary>Displayed level: fraction of the inner diameter (spring and new-peak surge included).</summary>
    public double Level => _f;

    /// <summary>The level's target <c>f*</c> for the current frame.</summary>
    public double LevelTarget => TargetLevel(HeatNow, OverdriveNow);

    /// <summary>Mean surface <c>y_L = 0.455 − 0.91 f</c> (S, from the center, y down).</summary>
    public double MeanSurfaceY => R - 2 * R * _f;

    /// <summary>Wave amplitude <c>A</c> (S).</summary>
    public double WaveAmplitude => Math.Max(0, _amplitude.Value);

    /// <summary>Boil chop amplitude (S); non-zero only at the redline tier, Standard and up.</summary>
    public double ChopAmplitude => Math.Max(0, _chop.Value);

    /// <summary>Accumulated phases φ1..φ4 (radians).</summary>
    public double Phase1 => _phase1;

    public double Phase2 => _phase2;

    public double Phase3 => _phase3;

    public double Phase4 => _phase4;

    /// <summary>
    /// The energy currents' drift phase (radians): current <c>k</c> runs at <c>(0.35 + 0.2 k)</c> × it, the phase that
    /// <see cref="CurrentY"/> takes as <c>φ</c>. It wraps at 400π, where both <c>φ</c> and <c>1.7 φ</c> of every current
    /// are whole turns.
    /// </summary>
    public double CurrentDrift => _drift;

    /// <summary>True while the surface boils (redline tier, Standard and up).</summary>
    public bool Boiling => _boiling;

    public int RippleCount => _rippleCount;

    public int DropletsInFlight => _dropletCount;

    /// <summary>Changes whenever the surface may have moved (level, waves, chop, ripples); stable while it rests.</summary>
    public int SurfaceVersion => _surfaceVersion;

    /// <summary>Back wave (layer 4): drawn only at S ≥ 120.</summary>
    public bool BackWaveVisible => _size >= 120;

    /// <summary>Energy currents (layer 6): Soft and up, S ≥ 120, not Reduce Motion, not high contrast, not idle.</summary>
    public bool CurrentsVisible =>
        _effects >= EffectsIntensity.Soft && !_reduceMotion && !_highContrast && _size >= 120 && Activity > 0.004;

    /// <summary>The flat split line for the two readout copies: the surface height at the disc center.</summary>
    public double ReadoutSplitY => SurfaceY(0);

    /// <summary>Front surface <c>y(x)</c> (S) at <paramref name="x"/> (S from the center).</summary>
    public double SurfaceY(double x) => Surface(x, back: false);

    /// <summary>Back wave surface: phase + 1.7 rad, 75 % amplitude, raised 0.008 S.</summary>
    public double BackSurfaceY(double x) => Surface(x, back: true);

    /// <summary>
    /// Fills <paramref name="ys"/> with the surface at <see cref="LiquidGeometry.SampleX"/> (evenly spaced across the
    /// glass, both ends included). Allocates nothing.
    /// </summary>
    public void FillSurface(Span<double> ys, bool back = false)
    {
        for (var i = 0; i < ys.Length; i++)
        {
            ys[i] = Surface(LiquidGeometry.SampleX(i, ys.Length), back);
        }
    }

    /// <summary>
    /// Fills <paramref name="ys"/> with energy current <paramref name="index"/> (0..2, at 25 / 50 / 75 % of the depth)
    /// at the same x samples as <see cref="FillSurface"/>.
    /// </summary>
    public void FillCurrent(int index, Span<double> ys)
    {
        for (var i = 0; i < ys.Length; i++)
        {
            ys[i] = CurrentY(index, ys.Length <= 1 ? 0.5 : i / (double)(ys.Length - 1));
        }
    }

    /// <summary>
    /// Current <paramref name="index"/> at <paramref name="u"/> (0..1 across the width):
    /// <c>depth + 0.03 sin(7u + φ) + 0.015 sin(13u − 1.7 φ)</c>.
    /// </summary>
    public double CurrentY(int index, double u)
    {
        index = Math.Clamp(index, 0, 2);
        var yL = MeanSurfaceY;
        var depth = yL + (R - yL) * (0.25 + 0.25 * index);
        var phase = _currentPhases[index] + 2.1 * index;
        return depth + 0.03 * Math.Sin(7 * u + phase) + 0.015 * Math.Sin(13 * u - 1.7 * phase);
    }

    public ReadOnlySpan<LiquidSprite> Sprites => _sprites.AsSpan(0, _spriteCount);

    /// <summary>Motes, bubbles and gold motes (drawn below the meniscus).</summary>
    public ReadOnlySpan<LiquidSprite> Motes => _sprites.AsSpan(0, _moteEnd);

    public ReadOnlySpan<LiquidSprite> Pops => _sprites.AsSpan(_moteEnd, _popEnd - _moteEnd);

    public ReadOnlySpan<LiquidSprite> Splashes => _sprites.AsSpan(_popEnd, _splashEnd - _popEnd);

    public ReadOnlySpan<LiquidSprite> Steam => _sprites.AsSpan(_splashEnd, _steamEnd - _splashEnd);

    public ReadOnlySpan<LiquidSprite> Droplets => _sprites.AsSpan(_steamEnd, _spriteCount - _steamEnd);

    /// <summary>The new-peak gold band's center (S), rising from the bottom to the surface.</summary>
    public double GoldBandY { get; private set; } = R;

    /// <summary>The gold band's opacity (<c>#FACC15</c>, additive in dark); 0 when none.</summary>
    public double GoldBandAlpha { get; private set; }

    public FrameClass Demand { get; private set; } = FrameClass.None;

    private double HeatNow => _heat * Activity;

    private double OverdriveNow => _overdrive * Activity;

    private bool Still => _reduceMotion || _effects == EffectsIntensity.Off || _size < 64;

    private bool Active => _tier != SpeedTier.Idle && !_unavailable;

    public void Update(in OrbFrame frame)
    {
        Read(frame);
        var dt = double.IsFinite(frame.Dt) ? Math.Clamp(frame.Dt, 0, 0.1) : 0;
        Step(dt);
        Finish();
    }

    public void Snap(in OrbFrame frame)
    {
        Read(frame);
        _activity.Snap(Active ? 1 : 0);
        var h = HeatNow;
        var o = OverdriveNow;
        _level.Snap(TargetLevel(h, o));
        _lastLevelTarget = _level.Value;
        _ratesPrimed = false;
        _surge.Snap(0);
        _amplitude.Snap(AmplitudeTarget(h, o));
        _boiling = BoilingNow();
        _chop.Snap(ChopTarget(o));
        _moteCount = _popCount = _splashCount = _steamCount = _dropletCount = _rippleCount = 0;
        _moteDebt.Reset();
        _steamDebt.Reset();
        _peakStart = double.NegativeInfinity;
        _lastSignOfLifeAge = frame.SignOfLifeAge;
        _f = Math.Clamp(_level.Value, 0.04, 0.95);
        _surfaceVersion++;
        _previousHeat = _heat;
        _previousOverdrive = _overdrive;
        Finish();
    }

    public void Pulse(double heat, double strength)
    {
        if (_effects < EffectsIntensity.Soft || _reduceMotion || _size < 64 || !Active || _dropletCount >= MaxDroplets)
        {
            return;
        }

        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1.25) : 0.5;
        heat = double.IsFinite(heat) ? Math.Clamp(heat, 0, 1) : 0;
        var x = _random.Range(-0.21, 0.21);
        var y = Math.Max(-0.414, Math.Min(SurfaceY(x) - 0.16, -0.3));
        var tint = Math.Max(0.15, heat);
        _droplets[_dropletCount++] = new Droplet
        {
            X = x,
            Y = y,
            Strength = strength,
            Color = _dark ? OkLab.Mix(EnergyPalette.Heat(tint), EnergyPalette.WhiteHot, 0.3) : EnergyPalette.Label(tint, false),
        };
    }

    public void Peak()
    {
        if (_effects < EffectsIntensity.Soft || _size < 64)
        {
            return;
        }

        _peakStart = _time;
        _peakReduced = _reduceMotion;
        if (_reduceMotion)
        {
            return;
        }

        for (var i = 0; i < GoldMoteCount && _moteCount < MotePool; i++)
        {
            ref var p = ref _motes[_moteCount++];
            p = default;
            p.Kind = MoteKind.Gold;
            p.X = _random.Range(-0.3, 0.3);
            p.Y = R * 0.85 - _random.Range(0, 0.1);
            p.Vy = -_random.Range(0.5, 0.9);
            p.Size = _random.Range(0.008, 0.016);
            p.Phase = _random.Next() * Tau;
        }

        if (_effects >= EffectsIntensity.Standard)
        {
            _surge.Kick(0.9);
        }
    }

    private void Read(in OrbFrame frame)
    {
        _time = frame.Time;
        _size = double.IsFinite(frame.Size) && frame.Size > 0 ? frame.Size : 148;
        _dark = frame.Dark;
        _effects = frame.Effects.Level;
        _reduceMotion = frame.Effects.ReduceMotion;
        _highContrast = frame.Effects.HighContrast;
        _tier = frame.Tier;
        _heat = Clamp01(frame.Heat);
        _overdrive = Clamp01(frame.Overdrive);
        _flash = Clamp01(frame.Flash);
        _unavailable = frame.Unavailable;
        _signOfLifeAge = frame.SignOfLifeAge;
        _readoutShift = double.IsFinite(frame.ReadoutShift) ? frame.ReadoutShift : 0;
    }

    private void Step(double dt)
    {
        var previousF = _f;
        var previousAmplitude = _amplitude.Value;
        var previousChop = _chop.Value;

        _activity.Step(Active ? 1 : 0, dt);
        var h = HeatNow;
        var o = OverdriveNow;
        var still = Still;

        var target = TargetLevel(h, o);
        if (still)
        {
            _level.StepCritical(target, dt, 0.9);
            _surge.Snap(0);
        }
        else
        {
            // The target moves between frames: a first-order hold keeps the slosh independent of the frame rate.
            _level.Step(_lastLevelTarget, target, dt);
            _surge.Step(0, dt);
        }

        _lastLevelTarget = target;

        _f = Math.Clamp(_level.Value + _surge.Value, 0.04, 0.95);
        _amplitude.Step(AmplitudeTarget(h, o), dt);
        _boiling = BoilingNow();
        _chop.Step(ChopTarget(o), dt);

        var moved = false;
        if (!still && dt > 0)
        {
            AdvancePhases(h, o, dt);
            moved = true;
        }
        else if (still)
        {
            _ratesPrimed = false;
        }

        ExpireRipples();
        if (still)
        {
            _moteCount = _popCount = _splashCount = _steamCount = _dropletCount = _rippleCount = 0;
            _moteDebt.Reset();
            _steamDebt.Reset();
        }
        else
        {
            EmitMotes(h, o, dt);
            EmitSignOfLife();
            StepMotes(h, dt);
            StepPops(dt);
            StepSteam(o, dt);
            StepDroplets(h, o, dt);
            StepSplashes(dt);
        }

        _lastSignOfLifeAge = _signOfLifeAge;
        if (moved || _rippleCount > 0 || _f != previousF || _amplitude.Value != previousAmplitude || _chop.Value != previousChop)
        {
            _surfaceVersion++;
        }
    }

    private void Finish()
    {
        UpdateGoldBand();
        RefreshColors(force: false);
        FillSprites();
        Demand = ComputeDemand();
        _previousHeat = _heat;
        _previousOverdrive = _overdrive;
    }

    private static double TargetLevel(double h, double o) => LiquidGeometry.IdleLevel + 0.74 * h + 0.06 * o;

    private double AmplitudeTarget(double h, double o)
    {
        if (Still)
        {
            return 0;
        }

        var active = 0.004 + 0.02 * h + 0.018 * o;
        var a = 0.002 + (active - 0.002) * Activity;
        return _effects == EffectsIntensity.Soft ? a * 0.6 : a;
    }

    private bool BoilingNow() => !Still && _effects >= EffectsIntensity.Standard && _tier == SpeedTier.Redline && !_unavailable;

    private double ChopTarget(double o) => _boiling ? 0.006 * (0.5 + 0.5 * o) * (_size < 120 ? 0.5 : 1) : 0;

    private void AdvancePhases(double h, double o, double dt)
    {
        var rate1 = 0.25 + 0.5 * h + 0.3 * o;
        var rate2 = 0.4 + 0.8 * h + 0.4 * o;
        var rate3 = 0.9 + 1.6 * h;
        if (!_ratesPrimed)
        {
            (_rate1, _rate2, _rate3, _ratesPrimed) = (rate1, rate2, rate3, true);
        }

        // Trapezoidal integration of the rates, so the phases do not depend on the frame rate.
        var mean1 = (_rate1 + rate1) / 2;
        _phase1 = Wrap(_phase1 + Tau * mean1 * dt);
        _phase2 = Wrap(_phase2 + Tau * (_rate2 + rate2) / 2 * dt);
        _phase3 = Wrap(_phase3 + Tau * (_rate3 + rate3) / 2 * dt);
        _phase4 = Wrap(_phase4 + Tau * 3.1 * dt);
        for (var k = 0; k < 3; k++)
        {
            _currentPhases[k] = Wrap(_currentPhases[k] + Tau * mean1 * (0.35 + 0.2 * k) * dt);
        }

        _drift += Tau * mean1 * dt;
        if (_drift >= DriftWrap)
        {
            _drift -= DriftWrap;
        }

        (_rate1, _rate2, _rate3) = (rate1, rate2, rate3);
    }

    private static double Wrap(double phase) => phase >= PhaseWrap ? phase - PhaseWrap : phase;

    private double Surface(double x, bool back)
    {
        var amplitude = WaveAmplitude * (back ? 0.75 : 1);
        var offset = back ? 1.7 : 0;
        var y = MeanSurfaceY - (back ? 0.008 : 0);
        if (amplitude > 0)
        {
            y += amplitude * (0.55 * Math.Sin(Tau * x / 0.9 + _phase1 + offset)
                + 0.30 * Math.Sin(Tau * x / 0.47 - _phase2 + offset * 1.3)
                + 0.15 * Math.Sin(Tau * x / 0.23 + _phase3 + offset * 0.7));
        }

        var chop = ChopAmplitude;
        if (chop > 1e-7)
        {
            y += chop * Math.Sin(Tau * x / 0.11 + _phase4 + offset) * (0.7 + 0.3 * Math.Sin(x / 0.05 + 1.7 * _phase4));
        }

        for (var i = 0; i < _rippleCount; i++)
        {
            ref readonly var r = ref _ripples[i];
            y += RippleOffset(Math.Abs(x - r.X), _time - r.Start, r.Amplitude) * (back ? 0.6 : 1);
        }

        return y;
    }

    /// <summary>Ripple of DESIGN 2a.3: <c>−A_r e^(−τ/0.75) exp(−((d − 0.42 τ)/0.075)²) cos(2π (d − 0.42 τ)/0.1)</c>.</summary>
    internal static double RippleOffset(double distance, double age, double amplitude)
    {
        if (age < 0)
        {
            return 0;
        }

        var front = distance - 0.42 * age;
        var envelope = Math.Exp(-age / 0.75) * Math.Exp(-(front / 0.075) * (front / 0.075));
        return -amplitude * envelope * Math.Cos(Tau * front / 0.1);
    }

    internal double RippleAmplitude(int index) => _ripples[index].Amplitude;

    /// <summary>Ripple <paramref name="index"/> (below <see cref="RippleCount"/>): impact x (S), seconds since the impact and <c>A_r</c> (S).</summary>
    public (double X, double Age, double Amplitude) RippleAt(int index)
    {
        ref readonly var ripple = ref _ripples[index];
        return (ripple.X, _time - ripple.Start, ripple.Amplitude);
    }

    private void ExpireRipples()
    {
        for (var i = 0; i < _rippleCount;)
        {
            if (_time - _ripples[i].Start >= RippleLifetime)
            {
                _ripples[i] = _ripples[--_rippleCount];
                continue;
            }

            i++;
        }
    }

    private void AddRipple(double x, double amplitude)
    {
        if (_rippleCount < MaxRipples)
        {
            _ripples[_rippleCount++] = new Ripple(x, _time, amplitude);
            return;
        }

        var oldest = 0;
        for (var i = 1; i < _rippleCount; i++)
        {
            if (_ripples[i].Start < _ripples[oldest].Start)
            {
                oldest = i;
            }
        }

        _ripples[oldest] = new Ripple(x, _time, amplitude);
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static class MoteKind
    {
        public const byte Bubble = 0;
        public const byte Gold = 1;
        public const byte Cool = 2;
    }

    private struct Body
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Age;
        public double Life;
        public double Size;
        public double Phase;
        public byte Kind;
    }

    private struct Droplet
    {
        public double X;
        public double Y;
        public double Vy;
        public double Strength;
        public Rgba Color;
    }

    private readonly record struct Ripple(double X, double Start, double Amplitude);
}
