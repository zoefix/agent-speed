using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// The card's scale (DESIGN 12, <i>Score card</i>, 2): the content width is the log scale. A 6 DIP track, radius 3; the
/// fill from the left to <c>f</c> of the shown value with a round end, painted with the pearl gradient laid across the
/// whole track; ticks at 500, 1000 and 2000 (the reference 1000 stronger and 4 taller) and their labels 4 under the track,
/// <c>4000</c> right-aligned to the end. A provisional fill is dotted, a resting fill at 45 %, none draws the track alone.
/// It follows the number's count frame by frame.
/// </summary>
internal sealed class PerfScaleView : FrameworkElement
{
    public const double TrackHeight = 6;
    public const double TickExtra = 4;
    public const double ReferenceExtra = 4;
    public const double LabelGap = 4;
    public const double LabelSize = 9.5;

    private const int ReferenceTick = 1000;

    private static readonly int[] Ticks = [500, ReferenceTick, 2000];

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfScaleView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty TertiaryProperty = DependencyProperty.Register(
        "Tertiary", typeof(Brush), typeof(PerfScaleView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        "Secondary", typeof(Brush), typeof(PerfScaleView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private PerfNumberView? _number;
    private PerfCardAppearance _appearance;
    private PerfNumberTone _tone = PerfNumberTone.None;
    private bool _provisional;

    public PerfScaleView()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        SetResourceReference(TertiaryProperty, PerfCardAppearance.TertiaryKey);
        SetResourceReference(SecondaryProperty, PerfCardAppearance.SecondaryKey);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>The number whose shown value the fill follows.</summary>
    public PerfNumberView? Number
    {
        get => _number;
        set
        {
            if (_number is not null)
            {
                _number.Frame -= OnFrame;
            }

            _number = value;
            if (value is not null)
            {
                value.Frame += OnFrame;
            }
        }
    }

    public void Apply(PerfNumberTone tone, bool provisional, PerfCardAppearance appearance)
    {
        _tone = tone;
        _provisional = provisional;
        _appearance = appearance;
        InvalidateVisual();
    }

    /// <summary>The scale is a drawn twin of the number, which carries the words.</summary>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Label("0", Brushes.Black, FontWeights.Normal);
        return new Size(0, Math.Ceiling(ReferenceExtra / 2 + TickExtra / 2 + TrackHeight + LabelGap + label.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        // The reference tick reaches 4 above the others, so the track starts that far down.
        var top = (TickExtra + ReferenceExtra) / 2;
        var track = new Rect(0, top, width, TrackHeight);
        var radius = TrackHeight / 2;
        if (_appearance.OutlinedTracks)
        {
            var pen = new Pen(_appearance.Track, 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, Inset(track, 0.5), radius - 0.5, radius - 0.5);
        }
        else
        {
            dc.DrawRoundedRectangle(_appearance.Track, null, track, radius, radius);
        }

        var shown = _number?.ShownNow;
        if (shown is { } value && _tone != PerfNumberTone.None)
        {
            var fill = Math.Clamp(PerfScale.Fraction(value), 0, 1) * width;
            var pearl = _appearance.Pearl(width);
            dc.PushOpacity(_tone == PerfNumberTone.Resting && !_appearance.HighContrast ? PerfColors.RestingFill : 1);
            if (_provisional)
            {
                PerfMarkDrawing.DottedRow(dc, new Rect(0, top, Math.Max(fill, TrackHeight), TrackHeight), pearl);
            }
            else if (fill > 0)
            {
                dc.DrawRoundedRectangle(pearl, null, new Rect(0, top, Math.Max(fill, TrackHeight), TrackHeight), radius, radius);
            }

            dc.Pop();
        }

        foreach (var tick in Ticks)
        {
            var reference = tick == ReferenceTick;
            var x = Math.Round(PerfScale.Fraction(tick) * width) - 0.5;
            var height = TrackHeight + TickExtra + (reference ? ReferenceExtra : 0);
            var brush = _appearance.Tick(reference ? PerfColors.ReferenceTickOpacity : PerfColors.TickOpacity);
            dc.DrawRectangle(brush, null, new Rect(x, top + TrackHeight / 2 - height / 2, 1, height));
        }

        var labelTop = top + TrackHeight + LabelGap;
        foreach (var tick in Ticks)
        {
            var reference = tick == ReferenceTick;
            var label = Label(tick.ToString(System.Globalization.CultureInfo.InvariantCulture), Ink(reference), reference ? FontWeights.SemiBold : FontWeights.Normal);
            var x = Math.Round(PerfScale.Fraction(tick) * width);
            dc.DrawText(label, new Point(x - label.WidthIncludingTrailingWhitespace / 2, labelTop));
        }

        var end = Label(PerfCard.ScaleEnd.ToString(System.Globalization.CultureInfo.InvariantCulture), Ink(false), FontWeights.Normal);
        dc.DrawText(end, new Point(width - end.WidthIncludingTrailingWhitespace, labelTop));
    }

    private Brush Ink(bool reference) => _appearance.HighContrast
        ? reference ? SystemColors.WindowTextBrush : SystemColors.GrayTextBrush
        : (Brush?)GetValue(reference ? SecondaryProperty : TertiaryProperty) ?? Brushes.Gray;

    private FormattedText Label(string text, Brush brush, FontWeight weight) =>
        PerfCardText.Format(this, text, FontFamily, LabelSize, weight, brush);

    private static Rect Inset(Rect rect, double by) => new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - 2 * by), Math.Max(0, rect.Height - 2 * by));

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();
}
