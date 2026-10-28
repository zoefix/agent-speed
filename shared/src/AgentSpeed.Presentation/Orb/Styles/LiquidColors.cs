using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Colors, gold band and frame demand of the liquid style (DESIGN 2a.3).</summary>
public sealed partial class LiquidModel
{
    private static readonly Rgba Ink = Rgba.FromHex("#0B0D12");
    private static readonly Rgba LightVolumeShade = Rgba.FromHex("#1E283C").WithAlpha(0.12);
    private static readonly Rgba LightSphericalShade = Rgba.FromHex("#283042").WithAlpha(0.14);

    private Rgba _liquid;
    private double _alpha;
    private Rgba _bodyTop;
    private Rgba _bodyBottom;
    private Rgba _backWave;
    private Rgba _spill;
    private Rgba _innerGlow;
    private Rgba _current;
    private Rgba _meniscus;
    private Rgba _meniscusGlow;
    private double _meniscusWidth;
    private double _meniscusGlowSigma;
    private Rgba _caustic;
    private Rgba _moteGlow;
    private double _moteHot;
    private Rgba _splashColor;
    private Rgba _popColor;
    private Rgba _steamColor;
    private Rgba _belowTitle;

    /// <summary>
    /// Changes whenever any color, alpha or the meniscus width below changes (heat quantized to 1/128, overdrive to
    /// 1/64, activity to 1/32, tier flash to 1/16, plus appearance, effects and size); the renderer re-creates its
    /// frozen brushes only then.
    /// </summary>
    public long ColorKey => _colorKey;

    /// <summary>The liquid color <c>L</c> (opaque): <c>heat(h)</c> dark, <c>mix(heat(h), white, 0.12)</c> light.</summary>
    public Rgba LiquidColor => _liquid;

    /// <summary>The liquid body's top opacity <c>α</c>: 0.46 + 0.10 h dark / 0.80 light; idle 0.40 / 0.55.</summary>
    public double LiquidAlpha => _alpha;

    /// <summary>Liquid body (layer 5): a vertical gradient from this y to <see cref="BodyGradientBottomY"/>.</summary>
    public double BodyGradientTopY => Math.Min(MeanSurfaceY, 0.2) - 0.04;

    public double BodyGradientBottomY => R;

    /// <summary><c>L</c> at <c>α</c>.</summary>
    public Rgba BodyTopColor => _bodyTop;

    /// <summary><c>L</c> darkened 62 % (light 14 %) at <c>α + 0.2</c>.</summary>
    public Rgba BodyBottomColor => _bodyBottom;

    /// <summary>Back wave fill (layer 4): <c>L</c> darkened 25 % (light 8 %) at 55 % of <c>α</c>.</summary>
    public Rgba BackWaveColor => _backWave;

    /// <summary>
    /// Light spill (layer 3): a band <see cref="LiquidGeometry.SpillHeight"/> tall above <see cref="MeanSurfaceY"/>, from
    /// transparent at the top to this color at the surface (additive in dark).
    /// </summary>
    public Rgba SpillColor => _spill;

    /// <summary>Inner glow (additive in dark): radial at (0, this y), radius 0.32 S, clipped to the liquid.</summary>
    public double InnerGlowCenterY => Math.Max(MeanSurfaceY + 0.08, 0.38);

    /// <summary>The inner glow's center color (fading to transparent at the radius).</summary>
    public Rgba InnerGlowColor => _innerGlow;

    /// <summary>Opacity (0..1) of the readout scrim of layer 5 (dark only, not idle).</summary>
    public double ScrimOpacity => _dark && !_highContrast ? Activity : 0;

    /// <summary>Energy current <paramref name="index"/> color with its alpha (additive in dark), blurred σ 0.006 S.</summary>
    public Rgba CurrentColor(int index) => _current.MultiplyAlpha(1 - 0.25 * Math.Clamp(index, 0, 2));

    /// <summary>Meniscus stroke width (S; ≥ 1 pt).</summary>
    public double MeniscusWidth => _meniscusWidth;

    /// <summary>Meniscus stroke color (tier flash included).</summary>
    public Rgba MeniscusColor => _meniscus;

    /// <summary>Meniscus glow σ (S).</summary>
    public double MeniscusGlowSigma => _meniscusGlowSigma;

    /// <summary>Meniscus glow color (dark only; transparent when none).</summary>
    public Rgba MeniscusGlowColor => _meniscusGlow;

    /// <summary>Bottom caustic (layer 9) color with its alpha (additive in dark).</summary>
    public Rgba CausticColor => _caustic;

    /// <summary>Hotness of the rising motes: <c>smoothstep(0.35, 0.9, h)</c>.</summary>
    public double MoteHot => _moteHot;

    /// <summary>The cool bubble's ring: white 50 % (light 85 %), multiplied by (1 − Hot) × the sprite's alpha.</summary>
    public Rgba BubbleRingColor => Rgba.White.WithAlpha(_dark ? 0.5 : 0.85);

    /// <summary>The cool bubble's highlight dot: white 60 % (light 95 %), multiplied by (1 − Hot).</summary>
    public Rgba BubbleHighlightColor => Rgba.White.WithAlpha(_dark ? 0.6 : 0.95);

    /// <summary>Readout below the surface (layer 10): value white 97 % / ink 94 %.</summary>
    public Rgba ReadoutBelowValueColor => _dark ? Rgba.White.WithAlpha(0.97) : Ink.WithAlpha(0.94);

    /// <summary>Unit below the surface: white 80 % / ink 72 %.</summary>
    public Rgba ReadoutBelowUnitColor => _dark ? Rgba.White.WithAlpha(0.80) : Ink.WithAlpha(0.72);

    /// <summary>Title below the surface: white 94 % (dark) or <c>heat(h)</c> darkened 55 % without glow (light).</summary>
    public Rgba ReadoutBelowTitleColor => _belowTitle;

    /// <summary>Shadow of the readout below the surface: black 55 %, σ 0.015 S (dark); none in light.</summary>
    public Rgba ReadoutBelowShadowColor => _dark ? Rgba.Black.WithAlpha(0.55) : Rgba.Transparent;

    public const double ReadoutBelowShadowSigma = 0.015;

    /// <summary>Halo behind each agent dot (1.2 pt): black 55 % / white 75 %.</summary>
    public Rgba AgentDotHaloColor => _dark ? Rgba.Black.WithAlpha(0.55) : Rgba.White.WithAlpha(0.75);

    /// <summary>Glass wall stroke: white 9 % / black 6 %.</summary>
    public Rgba GlassWallColor => _dark ? Rgba.White.WithAlpha(0.09) : Rgba.Black.WithAlpha(0.06);

    /// <summary>Specular ellipse center color (fading to 0): white 13 % / 75 %.</summary>
    public Rgba SpecularColor => Rgba.White.WithAlpha(_dark ? 0.13 : 0.75);

    /// <summary>Rim glint stroke: white 22 % / 95 %.</summary>
    public Rgba GlintColor => Rgba.White.WithAlpha(_dark ? 0.22 : 0.95);

    /// <summary>Body volume shading edge color: black 42 % / <c>#1E283C</c> 12 %.</summary>
    public Rgba VolumeShadeColor => _dark ? Rgba.Black.WithAlpha(0.42) : LightVolumeShade;

    /// <summary>Spherical shade edge color over the liquid: black 50 % / <c>#283042</c> 14 %.</summary>
    public Rgba SphericalShadeColor => _dark ? Rgba.Black.WithAlpha(0.5) : LightSphericalShade;

    private void RefreshColors(bool force)
    {
        var act = Activity;
        var hq = EnergyPalette.Bucket(HeatNow, 128);
        var oq = EnergyPalette.Bucket(OverdriveNow, 64);
        var aq = EnergyPalette.Bucket(act, 32);
        var fq = EnergyPalette.Bucket(_flash, 16);
        var max = _effects == EffectsIntensity.Max;
        // Reduce Motion has no boil: it reads as a brighter meniscus and inner glow instead.
        var boost = _reduceMotion && _tier == SpeedTier.Redline && !_unavailable;
        var sizeKey = (long)Math.Clamp(Math.Round(_size), 0, 1023);
        var key = (long)hq
            | (long)oq << 8
            | (long)aq << 15
            | (long)fq << 21
            | (_dark ? 1L : 0) << 26
            | (max ? 1L : 0) << 27
            | (_highContrast ? 1L : 0) << 28
            | (boost ? 1L : 0) << 29
            | sizeKey << 30;
        if (!force && key == _colorKey)
        {
            return;
        }

        _colorKey = key;
        var h = hq / 128.0;
        var o = oq / 64.0;
        var a = aq / 32.0;
        var idle = 1 - a;
        var flash = fq / 16.0;
        var glows = _highContrast ? 0 : 1;
        var white = EnergyPalette.WhiteHot;

        var heat = EnergyPalette.Heat(h);
        var liquid = _dark ? heat : EnergyPalette.Mix(heat, Rgba.White, 0.12);
        _liquid = liquid;
        var activeAlpha = _dark ? 0.46 + 0.10 * h : 0.80;
        _alpha = activeAlpha + ((_dark ? 0.40 : 0.55) - activeAlpha) * idle;
        _bodyTop = liquid.WithAlpha(_alpha);
        _bodyBottom = OkLab.Darken(liquid, _dark ? 0.62 : 0.14).WithAlpha(Math.Min(1, _alpha + 0.2));
        _backWave = OkLab.Darken(liquid, _dark ? 0.25 : 0.08).WithAlpha(_alpha * 0.55);
        _spill = liquid.WithAlpha((_dark ? 0.16 : 0.10) * (0.4 + 0.6 * h) * a * glows);

        var glowScale = (max ? 1.25 : 1) * (boost ? 1.35 : 1) * a * glows;
        _innerGlow = _dark
            ? EnergyPalette.Mix(liquid, white, 0.25 + 0.45 * o).WithAlpha((0.26 + 0.2 * o) * (0.3 + 0.7 * h) * glowScale)
            : liquid.WithAlpha(0.10 * (0.3 + 0.7 * h) * glowScale);
        _current = (_dark ? EnergyPalette.Mix(liquid, white, 0.35 + 0.4 * o) : Rgba.White)
            .WithAlpha((_dark ? 0.10 : 0.22) * (0.4 + 0.6 * h) * a * glows);

        var meniscusActive = _dark
            ? EnergyPalette.Mix(liquid, white, Math.Min(1, 0.45 + 0.5 * o + (boost ? 0.15 : 0))).WithAlpha(0.95)
            : OkLab.Darken(heat, 0.25).WithAlpha(0.95);
        var meniscusIdle = _dark ? Rgba.White.WithAlpha(0.28) : OkLab.Darken(liquid, 0.25).WithAlpha(0.5);
        _meniscus = EnergyPalette.Brighten(EnergyPalette.Mix(meniscusActive, meniscusIdle, idle), 0.25 * flash);
        _meniscusWidth = Math.Max(1 / _size, (0.008 + 0.004 * h) * (boost ? 1.25 : 1));
        _meniscusGlowSigma = (0.015 + 0.015 * h) * (max ? 1.3 : 1) * (boost ? 1.25 : 1);
        _meniscusGlow = _dark && !_highContrast ? liquid.WithAlpha(0.9 * a) : Rgba.Transparent;
        _caustic = liquid.WithAlpha((_dark ? 0.35 : 0.3) * (0.3 + 0.7 * h) * a * glows);

        _moteHot = Easing.SmoothStep(0.35, 0.9, h);
        _moteGlow = EnergyPalette.Mix(liquid, white, 0.55 + 0.35 * _moteHot);
        _splashColor = _dark ? EnergyPalette.Mix(liquid, white, 0.5) : OkLab.Darken(liquid, 0.1);
        _popColor = _dark ? white : Rgba.White;
        _steamColor = _dark ? white : Rgba.White;
        _belowTitle = _dark ? Rgba.White.WithAlpha(0.94) : OkLab.Darken(EnergyPalette.Heat(Math.Max(h, 0.05)), 0.55);
    }

    private void UpdateGoldBand()
    {
        var age = _time - _peakStart;
        var yL = MeanSurfaceY;
        if (_peakReduced)
        {
            if (age is >= 0 and < ReducedPeakDuration)
            {
                GoldBandY = (yL + R) / 2;
                GoldBandAlpha = 0.45 * Math.Sin(Math.PI * age / ReducedPeakDuration);
                return;
            }
        }
        else if (age is >= 0 and < PeakBandDuration)
        {
            var u = age / PeakBandDuration;
            GoldBandY = R - (R - yL) * Easing.OutCubic(u);
            GoldBandAlpha = 0.45 * (1 - u);
            return;
        }

        GoldBandAlpha = 0;
    }

    private FrameClass ComputeDemand()
    {
        var h = HeatNow;
        var o = OverdriveNow;
        var settling = Math.Abs(_heat - _previousHeat) > 0.0005
            || Math.Abs(_overdrive - _previousOverdrive) > 0.0005
            || !_level.IsSettled(TargetLevel(h, o))
            || !_activity.IsSettled(Active ? 1 : 0, 0.002)
            || !_amplitude.IsSettled(AmplitudeTarget(h, o), 0.0002)
            || !_chop.IsSettled(ChopTarget(o), 0.0002);
        var age = _time - _peakStart;
        var peak = age >= 0 && age < (_peakReduced ? ReducedPeakDuration : PeakBandDuration);
        if (Still)
        {
            return settling || peak ? FrameClass.Fps30 : FrameClass.None;
        }

        if (_flash > 0 || _dropletCount > 0 || HasYoungRipple() || peak || !_surge.IsSettled(0))
        {
            return FrameClass.Fps60;
        }

        if (!Active)
        {
            var signOfLife = _signOfLifeAge is { } signAge && signAge < SignOfLifeDuration
                && _effects >= EffectsIntensity.Soft && !_unavailable;
            return signOfLife || _spriteCount > 0 || settling ? FrameClass.Fps30 : FrameClass.None;
        }

        if (_tier >= SpeedTier.Overdrive)
        {
            return _effects == EffectsIntensity.Soft ? FrameClass.Fps30 : FrameClass.Fps60;
        }

        return FrameClass.Fps30;
    }

    private bool HasYoungRipple()
    {
        for (var i = 0; i < _rippleCount; i++)
        {
            if (_time - _ripples[i].Start < 1.2)
            {
                return true;
            }
        }

        return false;
    }
}
