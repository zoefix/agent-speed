using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentSpeed.Perf;

/// <summary>
/// A number laid out the way DESIGN 12 sets the card's score: every digit centred in one tabular cell (the widest digit's
/// advance), other characters — the grouping comma, <c>k</c>, <c>–</c> — at their own advance, and a tracking between
/// characters. Drawn glyph by glyph, so it holds in any font and never shifts while a count runs.
/// </summary>
internal sealed class PerfTabularText
{
    private readonly Dictionary<char, FormattedText> _glyphs = new();
    private readonly Typeface _typeface;
    private readonly double _size;
    private readonly double _pixelsPerDip;
    private readonly Brush _brush;
    private double _digit = -1;

    public PerfTabularText(FontFamily family, FontWeight weight, double size, double pixelsPerDip, Brush brush)
    {
        _typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        _size = size;
        _pixelsPerDip = pixelsPerDip;
        _brush = brush;
    }

    /// <summary>The tabular cell of a digit.</summary>
    public double Digit
    {
        get
        {
            if (_digit < 0)
            {
                _digit = 0;
                for (var c = '0'; c <= '9'; c++)
                {
                    _digit = Math.Max(_digit, Glyph(c).WidthIncludingTrailingWhitespace);
                }
            }

            return _digit;
        }
    }

    /// <summary>The distance from the line's top to its baseline.</summary>
    public double Baseline => Glyph('0').Baseline;

    public double Height => Glyph('0').Height;

    /// <summary>The cap height below the baseline's top, for centring things on the digits.</summary>
    public double CapHeight => _typeface.TryGetGlyphTypeface(out var face) ? face.CapsHeight * _size : 0.7 * _size;

    /// <summary>The advance width of <paramref name="text"/> with <paramref name="tracking"/> DIP between characters.</summary>
    public double Width(string text, double tracking)
    {
        var width = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            width += Advance(text[i]) + (i > 0 ? tracking : 0);
        }

        return width;
    }

    /// <summary>Draws <paramref name="text"/> from <paramref name="left"/> with its baseline at <paramref name="baseline"/>; returns the width.</summary>
    public double Draw(DrawingContext dc, string text, double left, double baseline, double tracking)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var x = left;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var glyph = Glyph(c);
            var cell = Advance(c);
            dc.DrawText(glyph, new Point(x + (cell - glyph.WidthIncludingTrailingWhitespace) / 2, baseline - glyph.Baseline));
            x += cell + (i < text.Length - 1 ? tracking : 0);
        }

        return x - left;
    }

    private double Advance(char c) => char.IsAsciiDigit(c) ? Digit : Glyph(c).WidthIncludingTrailingWhitespace;

    private FormattedText Glyph(char c)
    {
        if (!_glyphs.TryGetValue(c, out var glyph))
        {
            glyph = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _size, _brush, _pixelsPerDip);
            _glyphs[c] = glyph;
        }

        return glyph;
    }
}
