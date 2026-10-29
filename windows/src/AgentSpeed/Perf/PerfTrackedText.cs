using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentSpeed.Perf;

/// <summary>
/// One line of text with letter spacing, which WPF's text has none of: the card's group titles (11 DIP semibold, tracking
/// 0.06 em; Latin text upper-case at 10.5 DIP, DESIGN 12, <i>Score card</i>, 3). Tail-trimmed to its width.
/// </summary>
internal sealed class PerfTrackedText : FrameworkElement
{
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfTrackedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(PerfTrackedText),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private string _text = string.Empty;
    private double _size = 11;

    /// <summary>The tracking in ems.</summary>
    public double TrackingEm { get; set; } = 0.06;

    public FontWeight FontWeight { get; set; } = FontWeights.SemiBold;

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string Text => _text;

    /// <summary>A group title: Latin text upper-case at 10.5 DIP, any other script as written at 11.</summary>
    public void SetTitle(string text)
    {
        var latin = text.All(c => c < '⺀');
        _text = latin ? text.ToUpper(PerfCardText.Culture(this)) : text;
        _size = latin ? 10.5 : 11;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        var line = Format("Ag", Brushes.Black);
        var width = 0.0;
        foreach (var (_, advance) in Glyphs(Brushes.Black))
        {
            width += advance;
        }

        return new Size(Math.Min(Math.Ceiling(width), double.IsFinite(availableSize.Width) ? availableSize.Width : double.MaxValue), Math.Ceiling(line.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var limit = ActualWidth;
        var x = 0.0;
        var ellipsis = Format("…", Foreground);
        var glyphs = Glyphs(Foreground).ToList();
        var total = glyphs.Sum(g => g.Advance);
        foreach (var (glyph, advance) in glyphs)
        {
            if (total > limit + 0.5 && x + advance + ellipsis.WidthIncludingTrailingWhitespace > limit)
            {
                dc.DrawText(ellipsis, new Point(x, 0));
                return;
            }

            dc.DrawText(glyph, new Point(x, 0));
            x += advance;
        }
    }

    private IEnumerable<(FormattedText Glyph, double Advance)> Glyphs(Brush brush)
    {
        var tracking = TrackingEm * _size;
        var elements = StringInfo.GetTextElementEnumerator(_text);
        while (elements.MoveNext())
        {
            var glyph = Format(elements.GetTextElement(), brush);
            yield return (glyph, glyph.WidthIncludingTrailingWhitespace + tracking);
        }
    }

    private FormattedText Format(string text, Brush brush) => PerfCardText.Format(this, text, FontFamily, _size, FontWeight, brush);

    private sealed class Peer(PerfTrackedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(PerfTrackedText);

        protected override string GetNameCore() => ((PerfTrackedText)Owner).Text;
    }
}
