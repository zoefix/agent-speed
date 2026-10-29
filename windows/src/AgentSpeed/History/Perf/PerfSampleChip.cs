using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The small-sample chip (DESIGN 12): <c>history.perf.sample.low</c> after the model id of a row without a score, at 10 DIP medium in a neutral capsule, as the
/// unknown-price reason chip (DESIGN 9) — the label color at 9 % fill and a 0.5 DIP stroke at 16 %. High contrast: no
/// fill and a 1 px stroke in the text color. The chip is text for screen readers.
/// </summary>
internal sealed class PerfSampleChip : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PerfSampleChip), new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PerfSampleChip),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnForegroundChanged));

    private const double FillOpacity = 0.09;
    private const double StrokeOpacity = 0.16;

    private readonly TextBlock _label = new()
    {
        FontSize = 10,
        FontWeight = FontWeights.Medium,
        Margin = new Thickness(5, 1, 5, 1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public PerfSampleChip()
    {
        AddVisualChild(_label);
        AddLogicalChild(_label);
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override System.Collections.IEnumerator LogicalChildren => new[] { _label }.GetEnumerator();

    protected override Visual GetVisualChild(int index) => _label;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PerfHistoryAppearance.HighContrastProperty)
        {
            SetResourceReference(ForegroundProperty, (bool)e.NewValue ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return _label.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _label.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var radius = rect.Height / 2;
        var color = Foreground is SolidColorBrush solid ? solid.Color : Colors.Gray;
        if (PerfHistoryAppearance.GetHighContrast(this))
        {
            var pen = new Pen(Foreground, 1);
            pen.Freeze();
            var inner = Rect.Inflate(rect, -0.5, -0.5);
            dc.DrawRoundedRectangle(null, pen, inner, inner.Height / 2, inner.Height / 2);
            return;
        }

        dc.DrawRoundedRectangle(PerfHistoryAppearance.Brush(PerfHistoryAppearance.WithOpacity(color, FillOpacity)), null, rect, radius, radius);
        var stroke = new Pen(PerfHistoryAppearance.Brush(PerfHistoryAppearance.WithOpacity(color, StrokeOpacity)), 0.5);
        stroke.Freeze();
        var edge = Rect.Inflate(rect, -0.25, -0.25);
        dc.DrawRoundedRectangle(null, stroke, edge, edge.Height / 2, edge.Height / 2);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PerfSampleChip)d)._label.Text = e.NewValue as string ?? string.Empty;

    private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PerfSampleChip)d)._label.Foreground = e.NewValue as Brush;
}
