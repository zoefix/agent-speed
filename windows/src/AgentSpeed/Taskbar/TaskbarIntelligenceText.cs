using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Taskbar;

/// <summary>
/// Draws the taskbar 性能 block (DESIGN 10, Menu bar and taskbar): <c>性能 1,286</c> over <c>思考 128 tok/s</c> in 10 px
/// medium tabular text, labels left-aligned in a column, the thinking rate right-aligned in a cell as wide as the wider of
/// <c>8.88k</c> and <c>8,888</c> with the unit after it, and the score ending where that unit ends, so both lines share
/// the block's right edge. A live score and a thinking rate in the label color at 85 %;
/// labels, the unit, a resting score, <c>–</c> and a zero rate in the secondary color. High contrast follows the orb readout
/// for every part (<see cref="BarIntelligenceBlock.InkOf"/>): live numbers in the text color, everything else in the
/// gray-text color. A provisional score sits on the dotted provisional mark (DESIGN 12). With the thinking line hidden at
/// zero, line 1 alone, centred in the same cells. Runs on the taskbar thread.
/// </summary>
internal sealed class TaskbarIntelligenceText
{
    public const double TextSize = 10;

    /// <summary>Baseline distance of the two lines, as the money and traffic blocks.</summary>
    public const double Pitch = 12;

    /// <summary>Before the block.</summary>
    public const double Gap = 8;

    private readonly Dictionary<(char, int), FormattedText> _glyphs = new();
    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);
    private Typeface _face = Face(Presentation.Localization.LanguageCodes.ZhHant);
    private string _language = Presentation.Localization.LanguageCodes.ZhHant;
    private double _scale = -1;
    private Color _primary;
    private Color _secondary;
    private bool _highContrast;
    private double _digitWidth;

    /// <summary>The block's width with its gap; it depends on the language only below 1 M tok/s.</summary>
    public double Width(BarIntelligenceBlock block, double scale, Color primary, Color secondary, bool highContrast)
    {
        Ensure(block.LanguageCode, scale, primary, secondary, highContrast);
        return Gap + Cells(block).Width;
    }

    /// <summary>The block's height (vertical taskbars stack it under the value).</summary>
    public static double Height => Pitch * 2 + 2;

    /// <summary>
    /// Draws the block starting at <paramref name="x"/> (its gap included), the two lines (or line 1 alone) centered on
    /// <paramref name="centerY"/>.
    /// </summary>
    public void Draw(DrawingContext dc, BarIntelligenceBlock block, double x, double centerY, double scale, Color primary, Color secondary,
        bool highContrast)
    {
        Ensure(block.LanguageCode, scale, primary, secondary, highContrast);
        var cells = Cells(block);
        var left = x + Gap;
        var numberRight = left + cells.Label + BarIntelligenceCells.LabelGap + cells.Number;
        var scoreRight = left + cells.ScoreRight;
        var capHeight = (_face.CapsHeight > 0 ? _face.CapsHeight : 0.7) * TextSize;
        var first = Snap(block.FirstBaseline(centerY, capHeight, Pitch), scale);
        DrawLine(dc, block.Label, block.Index, block.IndexDim, null, left, scoreRight, first, scale);
        if (block.Provisional)
        {
            var width = Tabular(block.Index, Role(block.IndexDim));
            ProvisionalMark(dc, block.ProvisionalDots(scoreRight - width, width, first, TextSize, scale, highContrast), block.IndexDim);
        }

        if (!block.ThinkingHidden)
        {
            DrawLine(dc, block.ThinkingLabel, block.Thinking, block.ThinkingDim, block.Unit, left, numberRight, first + Pitch, scale);
        }
    }

    /// <summary>The dots in the number's color at 70 % (high contrast: 100 % and thicker, from <see cref="BarIntelligenceBlock.ProvisionalDots"/>).</summary>
    private void ProvisionalMark(DrawingContext dc, IReadOnlyList<BarDot> dots, bool dim)
    {
        if (dots.Count == 0)
        {
            return;
        }

        var color = NumberColor(Role(dim));
        var brush = new SolidColorBrush(_highContrast ? color : Color.FromArgb((byte)Math.Round(color.A * PerfProvisionalMark.Opacity), color.R, color.G, color.B));
        brush.Freeze();
        foreach (var dot in dots)
        {
            var r = dot.Diameter / 2;
            dc.DrawEllipse(brush, null, new Point(dot.X + r, dot.Y + r), r, r);
        }
    }

    private static int Role(bool dim) => dim ? 1 : 0;

    private void DrawLine(DrawingContext dc, string label, string number, bool dim, string? unit, double left, double numberRight,
        double baseline, double scale)
    {
        var labelText = Text(label);
        dc.DrawText(labelText, new Point(Snap(left, scale), baseline - labelText.Baseline));
        var role = Role(dim);
        var x = numberRight - Tabular(number, role);
        foreach (var c in number)
        {
            var glyph = Glyph(c, role);
            var width = char.IsDigit(c) ? _digitWidth : glyph.WidthIncludingTrailingWhitespace;
            dc.DrawText(glyph, new Point(Snap(x + (width - glyph.WidthIncludingTrailingWhitespace) / 2, scale), baseline - glyph.Baseline));
            x += width;
        }

        if (unit is not null)
        {
            var unitText = Text(unit);
            dc.DrawText(unitText, new Point(Snap(numberRight + BarIntelligenceCells.UnitGap, scale), baseline - unitText.Baseline));
        }
    }

    private BarIntelligenceCells Cells(BarIntelligenceBlock block) =>
        block.Cells(text => Text(text).WidthIncludingTrailingWhitespace, number => Tabular(number, 0));

    private double Tabular(string text, int role)
    {
        var width = 0.0;
        foreach (var c in text)
        {
            width += char.IsDigit(c) ? _digitWidth : Glyph(c, role).WidthIncludingTrailingWhitespace;
        }

        return width;
    }

    /// <summary>A label or the unit in the secondary color (high contrast: the gray-text color).</summary>
    private FormattedText Text(string text)
    {
        if (!_texts.TryGetValue(text, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, TextSize,
                new SolidColorBrush(InkColor(BarIntelligenceBlock.InkOf(number: false, dim: false, _highContrast))), null, TextFormattingMode.Ideal, _scale);
            if (_texts.Count > 32)
            {
                _texts.Clear();
            }

            _texts[text] = formatted;
        }

        return formatted;
    }

    /// <summary>A number character in its role's color (<see cref="NumberColor"/>).</summary>
    private FormattedText Glyph(char c, int role)
    {
        if (!_glyphs.TryGetValue((c, role), out var text))
        {
            text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, TextSize,
                new SolidColorBrush(NumberColor(role)), null, TextFormattingMode.Ideal, _scale);
            _glyphs[(c, role)] = text;
        }

        return text;
    }

    /// <summary>Role 0 (live) or 1 (resting, <c>–</c>, a zero rate) of a number, by <see cref="BarIntelligenceBlock.InkOf"/>.</summary>
    private Color NumberColor(int role) => InkColor(BarIntelligenceBlock.InkOf(number: true, dim: role != 0, _highContrast));

    /// <summary>The palette's color of an ink; in high contrast the palette's secondary is the text color, so gray text is the system's.</summary>
    private Color InkColor(BarIntelligenceInk ink) => ink switch
    {
        BarIntelligenceInk.Text => _primary,
        BarIntelligenceInk.Number => Color.FromArgb((byte)Math.Round(_primary.A * BarIntelligenceBlock.LiveOpacity), _primary.R,
            _primary.G, _primary.B),
        BarIntelligenceInk.GrayText => SystemColors.GrayTextColor,
        _ => _secondary,
    };

    private void Ensure(string language, double scale, Color primary, Color secondary, bool highContrast)
    {
        if (Math.Abs(scale - _scale) < 0.001 && primary == _primary && secondary == _secondary && language == _language
            && highContrast == _highContrast)
        {
            return;
        }

        if (language != _language)
        {
            _language = language;
            _face = Face(language);
        }

        _scale = scale;
        _primary = primary;
        _secondary = secondary;
        _highContrast = highContrast;
        _glyphs.Clear();
        _texts.Clear();
        _digitWidth = 0;
        for (var c = '0'; c <= '9'; c++)
        {
            _digitWidth = Math.Max(_digitWidth, Glyph(c, 0).WidthIncludingTrailingWhitespace);
        }
    }

    /// <summary>The UI face with the language's CJK fallback, so 性能 / 思考 take the right glyph forms.</summary>
    private static Typeface Face(string language) => new(AppFonts.Ui(language), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
