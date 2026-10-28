using AgentSpeed.Core;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Particles;

namespace AgentSpeed.Presentation.Orb;

public readonly record struct Shockwave(double Progress, double Strength, double Heat);

public readonly record struct TierTransition(SpeedTier From, SpeedTier To, double Progress);

public readonly record struct TitleTransition(SpeedTitle From, SpeedTitle To, double Progress);

/// <summary>
/// A unit switch of the readout (DESIGN 9 Orb, Transition): the old value and unit leave upward while the new ones
/// arrive from below (0.3 s, the new value already in the new unit); Reduce Motion: a 0.15 s crossfade.
/// </summary>
/// <param name="FromValue">The value text that leaves (with its <c>≥</c>, see <paramref name="FromAtLeast"/>).</param>
/// <param name="FromUnit">The unit text that leaves; null when the renderer's configured unit was shown.</param>
public readonly record struct UnitSwitch(string FromValue, bool FromAtLeast, string? FromUnit, double Progress);

public enum FrameDemand
{
    None,

    /// <summary>About 10 fps (ambient breathing and slow sweeps).</summary>
    Slow,

    /// <summary>About 15 fps (the parked hero vehicle: breathing and its idle light band).</summary>
    Medium,

    /// <summary>Every display frame.</summary>
    Full,
}

/// <summary>
/// Platform-neutral animation state of the orb (DESIGN section 2). The renderer reads the properties after each
/// <see cref="Update"/>; geometry is in units of S unless stated otherwise.
/// </summary>
public sealed class OrbScene
{
    public const double PulseDuration = 0.7;
    public const double SilkPulseDuration = 0.85;
    public const double LeanDegrees = -6;
    public const double SilkLeanDegrees = -7.5;
    public const double PulseCoalesce = 0.15;
    public const double PeakRayDuration = 0.9;
    public const double PeakSweepDuration = 0.5;
    public const double PeakPillDuration = 1.8;
    public const double TierTransitionDuration = 0.35;
    public const double FlashDuration = 0.4;
    public const double IdleSweepInterval = 6;
    public const double IdleSweepDuration = 1.6;
    public const double ReadoutMinInterval = 0.15;
    public const int MaxAgentDots = 6;
    public const int MaxPulses = 8;

    private readonly Random _random;
    private readonly List<PulseState> _pulses = new(MaxPulses);
    private readonly float[] _shimmer = new float[GaugeLayout.SegmentCount];
    private readonly List<SourceKind> _agents = new(MaxAgentDots);

    private CriticalSpring _heat = new(0.6);
    private CriticalSpring _overdrive = new(0.6);
    private CriticalSpring _cometVisibility = new(0.35);
    private CriticalSpring _redlineAmount = new(0.4);
    private CriticalSpring _skew = new(0.35);
    private CriticalSpring _hoverBlend = new(0.25);
    private DampedSpring _hover = DampedSpring.Layout();

    private double _lastUpdate = double.NaN;
    private double _now;
    private double _targetValue;
    private SpeedTier _tier = SpeedTier.Idle;
    private bool _hovering;
    private double _breathPhase;
    private double _cometPhase;
    private double _flare;
    private double _flashStart = double.NegativeInfinity;
    private double _transitionStart = double.NegativeInfinity;
    private SpeedTier _previousTier = SpeedTier.Idle;
    private SpeedTitle _title = SpeedTitle.Idle;
    private SpeedTitle _previousTitle = SpeedTitle.Idle;
    private double _titleTransitionStart = double.NegativeInfinity;
    private double _lastPulseAt = double.NegativeInfinity;
    private double _peakStart = double.NegativeInfinity;
    private double _nextShimmerAt;
    private double _nextJitterAt;
    private double _idleSince;
    private double _sweepStart = double.NegativeInfinity;
    private string _pendingText = "0";
    private double _pendingValue;
    private double _shownValue;
    private double _lastTextChange = double.NegativeInfinity;
    private double _sparkRate;
    private double _emberRate;
    private double _signOfLifeStart = double.NegativeInfinity;
    private Cost.LiveMoney? _money;
    private double _unitSwitchStart = double.NegativeInfinity;
    private string _switchFromValue = "0";
    private bool _switchFromAtLeast;
    private string? _switchFromUnit;
    private bool _unitHovered;
    private double _unitHoverFrom;
    private double _unitHoverStart = double.NegativeInfinity;

    public OrbScene(int? seed = null)
    {
        _random = seed is null ? new Random() : new Random(seed.Value);
        Particles = new ParticleSystem(ParticleSystem.DefaultCapacity, seed);
        Array.Fill(_shimmer, 1f);
        Readout.Set("0", 1, 0, animate: false);
    }

    public EffectsPolicy Effects { get; set; } = new(Settings.EffectsIntensity.Standard);

    public double Redline { get; set; } = HeatMath.DefaultRedline;

    public bool PulsesEnabled { get; set; } = true;

    public bool PeaksEnabled { get; set; } = true;

    /// <summary>
    /// The LED instrument's own effects (comet, sparks, embers, shimmer, edge glow). Other styles (DESIGN 2a) turn this
    /// off and keep only the shared state: springs, tiers, titles, readout, pulses, peak, hover.
    /// </summary>
    public bool LedEffects { get; set; } = true;

    /// <summary>
    /// The floating <c>led</c> orb's v1.4 effects (DESIGN 2.1): its style model owns the sparks and embers, the comet speeds
    /// up past the redline (period ÷ (1 + 0.6 o)), the value leans <c>−7.5° h</c> and jitters <c>0.9 + 1.2 o</c> px at the
    /// redline, and the response wave lasts 0.85 s. Off for the dashboard hero and the WPF fallback renderer (v1.3).
    /// </summary>
    public bool SilkEffects { get; set; }

    /// <summary>Length of a response wave: <see cref="PulseDuration"/>, 0.85 s with <see cref="SilkEffects"/>.</summary>
    public double PulseLength => SilkEffects ? SilkPulseDuration : PulseDuration;

    /// <summary>
    /// Floating orbs (DESIGN 2a): the idle sign of life runs on the shared clock, every 6 s at multiples of
    /// <see cref="IdleSweepInterval"/>, skipping one that would start less than 2 s after entering idle, for
    /// <see cref="SignOfLifeDuration"/>. The dashboard hero keeps the sweep relative to its own idle start.
    /// </summary>
    public bool SharedSignOfLife { get; set; }

    /// <summary>Length of one sign-of-life event (0.9 s; the liquid bubble 1.2 s; 0 for none).</summary>
    public double SignOfLifeDuration { get; set; } = 0.9;

    /// <summary>LOD multiplier of the LED spark and ember rates (0.5 below S 120, DESIGN 2a.1).</summary>
    public double ParticleRateScale { get; set; } = 1;

    /// <summary>The value odometer of the floating orb styles (DESIGN 2a).</summary>
    public OdometerText Odometer { get; } = new();

    /// <summary>Length of a unit switch (DESIGN 9 Orb, Transition); Reduce Motion uses <see cref="UnitSwitchFade"/>.</summary>
    public const double UnitSwitchDuration = 0.3;

    public const double UnitSwitchFade = 0.15;

    /// <summary>The money readout while the orb shows the money unit (DESIGN 9); null for tok/s.</summary>
    public Cost.LiveMoney? Money => _money;

    /// <summary>
    /// The unit line's text in money mode (<c>US$/s</c>); null keeps the configured unit (tok/s). Renderers draw this
    /// instead of their configured unit text when it is set.
    /// </summary>
    public string? UnitText => _money?.Unit;

    /// <summary>
    /// A small <c>≥</c> before the value (60 % of its size, secondary color, baseline-aligned, part of the value's width)
    /// when part of the live money value has no known price.
    /// </summary>
    public bool ValueAtLeast => _money?.AtLeast ?? false;

    /// <summary>Nothing is priced: the value (<c>–</c>) reads in the idle value color without glow.</summary>
    public bool ValueMuted => _money?.Unknown ?? false;

    /// <summary>The running unit switch, or null.</summary>
    public UnitSwitch? UnitSwitch
    {
        get
        {
            var duration = Effects.ReduceMotion || Effects.Level == Settings.EffectsIntensity.Off ? UnitSwitchFade : UnitSwitchDuration;
            var progress = (_now - _unitSwitchStart) / duration;
            return progress is >= 0 and < 1 ? new UnitSwitch(_switchFromValue, _switchFromAtLeast, _switchFromUnit, progress) : null;
        }
    }

    /// <summary>Length of the unit's hover affordance (55 % → 85 % with a 0.5 DIP underline at 40 %, DESIGN 9).</summary>
    public const double UnitHoverDuration = 0.15;

    /// <summary>The unit's hover envelope 0..1: renderers raise the unit text from 55 % to 85 % opacity and draw its underline at 40 % × this.</summary>
    public double UnitHoverAmount
    {
        get
        {
            var target = _unitHovered ? 1.0 : 0.0;
            var progress = Math.Clamp((_now - _unitHoverStart) / UnitHoverDuration, 0, 1);
            return _unitHoverFrom + (target - _unitHoverFrom) * Easing.OutCubic(progress);
        }
    }

    /// <summary>The pointer is over the unit's hit area (see <see cref="OrbUnitToggle"/>).</summary>
    public void SetUnitHover(bool hovering)
    {
        if (hovering == _unitHovered)
        {
            return;
        }

        _unitHoverFrom = UnitHoverAmount;
        _unitHovered = hovering;
        _unitHoverStart = _now;
    }

    /// <summary>
    /// Shows the money unit (<paramref name="money"/> non-null) or tok/s (null). Heat, tiers, titles, sparks and peaks
    /// keep following the token value of <see cref="SetTarget(double, SpeedTier)"/>. Switching the unit starts a
    /// <see cref="UnitSwitch"/> and shows the new text at once, without rolling digits.
    /// </summary>
    public void SetMoney(Cost.LiveMoney? money)
    {
        var switching = (money is null) != (_money is null);
        if (switching && !double.IsNaN(_lastUpdate))
        {
            _unitSwitchStart = _now;
            _switchFromValue = Readout.Current;
            _switchFromAtLeast = _money?.AtLeast ?? false;
            _switchFromUnit = _money?.Unit;
        }

        _money = money;
        _pendingText = money?.Number ?? SpeedFormat.Speed(_targetValue);
        _pendingValue = money?.Value ?? _targetValue;
        if (switching)
        {
            Readout.Set(_pendingText, 1, _now, animate: false);
            Odometer.Set(_pendingText, true, _now, animate: false);
            _shownValue = _pendingValue;
            _lastTextChange = _now;
        }
    }

    /// <summary>The live value the springs move toward (tok/s).</summary>
    public double TargetValue => _targetValue;

    /// <summary>Unsmoothed heat <c>h_raw</c>.</summary>
    public double HeatRaw => HeatMath.Heat(_targetValue, Redline);

    /// <summary>Unsmoothed overdrive <c>o_raw</c>.</summary>
    public double OverdriveRaw => HeatMath.Overdrive(_targetValue, Redline);

    /// <summary>True while the heat and overdrive springs are still moving.</summary>
    public bool SpringsMoving =>
        !_heat.IsSettled(HeatRaw, 0.0005) || !_overdrive.IsSettled(OverdriveRaw, 0.0005);

    /// <summary>Seconds since the running shared sign-of-life event started, or null (see <see cref="SharedSignOfLife"/>).</summary>
    public double? SignOfLifeAge
    {
        get
        {
            if (!SharedSignOfLife || _tier != SpeedTier.Idle || !Effects.IdleSweep || SignOfLifeDuration <= 0)
            {
                return null;
            }

            var age = _now - _signOfLifeStart;
            return age >= 0 && age < SignOfLifeDuration ? age : null;
        }
    }

    /// <summary>Start of the next shared sign-of-life event (for a single wake-up timer), or null.</summary>
    public double? NextSignOfLife
    {
        get
        {
            if (!SharedSignOfLife || _tier != SpeedTier.Idle || !Effects.IdleSweep || SignOfLifeDuration <= 0)
            {
                return null;
            }

            var earliest = Math.Max(_idleSince + 2, _now);
            var next = Math.Ceiling(earliest / IdleSweepInterval) * IdleSweepInterval;
            return next <= _signOfLifeStart ? next + IdleSweepInterval : next;
        }
    }

    /// <summary>
    /// The frame class the shared layers need (DESIGN 2a frame table, "Transitions" and "Off or Reduce Motion"): 60 fps
    /// (30 for <c>minimal</c>) while a title, peak, pulse, hover, flash or value roll runs; 30 fps while the heat springs
    /// settle; otherwise none (the style decides its steady class).
    /// </summary>
    public FrameClass SharedFrameClass(bool minimal)
    {
        var transition = minimal ? FrameClass.Fps30 : FrameClass.Fps60;
        if (_pulses.Count > 0 || _flare > 0.002
            || _now - _peakStart < Math.Max(PeakPillDuration, PeakRayDuration)
            || _now - _titleTransitionStart < TierTransitionDuration
            || Flash > 0
            || Odometer.IsAnimating(_now)
            || _pendingText != Odometer.Current
            || UnitSwitch is not null
            || !_hover.IsSettled(_hovering ? 1 : 0) || !_hoverBlend.IsSettled(_hovering ? 1 : 0)
            || !_skew.IsSettled(SkewTarget(HeatRaw), 0.01))
        {
            return transition;
        }

        return SpringsMoving ? FrameClass.Fps30 : FrameClass.None;
    }

    public ParticleSystem Particles { get; }

    public RollingText Readout { get; } = new();

    public double Time => _now;

    public double Value => _shownValue;

    public SpeedTier Tier => _tier;

    /// <summary>The visible label; changing it slides the label but never flashes the gauge.</summary>
    public SpeedTitle Title => _title;

    public double Heat => Math.Clamp(_heat.Value, 0, 1);

    public double Overdrive => Math.Clamp(_overdrive.Value, 0, 1);

    /// <summary>Gauge fill heat, including the new-peak sweep.</summary>
    public double GaugeHeat
    {
        get
        {
            var sweep = (_now - _peakStart) / PeakSweepDuration;
            return sweep is >= 0 and < 1 && Effects.PeakBurst ? Heat * Easing.OutCubic(sweep) : Heat;
        }
    }

    /// <summary>Aura opacity factor before the appearance token scale (0.55 dark / 0.32 light).</summary>
    public double AuraIntensity
    {
        get
        {
            if (!Effects.Aura)
            {
                return 0;
            }

            var breath = Effects.AuraBreathing ? 1 + 0.15 * Math.Sin(2 * Math.PI * _breathPhase) : 1;
            return Math.Clamp(Heat * breath + _flare * Math.Max(0.35, Heat), 0, 1.4);
        }
    }

    public double CometDegrees => 270 + 360 * _cometPhase;

    public double CometOpacity => Math.Clamp((0.25 + 0.6 * Heat) * _cometVisibility.Value, 0, 1);

    /// <summary>0..1 brightness boost after an effect-tier change.</summary>
    public double Flash
    {
        get
        {
            var t = (_now - _flashStart) / FlashDuration;
            return t is >= 0 and < 1 ? 1 - Easing.OutQuad(t) : 0;
        }
    }

    public ReadOnlySpan<float> Shimmer => _shimmer;

    /// <summary>Pulsing glow on the outer edge of the arc at redline (alpha 0..1).</summary>
    public double EdgeGlow
    {
        get
        {
            var amount = Math.Clamp(_redlineAmount.Value, 0, 1);
            if (amount <= 0.001 || !Effects.AuraBreathing)
            {
                return amount * 0.25;
            }

            return amount * (0.3 + 0.2 * Math.Sin(2 * Math.PI * 2.2 * _now));
        }
    }

    public TierTransition Transition => new(_previousTier, _tier,
        Math.Clamp((_now - _transitionStart) / TierTransitionDuration, 0, 1));

    /// <summary>The label slide between titles (same duration as a tier transition).</summary>
    public TitleTransition TitleTransition => new(_previousTitle, _title,
        Math.Clamp((_now - _titleTransitionStart) / TierTransitionDuration, 0, 1));

    public IEnumerable<Shockwave> Pulses => _pulses.Select(p => new Shockwave(
        Math.Clamp((_now - p.Start) / PulseLength, 0, 1), p.Strength, p.Heat));

    public int PulseCount => _pulses.Count;

    /// <summary>The pulse at <paramref name="index"/> (0 ≤ index &lt; <see cref="PulseCount"/>) without allocating.</summary>
    public Shockwave PulseAt(int index)
    {
        var p = _pulses[index];
        return new Shockwave(Math.Clamp((_now - p.Start) / PulseLength, 0, 1), p.Strength, p.Heat);
    }

    /// <summary>New-peak ray burst progress 0..1, or null when inactive.</summary>
    public double? PeakRays
    {
        get
        {
            var t = (_now - _peakStart) / PeakRayDuration;
            return t is >= 0 and < 1 && Effects.PeakBurst ? t : null;
        }
    }

    /// <summary>New-peak pill age in seconds, or null when hidden.</summary>
    public double? PeakPillAge
    {
        get
        {
            var age = _now - _peakStart;
            return age is >= 0 and < PeakPillDuration ? age : null;
        }
    }

    /// <summary>Seconds since the latest new peak (its pill shows for the first <see cref="PeakPillDuration"/>), or null before any.</summary>
    public double? PeakAge => double.IsNegativeInfinity(_peakStart) || _now < _peakStart ? null : _now - _peakStart;

    /// <summary>Idle light sweep position 0..1 along the arc, or null.</summary>
    public double? IdleSweep
    {
        get
        {
            if (_tier != SpeedTier.Idle || !Effects.IdleSweep)
            {
                return null;
            }

            var t = (_now - _sweepStart) / (SharedSignOfLife ? SignOfLifeDuration : IdleSweepDuration);
            return t is >= 0 and < 1 ? t : null;
        }
    }

    /// <summary>Start time of the gauge's latest idle sweep (negative infinity before the first one).</summary>
    public double IdleSweepStart => _tier == SpeedTier.Idle && Effects.IdleSweep ? _sweepStart : double.NegativeInfinity;

    public double JitterX { get; private set; }

    public double JitterY { get; private set; }

    public double SkewDegrees => _skew.Value;

    /// <summary>Hover spring 0..1 (may overshoot slightly).</summary>
    public double Hover => Effects.HoverScale ? _hover.Value : _hovering ? 1 : 0;

    /// <summary>0..1 crossfade from the title label to the info line.</summary>
    public double HoverBlend => Math.Clamp(_hoverBlend.Value, 0, 1);

    public IReadOnlyList<SourceKind> AgentDots => _agents;

    public int AgentOverflow { get; private set; }

    public double SparkRate => _sparkRate;

    public double EmberRate => _emberRate;

    /// <summary>Sets the value, the effect tier (flash, effects) and the speed title (label).</summary>
    public void SetTarget(double value, SpeedTier tier, SpeedTitle title)
    {
        SetTarget(value, tier);
        if (title != _title)
        {
            _previousTitle = _title;
            _title = title;
            _titleTransitionStart = _now;
        }
    }

    public void SetTarget(double value, SpeedTier tier)
    {
        _targetValue = double.IsFinite(value) && value > 0 ? value : 0;
        _pendingValue = _money?.Value ?? _targetValue;
        _pendingText = _money?.Number ?? SpeedFormat.Speed(_targetValue);

        if (tier != _tier)
        {
            _previousTier = _tier;
            _tier = tier;
            _transitionStart = _now;
            if (tier != SpeedTier.Idle || _previousTier != SpeedTier.Idle)
            {
                _flashStart = _now;
            }

            if (tier == SpeedTier.Idle)
            {
                _idleSince = _now;
                _sweepStart = double.NegativeInfinity;
            }
        }
    }

    public void SetAgents(IEnumerable<SourceKind> generating)
    {
        ArgumentNullException.ThrowIfNull(generating);
        _agents.Clear();
        var total = 0;
        foreach (var source in generating)
        {
            if (_agents.Count < MaxAgentDots)
            {
                _agents.Add(source);
            }

            total++;
        }

        AgentOverflow = Math.Max(0, total - MaxAgentDots);
    }

    public void SetHover(bool hovering) => _hovering = hovering;

    /// <returns>False when the pulse was coalesced into a recent one.</returns>
    public bool TriggerPulse(double speed, long outputTokens)
    {
        if (!PulsesEnabled || !Effects.Pulses)
        {
            return false;
        }

        var strength = Math.Clamp(Math.Log10(Math.Max(outputTokens, 1)) / 3, 0.3, 1.25);
        _flare = Math.Min(1, _flare + 0.35 * strength);
        if (_now - _lastPulseAt < PulseCoalesce)
        {
            return false;
        }

        _lastPulseAt = _now;
        if (_pulses.Count >= MaxPulses)
        {
            _pulses.RemoveAt(0);
        }

        _pulses.Add(new PulseState(_now, strength, HeatMath.Heat(speed, Redline)));
        return true;
    }

    public void TriggerPeak()
    {
        if (PeaksEnabled)
        {
            _peakStart = _now;
        }
    }

    /// <summary>
    /// Jumps every spring to its target and drops running particles and transitions, so the first frame after a pause
    /// shows the current state without a catch-up animation (DESIGN 2a, Frame policy).
    /// </summary>
    public void Snap(double nowSeconds)
    {
        Update(nowSeconds);
        _heat.Snap(HeatMath.Heat(_targetValue, Redline));
        _overdrive.Snap(HeatMath.Overdrive(_targetValue, Redline));
        _cometVisibility.Snap(LedEffects && Effects.Comet && _tier != SpeedTier.Idle ? 1 : 0);
        _redlineAmount.Snap(LedEffects && _tier == SpeedTier.Redline ? 1 : 0);
        _skew.Snap(SkewTarget(Heat));
        _pulses.Clear();
        _flare = 0;
        Particles.Clear();
        _transitionStart = double.NegativeInfinity;
        _titleTransitionStart = double.NegativeInfinity;
    }

    public void Update(double nowSeconds)
    {
        var dt = double.IsNaN(_lastUpdate) ? 0 : nowSeconds - _lastUpdate;
        if (dt < 0 || dt > 0.25)
        {
            dt = 1 / 60.0;
        }

        if (double.IsNaN(_lastUpdate))
        {
            _idleSince = nowSeconds;
            _transitionStart = double.NegativeInfinity;
            _titleTransitionStart = double.NegativeInfinity;
        }

        _lastUpdate = nowSeconds;
        _now = nowSeconds;

        var targetHeat = HeatMath.Heat(_targetValue, Redline);
        var targetOverdrive = HeatMath.Overdrive(_targetValue, Redline);
        _heat.Step(targetHeat, dt);
        _overdrive.Step(targetOverdrive, dt);

        var heat = Heat;
        _breathPhase = (_breathPhase + dt / Easing.Lerp(4, 0.7, heat)) % 1;
        _flare *= Math.Exp(-dt / 0.25);
        if (_flare < 0.002)
        {
            _flare = 0;
        }

        var cometOn = LedEffects && Effects.Comet && _tier != SpeedTier.Idle;
        _cometVisibility.Step(cometOn ? 1 : 0, dt);
        if (_cometVisibility.Value > 0.001)
        {
            var boost = SilkEffects ? 1 + 0.6 * Overdrive : 1;
            _cometPhase = (_cometPhase + dt / Easing.Lerp(6, 0.7, heat) * boost) % 1;
        }

        _redlineAmount.Step(LedEffects && _tier == SpeedTier.Redline ? 1 : 0, dt);
        _skew.Step(SkewTarget(heat), dt);
        _hover.Step(_hovering ? 1 : 0, dt);
        _hoverBlend.Step(_hovering ? 1 : 0, dt);

        UpdateReadout();
        UpdateShimmer();
        UpdateJitter();
        UpdatePulses();
        UpdateIdleSweep();
        UpdateParticles(dt);
    }

    public FrameDemand Demand
    {
        get
        {
            var targetHeat = HeatMath.Heat(_targetValue, Redline);
            var full =
                !_heat.IsSettled(targetHeat, 0.0005)
                || !_overdrive.IsSettled(HeatMath.Overdrive(_targetValue, Redline), 0.0005)
                || (Effects.AuraBreathing && Heat > 0.004)
                || _cometVisibility.Value > 0.001 && (Effects.Comet || !_cometVisibility.IsSettled(0))
                || Particles.Count > 0 || _sparkRate > 0 || _emberRate > 0
                || _pulses.Count > 0 || _flare > 0
                || _now - _peakStart < Math.Max(PeakPillDuration, PeakRayDuration)
                || _now - _transitionStart < TierTransitionDuration
                || _now - _titleTransitionStart < TierTransitionDuration
                || Flash > 0
                || Readout.IsAnimating(_now)
                || _pendingText != Readout.Current
                || UnitSwitch is not null
                || _now - _unitHoverStart < UnitHoverDuration
                || !_hover.IsSettled(_hovering ? 1 : 0) || !_hoverBlend.IsSettled(_hovering ? 1 : 0)
                || _redlineAmount.Value > 0.001 && Effects.AuraBreathing
                || !_redlineAmount.IsSettled(_tier == SpeedTier.Redline ? 1 : 0)
                || !_skew.IsSettled(SkewTarget(targetHeat), 0.01)
                || _agents.Count > 0 && Effects.AuraBreathing;
            if (full)
            {
                return FrameDemand.Full;
            }

            return IdleSweep is not null ? FrameDemand.Slow : FrameDemand.None;
        }
    }

    /// <summary>When nothing animates, the time of the next scheduled visual change (idle sweep).</summary>
    public double? NextWake
    {
        get
        {
            if (_tier != SpeedTier.Idle || !Effects.IdleSweep)
            {
                return null;
            }

            if (SharedSignOfLife)
            {
                return NextSignOfLife;
            }

            var last = double.IsNegativeInfinity(_sweepStart) ? _idleSince : _sweepStart;
            return last + IdleSweepInterval;
        }
    }

    /// <summary>The forward lean from overdrive up: <c>−6° h</c>, <c>−7.5° h</c> with <see cref="SilkEffects"/>.</summary>
    private double SkewTarget(double heat) =>
        Effects.Skew && _tier >= SpeedTier.Overdrive ? (SilkEffects ? SilkLeanDegrees : LeanDegrees) * heat : 0;

    private void UpdateReadout()
    {
        if (_pendingText == Readout.Current || _now - _lastTextChange < ReadoutMinInterval)
        {
            return;
        }

        var direction = _pendingValue >= _shownValue ? 1 : -1;
        Readout.Set(_pendingText, direction, _now, Effects.RollingDigits);
        Odometer.Set(_pendingText, direction > 0, _now, Effects.RollingDigits);
        _shownValue = _pendingValue;
        _lastTextChange = _now;
    }

    private void UpdateShimmer()
    {
        var active = LedEffects && Effects.Shimmer && _tier == SpeedTier.Redline;
        if (!active)
        {
            if (_shimmer[0] != 1f || _shimmer[^1] != 1f)
            {
                Array.Fill(_shimmer, 1f);
            }

            return;
        }

        if (_now < _nextShimmerAt)
        {
            return;
        }

        _nextShimmerAt = _now + 1 / 12.0;
        for (var i = 0; i < _shimmer.Length; i++)
        {
            _shimmer[i] = (float)(0.75 + 0.25 * _random.NextDouble());
        }
    }

    private void UpdateJitter()
    {
        if (!(Effects.Jitter && _tier == SpeedTier.Redline))
        {
            JitterX = 0;
            JitterY = 0;
            return;
        }

        if (_now < _nextJitterAt)
        {
            return;
        }

        _nextJitterAt = _now + 1 / 30.0;
        var amplitude = SilkEffects ? 0.9 + 1.2 * Overdrive : 0.6;
        JitterX = (_random.NextDouble() * 2 - 1) * amplitude;
        JitterY = (_random.NextDouble() * 2 - 1) * amplitude;
    }

    private void UpdatePulses()
    {
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            if (_now - _pulses[i].Start >= PulseLength)
            {
                _pulses.RemoveAt(i);
            }
        }
    }

    private void UpdateIdleSweep()
    {
        if (_tier != SpeedTier.Idle || !Effects.IdleSweep)
        {
            return;
        }

        if (SharedSignOfLife)
        {
            var slot = Math.Floor(_now / IdleSweepInterval) * IdleSweepInterval;
            if (slot >= _idleSince + 2 && slot > _signOfLifeStart && _now - slot < SignOfLifeDuration)
            {
                _signOfLifeStart = slot;
                _sweepStart = slot;
            }

            return;
        }

        var last = double.IsNegativeInfinity(_sweepStart) ? _idleSince : _sweepStart;
        if (_now - last >= IdleSweepInterval)
        {
            _sweepStart = _now;
        }
    }

    private void UpdateParticles(double dt)
    {
        var heat = Heat;
        if (Effects.Particles && LedEffects && !SilkEffects)
        {
            _sparkRate = heat >= 0.45 ? 120 * ((heat - 0.45) / 0.55) * Effects.ParticleRate * ParticleRateScale : 0;
            _emberRate = _tier == SpeedTier.Redline ? 90 * Math.Max(Overdrive, 0.3) * Effects.ParticleRate * ParticleRateScale : 0;
        }
        else
        {
            _sparkRate = 0;
            _emberRate = 0;
        }

        var leading = GaugeLayout.AngleForFraction(GaugeHeat);
        Particles.Emit(new EmissionParameters(heat, Overdrive, _sparkRate, _emberRate, leading, GaugeLayout.StartDegrees), dt);
        Particles.Step(dt);
    }

    private readonly record struct PulseState(double Start, double Strength, double Heat);
}
