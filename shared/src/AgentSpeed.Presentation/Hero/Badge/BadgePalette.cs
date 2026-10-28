using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Hero.Badge;

/// <summary>The colors of one badge frame (DESIGN 4c "Color and glow" with the idle slate mixed in by <c>wake</c>).</summary>
/// <param name="Color"><c>C</c>: rings, motes, streaks and the title fill.</param>
/// <param name="Hot"><c>H</c>: mote heads, the shockwave, the accent.</param>
/// <param name="Glow">The title glow: <c>C</c> (dark) or the undarkened heat color (light), from slate.</param>
/// <param name="TitleTop">Dark title gradient at the top of the ink box.</param>
/// <param name="TitleBottom">Dark title gradient at the bottom of the ink box.</param>
/// <param name="IdleFill">The idle label: slate (light: stepped to 4.5:1 on the body).</param>
/// <param name="Core">Ember and spark core: <c>#FFF7D6</c> (dark) / <c>#FFB020</c> (light).</param>
/// <param name="Unlit">Unlit pips: white / black at 12 % (Increase Contrast 30 %).</param>
public readonly record struct BadgeColors(Rgba Color, Rgba Hot, Rgba Glow, Rgba TitleTop, Rgba TitleBottom, Rgba IdleFill, Rgba Core, Rgba Unlit);

/// <summary>Resolves and caches the badge colors (OKLab mixes) for quantized heat, overdrive and wake.</summary>
public sealed class BadgePalette
{
    private const double Steps = 256;

    private readonly Rgba[] _pips = new Rgba[BadgeStage.PipCount];
    private (int Heat, int Overdrive, int Wake, bool Dark, bool Contrast) _key = (-1, -1, -1, false, false);
    private BadgeColors _colors;
    private (double Redline, bool Dark) _pipKey = (double.NaN, false);

    public BadgeColors Resolve(double heat, double overdrive, double wake, bool dark, bool increasedContrast)
    {
        var key = (Quantize(heat), Quantize(overdrive), Quantize(wake), dark, increasedContrast);
        if (key == _key)
        {
            return _colors;
        }

        _key = key;
        var h = key.Item1 / Steps;
        var o = key.Item2 / Steps;
        var w = key.Item3 / Steps;
        var slate = HeatPalette.Sample(0);
        var awake = HeroPalette.Stroke(h, dark, increasedContrast);
        var idle = HeroPalette.Stroke(0, dark, increasedContrast);
        var c = OkLab.Mix(idle, awake, w);
        var hot = OkLab.Mix(idle, HeroPalette.Hot(h, o, dark, increasedContrast), w);
        var glow = OkLab.Mix(slate, dark ? awake : HeatPalette.Sample(h), w);
        var top = OkLab.Mix(OkLab.Mix(c, hot, 0.45), HeatPalette.WhiteHot, 0.35 * o);
        var bottom = OkLab.Mix(c, HeatPalette.WhiteHot, 0.2 * o);
        var idleFill = dark ? slate : HeroPalette.Stroke(0, false, increasedContrast);
        var core = dark ? HeatPalette.WhiteHot : HeroPalette.AmberHot;
        var unlit = (dark ? Rgba.White : Rgba.Black).WithAlpha(increasedContrast ? 0.3 : 0.12);
        _colors = new BadgeColors(c, hot, glow, top, bottom, idleFill, core, unlit);
        return _colors;
    }

    /// <summary>Lit pip <paramref name="k"/>: the heat color of its threshold (light: its label color, ≥ 3:1 on the body).</summary>
    public Rgba Pip(int k, double redline, bool dark)
    {
        if (_pipKey != (redline, dark))
        {
            _pipKey = (redline, dark);
            for (var i = 1; i <= _pips.Length; i++)
            {
                var color = HeatPalette.Sample(BadgeStage.PipHeat(i, redline));
                _pips[i - 1] = dark ? color : HeroPalette.Legible(OkLab.Darken(color, 0.18), false, 3);
            }
        }

        return _pips[Math.Clamp(k, 1, _pips.Length) - 1];
    }

    private static int Quantize(double value) => (int)Math.Round(HeroMath.Clamp01(value) * Steps);
}
