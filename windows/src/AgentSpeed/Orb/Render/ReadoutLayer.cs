using System.Windows;
using System.Windows.Media;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Render;

/// <summary>Texts the readout shows besides the value.</summary>
internal sealed record ReadoutTexts(string Unit, string Peak, IReadOnlyList<string> Titles, bool Latin)
{
    public string Title(SpeedTitle title) => (int)title < Titles.Count ? Titles[(int)title] : string.Empty;
}

/// <summary>
/// The shared readout of every style (DESIGN 2a, Shared foundation): value with odometer, unit, title slide, hover info
/// line, agent dots and the new-peak pill. Every element is a small cached visual drawn once per content change; a
/// frame only moves them (transforms) and fades them (opacity), so the readout costs almost nothing per frame.
/// </summary>
internal sealed class ReadoutLayer
{
    private const int MaxDots = OrbScene.MaxAgentDots;

    private readonly TextDrawings _text;
    private readonly ContainerVisual _value = new();
    private readonly SkewTransform _skew = new();
    private readonly TranslateTransform _jitter = new();
    private readonly SlotRow _main = new();
    private readonly SlotRow _fade = new();
    private readonly Leaf _unit = new();
    private readonly Leaf _title = new();
    private readonly Leaf _titleOut = new();
    private readonly Leaf _info = new();
    private readonly ContainerVisual _dots = new();
    private readonly Leaf[] _dot = new Leaf[MaxDots];
    private readonly Leaf _overflow = new();
    private readonly Leaf _pill = new();
    private readonly ScaleTransform _pillScale = new();
    private OrbRenderContext? _context;
    private OrbReadoutLayout _layout = OrbReadoutLayout.Led;
    private ReadoutPalette _palette = ReadoutPalette.For(OrbTokens.Dark, false);
    private ReadoutTexts _texts = new("tok/s", "NEW PEAK", [], true);
    private double _digitCell;
    private double _valueSize;
    private long _dotsKey = -1;

    public ReadoutLayer(TextDrawings text)
    {
        _text = text;
        var group = new TransformGroup();
        group.Children.Add(_skew);
        group.Children.Add(_jitter);
        _value.Transform = group;
        _value.Children.Add(_fade.Root);
        _value.Children.Add(_main.Root);
        Root.Children.Add(_value);
        Root.Children.Add(_unit.Visual);
        Root.Children.Add(_titleOut.Visual);
        Root.Children.Add(_title.Visual);
        Root.Children.Add(_info.Visual);
        for (var i = 0; i < MaxDots; i++)
        {
            _dot[i] = new Leaf();
            _dots.Children.Add(_dot[i].Visual);
        }

        _dots.Children.Add(_overflow.Visual);
        Root.Children.Add(_dots);
        _pill.Visual.Transform = _pillScale;
        Root.Children.Add(_pill.Visual);
    }

    public ContainerVisual Root { get; } = new();

    /// <summary>Text replacing the value when every source is unavailable (an en dash), with its title.</summary>
    public (string Value, string Title)? Unavailable { get; set; }

    /// <summary>The hover info line (<c>today.summary</c> over the orb's sources).</summary>
    public string InfoLine { get; set; } = string.Empty;

    public void Configure(OrbRenderContext context, OrbReadoutLayout layout, ReadoutPalette palette, ReadoutTexts texts)
    {
        _context = context;
        _layout = layout.At(context.Size);
        _palette = palette;
        _texts = texts;
        _valueSize = layout.ValueSize * context.Size;
        _digitCell = _text.DigitCell(TextDrawings.ValueFace, _valueSize);
        _main.Reset();
        _fade.Reset();
        _unit.Reset();
        _title.Reset();
        _titleOut.Reset();
        _info.Reset();
        _overflow.Reset();
        _pill.Reset();
        foreach (var dot in _dot)
        {
            dot.Reset();
        }

        _dotsKey = -1;
    }

    /// <summary>Clips the whole readout (the liquid style splits it at its surface); null removes the clip.</summary>
    public void SetClip(Rect? clip)
    {
        if (clip == _clip)
        {
            return;
        }

        _clip = clip;
        Root.Clip = clip is { } r ? new RectangleGeometry(r) : null;
    }

    private Rect? _clip;

    public void Render(OrbScene scene, double now)
    {
        if (_context is not { } ctx)
        {
            return;
        }

        var s = ctx.Size;
        var c = ctx.Center;
        var effects = scene.Effects;
        var thumbnail = s < 64;
        var unavailable = Unavailable;
        var idle = scene.Tier == SpeedTier.Idle && scene.Value <= 0;
        var heat = scene.Heat;

        // Value.
        var valueColor = idle || unavailable is not null ? _palette.IdleValue : _palette.Value;
        Rgba? glow = null;
        var glowRadius = 0.0;
        if (_palette.Shadow is { } shadow)
        {
            glow = shadow;
            glowRadius = _palette.ShadowRadius * s;
        }
        else if (_layout.ValueGlow && _palette.Glow && effects.ValueGlow && scene.Tier >= SpeedTier.Boost && unavailable is null && !ctx.HighContrast)
        {
            var bucket = Math.Round(Math.Max(heat, 0.2) * 15) / 15;
            glow = HeatPalette.Sample(bucket).WithAlpha(ctx.Dark ? 1 : 0.5);
            glowRadius = 0.035 * s * effects.Bloom;
        }

        var odometer = scene.Odometer;
        var text = unavailable?.Value ?? odometer.Current;
        var natural = Width(text, _valueSize);
        var fit = natural > _layout.ValueMaxWidth * s ? _layout.ValueMaxWidth * s / natural : 1;
        var size = _valueSize * fit;
        var cell = fit < 1 ? _digitCell * fit : _digitCell;
        var baseline = c.Y + _layout.ValueBaseline * s;
        var rolls = _layout.Rolls && unavailable is null && s >= 80 && odometer.Previous is not null && odometer.IsAnimating(now);
        if (rolls && odometer.Rolls)
        {
            var eased = odometer.RollEase(now);
            var (outgoing, incoming) = odometer.Offsets(eased);
            var distance = OdometerText.Travel * size;
            _main.Layout(this, text, odometer.Previous, size, cell, valueColor, glow, glowRadius, c.X, baseline,
                eased, outgoing * distance, incoming * distance, 1);
            _fade.Hide();
        }
        else if (rolls)
        {
            var amount = odometer.CrossfadeAmount(now);
            _main.Layout(this, text, null, size, cell, valueColor, glow, glowRadius, c.X, baseline, 1, 0, 0, amount);
            var previous = odometer.Previous!;
            var previousFit = Math.Min(1, _layout.ValueMaxWidth * s / Math.Max(1, Width(previous, _valueSize)));
            _fade.Layout(this, previous, null, _valueSize * previousFit, _digitCell * previousFit, valueColor, glow, glowRadius, c.X,
                baseline, 1, 0, 0, 1 - amount);
        }
        else
        {
            _main.Layout(this, text, null, size, cell, valueColor, glow, glowRadius, c.X, baseline, 1, 0, 0, 1);
            _fade.Hide();
        }

        var skew = unavailable is null ? scene.SkewDegrees : 0;
        _skew.AngleX = skew;
        _skew.CenterX = c.X;
        _skew.CenterY = baseline - 0.35 * size;
        _jitter.X = unavailable is null ? scene.JitterX * s / 148 : 0;
        _jitter.Y = unavailable is null ? scene.JitterY * s / 148 : 0;

        if (thumbnail)
        {
            _unit.Hide();
            _title.Hide();
            _titleOut.Hide();
            _info.Hide();
            HideDots();
            _pill.Hide();
            return;
        }

        // Unit.
        if (_layout.UnitSize is { } unitSize && _layout.UnitBaseline is { } unitBaseline)
        {
            var unitDrawing = _text.Get(_texts.Unit, TextDrawings.UnitFace, OrbReadoutLayout.TextSize(unitSize, s),
                _palette.Secondary, _palette.Shadow, _palette.ShadowRadius * s, _texts.Latin ? 0.5 * s / 148 : 0);
            _unit.Show(unitDrawing, c.X - unitDrawing.Width / 2, c.Y + unitBaseline * s, 1);
        }
        else
        {
            _unit.Hide();
        }

        RenderTitle(scene, now, ctx, heat, unavailable);
        RenderDots(scene, now, ctx);
        RenderPill(scene, ctx);
    }

    private void RenderTitle(OrbScene scene, double now, OrbRenderContext ctx, double heat, (string Value, string Title)? unavailable)
    {
        var s = ctx.Size;
        var c = ctx.Center;
        var titleSize = OrbReadoutLayout.TextSize(_layout.TitleSize, s);
        var tracking = _texts.Latin ? 1.6 * s / 148 * titleSize / (0.062 * s) : 0;
        var maxWidth = _layout.TitleMaxWidth * s;
        var y = c.Y + _layout.TitleBaseline * s;
        var hover = unavailable is null ? scene.HoverBlend : 0;
        var transition = scene.TitleTransition;
        var heatBucket = Math.Round(Math.Max(heat, 0.08) * 31) / 31;

        Rgba TitleColor(SpeedTitle title) =>
            _palette.TitleOverride ?? (title == SpeedTitle.Idle ? _palette.Secondary : ctx.Tokens.TierLabel(heatBucket));

        TextDrawing Fitted(string text, Rgba color)
        {
            var natural = _text.Measure(text, TextDrawings.LabelFace, titleSize, tracking);
            var scale = natural > maxWidth ? Math.Max(OrbReadoutLayout.TitleMinScale, maxWidth / natural) : 1;
            return _text.Get(text, TextDrawings.LabelFace, titleSize * scale, color, _palette.Shadow, _palette.ShadowRadius * s,
                tracking * scale);
        }

        var labelAlpha = 1 - hover;
        if (unavailable is { } u)
        {
            var drawing = Fitted(u.Title, _palette.Secondary);
            _title.Show(drawing, c.X - drawing.Width / 2, y, 1);
            _titleOut.Hide();
        }
        else if (transition.Progress < 1 && transition.From != transition.To)
        {
            var progress = transition.Progress;
            double inAlpha, outAlpha, rise = 0;
            if (_layout.TitleCrossfade || !scene.Effects.SlideTransitions)
            {
                var t = Math.Clamp(progress * OrbScene.TierTransitionDuration / 0.25, 0, 1);
                inAlpha = t;
                outAlpha = 1 - t;
            }
            else
            {
                var t = Easing.OutCubic(progress);
                inAlpha = t;
                outAlpha = 1 - t;
                rise = titleSize * 1.1;
                var from = Fitted(_texts.Title(transition.From), TitleColor(transition.From));
                _titleOut.Show(from, c.X - from.Width / 2, y - rise * t, outAlpha * labelAlpha);
                var to = Fitted(_texts.Title(transition.To), TitleColor(transition.To));
                _title.Show(to, c.X - to.Width / 2, y + rise * (1 - t), inAlpha * labelAlpha);
                RenderInfo(ctx, y, titleSize, hover);
                return;
            }

            var outgoing = Fitted(_texts.Title(transition.From), TitleColor(transition.From));
            _titleOut.Show(outgoing, c.X - outgoing.Width / 2, y, outAlpha * labelAlpha);
            var incoming = Fitted(_texts.Title(transition.To), TitleColor(transition.To));
            _title.Show(incoming, c.X - incoming.Width / 2, y, inAlpha * labelAlpha);
        }
        else
        {
            var drawing = Fitted(_texts.Title(scene.Title), TitleColor(scene.Title));
            _title.Show(drawing, c.X - drawing.Width / 2, y, labelAlpha);
            _titleOut.Hide();
        }

        RenderInfo(ctx, y, titleSize, hover);
    }

    private void RenderInfo(OrbRenderContext ctx, double y, double titleSize, double hover)
    {
        if (hover <= 0.01 || string.IsNullOrEmpty(InfoLine))
        {
            _info.Hide();
            return;
        }

        var s = ctx.Size;
        var size = Math.Max(OrbReadoutLayout.MinTextSize, titleSize * _layout.InfoScale);
        var maxWidth = _layout.InfoMaxWidth * s;
        var natural = _text.Measure(InfoLine, TextDrawings.InfoFace, size);
        var scale = natural > maxWidth ? Math.Max(OrbReadoutLayout.InfoMinScale, maxWidth / natural) : 1;
        var drawing = _text.Get(InfoLine, TextDrawings.InfoFace, size * scale, _palette.Secondary, _palette.Shadow, _palette.ShadowRadius * s);
        _info.Show(drawing, ctx.Center.X - drawing.Width / 2, y, hover);
    }

    private void RenderDots(OrbScene scene, double now, OrbRenderContext ctx)
    {
        var agents = scene.AgentDots;
        if (_layout.DotsY is not { } dotsY || agents.Count == 0 || scene.Tier == SpeedTier.Idle || Unavailable is not null)
        {
            HideDots();
            return;
        }

        var s = ctx.Size;
        var unit = s / 148;
        var diameter = 4 * unit;
        var spacing = 3.2 * unit;
        var count = agents.Count;
        var total = count * diameter + (count - 1) * spacing;
        var x0 = ctx.Center.X - total / 2 + diameter / 2;
        var y = ctx.Center.Y + dotsY * s;
        long key = scene.AgentOverflow;
        foreach (var agent in agents)
        {
            key = key * 16 + (int)agent + 1;
        }

        var pulse = scene.Effects.AuraBreathing && scene.Effects.SlideTransitions;
        for (var i = 0; i < MaxDots; i++)
        {
            if (i >= count)
            {
                _dot[i].Hide();
                continue;
            }

            if (key != _dotsKey || _dot[i].Drawing is null)
            {
                _dot[i].Show(DotDrawing(agents[i], diameter / 2, ctx), 0, 0, 1);
            }

            _dot[i].Place(x0 + i * (diameter + spacing), y);
            _dot[i].Visual.Opacity = pulse ? 0.62 + 0.38 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (now * 0.9 + i * 0.19))) : 1;
        }

        if (scene.AgentOverflow > 0)
        {
            var text = "+" + scene.AgentOverflow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var drawing = _text.Get(text, TextDrawings.UnitFace, Math.Max(7, 0.05 * s), _palette.Secondary);
            _overflow.Show(drawing, x0 + count * (diameter + spacing) - diameter / 2, y + drawing.Ascent * 0.35, 1);
        }
        else
        {
            _overflow.Hide();
        }

        _dotsKey = key;
    }

    private Drawing DotDrawing(SourceKind source, double radius, OrbRenderContext ctx)
    {
        var color = ctx.HighContrast ? SystemColors.WindowTextColor.ToRgba()
            : ctx.Dark ? HeatPalette.SourceColor(source) : OkLab.Darken(HeatPalette.SourceColor(source), 0.12);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            if (_layout.DotHalo && !ctx.HighContrast)
            {
                var halo = radius + 1.2 * ctx.Size / 148;
                dc.DrawEllipse(ctx.Brushes.Get(_palette.DotHalo), null, new Point(0, 0), halo, halo);
            }

            dc.DrawEllipse(ctx.Brushes.Get(color), null, new Point(0, 0), radius, radius);
        }

        group.Freeze();
        return group;
    }

    private void HideDots()
    {
        foreach (var dot in _dot)
        {
            dot.Hide();
        }

        _overflow.Hide();
        _dotsKey = -1;
    }

    private void RenderPill(OrbScene scene, OrbRenderContext ctx)
    {
        if (scene.PeakPillAge is not { } age || string.IsNullOrEmpty(_texts.Peak))
        {
            _pill.Hide();
            return;
        }

        var s = ctx.Size;
        var effects = scene.Effects;
        var animated = effects.PeakBurst && effects.SlideTransitions;
        var appear = animated ? Easing.OutBack(age / 0.45, 2.2) : Math.Clamp(age / 0.2, 0, 1);
        var fadeOut = Math.Clamp((OrbScene.PeakPillDuration - age) / 0.25, 0, 1);
        var scale = animated ? 0.6 + 0.4 * appear : 1;
        var center = new Point(ctx.Center.X, ctx.Center.Y + _layout.PillY * s);
        if (_pill.Drawing is null)
        {
            _pill.Show(PillDrawing(ctx), 0, 0, 1);
        }

        _pill.Place(center.X, center.Y);
        _pillScale.CenterX = 0;
        _pillScale.CenterY = 0;
        _pillScale.ScaleX = scale;
        _pillScale.ScaleY = scale;
        _pill.Visual.Opacity = Math.Clamp(Math.Clamp(appear, 0, 1) * fadeOut, 0, 1);
    }

    private Drawing PillDrawing(OrbRenderContext ctx)
    {
        var s = ctx.Size;
        var size = 0.056 * s;
        var tracking = _texts.Latin ? 1.2 * s / 148 : 0;
        var textColor = ctx.HighContrast ? SystemColors.WindowTextColor.ToRgba() : ctx.Dark ? Rgba.FromHex("#FFE58A") : Rgba.FromHex("#7A5A00");
        var text = _text.Get(_texts.Peak, TextDrawings.PillFace, size, textColor, null, 0, tracking);
        var width = text.Width + 0.07 * s;
        var height = size * 1.55;
        var gold = ctx.Dark ? HeatPalette.Gold : Rgba.FromHex("#B98900");
        var fill = ctx.HighContrast ? SystemColors.WindowColor.ToRgba() : HeatPalette.Gold.WithAlpha(ctx.Dark ? 0.18 : 0.22);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var rect = new Rect(-width / 2, -height / 2, width, height);
            dc.DrawRoundedRectangle(ctx.Brushes.Get(fill), MediaColors.FrozenPen(ctx.Brushes.Get(gold, 0.85), Math.Max(1, s / 148)), rect,
                height / 2, height / 2);
            dc.PushTransform(new TranslateTransform(-text.Width / 2, size * 0.36));
            dc.DrawDrawing(text.Drawing);
            dc.Pop();
        }

        group.Freeze();
        return group;
    }

    private double Width(string text, double size)
    {
        var cell = _digitCell * size / Math.Max(0.001, _valueSize);
        var width = 0.0;
        foreach (var ch in text)
        {
            width += char.IsDigit(ch) ? cell : _text.Get(TextDrawings.CharText(ch), TextDrawings.ValueFace, size, Rgba.White).Width;
        }

        return width;
    }

    private double CellWidth(char ch, double size, double cell) =>
        char.IsDigit(ch) ? cell : _text.Get(TextDrawings.CharText(ch), TextDrawings.ValueFace, size, Rgba.White).Width;

    /// <summary>A cached leaf visual showing one frozen drawing; frames only move and fade it.</summary>
    private sealed class Leaf
    {
        private readonly TranslateTransform _offset = new();
        private Drawing? _drawing;

        public Leaf()
        {
            Visual.Transform = _offset;
            Visual.CacheMode = new BitmapCache(1) { SnapsToDevicePixels = false };
        }

        public DrawingVisual Visual { get; } = new();

        public Drawing? Drawing => _drawing;

        public void Show(TextDrawing text, double x, double baseline, double opacity) => Show(text.Drawing, x, baseline, opacity);

        public void Show(Drawing drawing, double x, double y, double opacity)
        {
            if (!ReferenceEquals(drawing, _drawing))
            {
                _drawing = drawing;
                using var dc = Visual.RenderOpen();
                dc.DrawDrawing(drawing);
            }

            Place(x, y);
            var alpha = Math.Clamp(opacity, 0, 1);
            if (Math.Abs(Visual.Opacity - alpha) > 0.002)
            {
                Visual.Opacity = alpha;
            }
        }

        public void Place(double x, double y)
        {
            if (_offset.X != x)
            {
                _offset.X = x;
            }

            if (_offset.Y != y)
            {
                _offset.Y = y;
            }
        }

        public void Hide()
        {
            if (_drawing is not null || Visual.Opacity != 0)
            {
                Visual.Opacity = 0;
            }
        }

        public void Reset()
        {
            _drawing = null;
            using var dc = Visual.RenderOpen();
            Visual.Opacity = 0;
        }
    }

    /// <summary>One row of value characters, each in its own clipped cell with an outgoing and an incoming leaf.</summary>
    private sealed class SlotRow
    {
        private readonly List<(ContainerVisual Cell, Leaf In, Leaf Out)> _slots = [];
        private readonly List<Rect> _clips = [];

        public ContainerVisual Root { get; } = new();

        public void Reset()
        {
            foreach (var (_, incoming, outgoing) in _slots)
            {
                incoming.Reset();
                outgoing.Reset();
            }
        }

        public void Hide()
        {
            foreach (var (_, incoming, outgoing) in _slots)
            {
                incoming.Hide();
                outgoing.Hide();
            }
        }

        public void Layout(ReadoutLayer owner, string text, string? previous, double size, double cell, Rgba color, Rgba? glow,
            double glowRadius, double centerX, double baseline, double eased, double outOffset, double inOffset, double opacity)
        {
            while (_slots.Count < text.Length)
            {
                var slot = (new ContainerVisual(), new Leaf(), new Leaf());
                slot.Item1.Children.Add(slot.Item3.Visual);
                slot.Item1.Children.Add(slot.Item2.Visual);
                Root.Children.Add(slot.Item1);
                _slots.Add(slot);
                _clips.Add(Rect.Empty);
            }

            var total = 0.0;
            foreach (var ch in text)
            {
                total += owner.CellWidth(ch, size, cell);
            }

            var x = centerX - total / 2;
            for (var i = 0; i < _slots.Count; i++)
            {
                var (container, incoming, outgoing) = _slots[i];
                if (i >= text.Length)
                {
                    incoming.Hide();
                    outgoing.Hide();
                    container.Clip = null;
                    continue;
                }

                var ch = text[i];
                var width = owner.CellWidth(ch, size, cell);
                var glyph = owner._text.Get(TextDrawings.CharText(ch), TextDrawings.ValueFace, size, color, glow, glowRadius);
                var glyphX = x + (width - glyph.Width) / 2;
                var changing = previous is not null && i < previous.Length && previous[i] != ch && eased < 1;
                if (changing)
                {
                    var old = owner._text.Get(TextDrawings.CharText(previous![i]), TextDrawings.ValueFace, size, color, glow, glowRadius);
                    var oldWidth = owner.CellWidth(previous[i], size, cell);
                    var rect = new Rect(x - 2, baseline - size * 0.92, width + 4, size * 1.12);
                    if (container.Clip is null || _clips[i] != rect)
                    {
                        var clip = new RectangleGeometry(rect);
                        clip.Freeze();
                        container.Clip = clip;
                        _clips[i] = rect;
                    }

                    outgoing.Show(old, x + (oldWidth - old.Width) / 2, baseline + outOffset, (1 - eased) * opacity);
                    incoming.Show(glyph, glyphX, baseline + inOffset, eased * opacity);
                }
                else
                {
                    if (container.Clip is not null)
                    {
                        container.Clip = null;
                    }

                    outgoing.Hide();
                    incoming.Show(glyph, glyphX, baseline, opacity);
                }

                x += width;
            }
        }
    }
}
