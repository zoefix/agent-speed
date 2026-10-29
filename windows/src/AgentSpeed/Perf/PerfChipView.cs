using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// A chip of the card (DESIGN 12, <i>Chips</i>): 18 DIP tall, fully rounded, 7 DIP side padding, 11 DIP semibold tabular
/// text. The usual chip is mint above the usual value, neutral (secondary text on the label color at 6 %) at or below it,
/// and outlined (a 1 DIP dotted outline, no fill) without a baseline; the provisional chip is outlined with a ring of 8
/// dots before its text. High contrast: the text color, a 1 px solid stroke, no fill. Read as text.
/// </summary>
internal sealed class PerfChipView : FrameworkElement
{
    public const double ChipHeight = 18;
    public const double Padding = 7;
    public const double GlyphSize = 10;
    public const double GlyphGap = 4;
    public const double TextSize = 11;

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfChipView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        "Secondary", typeof(Brush), typeof(PerfChipView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private PerfChip? _chip;
    private bool _dotRing;
    private PerfCardAppearance _appearance;

    public PerfChipView()
    {
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(SecondaryProperty, PerfCardAppearance.SecondaryKey);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public string Text => _chip?.Text ?? string.Empty;

    /// <param name="dotRing">The provisional chip's ring of 8 dots before the text.</param>
    public void Apply(PerfChip? chip, PerfCardAppearance appearance, bool dotRing = false)
    {
        if (chip == _chip && appearance == _appearance && dotRing == _dotRing)
        {
            return;
        }

        _chip = chip;
        _appearance = appearance;
        _dotRing = dotRing;
        Visibility = chip is null ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChipPeer(this);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_chip is not { } chip)
        {
            return default;
        }

        var text = Label(chip.Text, Brushes.Black);
        var glyph = _dotRing ? GlyphSize + GlyphGap : 0;
        return new Size(Math.Ceiling(Padding * 2 + glyph + text.WidthIncludingTrailingWhitespace), ChipHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_chip is not { } chip)
        {
            return;
        }

        var ink = _appearance.ChipText(chip.Tone) ?? (Brush?)GetValue(SecondaryProperty) ?? Brushes.Gray;
        var rect = new Rect(0, 0, ActualWidth, ChipHeight);
        var radius = ChipHeight / 2;
        var outline = new Rect(0.5, 0.5, Math.Max(0, rect.Width - 1), ChipHeight - 1);
        if (_appearance.HighContrast)
        {
            var pen = new Pen(ink, 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, outline, radius - 0.5, radius - 0.5);
        }
        else if (chip.Tone == PerfChipTone.Outlined)
        {
            dc.DrawRoundedRectangle(null, PerfMarkDrawing.DottedPen(_appearance.ChipOutline, 1), outline, radius - 0.5, radius - 0.5);
        }
        else
        {
            dc.DrawRoundedRectangle(_appearance.ChipFill(chip.Tone), null, rect, radius, radius);
        }

        var x = Padding;
        var middle = ChipHeight / 2;
        if (_dotRing)
        {
            DotRing(dc, new Point(x + GlyphSize / 2, middle), ink);
            x += GlyphSize + GlyphGap;
        }

        var text = Label(chip.Text, ink);
        dc.DrawText(text, new Point(x, middle - text.Height / 2));
    }

    /// <summary>The provisional chip's glyph: a drawn ring of 8 dots, the provisional mark's dotted language.</summary>
    private static void DotRing(DrawingContext dc, Point center, Brush brush)
    {
        const double ring = 3.6;
        const double dot = 0.8;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4 - Math.PI / 2;
            dc.DrawEllipse(brush, null, new Point(center.X + ring * Math.Cos(angle), center.Y + ring * Math.Sin(angle)), dot, dot);
        }
    }

    private FormattedText Label(string text, Brush brush) => PerfCardText.Format(this, text, FontFamily, TextSize, FontWeights.SemiBold, brush);

    private sealed class ChipPeer(PerfChipView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(PerfChipView);

        protected override string GetNameCore() => ((PerfChipView)Owner).Text;
    }
}
