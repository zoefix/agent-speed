using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Animation state of the <c>needle</c> style, the analog racing tachometer (DESIGN 2a.2). Platform neutral: after each
/// <see cref="Update"/> a renderer reads the properties. Angles are degrees clockwise from 3 o'clock, lengths and σ are
/// fractions of S from the disc center, opacities 0..1. The <c>*Key</c> properties change only when the matching
/// retained layer must be re-recorded; everything else (needle angle, lamp and red-zone opacities, wake, sparks) is a
/// per-frame number. Deterministic for a seed; never allocates after construction.
/// </summary>
public sealed class NeedleModel : IOrbStyleModel
{
    public const double OverdrivePress = 0.02;
    public const double OverdrivePressPerUnit = 0.05;
    public const double PinnedAt = 1.0105;
    public const double TrembleSmallDegrees = 0.3;
    public const double TrembleOverdriveDegrees = 1.0;
    public const double TrembleMaxScale = 1.4;
    public const double WakeMinSpeed = 12;
    public const double WakeBaseDegrees = 4;
    public const double WakeSecondsPerDegree = 0.085;
    public const double WakeMaxDegrees = 55;
    public const double WakeOpacity = 0.32;
    public const double FastTurn = 90;
    public const double LampFlashHz = 3;
    public const double LampHysteresis = 0.003;

    /// <summary>A lamp counts as lit (one flash) above this opacity, the middle of the 28 → 100 % flash.</summary>
    public const double LampLitLevel = 0.64;

    public const int MaxFlashesPerSecond = 3;
    public const double FlareDuration = 0.35;
    public const double SweepDuration = 1.0;
    public const double SignOfLifeDuration = 0.9;
    public const double FlashBoost = 0.25;
    public const double SparkRate = 25;
    public const double SparkRatePerOverdrive = 70;
    public const double TickOpacity = 0.95;

    private const int PulseSlots = 4;
    private const double Fresh = -1;

    private static readonly double[] TremblePhaseOffsets = [0, 1.3, 2.1];

    private readonly HeroRandom _random;
    private readonly NeedleSparks _sparks = new();
    private readonly NeedleTick[] _ticks = new NeedleTick[NeedleGeometry.TickCount];
    private readonly NeedleLamp[] _lamps = new NeedleLamp[NeedleGeometry.LampCount];
    private readonly bool[] _lampOn = new bool[NeedleGeometry.LampCount];
    private readonly double[] _pulseAge = new double[PulseSlots];
    private readonly double[] _pulseStrength = new double[PulseSlots];
    private readonly double[] _tremblePhase = new double[3];
    private readonly double[] _lampShown = new double[NeedleGeometry.LampCount];
    private readonly double[] _lampEdges = new double[NeedleGeometry.LampCount * MaxFlashesPerSecond];

    private NeedleDynamics _spring;
    private CriticalSpring _activity = new(0.5);
    private NeedlePalette _palette = NeedlePalette.DarkPalette;
    private double _redPhase;
    private double _clock;
    private double _peakAge = double.PositiveInfinity;
    private int _litCount;
    private long _builtTickKey = -1;
    private long _builtNeedleKey = -1;

    public NeedleModel(uint seed = 1)
    {
        _random = new HeroRandom(seed);
        for (var i = 0; i < _tremblePhase.Length; i++)
        {
            _tremblePhase[i] = _random.Next() * StyleMath.Tau;
        }

        Array.Fill(_pulseAge, double.PositiveInfinity);
        Array.Fill(_lampEdges, double.NegativeInfinity);
    }

    public OrbStyle Style => OrbStyle.Needle;

    public FrameClass Demand { get; private set; }

    public NeedlePalette Palette => _palette;

    public bool Dark => _palette.Dark;

    /// <summary>Windows high contrast: draw ticks, needle and trace in the theme's text color, the red zone in its highlight color, no glows.</summary>
    public bool HighContrast { get; private set; }

    // Level of detail (DESIGN 2a.2, LOD): S < 120 drops the minor ticks and the shift lights; thumbnails keep dial band,
    // red zone, majors, needle and value.
    public bool ShowMinorTicks { get; private set; } = true;

    public bool ShowLamps { get; private set; } = true;

    public bool ShowStopPins { get; private set; } = true;

    public bool ShowHub { get; private set; } = true;

    /// <summary>Displayed dial position of the needle (spring + tremble + new-peak sweep, clamped by the stops).</summary>
    public double NeedleDial { get; private set; }

    public double NeedleDegrees => StyleMath.DialDegrees(NeedleDial);

    /// <summary>Angular velocity of the displayed needle in degrees per second, without the tremble.</summary>
    public double AngularVelocity { get; private set; }

    /// <summary>The spring's dial position (no tremble, no sweep).</summary>
    public double SpringDial => _spring.Value;

    /// <summary>The needle grinds against the peg (sparks fly).</summary>
    public bool Pinned { get; private set; }

    /// <summary>Blade fill (alpha = opacity): heat blended toward white-hot by overdrive; slate at idle.</summary>
    public Rgba NeedleFill { get; private set; }

    /// <summary>Dark only: Gaussian glow of the blade (σ in S, 0 when off).</summary>
    public double NeedleGlowSigma { get; private set; }

    public double NeedleGlowOpacity { get; private set; }

    public Rgba NeedleGlowColor { get; private set; }

    /// <summary>Light only: the blade's drop shadow (<see cref="NeedlePalette.NeedleShadow"/>).</summary>
    public bool NeedleShadowVisible => !Dark && !HighContrast;

    /// <summary>The bright spine from r 0.21 S to the tip (alpha = opacity).</summary>
    public Rgba Spine { get; private set; }

    /// <summary>Pulse accent 0..1.25: tip flare (σ + 0.0175 S, spot of radius 0.06 S).</summary>
    public double Flare { get; private set; }

    /// <summary>Opacity of the radial <see cref="NeedlePalette.FlareSpot"/> at the tip (additive in dark).</summary>
    public double FlareSpotOpacity { get; private set; }

    /// <summary>Changes when the blade, its glow or spine must be re-recorded (colors, σ); the angle is a transform.</summary>
    public long NeedleKey { get; private set; }

    public ReadOnlySpan<NeedleTick> Ticks => _ticks;

    public int LitTickCount => _litCount;

    /// <summary>Changes when any graduation's color, opacity, visibility or glow changes.</summary>
    public long TickKey { get; private set; }

    public bool TraceVisible { get; private set; }

    /// <summary>The trace runs from n = 0 to this dial position (the displayed needle).</summary>
    public double TraceEnd { get; private set; }

    /// <summary>Trace opacity at n = 0 and at the needle (linear in between), including the tier flash.</summary>
    public double TraceOpacityStart { get; private set; }

    public double TraceOpacityEnd { get; private set; }

    /// <summary>Quantized trace extent (0.25°) and brightness; re-record the trace when it changes.</summary>
    public long TraceKey { get; private set; }

    public ReadOnlySpan<NeedleLamp> Lamps => _lamps;

    /// <summary>0..1: how far the lamp colors are blended toward <see cref="NeedlePalette.LampFlash"/> (redline flash, new-peak fire).</summary>
    public double LampFlashMix { get; private set; }

    /// <summary>Changes when a lamp color or glow σ changes; opacities change freely.</summary>
    public long LampKey { get; private set; }

    /// <summary>The redline-tier flash is running (all five lamps blink at 3 Hz).</summary>
    public bool LampsFlashing { get; private set; }

    public double RedZoneOpacity { get; private set; }

    /// <summary>Red-zone glow σ in S (0 when off), in <see cref="NeedlePalette.RedZone"/>.</summary>
    public double RedZoneGlowSigma { get; private set; }

    public double RedZoneGlowOpacity { get; private set; }

    public long RedZoneGlowKey { get; private set; }

    public bool WakeVisible { get; private set; }

    /// <summary>The wake's bright edge (the needle) and its transparent tail, degrees.</summary>
    public double WakeHeadDegrees { get; private set; }

    public double WakeTailDegrees { get; private set; }

    public Rgba WakeColor { get; private set; }

    /// <summary>Opacity at the needle edge (fading to 0 at the tail).</summary>
    public double WakeHeadOpacity { get; private set; }

    public ReadOnlySpan<NeedleSpark> Sparks => _sparks.View;

    /// <summary>Center (dial position) of the idle sign-of-life band, or null.</summary>
    public double? SignOfLifeCenter { get; private set; }

    /// <summary>New-peak gauge sweep progress τ (0..1 s), or null.</summary>
    public double? PeakSweep { get; private set; }

    /// <summary>Trace color at a dial position: heat (dark) or the label color (light).</summary>
    public Rgba TraceColor(double dial) => StyleColors.Label(dial, Dark);

    public void Update(in OrbFrame frame) => Advance(frame, snap: false);

    public void Snap(in OrbFrame frame) => Advance(frame, snap: true);

    public void Pulse(double heat, double strength)
    {
        var slot = 0;
        for (var i = 1; i < PulseSlots; i++)
        {
            if (_pulseAge[i] > _pulseAge[slot])
            {
                slot = i;
            }
        }

        _pulseAge[slot] = Fresh;
        _pulseStrength[slot] = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1.25) : 0;
        Demand = FramePolicy.Max(Demand, FrameClass.Fps60);
    }

    /// <summary>Starts the gauge sweep; like <see cref="Pulse"/>, <see cref="Demand"/> rises at once.</summary>
    public void Peak()
    {
        _peakAge = Fresh;
        Demand = FramePolicy.Max(Demand, FrameClass.Fps60);
    }

    private void Advance(in OrbFrame frame, bool snap)
    {
        var dt = snap ? 0 : StyleMath.Finite(frame.Dt);
        dt = Math.Clamp(dt, 0, 0.1);
        _clock += dt;
        var effects = frame.Effects;
        var level = effects.Level;
        var reduceMotion = effects.ReduceMotion;
        HighContrast = effects.HighContrast;
        var thumbnail = frame.Thumbnail;

        // Thumbnails draw the still instrument: no glows, no pulsing red zone.
        var glows = !HighContrast && !thumbnail;
        var small = frame.Small;
        var dark = frame.Dark;
        _palette = NeedlePalette.For(dark);
        var rest = frame.Idle || frame.Unavailable;
        var redline = frame.Tier == SpeedTier.Redline && !rest;
        var heat = StyleMath.Clamp01(frame.Heat);
        var overdrive = StyleMath.Clamp01(frame.Overdrive);
        var heatRaw = StyleMath.Clamp01(frame.HeatRaw);
        var overdriveRaw = StyleMath.Clamp01(frame.OverdriveRaw);
        var soft = level >= EffectsIntensity.Soft;
        var max = level == EffectsIntensity.Max;
        var motion = soft && !reduceMotion && !thumbnail;

        ShowMinorTicks = !small;
        ShowLamps = !small;
        ShowStopPins = !thumbnail;
        ShowHub = !thumbnail;

        if (snap)
        {
            Array.Fill(_pulseAge, double.PositiveInfinity);
            _peakAge = double.PositiveInfinity;
            _sparks.Clear();
            _activity.Snap(rest ? 0 : 1);
        }
        else
        {
            AgeEvents(dt);
            _activity.Step(rest ? 0 : 1, dt);
        }

        var activity = StyleMath.Clamp01(_activity.Value);

        // Needle dynamics.
        var rawTarget = rest ? 0 : heatRaw + (overdriveRaw > 0 ? OverdrivePress + OverdrivePressPerUnit * overdriveRaw : 0);
        var followed = Math.Min(NeedleGeometry.Peg, (heat + Press(overdrive)) * activity);
        double restDial;
        if (motion)
        {
            if (snap)
            {
                _spring.Snap(rawTarget);
            }
            else
            {
                _spring.Step(rawTarget, dt);
            }

            restDial = Math.Clamp(rawTarget, NeedleGeometry.ZeroStop, NeedleGeometry.Peg);
        }
        else
        {
            _spring.Snap(followed);
            restDial = Math.Min(NeedleGeometry.Peg, rest ? 0 : heatRaw + Press(overdriveRaw));
        }

        var springDial = _spring.Value;

        // Tremble: three sines with accumulated phases, added after the spring.
        _tremblePhase[0] = StyleMath.Advance(_tremblePhase[0], 11.3, dt);
        _tremblePhase[1] = StyleMath.Advance(_tremblePhase[1], 17.9, dt);
        _tremblePhase[2] = StyleMath.Advance(_tremblePhase[2], 26.1, dt);
        var trembleDegrees = 0.0;
        if (level >= EffectsIntensity.Standard && !thumbnail && !rest)
        {
            var amplitude = StyleMath.SmoothStep(0.55, 1, heat) * (TrembleSmallDegrees + TrembleOverdriveDegrees * overdrive) * (max ? TrembleMaxScale : 1);
            trembleDegrees = amplitude * (0.5 * Math.Sin(_tremblePhase[0] + TremblePhaseOffsets[0])
                + 0.3 * Math.Sin(_tremblePhase[1] + TremblePhaseOffsets[1])
                + 0.2 * Math.Sin(_tremblePhase[2] + TremblePhaseOffsets[2]));
        }

        // New-peak sweep: to the peg and back over 1 s.
        double sweep = 0, sweepSlope = 0;
        PeakSweep = null;
        if (motion && _peakAge < SweepDuration)
        {
            var tau = _peakAge;
            PeakSweep = tau;
            sweep = StyleMath.SmoothStep(0, 0.38, tau) - StyleMath.SmoothStep(0.5, 1.0, tau);
            sweepSlope = StyleMath.SmoothStepSlope(0, 0.38, tau) - StyleMath.SmoothStepSlope(0.5, 1.0, tau);
        }

        var steady = springDial + (NeedleGeometry.Peg - springDial) * sweep;
        var shaken = springDial + trembleDegrees / StyleMath.DialSweepDegrees;
        NeedleDial = Math.Clamp(shaken + (NeedleGeometry.Peg - shaken) * sweep, NeedleGeometry.ZeroStop, NeedleGeometry.Peg);
        var reference = Math.Clamp(steady, NeedleGeometry.ZeroStop, NeedleGeometry.Peg);
        AngularVelocity = motion
            ? StyleMath.DialSweepDegrees * (_spring.Velocity * (1 - sweep) + (NeedleGeometry.Peg - springDial) * sweepSlope)
            : 0;
        Pinned = motion && springDial >= PinnedAt && rawTarget > NeedleGeometry.Peg;

        var flash = soft ? StyleMath.Clamp01(StyleMath.Finite(frame.Flash)) : 0;
        var flashQ = StyleMath.Bucket(flash, 8);
        var boost = 1 + FlashBoost * flashQ / 8.0;

        UpdateTicks(rest, reference, snap, dark && soft && glows, boost, flashQ, SignOfLife(frame, soft && !reduceMotion && !thumbnail));
        UpdateTrace(rest, thumbnail, dark, boost, flashQ);
        var fire = PeakSweep is { } t ? StyleMath.SmoothStep(0.25, 0.38, t) - StyleMath.SmoothStep(0.5, 0.65, t) : 0;
        UpdateLamps(frame.Time, rest, reference, snap, redline, soft, reduceMotion, dark && glows, fire);
        UpdateRedZone(redline, soft && !thumbnail, reduceMotion, glows ? level : EffectsIntensity.Off, overdrive, dt);
        UpdateWake(motion, rest, heat);
        UpdateNeedle(heat, overdrive, activity, soft, max, dark, glows);
        UpdateSparks(dt, level, thumbnail, overdrive, max, dark);

        Demand = ComputeDemand(motion, rest, frame.Tier, restDial, flash);
    }

    private static double Press(double overdrive) =>
        overdrive > 0 ? Math.Min(OverdrivePress + OverdrivePressPerUnit * overdrive, 2.5 * overdrive) : 0;

    private void AgeEvents(double dt)
    {
        for (var i = 0; i < PulseSlots; i++)
        {
            _pulseAge[i] = _pulseAge[i] == Fresh ? 0 : _pulseAge[i] + dt;
        }

        _peakAge = _peakAge == Fresh ? 0 : _peakAge + dt;
    }

    private double? SignOfLife(in OrbFrame frame, bool allowed)
    {
        SignOfLifeCenter = null;
        if (!allowed || !frame.Idle || frame.Unavailable || frame.SignOfLifeAge is not { } age || age < 0 || age >= SignOfLifeDuration)
        {
            return null;
        }

        // Quantized so the band and the tick key move together.
        var u = Math.Round(age / SignOfLifeDuration * 255) / 255;
        SignOfLifeCenter = -0.15 + 1.3 * u;
        return SignOfLifeCenter;
    }

    private void UpdateTicks(bool rest, double reference, bool snap, bool glow, double boost, int flashQ, double? band)
    {
        var count = rest ? 0 : CountLit(reference);
        if (snap || count >= _litCount || rest)
        {
            _litCount = count;
        }
        else
        {
            _litCount = Math.Max(count, CountLit(reference + LampHysteresis));
        }

        var bandQ = band is { } b ? (int)Math.Round((b + 0.15) / 1.3 * 255) : 511;
        long key = _litCount;
        key = key << 1 | (Dark ? 1L : 0);
        key = key << 1 | (ShowMinorTicks ? 1L : 0);
        key = key << 1 | (glow ? 1L : 0);
        key = key << 1 | (HighContrast ? 1L : 0);
        key = key << 4 | (uint)flashQ;
        key = key << 9 | (uint)bandQ;
        TickKey = key;
        if (key == _builtTickKey)
        {
            return;
        }

        _builtTickKey = key;
        var palette = _palette;
        var redStart = NeedleGeometry.RedZoneStart - 1e-6;
        var bandBoost = Dark ? 0.35 : 0.25;
        for (var k = 0; k < NeedleGeometry.TickCount; k++)
        {
            var dial = NeedleGeometry.TickDial(k);
            var major = NeedleGeometry.IsMajor(k);
            var lit = k < _litCount;
            var leading = lit && k == _litCount - 1;
            Rgba color;
            if (lit)
            {
                color = (leading ? palette.LeadTick(k) : palette.LitTick(k)).WithAlpha(TickOpacity * boost);
            }
            else if (dial >= redStart)
            {
                color = major ? palette.RedUnlitMajor : palette.RedUnlitMinor;
            }
            else
            {
                color = major ? palette.UnlitMajor : palette.UnlitMinor;
            }

            if (band is { } center)
            {
                var d = dial - center;
                color = color.WithAlpha(color.A + bandBoost * Math.Exp(-90 * d * d));
            }

            var glowOn = glow && lit && major;
            _ticks[k] = new NeedleTick(
                k,
                dial,
                StyleMath.DialDegrees(dial),
                NeedleGeometry.TickOuterRadius - (major ? NeedleGeometry.MajorLength : NeedleGeometry.MinorLength),
                NeedleGeometry.TickOuterRadius,
                major ? NeedleGeometry.MajorWidth : NeedleGeometry.MinorWidth,
                major,
                major || ShowMinorTicks,
                lit,
                leading,
                color,
                glowOn ? 0.015 : 0,
                glowOn ? Math.Min(1, 0.8 * TickOpacity * boost) : 0);
        }
    }

    private static int CountLit(double dial) =>
        dial < -1e-6 ? 0 : Math.Min(NeedleGeometry.TickCount, (int)Math.Floor(dial * NeedleGeometry.TickDivisions + 4e-5) + 1);

    private void UpdateTrace(bool rest, bool thumbnail, bool dark, double boost, int flashQ)
    {
        TraceEnd = NeedleDial;
        TraceVisible = !rest && !thumbnail && NeedleDial > 0.002;
        var scale = (dark ? 0.7 : 0.85) * boost;
        TraceOpacityStart = Math.Min(1, 0.3 * scale);
        TraceOpacityEnd = Math.Min(1, scale);
        long key = TraceVisible ? (long)Math.Round(NeedleDial * StyleMath.DialSweepDegrees * 4) + 64 : 0;
        key = key << 4 | (uint)flashQ;
        key = key << 1 | (dark ? 1L : 0);
        TraceKey = key;
    }

    private void UpdateLamps(double time, bool rest, double reference, bool snap, bool redline, bool soft, bool reduceMotion, bool glowOn, double fire)
    {
        var thresholds = NeedleGeometry.LampThresholds;
        for (var i = 0; i < _lampOn.Length; i++)
        {
            var margin = _lampOn[i] && !snap ? LampHysteresis : 0;
            _lampOn[i] = !rest && reference >= thresholds[i] - margin;
        }

        LampsFlashing = redline && soft && !reduceMotion;
        var flashColored = redline && soft;
        var blink = reduceMotion ? 1 : 0.28 + 0.72 * StyleMath.SmoothStep(-0.35, 0.35, StyleMath.ClockSine(time, LampFlashHz));
        fire = ShowLamps ? StyleMath.Clamp01(fire) : 0;
        var mixQ = flashColored ? 16 : StyleMath.Bucket(fire, 16);
        LampFlashMix = mixQ / 16.0;
        long key = mixQ;
        key = key << 1 | (Dark ? 1L : 0);
        key = key << 1 | (glowOn ? 1L : 0);
        LampKey = key;

        var degrees = NeedleGeometry.LampDegrees;
        var flashColor = _palette.LampFlash;
        for (var i = 0; i < _lamps.Length; i++)
        {
            var opacity = LimitFlashes(i, flashColored ? blink : Math.Max(_lampOn[i] ? 1 : 0, fire), snap);
            var baseColor = _palette.Lamp(i);
            var color = LampFlashMix >= 1 ? flashColor : LampFlashMix <= 0 ? baseColor : Rgba.Lerp(baseColor, flashColor, LampFlashMix);
            var (x, y) = StyleMath.Polar(NeedleGeometry.LampRingRadius, degrees[i]);
            _lamps[i] = new NeedleLamp(x, y, degrees[i], color, opacity, glowOn ? 0.02 : 0, glowOn ? 0.9 * opacity : 0);
        }
    }

    /// <summary>
    /// The lamps are the product's only blinking element and never light up more than three times per second
    /// (DESIGN 2a.2): the 3 Hz flash never trips this, but a mode change or a bounce across a threshold could.
    /// </summary>
    private double LimitFlashes(int lamp, double opacity, bool snap)
    {
        var edges = _lampEdges.AsSpan(lamp * MaxFlashesPerSecond, MaxFlashesPerSecond);
        var previous = _lampShown[lamp];
        if (snap)
        {
            edges.Fill(double.NegativeInfinity);
        }
        else if (previous < LampLitLevel && opacity >= LampLitLevel)
        {
            var oldest = 0;
            for (var j = 1; j < edges.Length; j++)
            {
                if (edges[j] < edges[oldest])
                {
                    oldest = j;
                }
            }

            if (_clock - edges[oldest] < 1 - 1e-6)
            {
                opacity = previous;
            }
            else
            {
                edges[oldest] = _clock;
            }
        }

        _lampShown[lamp] = opacity;
        return opacity;
    }

    private void UpdateRedZone(bool redline, bool soft, bool reduceMotion, EffectsIntensity level, double overdrive, double dt)
    {
        _redPhase = StyleMath.Advance(_redPhase, 2 + overdrive, dt);
        if (redline && soft)
        {
            RedZoneOpacity = reduceMotion ? 1 : 0.8 + 0.2 * (0.5 + 0.5 * Math.Sin(_redPhase));
        }
        else
        {
            RedZoneOpacity = _palette.RedZoneBaseOpacity;
        }

        var glowOn = redline && level >= EffectsIntensity.Standard && !HighContrast;
        var oQ = StyleMath.Bucket(overdrive, 32);
        var max = level == EffectsIntensity.Max;
        RedZoneGlowSigma = glowOn ? 0.025 * (0.6 + 0.4 * oQ / 32.0) * (max ? 1.3 : 1) : 0;
        RedZoneGlowOpacity = glowOn ? 0.9 * RedZoneOpacity : 0;
        RedZoneGlowKey = glowOn ? (oQ << 2) | (max ? 2 : 0) | 1 : 0;
    }

    private void UpdateWake(bool motion, bool rest, double heat)
    {
        var speed = Math.Abs(AngularVelocity);
        WakeVisible = motion && !rest && !HighContrast && speed > WakeMinSpeed;
        WakeHeadDegrees = NeedleDegrees;
        if (!WakeVisible)
        {
            WakeTailDegrees = WakeHeadDegrees;
            WakeHeadOpacity = 0;
            return;
        }

        var length = Math.Min(WakeMaxDegrees, WakeBaseDegrees + WakeSecondsPerDegree * speed);
        WakeTailDegrees = WakeHeadDegrees - Math.Sign(AngularVelocity) * length;
        WakeColor = StyleColors.Label(heat, Dark);
        WakeHeadOpacity = WakeOpacity * StyleMath.SmoothStep(WakeMinSpeed, 30, speed);
    }

    private void UpdateNeedle(double heat, double overdrive, double activity, bool soft, bool max, bool dark, bool glows)
    {
        var flare = 0.0;
        if (soft)
        {
            for (var i = 0; i < PulseSlots; i++)
            {
                var age = _pulseAge[i];
                if (age >= 0 && age < FlareDuration)
                {
                    flare = Math.Max(flare, (1 - age / FlareDuration) * _pulseStrength[i]);
                }
            }
        }

        var hQ = StyleMath.Bucket(heat, 255);
        var oQ = StyleMath.Bucket(overdrive, 32);
        var aQ = StyleMath.Bucket(activity, 16);
        var fQ = StyleMath.Bucket(flare / 1.25, 20);
        Flare = fQ / 20.0 * 1.25;
        long key = hQ;
        key = key << 6 | (uint)oQ;
        key = key << 5 | (uint)aQ;
        key = key << 5 | (uint)fQ;
        key = key << 1 | (dark ? 1L : 0);
        key = key << 1 | (glows ? 1L : 0);
        key = key << 1 | (max ? 1L : 0);
        NeedleKey = key;
        FlareSpotOpacity = Math.Min(1, 0.7 * Flare);
        if (key == _builtNeedleKey)
        {
            return;
        }

        _builtNeedleKey = key;
        var h = hQ / 255.0;
        var o = oQ / 32.0;
        var a = aQ / 16.0;
        var palette = _palette;
        var active = dark
            ? OkLab.Mix(HeatPalette.Sample(Math.Max(h, 0.04)), StyleColors.WhiteHot, 0.05 + 0.45 * o)
            : StyleColors.Label(Math.Max(h, 0.05), false);
        var fill = a >= 1 ? active : a <= 0 ? palette.NeedleIdle : OkLab.Mix(palette.NeedleIdle, active, a);
        NeedleFill = fill.WithAlpha(palette.NeedleIdleOpacity + (1 - palette.NeedleIdleOpacity) * a);

        var glowOn = dark && glows;
        NeedleGlowColor = HeatPalette.Sample(Math.Max(0.1, h));
        NeedleGlowSigma = glowOn ? (0.0275 + 0.0175 * Flare) * (max ? 1.25 : 1) : 0;
        NeedleGlowOpacity = glowOn ? Math.Min(1, 0.45 + 0.35 * h + 0.6 * Flare) : 0;
        Spine = palette.Spine.WithAlpha(dark ? Math.Min(1, palette.SpineOpacity + 0.25 * Flare) : palette.SpineOpacity);
    }

    private void UpdateSparks(double dt, EffectsIntensity level, bool thumbnail, double overdrive, bool max, bool dark)
    {
        if (level < EffectsIntensity.Standard || thumbnail)
        {
            _sparks.Clear();
            return;
        }

        var rate = Pinned ? (SparkRate + SparkRatePerOverdrive * overdrive) * (max ? 1.6 : 1) : 0;
        _sparks.Step(dt, rate, max ? NeedleSparks.MaxCapacity : NeedleSparks.StandardCapacity, _random);
        _sparks.Publish(_palette, dark ? 1 : 1.2);
    }

    private FrameClass ComputeDemand(bool motion, bool rest, SpeedTier tier, double restDial, double flash)
    {
        var flareOn = Flare > 0;
        var activitySettling = !_activity.IsSettled(rest ? 0 : 1);
        if (!motion)
        {
            // Off, Reduce Motion and thumbnails: on change only.
            var changing = Math.Abs(NeedleDial - restDial) >= 0.001 || activitySettling || flareOn || flash > 0;
            return changing ? FrameClass.Fps30 : FrameClass.None;
        }

        if (flareOn || PeakSweep is not null || flash > 0 || _sparks.Count > 0)
        {
            return FrameClass.Fps60;
        }

        if (rest)
        {
            var settling = !_spring.IsSettled(restDial) || activitySettling || SignOfLifeCenter is not null;
            return settling ? FrameClass.Fps30 : FrameClass.None;
        }

        if (tier >= SpeedTier.Overdrive || Math.Abs(AngularVelocity) > FastTurn)
        {
            return FrameClass.Fps60;
        }

        return FrameClass.Fps30;
    }
}
