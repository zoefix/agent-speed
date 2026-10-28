using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>The strings the readout shows besides the value (localized; rebuilt on a language change).</summary>
/// <param name="Titles">The speed titles by <see cref="SpeedTitle"/>.</param>
/// <param name="Latin">Latin script: the unit and the titles are tracked.</param>
public sealed record OrbReadoutStrings(string Unit, string Peak, IReadOnlyList<string> Titles, bool Latin)
{
    public string Title(SpeedTitle title) => (int)title < Titles.Count ? Titles[(int)title] : string.Empty;
}

/// <summary>
/// The shared readout on the GPU (DESIGN 2 layers 6 and 8, 2a Shared foundation): the value with the odometer, heat glow,
/// forward lean and redline jitter; the unit; the speed title with its staggered slide (or the hover info line); the agents
/// overflow and the new-peak pill, placed by the style's frame table. One per orb; it keeps the glow and activity envelopes
/// and draws without allocating.
/// </summary>
public sealed class OrbReadoutDraw
{
    public const double TitleSlide = 0.35;
    public const double TitleFade = 0.25;

    /// <summary>The prototype's value cell (its font's em box): 1.193 × the size tall, centered 0.355 × the size above the baseline.</summary>
    public const double CellHeight = 1.193;

    public const double CellCenter = 0.355;

    private const string Dash = "–";
    private const string AtLeastSign = "≥";
    private const double PrefixScale = 0.6;

    /// <summary>
    /// The text shader brightens a glow 1.5 × (<c>saturate(1.5 g)</c>); the <c>led</c>'s value layers follow the prototype's
    /// CSS text shadows (<c>g × alpha</c>), so their alphas are divided back.
    /// </summary>
    private const double CssGlow = 1 / 1.5;

    /// <summary>Share of a bold digit's box its ink covers, for the value's widest glow.</summary>
    private const double InkDensity = 0.4;

    private static readonly string[] Ascii = BuildAscii();
    private static readonly string[] OverflowLabels = BuildOverflow();
    private static readonly Rgba PillInk = new(0.10, 0.07, 0.02);
    private static readonly Rgba PillGold = OkLab.Mix(OkLab.Mix(HeatPalette.Gold, HeatPalette.WhiteHot, 0.2), HeatPalette.Sample(0.92), 0.25);
    private static readonly Rgba SplitLeft = new(1, 60 / 255.0, 140 / 255.0, 0.55);
    private static readonly Rgba SplitRight = new(60 / 255.0, 210 / 255.0, 1, 0.55);

    private IOrbGlyphs _glyphs;
    private OrbGpuContext? _context;
    private OrbReadoutStrings _strings = new("tok/s", "NEW PEAK", [], true);
    private CriticalSpring _glow = new(0.3);
    private CriticalSpring _activity = new(0.4);
    private readonly TitleRenameTracker _rename = new();
    private string? _gapsText;
    private int _gaps;
    private readonly ValueLayer[] _layers = new ValueLayer[4];
    private int _layerCount;
    private readonly LedDigitCell[] _cells = new LedDigitCell[LedLightning.MaxCells];
    private int _cellCount;

    public OrbReadoutDraw(IOrbGlyphs glyphs)
    {
        _glyphs = glyphs ?? throw new ArgumentNullException(nameof(glyphs));
    }

    /// <summary>The glyph source (the atlas at the orb's display scale).</summary>
    public IOrbGlyphs Glyphs
    {
        get => _glyphs;
        set => _glyphs = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The hover info line (<c>today.summary</c> over the orb's sources).</summary>
    public string InfoLine { get; set; } = string.Empty;

    /// <summary>Width (points) of the unit text drawn last, for the unit's hit area (<see cref="OrbUnitToggle.HitRect"/>).</summary>
    public double UnitWidth { get; private set; }

    /// <summary>Width (points) of the value drawn last (the hit area of <c>minimal</c>, which has no unit line).</summary>
    public double ValueWidth { get; private set; }

    /// <summary>Set when every source of the orb is unavailable: the value reads an en dash and this title.</summary>
    public string? UnavailableTitle { get; set; }

    /// <summary>
    /// How far the value, unit, title and pill sit below their resting place (points) while the traffic block shows above
    /// them (DESIGN 10, Centering); the agent dots never move.
    /// </summary>
    public double Shift { get; set; }

    /// <summary><c>needle</c> with the traffic readout on: the unit is printed on the dial face, not in the hub (DESIGN 10).</summary>
    public bool UnitOnDial { get; set; }

    /// <summary>The character cells of the value drawn last (S from the disc center, y down), one per character of <see cref="ValueCellsText"/>.</summary>
    public ReadOnlySpan<LedDigitCell> ValueCells => _cells.AsSpan(0, _cellCount);

    /// <summary>The value text of <see cref="ValueCells"/>.</summary>
    public string ValueCellsText { get; private set; } = string.Empty;

    public void Configure(OrbGpuContext context, OrbReadoutStrings strings)
    {
        _context = context;
        _strings = strings;
    }

    /// <summary>Advances the value-glow and activity envelopes (once per rendered frame).</summary>
    public void Step(OrbScene scene, double dt, bool snap)
    {
        var glowOn = scene.Effects.ValueGlow && scene.Tier >= SpeedTier.Boost && UnavailableTitle is null ? 1 : 0;
        var active = scene.Tier != SpeedTier.Idle || scene.Value > 0 ? 1 : 0;
        if (snap || dt <= 0)
        {
            _glow.Snap(glowOn);
            _activity.Snap(active);
            return;
        }

        _glow.Step(glowOn, dt);
        _activity.Step(active, dt);
    }

    /// <summary>True while an envelope still moves.</summary>
    public bool IsMoving(OrbScene scene) =>
        _rename.IsRunning(scene.Time) ||
        !_glow.IsSettled(scene.Effects.ValueGlow && scene.Tier >= SpeedTier.Boost && UnavailableTitle is null ? 1 : 0, 0.005)
        || !_activity.IsSettled(scene.Tier != SpeedTier.Idle || scene.Value > 0 ? 1 : 0, 0.005);

    /// <summary>Adds the readout clipped by <paramref name="clip"/> in <paramref name="colors"/>; the pill only when <paramref name="pill"/>.</summary>
    public void Add(OrbDrawList list, OrbScene scene, OrbReadoutLayout layout, in OrbReadoutColors colors, OrbTextClip clip, bool pill)
    {
        if (_context is not { } ctx)
        {
            return;
        }

        var s = ctx.Size;
        _cellCount = 0;
        var valueLine = ctx.CenterY + Shift + layout.ValueBaseline * s;
        var unitLine = layout.UnitBaseline is { } unitBaseline ? ctx.CenterY + Shift + unitBaseline * s : valueLine;
        var unitText = scene.UnitText ?? _strings.Unit;
        var unavailable = UnavailableTitle is not null;
        var current = unavailable ? Dash : scene.Odometer.Current;
        if (scene.UnitSwitch is { } change && !unavailable)
        {
            // DESIGN 9: the old value and unit leave upward while the new ones arrive from below, inside their own band.
            var reduce = !scene.Effects.SlideTransitions || scene.Effects.Level == Settings.EffectsIntensity.Off;
            var p = change.Progress;
            var travel = reduce ? 0 : layout.ValueSize * s * 0.9;
            var (outAlpha, outTravel) = reduce ? (1 - p, 0.0) : TitleOut(p);
            var (inAlpha, inTravel) = reduce ? (p, 1.0) : TitleIn(p);
            var top = valueLine - layout.ValueSize * s * 1.02;
            var bottom = layout.UnitSize is { } size ? unitLine + size * s * 0.45 : valueLine + layout.ValueSize * s * 0.25;
            var band = clip.Intersect(ctx.CenterX - s / 2, top, ctx.CenterX + s / 2, bottom);
            AddValue(list, ctx, scene, layout, colors, band, valueLine, change.FromValue, change.FromAtLeast, false, false, -travel * outTravel,
                outAlpha);
            AddValue(list, ctx, scene, layout, colors, band, valueLine, current, scene.ValueAtLeast, scene.ValueMuted, false,
                travel * (1 - inTravel), inAlpha);
            if (s >= 64 && layout.UnitSize is { } unitSize && !UnitOnDial)
            {
                AddUnit(list, ctx, scene, unitSize, unitLine, colors, band, change.FromUnit ?? _strings.Unit, -travel * outTravel, outAlpha, false);
                AddUnit(list, ctx, scene, unitSize, unitLine, colors, band, unitText, travel * (1 - inTravel), inAlpha, true);
            }
        }
        else
        {
            AddValue(list, ctx, scene, layout, colors, clip, valueLine, current, !unavailable && scene.ValueAtLeast,
                !unavailable && scene.ValueMuted, true, 0, 1);
            if (s >= 64 && layout.UnitSize is { } unitSize && !UnitOnDial)
            {
                AddUnit(list, ctx, scene, unitSize, unitLine, colors, clip, unitText, 0, 1, true);
            }
        }

        if (s < 64)
        {
            return;
        }

        AddLabels(list, ctx, scene, layout, ctx.CenterY + Shift + layout.TitleBaseline * s, colors, clip);
        if (layout.DotsY is { } dots)
        {
            AddOverflow(list, ctx, scene, dots, colors, clip);
        }

        if (pill)
        {
            AddPill(list, ctx, scene, ctx.CenterY + Shift + layout.PillY * s);
        }
    }

    private Rgba Secondary(OrbGpuContext ctx) => ctx.HighContrast ? ctx.Contrast.WindowText : ctx.Tokens.SecondaryText;

    // ---- Value ----

    /// <param name="text">The value text (the odometer's, or the one leaving during a unit switch).</param>
    /// <param name="atLeast">A small <c>≥</c> (60 %, secondary color) precedes the value and is part of its width (DESIGN 9).</param>
    /// <param name="muted">Nothing is priced: the value reads in the idle value color, without glow.</param>
    /// <param name="allowRoll">The odometer may roll (not while the unit switches).</param>
    /// <param name="slide">Vertical offset of the whole value (points).</param>
    /// <param name="fade">Alpha of the whole value.</param>
    private void AddValue(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, OrbReadoutLayout layout, in OrbReadoutColors colors,
        OrbTextClip clip, double line, string text, bool atLeast, bool muted, bool allowRoll, double slide, double fade)
    {
        if (fade <= 0.01)
        {
            return;
        }

        var s = ctx.Size;
        var effects = scene.Effects;
        var unavailable = UnavailableTitle is not null;
        var odometer = scene.Odometer;
        var fullSize = layout.ValueSize * s;

        Rgba? glow = null;
        var sigma = 0.0;
        if (colors.Shadow is { } shadow)
        {
            glow = shadow;
            sigma = colors.ShadowSigma;
        }
        else if (layout.ValueGlow && colors.ValueGlow && !unavailable && !muted && !ctx.HighContrast)
        {
            var strength = _glow.Value * (ctx.Dark ? 0.85 : 0.45) * (layout.ValueEffects ? CssGlow : 1);
            if (strength > 0.01)
            {
                var h = Math.Round(Math.Max(scene.Heat, 0.2) * 31) / 31;
                glow = (ctx.Dark ? HeatPalette.Sample(h) : ctx.Label(h)).WithAlpha(strength);
                sigma = 0.0167 * s * effects.Bloom;
                if (layout.ValueEffects)
                {
                    sigma *= 1 + 0.8 * Math.Round(scene.Overdrive * 4) / 4;
                }
            }
        }

        var natural = Width(text, fullSize, sigma) + (atLeast ? PrefixAdvance(fullSize * PrefixScale) : 0);
        var limit = layout.ValueMaxWidth * s;
        var fit = natural > limit && natural > 0 ? Math.Max(0.4, Math.Floor(limit / natural * 50) / 50) : 1;
        var size = fullSize * fit;
        sigma *= fit;
        _layerCount = 0;
        if (layout.ValueEffects && colors.Shadow is null && !unavailable && !muted && !ctx.HighContrast)
        {
            AddValueLayers(ctx, scene, sigma, fade);
        }

        var idle = ctx.HighContrast ? ctx.Contrast.WindowText : ctx.Tokens.IdleValueText;
        var value = ctx.HighContrast ? ctx.Contrast.WindowText : ctx.Tokens.ValueText;
        var color = (colors.Value ?? (unavailable || muted ? idle : Rgba.Lerp(idle, value, _activity.Value))).MultiplyAlpha(fade);
        glow = glow?.MultiplyAlpha(fade);
        var shear = layout.ValueGlow && !unavailable ? Math.Tan(-scene.SkewDegrees * Math.PI / 180) : 0;
        var unit = ctx.Unit;
        var jitterX = unavailable ? 0 : scene.JitterX * unit;
        var jitterY = unavailable ? 0 : scene.JitterY * unit;
        var baseline = line + jitterY + slide;
        var rolls = allowRoll && layout.Rolls && effects.RollingDigits && s >= 80 && !unavailable && odometer.Previous is not null
            && odometer.IsAnimating(scene.Time);

        var cell = _glyphs.DigitCell(OrbFont.Value, size);
        var prefix = atLeast ? PrefixAdvance(size * PrefixScale) : 0;
        var total = RunWidth(text, size, cell, sigma) + prefix;
        var x = ctx.CenterX - total / 2 + jitterX;
        ValueWidth = total;
        if (allowRoll && !unavailable)
        {
            RecordCells(ctx, text, x - jitterX + prefix, line + slide, size, cell, sigma);
        }

        if (layout.ValueEffects && colors.Shadow is null && glow is { } heatGlow && sigma > 0)
        {
            AddWideGlow(list, scene, heatGlow, sigma, x + prefix, total - prefix, baseline, size, fade, clip);
        }

        if (atLeast && _glyphs.TryGet(OrbTextKey.Create(AtLeastSign, OrbFont.Value, size * PrefixScale), out var sign))
        {
            list.AddText(sign, x, baseline, (colors.Unit ?? Secondary(ctx)).MultiplyAlpha(fade), colors.Shadow?.MultiplyAlpha(fade), clip, shear,
                baseline);
        }

        x += prefix;
        if (!rolls)
        {
            Run(list, text, x, baseline, size, cell, sigma, color, glow, 1, shear, clip);
            return;
        }

        var previous = odometer.Previous!;
        var progress = odometer.Progress(scene.Time);
        if (previous.Length != text.Length)
        {
            var mix = odometer.CrossfadeAmount(scene.Time);
            var previousFit = Math.Min(1, limit / Math.Max(1, Width(previous, fullSize, sigma)));
            var previousSize = fullSize * Math.Max(0.4, Math.Floor(previousFit * 50) / 50);
            var previousCell = _glyphs.DigitCell(OrbFont.Value, previousSize);
            var previousX = ctx.CenterX - (RunWidth(previous, previousSize, previousCell, sigma) + prefix) / 2 + jitterX + prefix;
            Run(list, previous, previousX, baseline, previousSize, previousCell, sigma, color, glow, 1 - mix, shear, clip);
            Run(list, text, x, baseline, size, cell, sigma, color, glow, mix, shear, clip);
            return;
        }

        // Odometer: only the characters that changed roll, each clipped to its own cell (DESIGN 2a).
        var eased = odometer.RollEase(scene.Time);
        var (outgoing, incoming) = odometer.Offsets(eased);
        var distance = OdometerText.Travel * size;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var width = CellWidth(ch, size, cell, sigma);
            if (previous[i] == ch || progress >= 1)
            {
                Char(list, ch, x, width, baseline, size, sigma, color, glow, 1, shear, baseline, clip);
            }
            else
            {
                var cellClip = clip.Intersect(x - 2, baseline - size * 0.92, x + width + 2, baseline + size * 0.2);
                var oldWidth = CellWidth(previous[i], size, cell, sigma);
                Char(list, previous[i], x + (width - oldWidth) / 2, oldWidth, baseline + outgoing * distance, size, sigma, color, glow,
                    1 - eased, shear, baseline, cellClip);
                Char(list, ch, x, width, baseline + incoming * distance, size, sigma, color, glow, eased, shear, baseline, cellClip);
            }

            x += width;
        }
    }

    private void Run(OrbDrawList list, string text, double x, double baseline, double size, double cell, double sigma, Rgba color,
        Rgba? glow, double alpha, double shear, OrbTextClip clip)
    {
        if (alpha <= 0.01)
        {
            return;
        }

        foreach (var ch in text)
        {
            var width = CellWidth(ch, size, cell, sigma);
            Char(list, ch, x, width, baseline, size, sigma, color, glow, alpha, shear, baseline, clip);
            x += width;
        }
    }

    private void Char(OrbDrawList list, char ch, double x, double cellWidth, double baseline, double size, double sigma, Rgba color,
        Rgba? glow, double alpha, double shear, double shearPivot, OrbTextClip clip)
    {
        if (alpha <= 0.01)
        {
            return;
        }

        var text = CharText(ch);
        for (var k = 0; k < _layerCount; k++)
        {
            ref readonly var layer = ref _layers[k];
            if (_glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Value, size, 0, layer.Sigma), out var under))
            {
                list.AddText(under, x + (cellWidth - under.Advance) / 2 + layer.Dx, baseline + layer.Dy, layer.Color.MultiplyAlpha(alpha),
                    layer.Glow?.MultiplyAlpha(alpha), clip, shear, shearPivot);
            }
        }

        if (!_glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Value, size, 0, glow is null ? 0 : sigma), out var glyph))
        {
            return;
        }

        list.AddText(glyph, x + (cellWidth - glyph.Advance) / 2, baseline, color.MultiplyAlpha(alpha), glow?.MultiplyAlpha(alpha), clip, shear,
            shearPivot);
    }

    /// <summary>
    /// The layers under the value's glyphs (DESIGN 2.1, Readout), bottom to top as the prototype's text shadows: in dark a drop
    /// shadow (0.006 S down, σ 0.006 S, black 60 %) and a backing (σ 0.0125 S, black 70 %) from 0.5 tok/s; at the redline in
    /// dark the chromatic split.
    /// </summary>
    private void AddValueLayers(OrbGpuContext ctx, OrbScene scene, double sigma, double fade)
    {
        var s = ctx.Size;
        if (ctx.Dark && scene.Value >= 0.5)
        {
            _layers[_layerCount++] = new ValueLayer(0, 0.006 * s, 0.006 * s, Rgba.Transparent, Rgba.Black.WithAlpha(0.6 * CssGlow * fade));
            _layers[_layerCount++] = new ValueLayer(0, 0, 0.0125 * s, Rgba.Transparent, Rgba.Black.WithAlpha(0.7 * CssGlow * fade));
        }

        if (ctx.Dark && scene.Tier == SpeedTier.Redline && scene.Effects.Level >= Settings.EffectsIntensity.Standard)
        {
            var offset = (0.6 + 1.6 * Math.Round(scene.Overdrive * 16) / 16) * s / 300;
            _layers[_layerCount++] = new ValueLayer(offset, 0, sigma, SplitRight.MultiplyAlpha(fade), null);
            _layers[_layerCount++] = new ValueLayer(-offset, 0, sigma, SplitLeft.MultiplyAlpha(fade), null);
        }
    }

    /// <summary>
    /// From overdrive the value's second glow, 2.4 × wider at 45 % (light 20 %). At that width the blurred glyphs are a blurred
    /// box of their ink density, so it is one soft rounded rectangle under the value instead of a wide atlas entry per digit.
    /// </summary>
    private void AddWideGlow(OrbDrawList list, OrbScene scene, Rgba glow, double sigma, double x, double width, double baseline,
        double size, double fade, OrbTextClip clip)
    {
        var alpha = _glow.Value * (_context!.Dark ? 0.45 : 0.2) * InkDensity * fade;
        if (scene.Tier < SpeedTier.Overdrive || alpha <= 0.004 || clip.Mode != OrbTextClipMode.None)
        {
            return;
        }

        var wide = 2.4 * sigma;
        var halfWidth = Math.Max(0, width / 2 - 1.59 * wide);
        var halfHeight = Math.Max(0, 0.36 * size - 1.59 * wide);
        list.Add(OrbGpuInstance.RoundedRect(x + width / 2, baseline - 0.36 * size, halfWidth, halfHeight, 0, Math.Min(halfWidth, halfHeight),
            glow.WithAlpha(alpha), core: 1, glow: 1.91 * wide, glowAlpha: 1));
    }

    /// <summary>Notes where each character of the value sits (the digit lightning starts there).</summary>
    private void RecordCells(OrbGpuContext ctx, string text, double x, double baseline, double size, double cell, double sigma)
    {
        var s = ctx.Size;
        var y = (baseline - CellCenter * size - ctx.CenterY) / s;
        var height = CellHeight * size / s;
        _cellCount = 0;
        foreach (var ch in text)
        {
            if (_cellCount == _cells.Length)
            {
                break;
            }

            var width = CellWidth(ch, size, cell, sigma);
            _cells[_cellCount++] = new LedDigitCell((x + width / 2 - ctx.CenterX) / s, y, width / s, height);
            x += width;
        }

        ValueCellsText = text;
    }

    private double CellWidth(char ch, double size, double cell, double sigma) =>
        char.IsAsciiDigit(ch) ? cell
        : _glyphs.TryGet(OrbTextKey.Create(CharText(ch), OrbFont.Value, size, 0, sigma), out var glyph) ? glyph.Advance : 0;

    private double RunWidth(string text, double size, double cell, double sigma)
    {
        var total = 0.0;
        foreach (var ch in text)
        {
            total += CellWidth(ch, size, cell, sigma);
        }

        return total;
    }

    private double Width(string text, double size, double sigma) => RunWidth(text, size, _glyphs.DigitCell(OrbFont.Value, size), sigma);

    private double PrefixAdvance(double size) =>
        _glyphs.TryGet(OrbTextKey.Create(AtLeastSign, OrbFont.Value, size), out var sign) ? sign.Advance : 0;

    // ---- Unit and labels ----

    /// <param name="current">The unit now shown (its hover affordance and hit width apply), not the one leaving.</param>
    private void AddUnit(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, double unitSize, double line, in OrbReadoutColors colors,
        OrbTextClip clip, string text, double offset, double alpha, bool current)
    {
        if (alpha <= 0.01 || string.IsNullOrEmpty(text))
        {
            return;
        }

        var size = OrbReadoutLayout.TextSize(unitSize, ctx.Size);
        var tracking = _strings.Latin ? 0.5 * ctx.Unit : 0;
        var key = OrbTextKey.Create(text, OrbFont.Unit, size, tracking, colors.Shadow is null ? 0 : colors.ShadowSigma);
        if (!_glyphs.TryGet(key, out var glyph))
        {
            return;
        }

        // Hovering the unit raises it from 55 % to 85 % with a 0.5 pt underline at 40 % (DESIGN 9, Switching).
        var hover = current ? scene.UnitHoverAmount : 0;
        var baseColor = colors.Unit ?? Secondary(ctx);
        var color = hover > 0.001 ? baseColor.MultiplyAlpha(1 + hover * (0.85 / 0.55 - 1)) : baseColor;
        list.AddText(glyph, ctx.CenterX - glyph.Advance / 2, line + offset, color.MultiplyAlpha(alpha), colors.Shadow?.MultiplyAlpha(alpha), clip);
        if (current)
        {
            UnitWidth = glyph.Advance;
        }

        if (hover > 0.001)
        {
            list.Add(OrbGpuInstance.RoundedRect(ctx.CenterX, line + offset + 0.18 * size, glyph.Advance / 2, 0.25, 0, 0,
                baseColor.WithAlpha(baseColor.A * 0.4 / 0.55 * hover * alpha)));
        }
    }

    private void AddLabels(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, OrbReadoutLayout layout, double line,
        in OrbReadoutColors colors, OrbTextClip clip)
    {
        var s = ctx.Size;
        var unavailable = UnavailableTitle is not null;
        var hover = unavailable ? 0 : scene.HoverBlend;
        var labelAlpha = 1 - hover;
        var size = OrbReadoutLayout.TextSize(layout.TitleSize, s);
        var tracking = _strings.Latin ? 1.6 * ctx.Unit * size / (0.062 * s) : 0;
        var limit = layout.TitleMaxWidth * s;
        var sigma = colors.Shadow is null ? 0 : colors.ShadowSigma;

        if (labelAlpha > 0.01)
        {
            if (unavailable)
            {
                Title(list, ctx, UnavailableTitle!, colors.Title ?? Secondary(ctx), line, 0, labelAlpha, size, tracking, limit, sigma, colors.Shadow,
                    clip);
            }
            else
            {
                var transition = scene.TitleTransition;
                var elapsed = transition.Progress * OrbScene.TierTransitionDuration;
                var duration = layout.TitleCrossfade ? TitleFade : TitleSlide;
                var p = Math.Clamp(elapsed / duration, 0, 1);
                if (p < 1 && transition.From != transition.To)
                {
                    var travel = scene.Effects.SlideTransitions && !layout.TitleCrossfade && scene.Effects.Level > Settings.EffectsIntensity.Off
                        ? size * 1.1
                        : 0;
                    var box = clip.Intersect(ctx.CenterX - s / 2, line - size * 1.25, ctx.CenterX + s / 2, line + size * 0.35);
                    // Staggered even without motion: the old title is gone before the new one shows (DESIGN 1).
                    var (outAlpha, outTravel) = TitleOut(p);
                    var (inAlpha, inTravel) = TitleIn(p);
                    _rename.Note((int)transition.To, _strings.Title(transition.To), scene.Time);
                    Title(list, ctx, _strings.Title(transition.From), TitleColor(ctx, scene, transition.From, colors), line, -travel * outTravel,
                        labelAlpha * outAlpha, size, tracking, limit, sigma, colors.Shadow, box);
                    Title(list, ctx, _strings.Title(transition.To), TitleColor(ctx, scene, transition.To, colors), line, travel * (1 - inTravel),
                        labelAlpha * inAlpha, size, tracking, limit, sigma, colors.Shadow, box);
                }
                else
                {
                    var label = _strings.Title(scene.Title);
                    var color = TitleColor(ctx, scene, scene.Title, colors);
                    _rename.Note((int)scene.Title, label, scene.Time);
                    if (_rename.IsRunning(scene.Time) && _rename.From is { } renamed)
                    {
                        // A theme or language change renames the shown level: a plain 0.25 s crossfade, no level-up motion.
                        var mix = _rename.Mix(scene.Time);
                        Title(list, ctx, renamed, color, line, 0, labelAlpha * (1 - mix), size, tracking, limit, sigma, colors.Shadow, clip);
                        Title(list, ctx, label, color, line, 0, labelAlpha * mix, size, tracking, limit, sigma, colors.Shadow, clip);
                    }
                    else
                    {
                        Title(list, ctx, label, color, line, 0, labelAlpha, size, tracking, limit, sigma, colors.Shadow, clip);
                    }
                }
            }
        }

        if (hover > 0.01 && !string.IsNullOrEmpty(InfoLine))
        {
            var infoSize = Math.Max(OrbReadoutLayout.MinTextSize, size * layout.InfoScale);
            var infoLimit = layout.InfoMaxWidth * s;
            var key = OrbTextKey.Create(InfoLine, OrbFont.Info, infoSize, 0, sigma);
            if (_glyphs.TryGet(key, out var natural))
            {
                var fit = natural.Advance > infoLimit ? Math.Max(OrbReadoutLayout.InfoMinScale, infoLimit / natural.Advance) : 1;
                if (fit < 1 && _glyphs.TryGet(OrbTextKey.Create(InfoLine, OrbFont.Info, Math.Floor(infoSize * fit * 4) / 4, 0, sigma), out var fitted))
                {
                    natural = fitted;
                }

                list.AddText(natural, ctx.CenterX - natural.Advance / 2, line, (colors.Unit ?? Secondary(ctx)).MultiplyAlpha(hover), colors.Shadow,
                    clip);
            }
        }
    }

    private Rgba TitleColor(OrbGpuContext ctx, OrbScene scene, SpeedTitle title, in OrbReadoutColors colors)
    {
        if (colors.Title is { } fixedColor)
        {
            return fixedColor;
        }

        if (ctx.HighContrast)
        {
            return ctx.Contrast.WindowText;
        }

        return title == SpeedTitle.Idle ? Secondary(ctx) : ctx.Label(Math.Max(scene.Heat, 0.08));
    }

    private void Title(OrbDrawList list, OrbGpuContext ctx, string text, Rgba color, double line, double offset, double alpha, double size,
        double tracking, double limit, double sigma, Rgba? shadow, OrbTextClip clip)
    {
        if (alpha <= 0.01 || string.IsNullOrEmpty(text))
        {
            return;
        }

        var useTracking = _strings.Latin ? tracking : 0;
        if (!_glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Title, size, useTracking, sigma), out var glyph))
        {
            return;
        }

        if (glyph.Advance > limit && _glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Title, size, 0, sigma), out var untracked))
        {
            var (fitTracking, fitSize) = OrbLabelFit.Fit(untracked.Advance, useTracking, Gaps(text), limit, size);
            if (_glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Title, fitSize, fitTracking, sigma), out var fitted))
            {
                glyph = fitted;
            }
        }

        list.AddText(glyph, ctx.CenterX - glyph.Advance / 2, line + offset, color.MultiplyAlpha(alpha), shadow?.MultiplyAlpha(alpha), clip);
    }

    /// <summary>Outgoing title of a level change at progress <paramref name="p"/> (<see cref="TitleTransitions.Outgoing"/>).</summary>
    public static (double Alpha, double Travel) TitleOut(double p)
    {
        var frame = TitleTransitions.Outgoing(p);
        return (frame.Alpha, frame.Travel);
    }

    /// <summary>Incoming title (<see cref="TitleTransitions.Incoming"/>): two titles are never both clearly visible.</summary>
    public static (double Alpha, double Travel) TitleIn(double p)
    {
        var frame = TitleTransitions.Incoming(p);
        return (frame.Alpha, frame.Travel);
    }

    /// <summary>Gaps between the grapheme clusters of a title (custom titles may hold emoji), cached for the last title.</summary>
    private int Gaps(string text)
    {
        if (!ReferenceEquals(text, _gapsText))
        {
            _gapsText = text;
            _gaps = Math.Max(1, new System.Globalization.StringInfo(text).LengthInTextElements - 1);
        }

        return _gaps;
    }

    // ---- Overflow and pill ----

    private void AddOverflow(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, double offset, in OrbReadoutColors colors, OrbTextClip clip)
    {
        var count = scene.AgentDots.Count + scene.AgentOverflow;
        if (count <= 6 || scene.Tier == SpeedTier.Idle || UnavailableTitle is not null)
        {
            return;
        }

        var dots = OrbDotsLayout.Create(count, ctx, offset);
        var size = Math.Max(OrbReadoutLayout.MinTextSize, 0.05 * ctx.Size);
        var label = OverflowLabels[Math.Clamp(dots.Overflow, 0, OverflowLabels.Length - 1)];
        if (_glyphs.TryGet(OrbTextKey.Create(label, OrbFont.Unit, size), out var glyph))
        {
            list.AddText(glyph, dots.OverflowX, dots.Y + _glyphs.CapHeight(OrbFont.Unit, size) / 2, colors.Unit ?? Secondary(ctx), null, clip);
        }
    }

    private void AddPill(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, double line)
    {
        if (scene.PeakPillAge is not { } age || string.IsNullOrEmpty(_strings.Peak))
        {
            return;
        }

        var total = OrbScene.PeakPillDuration;
        var reduce = !scene.Effects.SlideTransitions;
        var fadeOut = Easing.SmoothStep(total - 0.32, total, age);
        var alpha = Math.Min(1, age / (reduce ? 0.2 : 0.12)) * (1 - fadeOut);
        if (alpha <= 0.01)
        {
            return;
        }

        var pop = reduce || !scene.Effects.PeakBurst ? 1 : (0.55 + 0.45 * SpringPop(age)) * (1 - 0.04 * fadeOut);
        var unit = ctx.Unit;
        var tracking = (_strings.Latin ? 0.9 : 0.4) * unit;
        var size = Math.Max(OrbReadoutLayout.MinTextSize, 0.056 * ctx.Size);
        if (!_glyphs.TryGet(OrbTextKey.Create(_strings.Peak, OrbFont.Pill, size, _strings.Latin ? tracking : 0), out var glyph))
        {
            return;
        }

        var cx = ctx.CenterX;
        var height = 0.082 * ctx.Size * pop;
        var width = (glyph.Advance + 0.085 * ctx.Size) * pop;
        var fill = ctx.HighContrast ? ctx.Contrast.Window : PillGold;
        list.Add(OrbGpuInstance.RoundedRect(cx, line, width / 2, height / 2, 0, height / 2, fill.WithAlpha(alpha),
            glow: ctx.HighContrast ? 0 : ctx.Dark ? 0.025 * ctx.Size : 0.012 * ctx.Size, glowAlpha: ctx.Dark ? 0.55 : 0.3));
        var cap = _glyphs.CapHeight(OrbFont.Pill, size) * pop;
        var ink = ctx.HighContrast ? ctx.Contrast.WindowText : PillInk;
        list.AddText(glyph, cx - glyph.Advance / 2 * pop, line + cap / 2, ink.WithAlpha(alpha), scale: pop, pivotX: cx - glyph.Advance / 2 * pop,
            pivotY: line + cap / 2);
    }

    /// <summary>Underdamped spring from 0 to 1 (overshoots slightly), for the pill's pop-in.</summary>
    public static double SpringPop(double t, double response = 0.45, double damping = 0.62)
    {
        if (t <= 0)
        {
            return 0;
        }

        var omega = 2 * Math.PI / response;
        var wd = omega * Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-damping * omega * t) * (Math.Cos(wd * t) + damping * omega / wd * Math.Sin(wd * t));
    }

    /// <summary>A copy of the value drawn under it: offset (points), glow σ, glyph color and glow color.</summary>
    private readonly record struct ValueLayer(double Dx, double Dy, double Sigma, Rgba Color, Rgba? Glow);

    /// <summary>A one-character string without allocating for ASCII and the en dash.</summary>
    public static string CharText(char ch) => ch < 128 ? Ascii[ch] : ch == '–' ? Dash : ch == '≥' ? AtLeastSign : ch.ToString();

    private static string[] BuildAscii()
    {
        var table = new string[128];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = ((char)i).ToString();
        }

        return table;
    }

    private static string[] BuildOverflow()
    {
        var table = new string[100];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = "+" + i.ToString(CultureInfo.InvariantCulture);
        }

        return table;
    }
}
