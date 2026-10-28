using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The fixed colors of the <c>needle</c> style for one appearance (DESIGN 2a.2). Colors carry their opacity in
/// <see cref="Rgba.A"/>; per-tick and per-lamp tables are built once.
/// </summary>
public sealed class NeedlePalette
{
    public const int SparkRampSteps = 32;

    private readonly Rgba[] _litTicks = new Rgba[NeedleGeometry.TickCount];
    private readonly Rgba[] _leadTicks = new Rgba[NeedleGeometry.TickCount];
    private readonly Rgba[] _lamps = new Rgba[NeedleGeometry.LampCount];
    private readonly Rgba[] _sparkRamp = new Rgba[SparkRampSteps + 1];

    private NeedlePalette(bool dark)
    {
        Dark = dark;
        var white = Rgba.White;
        var black = Rgba.Black;
        DialBand = dark ? white.WithAlpha(0.028) : black.WithAlpha(0.035);
        RedZone = dark ? Rgba.FromHex("#F43F5E") : Rgba.FromHex("#E11D48");
        RedZoneBaseOpacity = dark ? 0.62 : 0.8;
        UnlitMajor = dark ? white.WithAlpha(0.5) : black.WithAlpha(0.42);
        UnlitMinor = dark ? white.WithAlpha(0.24) : black.WithAlpha(0.2);
        RedUnlitMajor = RedZone.WithAlpha(0.85);
        RedUnlitMinor = RedZone.WithAlpha(0.6);
        Pin = dark ? white.WithAlpha(0.32) : black.WithAlpha(0.28);
        LampHousing = dark ? white.WithAlpha(0.08) : black.WithAlpha(0.09);
        LampFlash = dark ? OkLab.Mix(HeatPalette.Sample(0.97), StyleColors.WhiteHot, 0.55) : Rgba.FromHex("#F59E0B");
        NeedleIdle = Rgba.FromHex("#9EA8BA");
        NeedleShadow = black.WithAlpha(0.22);
        Spine = dark ? StyleColors.WhiteHot : white;
        SpineOpacity = dark ? 0.75 : 0.55;
        FlareSpot = dark ? StyleColors.WhiteHot : StyleColors.AmberHot;
        HubTop = dark ? Rgba.FromHex("#262C3C").WithAlpha(0.95) : Rgba.FromHex("#FFFFFF").WithAlpha(0.98);
        HubBottom = dark ? Rgba.FromHex("#0C0E14").WithAlpha(0.95) : Rgba.FromHex("#ECEFF5").WithAlpha(0.98);
        HubShadow = black.WithAlpha(dark ? 0.55 : 0.16);
        BezelStart = dark ? white.WithAlpha(0.42) : white;
        BezelMiddle = dark ? white.WithAlpha(0.10) : Rgba.FromHex("#A0A8B6").WithAlpha(0.55);
        BezelEnd = dark ? white.WithAlpha(0.22) : Rgba.FromHex("#78808E").WithAlpha(0.6);

        for (var k = 0; k < NeedleGeometry.TickCount; k++)
        {
            var lit = StyleColors.Label(NeedleGeometry.TickDial(k), dark);
            _litTicks[k] = lit;
            _leadTicks[k] = dark ? OkLab.Mix(lit, StyleColors.WhiteHot, 0.75) : OkLab.Mix(lit, StyleColors.AmberHot, 0.25);
        }

        var thresholds = NeedleGeometry.LampThresholds;
        for (var i = 0; i < _lamps.Length; i++)
        {
            var heat = HeatPalette.Sample(thresholds[i]);
            _lamps[i] = dark ? heat : OkLab.Darken(heat, 0.15);
        }

        var sparkStart = dark ? StyleColors.WhiteHot : StyleColors.AmberHot;
        var sparkEnd = dark ? HeatPalette.Sample(0.95) : StyleColors.Label(0.95, false);
        for (var i = 0; i <= SparkRampSteps; i++)
        {
            _sparkRamp[i] = OkLab.Mix(sparkStart, sparkEnd, i / (double)SparkRampSteps);
        }
    }

    public static NeedlePalette DarkPalette { get; } = new(true);

    public static NeedlePalette LightPalette { get; } = new(false);

    public static NeedlePalette For(bool dark) => dark ? DarkPalette : LightPalette;

    public bool Dark { get; }

    /// <summary>Glows, the trace, the wake and the sparks add light in dark; light uses normal blending.</summary>
    public bool Additive => Dark;

    public Rgba DialBand { get; }

    /// <summary>Red-zone color at full opacity; the frame's opacity is <see cref="NeedleModel.RedZoneOpacity"/>.</summary>
    public Rgba RedZone { get; }

    public double RedZoneBaseOpacity { get; }

    public Rgba UnlitMajor { get; }

    public Rgba UnlitMinor { get; }

    public Rgba RedUnlitMajor { get; }

    public Rgba RedUnlitMinor { get; }

    public Rgba Pin { get; }

    public Rgba LampHousing { get; }

    /// <summary>The redline flash color of all five lamps.</summary>
    public Rgba LampFlash { get; }

    public Rgba NeedleIdle { get; }

    public double NeedleIdleOpacity => 0.75;

    /// <summary>Light only: the needle's drop shadow (offset <see cref="NeedleShadowOffsetX"/>, <see cref="NeedleShadowOffsetY"/>, σ <see cref="NeedleShadowSigma"/>).</summary>
    public Rgba NeedleShadow { get; }

    public double NeedleShadowOffsetX => 0.006;

    public double NeedleShadowOffsetY => 0.01;

    public double NeedleShadowSigma => 0.007;

    public Rgba Spine { get; }

    public double SpineOpacity { get; }

    public Rgba FlareSpot { get; }

    public Rgba HubTop { get; }

    public Rgba HubBottom { get; }

    public Rgba HubShadow { get; }

    /// <summary>The chrome bezel's 135° gradient: start (top-left), middle, end (bottom-right).</summary>
    public Rgba BezelStart { get; }

    public Rgba BezelMiddle { get; }

    public Rgba BezelEnd { get; }

    /// <summary>Lit tick k: <c>heat(n_k)</c> (light: its label color), opaque.</summary>
    public Rgba LitTick(int k) => _litTicks[k];

    /// <summary>The last lit tick: 75 % toward #FFF7D6 (light: 25 % toward #FFB020).</summary>
    public Rgba LeadTick(int k) => _leadTicks[k];

    /// <summary>Lamp i lit outside the redline flash: <c>heat(threshold)</c> (light: darkened 15 %).</summary>
    public Rgba Lamp(int i) => _lamps[i];

    /// <summary>A peg spark's color at life fraction <paramref name="u"/>.</summary>
    public Rgba Spark(double u)
    {
        var position = StyleMath.Clamp01(u) * SparkRampSteps;
        var index = (int)position;
        return index >= SparkRampSteps ? _sparkRamp[SparkRampSteps] : Rgba.Lerp(_sparkRamp[index], _sparkRamp[index + 1], position - index);
    }
}
