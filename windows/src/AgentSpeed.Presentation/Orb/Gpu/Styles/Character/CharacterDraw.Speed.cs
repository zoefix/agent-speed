using System.Numerics;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

public sealed partial class CharacterDraw
{
    private static readonly Rgba Gold = new(1, 200 / 255.0, 80 / 255.0);

    /// <summary>
    /// The ink of the floating speed (DESIGN 2a.6): digits with an outline in the opposite tone and a soft halo, so they read
    /// over her dress and any wallpaper without a plate; a changed digit's halo flares electric with the lightning.
    /// </summary>
    private readonly record struct SpeedInk(bool Dark, bool Contrast)
    {
        public Rgba Outline => Dark ? new Rgba(0.02, 0.025, 0.045, Contrast ? 1 : 0.9) : Rgba.White.WithAlpha(Contrast ? 1 : 0.95);

        public Rgba Shadow => Dark ? new Rgba(0, 0, 0.02) : Rgba.White;

        public double ShadowAlpha => Contrast ? 0.6 : 0.45;

        /// <summary>Outline width for text of <paramref name="size"/> points.</summary>
        public double Width(double size) => size * (Contrast ? 0.085 : 0.06);

        private Rgba Electric => Dark ? new Rgba(0.55, 0.82, 1) : new Rgba(0.25, 0.5, 1);

        public (Rgba Color, double Alpha, double Radius) Halo(double flash, Rgba heat)
        {
            var k = Math.Clamp(flash, 0, 1);
            var e = Rgba.Lerp(Electric, heat, 0.2);
            return (Rgba.Lerp(Shadow, e, k), Math.Max(ShadowAlpha * (1 - k), 0.9 * k), 0.1 + 0.08 * k);
        }

        public (Rgba Glow, Rgba Core) Bolt(Rgba heat) => Dark
            ? (Rgba.Lerp(Electric, heat, 0.2), new Rgba(0.95, 0.98, 1))
            : (new Rgba(0.3, 0.55, 1), new Rgba(0.12, 0.3, 0.95));
    }

    /// <summary>The value when it does not roll: <c>–</c> while unavailable, the money readout in the money unit.</summary>
    private static string? StaticValue(in OrbFrame frame, OrbScene scene) => frame.Unavailable ? "–" : scene.Money?.Number;

    private string UnitText(OrbScene scene) => scene.UnitText ?? Strings?.Unit ?? "tok/s";

    private string TitleText(in OrbFrame frame, OrbScene scene) => Strings?.Title(frame.Unavailable ? SpeedTitle.Idle : scene.Title) ?? string.Empty;

    /// <summary>Half the size of a chip for <paramref name="text"/> (no dot), unscaled; null when it cannot be drawn.</summary>
    private (double W, double H)? ChipHalf(string text, double size)
    {
        if (string.IsNullOrEmpty(text) || Glyphs is not { } glyphs || !glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Title, size), out var glyph))
        {
            return null;
        }

        return (glyph.Advance / 2 + 0.7 * size, glyphs.CapHeight(OrbFont.Title, size) / 2 + 0.55 * size);
    }

    /// <summary>The speed's card: the rolling digits over the unit and the title chip; the third value is the digits' center below the card's center.</summary>
    private (double W, double H, double Drop) SpeedBox(OrbGpuContext ctx, in OrbFrame frame)
    {
        var m = new ReadoutMetrics(ctx.Size);
        var ink = new SpeedInk(ctx.Dark, ctx.HighContrast);
        var glyphs = Glyphs;
        var pad = ink.Width(m.Value) + 1;
        var half = (glyphs?.CapHeight(OrbFont.Value, m.Value) ?? 0.7 * m.Value) * 0.62 + pad;
        var width = _hud.SpeedWidth + 2 * pad;
        var scene = ((CharacterModel)_model).Scene;
        if (scene is not null && StaticValue(frame, scene) is { } text)
        {
            width = Advance(text, OrbFont.Value, m.Value) + 2 * pad;
        }

        if (scene is not null)
        {
            width = Math.Max(width, Advance(UnitText(scene), OrbFont.Unit, m.Unit));
        }

        var bottom = 50 * m.Q + (glyphs?.CapHeight(OrbFont.Unit, m.Unit) ?? 0.7 * m.Unit);
        if (scene is not null && ChipHalf(TitleText(frame, scene), m.Title) is { } chip)
        {
            width = Math.Max(width, 2 * chip.W);
            bottom = 86 * m.Q + m.Title + chip.H;
        }

        return (width, bottom + half, (bottom - half) / 2);
    }

    private void DrawSpeed(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frameIn, OrbScene scene, in Pen penIn)
    {
        var frame = frameIn;
        var pen = penIn;
        if (Glyphs is not { } glyphs)
        {
            return;
        }

        var m = new ReadoutMetrics(ctx.Size);
        var q = m.Q;
        var width = _hud.SpeedWidth;
        var heat = frame.Unavailable ? 0 : frame.Heat;
        var glow = ctx.Label(heat);
        var glowAlpha = (0.12 + 0.5 * Math.Max(0, heat - 0.3)) * pen.Alpha;
        if (!ctx.HighContrast)
        {
            list.AddCharacterDot(new OrbCharacterDot((float)pen.Cx, (float)pen.Cy, (float)((width * 0.75 + 30 * q) * pen.Scale), (float)(50 * q * pen.Scale),
                (float)glow.R, (float)glow.G, (float)glow.B, (float)glowAlpha, 1), ctx.Dark ? OrbBlend.Additive : OrbBlend.Over);
        }

        var cap = glyphs.CapHeight(OrbFont.Value, m.Value);
        var ink = new SpeedInk(ctx.Dark, ctx.HighContrast);
        var valueColor = ctx.HighContrast ? ctx.Contrast.WindowText
            : frame.Idle || frame.Unavailable ? ctx.Tokens.IdleValueText : ctx.Tokens.ValueText;
        var outline = ctx.HighContrast ? ctx.Contrast.Window : ink.Outline;
        var outlineSigma = ink.Width(m.Value) * 0.75;
        if (StaticValue(frame, scene) is { } text)
        {
            var advance = Advance(text, OrbFont.Value, m.Value);
            Put(list, text, OrbFont.Value, m.Value, pen, -advance / 2, cap / 2, Rgba.Transparent, ink.Shadow.WithAlpha(ink.ShadowAlpha), 0.1 * m.Value);
            Put(list, text, OrbFont.Value, m.Value, pen, -advance / 2, cap / 2, valueColor, outline, outlineSigma);
        }
        else
        {
            var digits = _hud.Odometer.Digits;
            var cell = digits.Count == 0 ? 0 : width / digits.Count;
            DrawLightning(list, ctx, frame, pen, digits.Count, cell, cap, m);
            var pad = ink.Width(m.Value) + 1;
            var half = cap * 0.62 + pad;
            var line = 2 * half;
            var clip = OrbTextClip.Rect(pen.Cx - (width / 2 + pad) * pen.Scale, pen.Cy - half * pen.Scale - 0.5, pen.Cx + (width / 2 + pad) * pen.Scale,
                pen.Cy + half * pen.Scale + 0.5);
            for (var i = 0; i < digits.Count; i++)
            {
                var digit = digits[i];
                var ox = -width / 2 + cell * (i + 0.5);
                var ease = 1 - Math.Pow(1 - digit.T, 3);
                var flash = _lightning.Flash(digits.Count - 1 - i);
                var halo = ink.Halo(flash, glow);
                Roll(digit.Previous, -ease, 1 - ease);
                Roll(digit.Current, 1 - ease, ease);

                void Roll(char? ch, double dy, double a)
                {
                    if (a <= 0.01 || ch is not { } c)
                    {
                        return;
                    }

                    var s = CharText(c);
                    var advance = Advance(s, OrbFont.Value, m.Value);
                    var baseline = cap / 2 + dy * line;
                    if (!ctx.HighContrast)
                    {
                        // Two fixed halo widths (rest and flash) keep the glyph atlas from rasterizing every step of a flash.
                        if (flash < 0.99)
                        {
                            Put(list, s, OrbFont.Value, m.Value, pen, ox - advance / 2, baseline, Rgba.Transparent,
                                ink.Shadow.WithAlpha(ink.ShadowAlpha * (1 - flash) * a), 0.1 * m.Value, clip);
                        }

                        if (flash > 0.01)
                        {
                            Put(list, s, OrbFont.Value, m.Value, pen, ox - advance / 2, baseline, Rgba.Transparent,
                                halo.Color.WithAlpha(0.9 * flash * a), 0.18 * m.Value, clip);
                        }
                    }

                    Put(list, s, OrbFont.Value, m.Value, pen, ox - advance / 2, baseline, valueColor.MultiplyAlpha(a), outline.MultiplyAlpha(a), outlineSigma, clip);
                }
            }
        }

        var unit = UnitText(scene);
        var unitCap = glyphs.CapHeight(OrbFont.Unit, m.Unit);
        var unitAdvance = Advance(unit, OrbFont.Unit, m.Unit);
        var secondary = ctx.HighContrast ? ctx.Contrast.GrayText : ctx.Tokens.SecondaryText;
        Put(list, unit, OrbFont.Unit, m.Unit, pen, -unitAdvance / 2, 50 * q + unitCap, secondary, outline.MultiplyAlpha(0.85), ink.Width(m.Unit) * 0.75);

        var titleColor = ctx.HighContrast ? ctx.Contrast.Highlight : glow;
        var e = 1 - Math.Pow(1 - _hud.TitleProgress, 3);
        if (frame.Effects.ReduceMotion)
        {
            e = _hud.TitleProgress;
        }

        TitleChip(_hud.PreviousTitle, incoming: false);
        TitleChip(_hud.Title, incoming: true);

        void TitleChip(string? title, bool incoming)
        {
            if (title is null)
            {
                return;
            }

            var a = incoming ? Math.Max(0, (e - 0.3) / 0.7) : Math.Max(0, 1 - 2.5 * e);
            if (a <= 0.01)
            {
                return;
            }

            var slide = frame.Effects.ReduceMotion ? 0 : incoming ? (1 - e) * 14 * q : -e * 14 * q;
            Chip(list, ctx, pen with { Alpha = pen.Alpha * a }, title, m.Title, 86 * q + m.Title + slide, titleColor, titleColor.WithAlpha(0.35), null);
        }
    }

    /// <summary>A glass chip centered at a local offset: rim, fill, an optional leading dot, then the text. Returns its height.</summary>
    private double Chip(OrbDrawList list, OrbGpuContext ctx, in Pen pen, string text, double size, double oy, Rgba color, Rgba rim, Rgba? dot)
    {
        if (Glyphs is not { } glyphs || !glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Title, size), out var glyph))
        {
            return 0;
        }

        var cap = glyphs.CapHeight(OrbFont.Title, size);
        var dotSpace = dot is null ? 0 : size * 0.85;
        var halfW = (glyph.Advance + dotSpace) / 2 + 0.7 * size;
        var halfH = cap / 2 + 0.55 * size;
        var contrast = ctx.HighContrast;
        var fill = contrast ? ctx.Contrast.Window
            : ctx.Dark ? new Rgba(16 / 255.0, 18 / 255.0, 26 / 255.0, 0.6) : Rgba.White.WithAlpha(0.78);
        var (cx, cy) = pen.Point(0, oy);
        var degrees = pen.Spin * 180 / Math.PI;
        var s = pen.Scale;
        list.Add(OrbGpuInstance.RoundedRect(cx, cy, (halfW + 0.6) * s, (halfH + 0.6) * s, degrees, (halfH + 0.6) * s,
            (contrast ? ctx.Contrast.WindowText : rim).MultiplyAlpha(pen.Alpha)));
        list.Add(OrbGpuInstance.RoundedRect(cx, cy, halfW * s, halfH * s, degrees, halfH * s, fill.MultiplyAlpha(pen.Alpha)));
        var textX = 0.0;
        if (dot is { } d)
        {
            var (dx, dy) = pen.Point(-halfW + 0.7 * size + size * 0.28, oy);
            list.Add(OrbGpuInstance.Circle(dx, dy, size * 0.28 * s, d.MultiplyAlpha(pen.Alpha)));
            textX = dotSpace / 2;
        }

        list.AddInlineText(glyph, pen.Cx + textX - glyph.Advance / 2, pen.Cy + oy + cap / 2, (contrast ? ctx.Contrast.WindowText : color).MultiplyAlpha(pen.Alpha),
            null, OrbTextClip.None, s, pen.Spin, pen.Cx, pen.Cy);
        return halfH * 2;
    }

    /// <summary>The bolts of every live strike, as glowing three-stroke polylines under the digits.</summary>
    private void DrawLightning(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, in Pen pen, int digitCount, double advance, double cap,
        in ReadoutMetrics m)
    {
        if (!_lightning.IsActive || ctx.HighContrast)
        {
            return;
        }

        var strength = LightningStrength(frame.Effects, IsSmall(ctx));
        var ink = new SpeedInk(ctx.Dark, false);
        var (glow, core) = ink.Bolt(ctx.Label(frame.Heat));
        var value = m.Value;
        foreach (var strike in _lightning.Strikes)
        {
            var envelope = _lightning.Envelope(strike) * strike.Power * pen.Alpha;
            if (envelope <= 0.01)
            {
                continue;
            }

            for (var index = 0; _lightning.Bolt(strike, index, strength, digitCount, advance, cap); index++)
            {
                var path = _lightning.Path;
                var last = path.Count - 1;
                for (var i = 0; i < last; i++)
                {
                    var a = pen.Point(path[i].X, path[i].Y);
                    var b = pen.Point(path[i + 1].X, path[i + 1].Y);
                    var startCut = i == 0 ? Vector2.Zero : Bisector(pen.Point(path[i - 1].X, path[i - 1].Y), a, b);
                    var endCut = i == last - 1 ? Vector2.Zero : Bisector(a, b, pen.Point(path[i + 2].X, path[i + 2].Y));
                    list.Add(OrbGpuInstance.Stroke(a.X, a.Y, b.X, b.Y, 0.11 * value * pen.Scale, 0.045 * value * pen.Scale, 0.02 * value * pen.Scale,
                        glow, core, 0.3 * envelope, 0.6 * envelope, envelope, ctx.Dark, startCut, endCut), ctx.GlowBlend);
                }
            }
        }
    }

    private static Vector2 Bisector((double X, double Y) before, (double X, double Y) joint, (double X, double Y) after)
    {
        static Vector2 Direction((double X, double Y) from, (double X, double Y) to)
        {
            var d = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
            return d.LengthSquared() < 1e-14f ? Vector2.Zero : Vector2.Normalize(d);
        }

        var incoming = Direction(before, joint);
        var outgoing = Direction(joint, after);
        if (incoming == Vector2.Zero || outgoing == Vector2.Zero)
        {
            return incoming == Vector2.Zero ? outgoing : incoming;
        }

        var mean = incoming + outgoing;
        return mean.LengthSquared() < 1e-8f ? outgoing : Vector2.Normalize(mean);
    }

    /// <summary>The new-peak pill: gold, above the speed, with its 24 rays (the rays are lights of the hud).</summary>
    private void DrawPeak(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, OrbScene scene)
    {
        if (!scene.PeaksEnabled || !frame.Effects.PeakBurst || _hud.Peak is not { } peak)
        {
            return;
        }

        var side = ctx.Canvas;
        var m = new ReadoutMetrics(ctx.Size);
        var x = Math.Clamp(peak.X, 0.2 * side, 0.8 * side);
        var pen = new Pen(x, peak.Y, peak.Scale, 0, peak.Alpha);
        var text = Strings?.Peak ?? string.Empty;
        Chip(list, ctx, pen, text, m.Peak, 0, ctx.Dark ? Gold : Rgba.Lerp(Gold, Rgba.Black, 0.35), Gold.WithAlpha(0.5), null);
    }
}
