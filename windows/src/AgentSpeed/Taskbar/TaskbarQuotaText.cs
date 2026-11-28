using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Theme;

namespace AgentSpeed.Taskbar;

/// <summary>
/// Draws the taskbar 額度 block (DESIGN 10, Menu bar and taskbar, <i>Quota</i>): <c>5h 68%</c> over <c>7d 42%</c> in 10 px medium
/// tabular text, the labels left-aligned in a column, the percents right-aligned in a cell at least as wide as <c>100%</c>, both
/// cells as the host's governor holds them (wider at once, narrower after 6 s). Labels and <c>≈</c> in the secondary color; a
/// number in the label color at 85 %, pale while old or signed out, red from 95 % used (<c>#C42B1C</c> on a light taskbar,
/// <c>#FF99A4</c> on a dark one). High contrast as the intelligence block: numbers in the text color, the rest in gray text.
/// One line is centred alone. Runs on the taskbar thread.
/// </summary>
internal sealed class TaskbarQuotaText
{
    public const double TextSize = TaskbarIntelligenceText.TextSize;

    public const double Pitch = TaskbarIntelligenceText.Pitch;

    /// <summary>Before the block.</summary>
    public const double Gap = TaskbarIntelligenceText.Gap;

    /// <summary>The estimate's mark, in the labels' color.</summary>
    private const char Approx = '≈';

    private static readonly Color CriticalLight = Color.FromRgb(0xC4, 0x2B, 0x1C);
    private static readonly Color CriticalDark = Color.FromRgb(0xFF, 0x99, 0xA4);

    private readonly Dictionary<(char, BarQuotaInk), FormattedText> _glyphs = new();
    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);
    private Typeface _face = Face(Presentation.Localization.LanguageCodes.ZhHant);
    private string _language = Presentation.Localization.LanguageCodes.ZhHant;
    private double _scale = -1;
    private Color _primary;
    private Color _secondary;
    private bool _highContrast;
    private bool _dark;
    private double _digitWidth;

    /// <summary>The block's height (vertical taskbars stack it under the value).</summary>
    public static double Height => Pitch * 2 + 2;

    /// <summary>The cells (DIP) the drawn texts need now, for the host's governor.</summary>
    public BarQuotaCells Needed(BarQuotaBlock block, double scale, Color primary, Color secondary, bool highContrast, bool dark)
    {
        Ensure(block.LanguageCode, scale, primary, secondary, highContrast, dark);
        return block.Cells(text => Text(text).WidthIncludingTrailingWhitespace, Tabular);
    }

    /// <summary>The block's width with its gap, its cells at least <paramref name="held"/>.</summary>
    public double Width(BarQuotaBlock block, BarQuotaCells held, double scale, Color primary, Color secondary, bool highContrast, bool dark) =>
        Gap + Needed(block, scale, primary, secondary, highContrast, dark).Max(held).Width;

    /// <summary>Draws the block starting at <paramref name="x"/> (its gap included), the lines centred on <paramref name="centerY"/>.</summary>
    public void Draw(DrawingContext dc, BarQuotaBlock block, double x, double centerY, BarQuotaCells held, double scale, Color primary,
        Color secondary, bool highContrast, bool dark)
    {
        var cells = Needed(block, scale, primary, secondary, highContrast, dark).Max(held);
        var left = x + Gap;
        var numberRight = left + cells.Width;
        var capHeight = (_face.CapsHeight > 0 ? _face.CapsHeight : 0.7) * TextSize;
        var baseline = Snap(block.FirstBaseline(centerY, capHeight, Pitch), scale);
        foreach (var line in block.Lines.Take(2))
        {
            DrawLine(dc, line, left, numberRight, baseline, scale);
            baseline += Pitch;
        }
    }

    private void DrawLine(DrawingContext dc, BarQuotaLine line, double left, double numberRight, double baseline, double scale)
    {
        var label = Text(line.Label);
        dc.DrawText(label, new Point(Snap(left, scale), baseline - label.Baseline));
        var ink = BarQuotaBlock.InkOf(number: true, line.Pale, line.Critical, _highContrast);
        var mark = BarQuotaBlock.InkOf(number: false, pale: false, critical: false, _highContrast);
        var x = numberRight - Tabular(line.Text);
        foreach (var c in line.Text)
        {
            var glyph = Glyph(c, c == Approx ? mark : ink);
            var width = char.IsDigit(c) ? _digitWidth : glyph.WidthIncludingTrailingWhitespace;
            dc.DrawText(glyph, new Point(Snap(x + (width - glyph.WidthIncludingTrailingWhitespace) / 2, scale), baseline - glyph.Baseline));
            x += width;
        }
    }

    private double Tabular(string text)
    {
        var width = 0.0;
        foreach (var c in text)
        {
            width += char.IsDigit(c) ? _digitWidth : Glyph(c, BarQuotaInk.Number).WidthIncludingTrailingWhitespace;
        }

        return width;
    }

    /// <summary>A label in the secondary color (high contrast: the gray-text color).</summary>
    private FormattedText Text(string text)
    {
        if (!_texts.TryGetValue(text, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, TextSize,
                Brush(InkColor(BarQuotaBlock.InkOf(number: false, pale: false, critical: false, _highContrast))), null, TextFormattingMode.Ideal,
                _scale);
            if (_texts.Count > 32)
            {
                _texts.Clear();
            }

            _texts[text] = formatted;
        }

        return formatted;
    }

    private FormattedText Glyph(char c, BarQuotaInk ink)
    {
        if (!_glyphs.TryGetValue((c, ink), out var text))
        {
            text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, TextSize,
                Brush(InkColor(ink)), null, TextFormattingMode.Ideal, _scale);
            _glyphs[(c, ink)] = text;
        }

        return text;
    }

    /// <summary>The palette's color of an ink; in high contrast the palette's secondary is the text color, so gray text is the system's.</summary>
    private Color InkColor(BarQuotaInk ink) => ink switch
    {
        BarQuotaInk.Text => _primary,
        BarQuotaInk.Number => Color.FromArgb((byte)Math.Round(_primary.A * BarIntelligenceBlock.LiveOpacity), _primary.R, _primary.G, _primary.B),
        BarQuotaInk.Critical => _dark ? CriticalDark : CriticalLight,
        BarQuotaInk.GrayText => SystemColors.GrayTextColor,
        _ => _secondary,
    };

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Ensure(string language, double scale, Color primary, Color secondary, bool highContrast, bool dark)
    {
        if (Math.Abs(scale - _scale) < 0.001 && primary == _primary && secondary == _secondary && language == _language
            && highContrast == _highContrast && dark == _dark)
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
        _dark = dark;
        _glyphs.Clear();
        _texts.Clear();
        _digitWidth = 0;
        for (var c = '0'; c <= '9'; c++)
        {
            _digitWidth = Math.Max(_digitWidth, Glyph(c, BarQuotaInk.Number).WidthIncludingTrailingWhitespace);
        }
    }

    /// <summary>The UI face with the language's CJK fallback, so 額度 and 月 take the right glyph forms.</summary>
    private static Typeface Face(string language) => new(AppFonts.Ui(language), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
