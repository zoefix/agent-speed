using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Colors of the reactor style (DESIGN 2a.4), recomputed only when <see cref="ColorKey"/> changes.</summary>
public sealed partial class ReactorModel
{
    private static readonly Rgba DarkSlate = new(0.6, 0.65, 0.72);
    private static readonly Rgba LightSlate = new(0.35, 0.40, 0.48);
    private static readonly Rgba DarkCoreTop = Rgba.FromHex("#181C28").WithAlpha(0.97);
    private static readonly Rgba DarkCoreBottom = Rgba.FromHex("#080A0F").WithAlpha(0.97);
    private static readonly Rgba LightCoreTop = Rgba.White.WithAlpha(0.99);
    private static readonly Rgba LightCoreBottom = Rgba.FromHex("#EEF1F6").WithAlpha(0.99);

    private long _colorKey = -1;
    private Rgba _heatColor;
    private Rgba _hotColor;
    private Rgba _arcAHead;
    private Rgba _arcATail;
    private Rgba _arcCHead;
    private Rgba _arcCTail;
    private double _arcAWidth;
    private Rgba _arcAGlow;
    private double _arcAGlowSigma;
    private Rgba _coilColor;
    private Rgba _coilHotColor;
    private Rgba _coronaColor;
    private Rgba _rim;
    private Rgba _rimGlow;
    private double _rimGlowSigma;
    private Rgba _boltColor;
    private Rgba _boltCoreColor;

    /// <summary>
    /// Changes whenever a color, alpha or width below changes (heat quantized to 1/128, overdrive 1/64, activity 1/32,
    /// tier flash 1/16, lock 1/32, plus appearance, effects and size). Re-record the rotor A / C visuals when this or
    /// <see cref="ArcABucket"/> / <see cref="ArcCBucket"/> changes; otherwise only rotate them.
    /// </summary>
    public long ColorKey => _colorKey;

    /// <summary><c>heat(h)</c> (dark) or the label color (light), the rotors' base color.</summary>
    public Rgba HeatColor => _heatColor;

    /// <summary><c>hot</c> of DESIGN 2a Conventions.</summary>
    public Rgba HotColor => _hotColor;

    /// <summary>
    /// Rotor A arc head color with its alpha (tier flash and lock gold included). Each arc runs from its tail (head −
    /// <see cref="ArcABucket"/>°) in <see cref="ArcATailColor"/> to the head in this color, color and alpha interpolated
    /// along the arc (the prototype draws 14 segments).
    /// </summary>
    public Rgba ArcAHeadColor => _arcAHead;

    public Rgba ArcATailColor => _arcATail;

    /// <summary>Rotor A stroke width (S): 0.024 S, idle 0.6 ×.</summary>
    public double ArcAWidth => _arcAWidth;

    /// <summary>Rotor A glow σ (S), dark only.</summary>
    public double ArcAGlowSigma => _arcAGlowSigma;

    /// <summary>Rotor A glow color (transparent when none).</summary>
    public Rgba ArcAGlowColor => _arcAGlow;

    /// <summary>Rotor C head and tail (as A; the prototype draws 10 segments), width 0.009 S, no glow.</summary>
    public Rgba ArcCHeadColor => _arcCHead;

    public Rgba ArcCTailColor => _arcCTail;

    public double ArcCWidth => ReactorGeometry.RotorCWidth;

    /// <summary>Coil base color (opaque; slate at idle, gold during the lock).</summary>
    public Rgba CoilColor => _coilColor;

    /// <summary>Coil color above energy 0.75 (opaque): <c>hot</c>.</summary>
    public Rgba CoilHotColor => _coilHotColor;

    /// <summary>Corona color (opaque); its opacity is <see cref="CoronaOpacity"/>.</summary>
    public Rgba CoronaColor => _coronaColor;

    /// <summary>Core window rim stroke color with its alpha.</summary>
    public Rgba RimColor => _rim;

    /// <summary>Core window rim width (S; ≥ 1 pt).</summary>
    public double RimWidth => Math.Max(1 / _size, ReactorGeometry.CoreRimWidth);

    public double RimGlowSigma => _rimGlowSigma;

    /// <summary>Rim glow color (dark only; transparent when none).</summary>
    public Rgba RimGlowColor => _rimGlow;

    /// <summary>Bolt glow and body color: <c>heat(0.92)</c> (light: the label color of 0.9).</summary>
    public Rgba BoltColor => _boltColor;

    /// <summary>Bolt core color: <c>#FFF7D6</c> (light <c>#FFB020</c>).</summary>
    public Rgba BoltCoreColor => _boltCoreColor;

    /// <summary>The unlit token: white 7 % (dark) / black 8 % (light).</summary>
    public Rgba UnlitColor => _dark ? Rgba.White.WithAlpha(0.07) : Rgba.Black.WithAlpha(0.08);

    /// <summary>Stator ring: unlit × 1.4.</summary>
    public Rgba StatorColor => UnlitColor.MultiplyAlpha(1.4);

    /// <summary>Notch <paramref name="index"/>: unlit × 1.4, every 6th (all four below S 120) × 2.6.</summary>
    public Rgba NotchColor(int index) => UnlitColor.MultiplyAlpha(NotchCount == 4 || index % 6 == 0 ? 2.6 : 1.4);

    /// <summary>Tracks under rotors A and C: unlit × 1.</summary>
    public Rgba TrackColor => UnlitColor;

    /// <summary>Core window radial gradient: <c>#181C28 → #080A0F</c> at 97 % (light white → <c>#EEF1F6</c> at 99 %).</summary>
    public Rgba CoreTopColor => _dark ? DarkCoreTop : LightCoreTop;

    public Rgba CoreBottomColor => _dark ? DarkCoreBottom : LightCoreBottom;

    private void RefreshColors(bool force)
    {
        var hq = EnergyPalette.Bucket(HeatNow, 128);
        var oq = EnergyPalette.Bucket(OverdriveNow, 64);
        var aq = EnergyPalette.Bucket(Activity, 32);
        var fq = EnergyPalette.Bucket(_flash, 16);
        var lq = EnergyPalette.Bucket(LockIntensity, 32);
        var max = _effects == EffectsIntensity.Max;
        var sizeKey = (long)Math.Clamp(Math.Round(_size), 0, 1023);
        var key = (long)hq
            | (long)oq << 8
            | (long)aq << 15
            | (long)fq << 21
            | (long)lq << 26
            | (_dark ? 1L : 0) << 32
            | (max ? 1L : 0) << 33
            | (_highContrast ? 1L : 0) << 34
            | sizeKey << 35;
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
        var gold = lq / 32.0;
        var glow = _dark && !_highContrast;

        var heat = _dark ? EnergyPalette.Heat(Math.Max(h, 0.02)) : EnergyPalette.Label(Math.Max(h, 0.05), false);
        var hot = EnergyPalette.Hot(h, o, _dark);
        var slate = _dark ? DarkSlate : LightSlate;
        var dim = _dark ? 0.38 : 0.45;
        var tailMin = _dark ? 0.25 : 0.5;
        _heatColor = heat;
        _hotColor = hot;

        var headActive = EnergyPalette.Brighten(gold > 0 ? EnergyPalette.Mix(hot, EnergyPalette.Gold, gold) : hot, 0.25 * flash);
        var head = EnergyPalette.Mix(headActive, slate, idle);
        var tail = EnergyPalette.Mix(heat, slate, idle);
        var alphaA = 1 + (dim * 0.55 - 1) * idle;
        var alphaC = 0.95 + (dim * 0.5 - 0.95) * idle;
        _arcAHead = head.WithAlpha(alphaA);
        _arcATail = tail.WithAlpha(alphaA * tailMin);
        _arcCHead = head.WithAlpha(alphaC);
        _arcCTail = tail.WithAlpha(alphaC * tailMin);
        _arcAWidth = ReactorGeometry.RotorAWidth * (1 - 0.4 * idle);
        _arcAGlowSigma = 0.015 * (max ? 1.3 : 1);
        _arcAGlow = glow ? heat.WithAlpha(0.7 * a) : Rgba.Transparent;

        var coil = EnergyPalette.Mix(heat, slate, idle);
        _coilColor = gold > 0 ? EnergyPalette.Mix(coil, EnergyPalette.Gold, gold) : coil;
        _coilHotColor = hot;
        _coronaColor = _dark ? EnergyPalette.Mix(heat, EnergyPalette.WhiteHot, 0.2 * o) : heat;

        var rimActive = (gold > 0 ? EnergyPalette.Mix(heat, EnergyPalette.Gold, gold) : EnergyPalette.Mix(heat, hot, 0.4 * o)).WithAlpha(0.6 + 0.4 * h);
        var rimIdle = UnlitColor.MultiplyAlpha(2.2);
        _rim = EnergyPalette.Mix(rimActive, rimIdle, idle);
        _rimGlowSigma = (0.015 + 0.015 * h) * (max ? 1.3 : 1);
        _rimGlow = glow ? heat.WithAlpha(0.9 * a) : Rgba.Transparent;

        _boltColor = _dark ? EnergyPalette.Heat(0.92) : EnergyPalette.Label(0.9, false);
        _boltCoreColor = _dark ? EnergyPalette.WhiteHot : EnergyPalette.Amber;
    }
}
