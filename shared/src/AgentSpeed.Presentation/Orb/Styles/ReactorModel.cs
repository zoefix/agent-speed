using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Animation state of the <c>reactor</c> style (DESIGN 2a.4): three counter-rotating energy rings around a core window.
/// Angles are degrees clockwise from 3 o'clock (y down), lengths in units of S from the disc center, colors straight
/// sRGB with their alpha included. Rotors A and C are drawn once per <see cref="ColorKey"/> and arc-length bucket and
/// only rotated per frame; rotor B is a fixed ring of coils whose opacities change; the corona is one opacity.
/// </summary>
public sealed partial class ReactorModel : IOrbStyleModel
{
    public const int MaxCoils = 24;
    public const double KickTau = 0.5;
    public const double LockSwing = 0.42;
    public const double LockEnd = 0.42 * 1.35;
    public const double ReducedLockDuration = 0.3;
    public const double FlareDuration = 0.32;
    public const double CoronaFlareDuration = 0.35;
    public const double SignOfLifeDuration = 0.9;

    private const double Tau = 2 * Math.PI;

    // Mean of ((1 + cos x) / 2)^2.2 over a cycle: the coils' uniform energy when bands are off.
    private static readonly double MeanBand = ComputeMeanBand();

    private readonly double[] _coilEnergy = new double[MaxCoils];
    private readonly double[] _coilOpacity = new double[MaxCoils];
    private readonly double[] _coilHot = new double[MaxCoils];

    private XorShift32 _random;
    private CriticalSpring _activity = new(0.5);

    // The prototype's start pose (0.3, 1.1 and 2.0 rad).
    private double _angleA = 0.3 * 180 / Math.PI;
    private double _angleB = 1.1 * 180 / Math.PI;
    private double _angleC = 2.0 * 180 / Math.PI;
    private double _kick;
    private double _bandPhase;
    private double _coronaPhase;
    private double _lastSpeedA;
    private double _lastSpeedB;
    private double _lastSpeedC;
    private double _lastBandRate;
    private double _lastCoronaRate;
    private bool _ratesPrimed;

    private LockMode _lock;
    private double _lockStart = double.NegativeInfinity;
    private double _lockFromA;
    private double _lockFromB;
    private double _lockFromC;
    private double _lockToA;
    private double _lockToB;
    private double _lockToC;

    private double _flareStart = double.NegativeInfinity;
    private Rgba _flareColor;

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
    private double _previousHeat;
    private double _previousOverdrive;

    public ReactorModel(int seed = 1)
    {
        _random = new XorShift32(seed);
        _boltFlashTimes.AsSpan().Fill(double.NegativeInfinity);
        Finish();
    }

    private enum LockMode
    {
        None,
        Swing,
        Crossfade,
    }

    public OrbStyle Style => OrbStyle.Reactor;

    public FrameClass Demand { get; private set; } = FrameClass.None;

    /// <summary>0 at idle (or with every source unavailable), 1 when active; a 0.5 s crossfade (and spin-down).</summary>
    public double Activity => Math.Clamp(_activity.Value, 0, 1);

    /// <summary>Rotor A (outer, clockwise): the head of arc 0; arc k's head is at + 120° k, its tail <see cref="ArcADegrees"/> behind.</summary>
    public double RotorADegrees => Normalize(_angleA);

    /// <summary>Rotor B (middle, counter-clockwise): the center of coil 0; coil i is at + 360° i / <see cref="CoilCount"/>.</summary>
    public double RotorBDegrees => Normalize(_angleB);

    /// <summary>Rotor C (inner, clockwise): the head of arc 0; arc 1's head is at + 180°.</summary>
    public double RotorCDegrees => Normalize(_angleC);

    /// <summary>Nominal speeds (rev/s, B negative) from heat, overdrive and the pulse kick; 0 at idle.</summary>
    public double RotorASpeed => SpeedA(Activity) + _kick;

    public double RotorBSpeed => SpeedB(Activity) - 0.6 * _kick;

    public double RotorCSpeed => SpeedC(Activity) + 1.2 * _kick;

    /// <summary>The pulse kick <c>k</c> (rev/s), decaying with τ 0.5 s.</summary>
    public double Kick => _kick;

    /// <summary>The energy band phase ψ (radians).</summary>
    public double BandPhase => _bandPhase;

    /// <summary>Rotor A arc length <c>min(120°, 20° + 90° h + 10° o)</c>; 120° closes the ring.</summary>
    public double ArcADegrees => Math.Min(120, 20 + 90 * HeatNow + 10 * OverdriveNow);

    /// <summary><see cref="ArcADegrees"/> rounded to whole degrees: the cached rotor A visual changes only with it.</summary>
    public int ArcABucket => (int)Math.Round(ArcADegrees);

    /// <summary>Rotor C arc length <c>min(175°, 14° + 130° h + 20° o)</c>.</summary>
    public double ArcCDegrees => Math.Min(175, 14 + 130 * HeatNow + 20 * OverdriveNow);

    public int ArcCBucket => (int)Math.Round(ArcCDegrees);

    /// <summary>24 coils (12 below S 120).</summary>
    public int CoilCount => _size < 120 ? 12 : MaxCoils;

    /// <summary>Coil length: 7.5° (13° for 12 coils).</summary>
    public double CoilArcDegrees => _size < 120 ? 13 : 7.5;

    /// <summary>24 stator notches (4 below S 120).</summary>
    public int NotchCount => _size < 120 ? 4 : 24;

    /// <summary>Coil energies <c>e_i</c> (sign of life and lock included; may exceed 1).</summary>
    public ReadOnlySpan<double> CoilEnergies => _coilEnergy.AsSpan(0, CoilCount);

    /// <summary>Per-coil opacity (0..1): draw coil i in <see cref="CoilColor"/> at <c>opacity × (1 − hot)</c> and <see cref="CoilHotColor"/> at <c>opacity × hot</c>.</summary>
    public ReadOnlySpan<double> CoilOpacities => _coilOpacity.AsSpan(0, CoilCount);

    /// <summary>Per-coil blend (0..1) toward <see cref="CoilHotColor"/> (energy above 0.75).</summary>
    public ReadOnlySpan<double> CoilHotness => _coilHot.AsSpan(0, CoilCount);

    /// <summary>Gold intensity (0..1) of the new-peak lock (or its Reduce Motion crossfade).</summary>
    public double LockIntensity
    {
        get
        {
            var age = _time - _lockStart;
            return _lock switch
            {
                LockMode.Swing when age is >= 0 and < LockEnd => Math.Max(0, 1 - Math.Abs(age / LockSwing - 1) / 0.35),
                LockMode.Crossfade when age is >= 0 and < ReducedLockDuration => Math.Sin(Math.PI * age / ReducedLockDuration),
                _ => 0,
            };
        }
    }

    /// <summary>True while the rotors swing to (and hold) the lock pose.</summary>
    public bool Locking => _lock == LockMode.Swing;

    /// <summary>Corona opacity (0..1) applied to <see cref="CoronaColor"/> at the inner radius (additive in dark).</summary>
    public double CoronaOpacity { get; private set; }

    /// <summary>Pulse flare ring: visible while true.</summary>
    public bool FlareVisible => FlareAge is >= 0 and < FlareDuration && FlareAllowed;

    /// <summary>Flare ring radius (S): 0.30 → 0.50 over 0.32 s, ease-out.</summary>
    public double FlareRadius => ReactorGeometry.FlareStart
        + (ReactorGeometry.FlareEnd - ReactorGeometry.FlareStart) * Easing.OutCubic(FlareAge / FlareDuration);

    /// <summary>Flare ring stroke width (S): 0.012 S → 0, plus 0.5 pt.</summary>
    public double FlareWidth => ReactorGeometry.FlareWidth * (1 - Easing.Clamp01(FlareAge / FlareDuration)) + 0.5 / _size;

    /// <summary>Flare ring color with its alpha: the response's heat color at 55 % fading out (additive in dark).</summary>
    public Rgba FlareColor => _flareColor.WithAlpha(FlareVisible ? 0.55 * (1 - FlareAge / FlareDuration) : 0);

    /// <summary>The center of coil <paramref name="index"/> (degrees).</summary>
    public double CoilAngleDegrees(int index) => Normalize(_angleB + 360.0 * index / CoilCount);

    private double FlareAge => _time - _flareStart;

    private bool FlareAllowed => _effects >= EffectsIntensity.Soft && !_reduceMotion;

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
        _kick = 0;
        _ratesPrimed = false;
        if (_lock == LockMode.Swing)
        {
            _angleA = Normalize(_lockToA);
            _angleB = Normalize(_lockToB);
            _angleC = Normalize(_lockToC);
        }

        _lock = LockMode.None;
        _lockStart = double.NegativeInfinity;
        _flareStart = double.NegativeInfinity;
        ClearLightning();
        _previousHeat = _heat;
        _previousOverdrive = _overdrive;
        Finish();
    }

    public void Pulse(double heat, double strength)
    {
        if (!FlareAllowed || _size < 64 || !Active)
        {
            return;
        }

        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1.25) : 0.5;
        heat = Math.Max(0.15, double.IsFinite(heat) ? Math.Clamp(heat, 0, 1) : 0);
        _kick += 0.25 * strength;
        _flareStart = _time;
        _flareColor = EnergyPalette.Label(heat, _dark);
    }

    public void Peak()
    {
        if (_effects < EffectsIntensity.Soft || _size < 64)
        {
            return;
        }

        _lockStart = _time;
        if (_reduceMotion)
        {
            _lock = LockMode.Crossfade;
            return;
        }

        if (_lock == LockMode.Swing)
        {
            _angleA = _lockToA;
            _angleB = _lockToB;
            _angleC = _lockToC;
        }

        _lock = LockMode.Swing;
        _lockFromA = _angleA;
        _lockFromB = _angleB;
        _lockFromC = _angleC;
        _lockToA = LockTarget(_angleA, 120, clockwise: true);
        _lockToB = LockTarget(_angleB, 360.0 / CoilCount, clockwise: false);
        _lockToC = LockTarget(_angleC, 180, clockwise: true);
    }

    /// <summary>The nearest pose ahead in the direction of travel with an arc head (or coil) at 12 o'clock (−90°).</summary>
    internal static double LockTarget(double angle, double symmetry, bool clockwise)
    {
        const double top = -90;
        var steps = (angle - top) / symmetry;
        var k = clockwise ? Math.Ceiling(steps - 1e-9) : Math.Floor(steps + 1e-9);
        return top + k * symmetry;
    }

    internal static double Normalize(double degrees)
    {
        var d = degrees % 360;
        return d < 0 ? d + 360 : d;
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
    }

    private double SpeedA(double activity) => activity * (0.03 + 0.45 * Math.Pow(_heat, 1.3) + 0.35 * _overdrive);

    private double SpeedB(double activity) => -activity * (0.02 + 0.30 * Math.Pow(_heat, 1.3) + 0.25 * _overdrive);

    private double SpeedC(double activity) => activity * (0.05 + 1.1 * Math.Pow(_heat, 1.3) + 0.7 * _overdrive);

    private void Step(double dt)
    {
        _activity.Step(Active ? 1 : 0, dt);
        var act = Activity;
        var still = Still;

        // The kick decays exponentially; integrate it exactly so the spin does not depend on the frame rate.
        var decay = Math.Exp(-dt / KickTau);
        var kickTurns = _kick * KickTau * (1 - decay);
        _kick = _kick * decay < 1e-6 ? 0 : _kick * decay;

        if (_lock == LockMode.Swing)
        {
            var age = _time - _lockStart;
            if (age >= LockEnd)
            {
                _lock = LockMode.None;
                _angleA = Normalize(_lockToA);
                _angleB = Normalize(_lockToB);
                _angleC = Normalize(_lockToC);
            }
            else
            {
                var e = Easing.OutBack(Math.Max(0, age) / LockSwing, 1.2);
                _angleA = _lockFromA + (_lockToA - _lockFromA) * e;
                _angleB = _lockFromB + (_lockToB - _lockFromB) * e;
                _angleC = _lockFromC + (_lockToC - _lockFromC) * e;
            }
        }
        else if (_lock == LockMode.Crossfade && _time - _lockStart >= ReducedLockDuration)
        {
            _lock = LockMode.None;
        }

        // Speeds and rates are integrated with the trapezoidal rule, so the motion does not depend on the frame rate.
        var h = HeatNow;
        var o = OverdriveNow;
        var speedA = SpeedA(act);
        var speedB = SpeedB(act);
        var speedC = SpeedC(act);
        var bandRate = 0.15 + 1.4 * h + 0.6 * o;
        var coronaRate = 0.3 + 1.7 * h;
        if (!_ratesPrimed)
        {
            (_lastSpeedA, _lastSpeedB, _lastSpeedC, _lastBandRate, _lastCoronaRate) = (speedA, speedB, speedC, bandRate, coronaRate);
            _ratesPrimed = true;
        }

        if (!still)
        {
            if (_lock != LockMode.Swing)
            {
                _angleA = Normalize(_angleA + 360 * ((_lastSpeedA + speedA) / 2 * dt + kickTurns));
                _angleB = Normalize(_angleB + 360 * ((_lastSpeedB + speedB) / 2 * dt - 0.6 * kickTurns));
                _angleC = Normalize(_angleC + 360 * ((_lastSpeedC + speedC) / 2 * dt + 1.2 * kickTurns));
            }

            _bandPhase = (_bandPhase + Tau * (_lastBandRate + bandRate) / 2 * dt) % (Tau * 1000);
        }

        if (_effects >= EffectsIntensity.Soft && !_reduceMotion)
        {
            _coronaPhase = (_coronaPhase + Tau * (_lastCoronaRate + coronaRate) / 2 * dt) % (Tau * 1000);
        }

        (_lastSpeedA, _lastSpeedB, _lastSpeedC, _lastBandRate, _lastCoronaRate) = (speedA, speedB, speedC, bandRate, coronaRate);

        if (still)
        {
            ClearLightning();
        }
        else
        {
            StepLightning(o, dt);
        }
    }

    private void Finish()
    {
        RefreshColors(force: false);
        UpdateCoils();
        UpdateCorona();
        FillLightning();
        Demand = ComputeDemand();
        _previousHeat = _heat;
        _previousOverdrive = _overdrive;
    }

    private void UpdateCoils()
    {
        var n = CoilCount;
        var act = Activity;
        var idle = 1 - act;
        var h = HeatNow;
        var uniform = _effects == EffectsIntensity.Off || _size < 64;
        var lockGold = LockIntensity;
        var sweep = -1.0;
        if (!Active && !_unavailable && _signOfLifeAge is { } age && age is >= 0 and < SignOfLifeDuration
            && _effects >= EffectsIntensity.Soft && !_reduceMotion)
        {
            sweep = age / SignOfLifeDuration;
        }

        var idleScale = 1 + ((_dark ? 0.38 : 0.45) * 0.8 - 1) * idle;
        for (var i = 0; i < n; i++)
        {
            var band = uniform ? MeanBand : Math.Pow(0.5 + 0.5 * Math.Cos(3 * Tau * i / n - _bandPhase), 2.2);
            var active = 0.18 + 0.82 * band * (0.35 + 0.65 * h);
            var e = active + (0.25 - active) * idle;
            if (sweep >= 0)
            {
                var d = i / (double)n - sweep;
                d -= Math.Floor(d);
                var m = Math.Min(d, 1 - d) * 7;
                e += 0.6 * Math.Exp(-m * m);
            }

            if (lockGold > 0)
            {
                e = Math.Max(e, lockGold);
            }

            _coilEnergy[i] = e;
            _coilOpacity[i] = Math.Clamp(e, 0, 1) * idleScale;
            _coilHot[i] = lockGold > 0 ? 0 : Math.Clamp((e - 0.75) * 3, 0, 1) * act;
        }
    }

    private void UpdateCorona()
    {
        if (_highContrast)
        {
            CoronaOpacity = 0;
            return;
        }

        var h = HeatNow;
        var motion = _effects >= EffectsIntensity.Soft && !_reduceMotion;
        var breath = motion ? 1 + 0.15 * Math.Sin(_coronaPhase) : 1;
        var flare = motion && FlareAge is >= 0 and < CoronaFlareDuration ? 0.25 * (1 - FlareAge / CoronaFlareDuration) : 0;
        var boltAge = _time - _boltFlashStart;
        var bolt = boltAge is >= 0 and < BoltFlashDuration ? BoltFlashBoost * (1 - boltAge / BoltFlashDuration) : 0;
        var baseline = _dark ? 0.10 + 0.42 * h : 0.06 + 0.20 * h;
        var intensity = (baseline * breath + flare + bolt) * (_effects == EffectsIntensity.Max ? 1.2 : 1) * (1 + 0.25 * _flash);
        CoronaOpacity = Math.Clamp(intensity * Activity, 0, 1);
    }

    private FrameClass ComputeDemand()
    {
        var settling = Math.Abs(_heat - _previousHeat) > 0.0005
            || Math.Abs(_overdrive - _previousOverdrive) > 0.0005
            || !_activity.IsSettled(Active ? 1 : 0, 0.002);
        if (Still)
        {
            return settling || _lock != LockMode.None ? FrameClass.Fps30 : FrameClass.None;
        }

        var boltFlash = _time - _boltFlashStart is >= 0 and < BoltFlashDuration;
        if (_flash > 0 || _lock != LockMode.None || FlareAge is >= 0 and < CoronaFlareDuration || _kick > 0.01 || boltFlash)
        {
            return FrameClass.Fps60;
        }

        if (!Active)
        {
            var signOfLife = _signOfLifeAge is { } age && age < SignOfLifeDuration && !_unavailable;
            return signOfLife || _sparkCount > 0 || _boltViewCount > 0 || settling ? FrameClass.Fps30 : FrameClass.None;
        }

        return _tier >= SpeedTier.Overdrive ? FrameClass.Fps60 : FrameClass.Fps30;
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static double ComputeMeanBand()
    {
        const int samples = 4096;
        var sum = 0.0;
        for (var i = 0; i < samples; i++)
        {
            sum += Math.Pow(0.5 + 0.5 * Math.Cos(Tau * (i + 0.5) / samples), 2.2);
        }

        return sum / samples;
    }
}
