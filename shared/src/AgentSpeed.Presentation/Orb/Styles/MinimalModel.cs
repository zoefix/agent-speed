using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Animation state of the <c>minimal</c> style, one thin progress ring (DESIGN 2a.5). Progress <c>p = h + o</c> runs from
/// 12 o'clock clockwise; beyond the redline a second lap runs over the first (complete at <c>v = 2R</c>). Angles are
/// degrees clockwise from 3 o'clock (12 o'clock = 270°), lengths and σ are fractions of S, opacities 0..1.
/// <see cref="GeometryKey"/> and <see cref="ColorKey"/> tell a retained renderer when to re-record the ring; glow and
/// head-dot opacities are per-frame numbers. Allocates nothing after construction.
/// </summary>
public sealed class MinimalModel : IOrbStyleModel
{
    public const double TrackRadius = 0.425;
    public const double RingWidthFraction = 0.034;
    public const double MinRingWidth = 2.5;
    public const double TrackUnlitScale = 1.15;
    public const double HeadDotScale = 0.32;
    public const double ShadowSigmaScale = 0.35;
    public const double ShadowBehindRadians = 0.02;
    public const double ShadowAheadRadians = 0.07;
    public const double GlowSigma = 0.0125;
    public const double BreathGlowSigma = 0.025;
    public const double GlowOpacity = 0.35;
    public const double BreathGlowOpacity = 0.7;
    public const double BlinkGlowSigma = 0.01;
    public const double BlinkBoost = 0.3;
    public const double MaxGlowScale = 1.3;
    public const double BreathHz = 1;
    public const double BlinkDuration = 0.4;
    public const double GoldDuration = 0.7;
    public const double CrossfadeDuration = 0.2;
    public const double HeadDotBreath = 0.6;
    public const double FlashLift = 0.25;

    private const double Fresh = -1;
    private const double EndTolerance = 1e-9;

    private static readonly Rgba DarkGold = Rgba.FromHex("#FACC15");
    private static readonly Rgba LightGold = Rgba.FromHex("#C99700");

    private CriticalSpring _activity = new(0.5);
    private double _blinkAge = double.PositiveInfinity;
    private double _goldAge = double.PositiveInfinity;
    private long _builtColorKey = -1;

    public OrbStyle Style => OrbStyle.Minimal;

    public FrameClass Demand { get; private set; }

    public bool Dark { get; private set; } = true;

    /// <summary>Windows high contrast: track in the theme's gray text color, ring in its highlight color, no glow.</summary>
    public bool HighContrast { get; private set; }

    /// <summary>Ring stroke width <c>w = max(2.5 pt, 0.034 S)</c> in S.</summary>
    public double RingWidth { get; private set; } = RingWidthFraction;

    /// <summary>The full-circle track at <see cref="TrackRadius"/> (unlit token × 1.15).</summary>
    public Rgba TrackColor { get; private set; } = Rgba.White.WithAlpha(0.07 * TrackUnlitScale);

    /// <summary>Progress 0..2 (<c>(h + o)</c>, eased to 0 when entering idle).</summary>
    public double Progress { get; private set; }

    /// <summary>The ring is drawn (round caps); false at idle.</summary>
    public bool RingVisible { get; private set; }

    public double StartDegrees => StyleMath.TopDegrees;

    /// <summary>End of the first lap: 270° + 360° min(p, 1).</summary>
    public double Lap1EndDegrees { get; private set; } = StyleMath.TopDegrees;

    public bool HasSecondLap { get; private set; }

    /// <summary>End of the second lap (p &gt; 1): 270° + 360° (p − 1).</summary>
    public double Lap2EndDegrees { get; private set; } = StyleMath.TopDegrees;

    /// <summary>The head of the ring (the second lap's head beyond the redline).</summary>
    public double HeadDegrees { get; private set; } = StyleMath.TopDegrees;

    /// <summary>
    /// First lap: a conic gradient from <see cref="StartColor"/> at 12 o'clock to <see cref="HeadColor"/> at its end
    /// (sRGB interpolation, as a canvas conic gradient). The second lap is <see cref="HeadColor"/> throughout.
    /// </summary>
    public Rgba StartColor { get; private set; }

    public Rgba HeadColor { get; private set; }

    /// <summary>New-peak gold tint 0..1 (already mixed into the ring colors).</summary>
    public double GoldTint { get; private set; }

    /// <summary>The second lap's head shadow onto the lap below: an arc from <see cref="ShadowStartDegrees"/> to <see cref="ShadowEndDegrees"/>.</summary>
    public bool ShadowVisible { get; private set; }

    public double ShadowStartDegrees { get; private set; }

    public double ShadowEndDegrees { get; private set; }

    public Rgba ShadowColor { get; private set; }

    /// <summary>σ of the shadow blur in S (0.35 w).</summary>
    public double ShadowSigma { get; private set; }

    /// <summary>Dark, boost and up: glow of the ring in <see cref="HeadColor"/> (σ in S, 0 when off).</summary>
    public double RingGlowSigma { get; private set; }

    public double RingGlowOpacity { get; private set; }

    public bool HeadDotVisible { get; private set; }

    public double HeadDotRadius { get; private set; }

    public Rgba HeadDotColor { get; private set; }

    public double HeadDotOpacity { get; private set; }

    /// <summary>Redline breathing 0..1 (1 when steady).</summary>
    public double Breath { get; private set; }

    /// <summary>Changes when the ring's arcs move by 0.1° (or the width changes).</summary>
    public long GeometryKey { get; private set; }

    /// <summary>Changes when the ring, head-dot or track colors change.</summary>
    public long ColorKey { get; private set; }

    /// <summary>Ring color at a fraction of the first lap (0 at 12 o'clock, 1 at its end).</summary>
    public Rgba Lap1ColorAt(double fraction) => Rgba.Lerp(StartColor, HeadColor, fraction);

    public void Update(in OrbFrame frame) => Advance(frame, snap: false);

    public void Snap(in OrbFrame frame) => Advance(frame, snap: true);

    /// <summary>Starts the blink; <see cref="Demand"/> rises at once so a static orb wakes up.</summary>
    public void Pulse(double heat, double strength)
    {
        _blinkAge = Fresh;
        Demand = FramePolicy.Max(Demand, FrameClass.Fps30);
    }

    public void Peak()
    {
        _goldAge = Fresh;
        Demand = FramePolicy.Max(Demand, FrameClass.Fps30);
    }

    private void Advance(in OrbFrame frame, bool snap)
    {
        var dt = Math.Clamp(snap ? 0 : StyleMath.Finite(frame.Dt), 0, 0.1);
        var effects = frame.Effects;
        var level = effects.Level;
        var reduceMotion = effects.ReduceMotion;
        HighContrast = effects.HighContrast;
        Dark = frame.Dark;
        var rest = frame.Idle || frame.Unavailable;
        var redline = frame.Tier == SpeedTier.Redline && !rest;
        var soft = level >= EffectsIntensity.Soft;
        var still = !soft || reduceMotion || frame.Thumbnail;
        var heat = StyleMath.Clamp01(frame.Heat);
        var overdrive = StyleMath.Clamp01(frame.Overdrive);

        if (snap)
        {
            _blinkAge = double.PositiveInfinity;
            _goldAge = double.PositiveInfinity;
            _activity.Snap(rest ? 0 : 1);
        }
        else
        {
            _blinkAge = _blinkAge == Fresh ? 0 : _blinkAge + dt;
            _goldAge = _goldAge == Fresh ? 0 : _goldAge + dt;
            _activity.Step(rest ? 0 : 1, dt);
        }

        var activity = StyleMath.Clamp01(_activity.Value);
        var progress = Math.Clamp(heat + overdrive, 0, 2) * activity;
        Progress = rest && activity < 0.0005 ? 0 : progress;
        var size = frame.Size > 1 && double.IsFinite(frame.Size) ? frame.Size : 148;
        RingWidth = Math.Max(MinRingWidth / size, RingWidthFraction);
        UpdateGeometry();

        var gold = Envelope(_goldAge, GoldDuration, reduceMotion);
        var blink = soft ? BlinkEnvelope(_blinkAge, reduceMotion) : 0;
        var flash = soft ? StyleMath.Clamp01(StyleMath.Finite(frame.Flash)) : 0;
        Breath = redline ? still ? 1 : 0.5 + 0.5 * StyleMath.ClockSine(frame.Time, BreathHz) : 0;
        UpdateColors(heat, gold, flash);

        var glowOn = Dark && soft && !HighContrast && !rest && frame.Tier >= SpeedTier.Boost && RingVisible;
        var glowScale = level == EffectsIntensity.Max ? MaxGlowScale : 1;
        RingGlowSigma = glowOn ? (GlowSigma + (BreathGlowSigma - GlowSigma) * Breath + BlinkGlowSigma * blink) * glowScale : 0;
        RingGlowOpacity = glowOn
            ? Math.Min(1, (GlowOpacity + (BreathGlowOpacity - GlowOpacity) * Breath + BlinkBoost * blink) * (1 + FlashLift * flash))
            : 0;

        HeadDotRadius = HeadDotScale * RingWidth;
        HeadDotOpacity = RingVisible ? Math.Min(1, Math.Max(blink, HeadDotBreath * Breath)) : 0;
        HeadDotVisible = HeadDotOpacity > 0.001;

        var settling = Math.Abs(Progress - RestProgress(frame, rest)) >= 0.001 || !_activity.IsSettled(rest ? 0 : 1);
        var transient = _goldAge < GoldDuration - EndTolerance || (soft && _blinkAge < BlinkDuration - EndTolerance) || flash > 0;
        Demand = settling || transient
            ? FrameClass.Fps30
            : redline && !still && RingVisible ? FrameClass.Fps15 : FrameClass.None;
    }

    private static double RestProgress(in OrbFrame frame, bool rest) =>
        rest ? 0 : Math.Clamp(StyleMath.Clamp01(frame.HeatRaw) + StyleMath.Clamp01(frame.OverdriveRaw), 0, 2);

    private void UpdateGeometry()
    {
        var p = Progress;
        RingVisible = p > 0.002;
        Lap1EndDegrees = StyleMath.TopDegrees + 360 * Math.Min(p, 1);
        HasSecondLap = p > 1;
        Lap2EndDegrees = StyleMath.TopDegrees + 360 * Math.Max(0, p - 1);
        HeadDegrees = HasSecondLap ? Lap2EndDegrees : Lap1EndDegrees;
        ShadowVisible = HasSecondLap && !HighContrast;
        ShadowStartDegrees = HeadDegrees - ShadowBehindRadians * StyleMath.DegreesPerRadian;
        ShadowEndDegrees = HeadDegrees + ShadowAheadRadians * StyleMath.DegreesPerRadian;
        ShadowSigma = ShadowSigmaScale * RingWidth;
        ShadowColor = Rgba.Black.WithAlpha(Dark ? 0.55 : 0.22);
        long key = RingVisible ? (long)Math.Round(p * 3600) + 1 : 0;
        key = key << 16 | (uint)Math.Min(65535, Math.Round(RingWidth * 100000));
        GeometryKey = key;
    }

    private void UpdateColors(double heat, double gold, double flash)
    {
        var hQ = StyleMath.Bucket(heat, 255);
        var gQ = StyleMath.Bucket(gold, 32);
        var fQ = StyleMath.Bucket(flash, 8);
        long key = hQ;
        key = key << 6 | (uint)gQ;
        key = key << 4 | (uint)fQ;
        key = key << 1 | (Dark ? 1L : 0);
        ColorKey = key;
        GoldTint = gQ / 32.0;
        if (key == _builtColorKey)
        {
            return;
        }

        _builtColorKey = key;
        var head = StyleColors.Label(Math.Max(hQ / 255.0, 0.04), Dark);
        var start = OkLab.Darken(head, Dark ? 0.28 : 0.10);
        if (gQ > 0)
        {
            var goldColor = Dark ? DarkGold : LightGold;
            head = OkLab.Mix(head, goldColor, GoldTint);
            start = OkLab.Mix(start, goldColor, GoldTint);
        }

        if (fQ > 0)
        {
            head = StyleColors.Lift(head, FlashLift * fQ / 8.0);
            start = StyleColors.Lift(start, FlashLift * fQ / 8.0);
        }

        HeadColor = head;
        StartColor = start;
        HeadDotColor = Dark ? OkLab.Mix(head, StyleColors.WhiteHot, 0.6) : head;
        TrackColor = Dark ? Rgba.White.WithAlpha(0.07 * TrackUnlitScale) : Rgba.Black.WithAlpha(0.08 * TrackUnlitScale);
    }

    /// <summary>The gold tint: <c>sin(π τ / 0.7)</c>; Reduce Motion: a 0.2 s crossfade in and out.</summary>
    private static double Envelope(double age, double duration, bool crossfade)
    {
        if (!(age >= 0) || age >= duration - EndTolerance)
        {
            return 0;
        }

        return crossfade
            ? Math.Min(1, Math.Min(age, duration - age) / CrossfadeDuration)
            : Math.Sin(Math.PI * age / duration);
    }

    /// <summary>The pulse blink: 1 → 0 over 0.4 s; Reduce Motion: a 0.2 s crossfade up and back.</summary>
    private static double BlinkEnvelope(double age, bool crossfade)
    {
        if (!(age >= 0) || age >= BlinkDuration - EndTolerance)
        {
            return 0;
        }

        return crossfade
            ? Math.Min(1, Math.Min(age, BlinkDuration - age) / CrossfadeDuration)
            : 1 - age / BlinkDuration;
    }
}
