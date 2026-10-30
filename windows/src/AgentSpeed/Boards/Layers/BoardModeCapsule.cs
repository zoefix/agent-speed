using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Modes;
using Vortice;
using Vortice.Direct2D1;

namespace AgentSpeed.Boards.Layers;

/// <summary>
/// A style's effort capsule (the prototypes' <c>.cap</c>, DESIGN 4 Agents): shape, word font and glows. Colors come from the
/// board's palette and, like the dashboard's WPF capsule, from <see cref="EffortCapsulePalette"/>.
/// </summary>
/// <param name="Corner">Corner radius (glass and neon a pill, timing a square-cornered chip).</param>
/// <param name="Padding">Left and right padding.</param>
/// <param name="Gap">Space between the meter, the word and the bolts.</param>
/// <param name="Stroke">Width of the inner outline.</param>
/// <param name="Word">The word's text style.</param>
/// <param name="Glows">Neon's glowing bars and words.</param>
/// <param name="TopGlow">The top rung's bar glow (CSS blur).</param>
/// <param name="BoltGlow">The bolts' glow, 0 for none.</param>
/// <param name="Compact">S boards drop the word (DESIGN 2c Layout per size).</param>
/// <param name="Mark"><c>mode.configured.mark</c> in the board's language.</param>
internal sealed record BoardCapsuleLook(float Corner, float Padding, float Gap, float Stroke, BoardTextStyle Word, bool Glows, float TopGlow,
    float BoltGlow, bool Compact, BoardPalette Palette, string Mark = "")
{
    public bool Dark => Palette.Dark;

    public bool HighContrast => Palette.HighContrast;

    /// <summary>The mark next to the word: as much smaller as on the dashboard.</summary>
    public BoardTextStyle MarkStyle => Word.With(Word.Size - (float)(EffortCapsule.WordFontSize - EffortCapsule.MarkFontSize));
}

/// <summary>
/// Measures and draws the effort and speed capsule into a row layer (DESIGN 2c Rows; the seam <see cref="IBoardModeCapsule"/>):
/// meter, word and bolts from <see cref="EffortCapsule"/>, the bolts pushed to the right end when the shared column is wider.
/// </summary>
internal sealed class BoardModeCapsule : IBoardModeCapsule
{
    private const float BoltScale = 1;
    private const float BoltWidth = 8;

    private readonly BoardText _text;
    private BoardCapsuleLook? _look;

    public BoardModeCapsule(BoardText text, BoardCapsuleLook? look = null)
    {
        _text = text;
        _look = look;
    }

    /// <summary>The style's look; set before measuring or drawing.</summary>
    public BoardCapsuleLook Look
    {
        get => _look ?? throw new InvalidOperationException("The capsule has no look yet.");
        set => _look = value;
    }

    public double Measure(ExecutionModes modes, double height) => Measure(EffortCapsule.Of(modes));

    public void Paint(ID2D1DeviceContext context, ExecutionModes modes, RawRectF box) => Paint(context, EffortCapsule.Of(modes), box);

    public float Measure(EffortCapsule capsule)
    {
        if (!capsule.IsVisible)
        {
            return 0;
        }

        var look = Look;
        var parts = 0;
        var width = 0f;
        if (capsule.Level is not null)
        {
            width += (float)EffortCapsule.MeterWidth;
            parts++;
        }

        if (capsule.ShowsWord(look.Compact))
        {
            width += MathF.Ceiling(_text.Measure(capsule.Effort!, look.Word));
            parts++;
        }

        if (capsule.ShowsMark(look.Compact))
        {
            width += MathF.Ceiling(_text.Measure(look.Mark, look.MarkStyle));
            parts++;
        }

        if (capsule.Bolts > 0)
        {
            width += BoltWidth * capsule.Bolts - 3 * (capsule.Bolts - 1);
            parts++;
        }

        return parts == 0 ? 0 : 2 * look.Padding + width + look.Gap * (parts - 1);
    }

    public void Paint(ID2D1DeviceContext context, EffortCapsule capsule, RawRectF box)
    {
        if (!capsule.IsVisible)
        {
            return;
        }

        var look = Look;
        var palette = look.Palette;
        var height = box.Bottom - box.Top;
        var hc = look.HighContrast;
        if (capsule.IsConfigured)
        {
            BoardPaint.DashRound(context, box.Left, box.Top, box.Right - box.Left, height, look.Corner, hc ? palette.Ink : palette.Line,
                hc ? 1 : Math.Max(look.Stroke, (float)EffortCapsule.DashedStroke), (float)EffortCapsule.DashLength, (float)EffortCapsule.DashGap);
        }
        else
        {
            if (!hc)
            {
                BoardPaint.FillRound(context, box.Left, box.Top, box.Right - box.Left, height, look.Corner, palette.Fill);
            }

            BoardPaint.StrokeRound(context, box.Left, box.Top, box.Right - box.Left, height, look.Corner, hc ? palette.Ink : palette.Line,
                hc ? 1 : look.Stroke);
        }

        var mid = box.Top + height / 2;
        var x = box.Left + look.Padding;
        var tone = capsule.Tone;
        if (capsule.Level is { } level)
        {
            Meter(context, x, mid, level, tone);
            x += (float)EffortCapsule.MeterWidth + look.Gap;
        }

        if (capsule.ShowsWord(look.Compact))
        {
            x += Word(context, capsule.Effort!, x, mid, tone) + look.Gap;
        }

        if (capsule.ShowsMark(look.Compact))
        {
            using var ink = BoardPaint.Brush(context, hc ? palette.Ink : palette.Ink3);
            _text.Draw(context, look.Mark, look.MarkStyle, x, mid, ink);
        }

        if (capsule.Bolts > 0)
        {
            var width = BoltWidth * capsule.Bolts - 3 * (capsule.Bolts - 1);
            Bolts(context, box.Right - look.Padding - width, mid, capsule.Bolts, capsule.SpeedConfigured);
        }
    }

    private void Meter(ID2D1DeviceContext context, float x, float mid, int level, EffortTone tone)
    {
        var look = Look;
        var palette = look.Palette;
        var bottom = mid + 5;
        var radius = look.Corner < 6 ? 0.5f : (float)EffortCapsule.BarCornerRadius;
        var w = (float)EffortCapsule.BarWidth;
        float Left(int i) => x + i * (float)(EffortCapsule.BarWidth + EffortCapsule.BarSpacing);
        float Height(int i) => (float)EffortCapsule.BarHeights[i];

        // The lit bars' glows (the CSS box-shadow of each bar) as one blur of all of them: one Direct2D effect, not five.
        var (glow, blur) = look.HighContrast ? (default, 0f) : tone switch
        {
            EffortTone.TopRung => (EffortCapsulePalette.Heat(1, look.Dark).WithAlpha(look.Glows ? 1 : 0.55), look.TopGlow),
            EffortTone.Max when look.Glows && look.Dark => (EffortCapsulePalette.Heat(EffortCapsulePalette.MaxHeat, look.Dark), 5f),
            EffortTone.Neutral when look.Glows && look.Dark => (new Rgba(170 / 255.0, 200 / 255.0, 1, 0.8), 4f),
            _ => (default, 0f),
        };
        if (blur > 0 && level > 0)
        {
            BoardPaint.Glow(context, glow, blur, c =>
            {
                for (var i = 0; i < Math.Min(level, EffortCapsule.BarHeights.Count); i++)
                {
                    BoardPaint.FillRound(c, Left(i), bottom - Height(i), w, Height(i), radius, Rgba.White);
                }
            });
        }

        for (var i = 0; i < EffortCapsule.BarHeights.Count; i++)
        {
            var h = Height(i);
            var left = Left(i);
            if (i >= level)
            {
                BoardPaint.FillRound(context, left, bottom - h, w, h, radius, palette.Unlit);
                continue;
            }

            if (look.HighContrast)
            {
                BoardPaint.FillRound(context, left, bottom - h, w, h, radius, palette.Ink);
                continue;
            }

            switch (tone)
            {
                case EffortTone.TopRung:
                {
                    var (top, low) = EffortCapsulePalette.TopRungBar(h, look.Dark);
                    using var brush = BoardPaint.Gradient(context, left, bottom - h, w, h, [(0, top), (1, low)], vertical: true);
                    context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(left, bottom - h, left + w, bottom), radius, radius), brush);
                    break;
                }

                case EffortTone.Max:
                    BoardPaint.FillRound(context, left, bottom - h, w, h, radius, EffortCapsulePalette.Heat(EffortCapsulePalette.MaxHeat, look.Dark));
                    break;
                case EffortTone.Configured:
                    BoardPaint.FillRound(context, left, bottom - h, w, h, radius, palette.Ink3);
                    break;
                default:
                    // Neon's lit bars on dark: #cfe0ff (their glow is above).
                    BoardPaint.FillRound(context, left, bottom - h, w, h, radius, look.Glows && look.Dark ? Rgba.FromHex("#CFE0FF") : palette.Ink2);
                    break;
            }
        }
    }

    private float Word(ID2D1DeviceContext context, string word, float x, float mid, EffortTone tone)
    {
        var look = Look;
        var style = look.Word;
        var width = _text.Measure(word, style);
        if (look.HighContrast)
        {
            using var ink = BoardPaint.Brush(context, look.Palette.Ink);
            return _text.Draw(context, word, style, x, mid, ink);
        }

        switch (tone)
        {
            case EffortTone.TopRung:
            {
                var low = EffortCapsulePalette.Heat(EffortCapsulePalette.TopRungLow, look.Dark);
                var high = EffortCapsulePalette.Heat(EffortCapsulePalette.TopRungHigh, look.Dark);
                if (look.Glows)
                {
                    BoardPaint.Glow(context, high.WithAlpha(0.7), 4, c =>
                    {
                        using var white = BoardPaint.Brush(c, Rgba.White);
                        _text.Draw(c, word, style, x, mid, white);
                    });
                }

                using var brush = BoardPaint.Gradient(context, x, mid, Math.Max(1, width), 1, [(0, low), (1, high)]);
                return _text.Draw(context, word, style, x, mid, brush);
            }

            case EffortTone.Configured:
            {
                using var brush = BoardPaint.Brush(context, look.Palette.Ink3);
                return _text.Draw(context, word, style, x, mid, brush);
            }

            case EffortTone.Max:
            {
                var color = EffortCapsulePalette.Heat(EffortCapsulePalette.MaxHeat, look.Dark);
                if (look.Glows && look.Dark)
                {
                    BoardPaint.Glow(context, color, 6, c =>
                    {
                        using var white = BoardPaint.Brush(c, Rgba.White);
                        _text.Draw(c, word, style, x, mid, white);
                    });
                }

                using var brush = BoardPaint.Brush(context, color);
                return _text.Draw(context, word, style, x, mid, brush);
            }

            default:
            {
                using var brush = BoardPaint.Brush(context, look.Palette.Ink2);
                return _text.Draw(context, word, style, x, mid, brush);
            }
        }
    }

    private void Bolts(ID2D1DeviceContext context, float x, float mid, int count, bool configured)
    {
        var look = Look;
        var color = look.HighContrast ? look.Palette.Ink : configured ? look.Palette.Ink3 : look.Palette.AmberInk;
        var top = mid - 5;
        float Left(int i) => x + i * (BoltWidth - 3);
        double Alpha(int i) => count > 1 && i < count - 1 ? EffortCapsule.RearBoltOpacity : 1;
        if (!look.HighContrast && !configured && look.BoltGlow > 0)
        {
            // Glass: drop-shadow 2 px of amber at 45 %; neon: 3 px of amber; the rear bolt's at its opacity. One effect for both.
            BoardPaint.Glow(context, Rgba.FromHex("#F5A524").WithAlpha(look.Glows ? 1 : 0.45), look.BoltGlow, c =>
            {
                for (var i = 0; i < count; i++)
                {
                    using var white = BoardPaint.Brush(c, Rgba.White.WithAlpha(Alpha(i)));
                    BoardPaint.Bolt(c, Left(i), top, BoltScale, white);
                }
            });
        }

        for (var i = 0; i < count; i++)
        {
            using var brush = BoardPaint.Brush(context, color.MultiplyAlpha(Alpha(i)));
            BoardPaint.Bolt(context, Left(i), top, BoltScale, brush);
        }
    }
}
