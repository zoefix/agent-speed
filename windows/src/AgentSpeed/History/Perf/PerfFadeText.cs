using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentSpeed.Presentation.History.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>
/// A value that crossfades (0.2 s) into its next text, weight and color, also with Reduce Motion (DESIGN 12,
/// <i>Motion</i>): the label color, secondary for a row without a score (<see cref="Secondary"/>), tertiary for an absent
/// value (<see cref="Absent"/>, a <c>–</c> whose tooltip says why).
/// </summary>
internal sealed class PerfFadeText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PerfFadeText), new FrameworkPropertyMetadata(string.Empty, OnShownChanged));

    /// <summary>The value is absent: a tertiary <c>–</c>.</summary>
    public static readonly DependencyProperty AbsentProperty = DependencyProperty.Register(
        nameof(Absent), typeof(bool), typeof(PerfFadeText), new FrameworkPropertyMetadata(false, OnShownChanged));

    /// <summary>The text reads in secondary (a small sample's values).</summary>
    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(bool), typeof(PerfFadeText), new FrameworkPropertyMetadata(false, OnShownChanged));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(PerfFadeText), new FrameworkPropertyMetadata(TextAlignment.Left, OnLayoutChanged));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(PerfFadeText), new FrameworkPropertyMetadata(TextWrapping.NoWrap, OnLayoutChanged));

    /// <summary>A block line height (as the summary cards' large values), NaN for the font's own.</summary>
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(PerfFadeText), new FrameworkPropertyMetadata(double.NaN, OnLayoutChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(PerfFadeText));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(PerfFadeText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits, OnShownChanged));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfFadeText));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(PerfFadeText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits, OnShownChanged));

    private readonly TextBlock _front = new();
    private readonly TextBlock _back = new() { Opacity = 0 };
    private bool _captured;
    private int _fades;

    public PerfFadeText()
    {
        foreach (var block in new[] { _back, _front })
        {
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            AddVisualChild(block);
            AddLogicalChild(block);
        }
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool Absent
    {
        get => (bool)GetValue(AbsentProperty);
        set => SetValue(AbsentProperty, value);
    }

    public bool Secondary
    {
        get => (bool)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

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

    /// <summary>What the element shows now, as a screen reader says it.</summary>
    public string Spoken => Text ?? string.Empty;

    protected override int VisualChildrenCount => 2;

    protected override System.Collections.IEnumerator LogicalChildren => new[] { _back, _front }.GetEnumerator();

    protected override Visual GetVisualChild(int index) => index == 0 ? _back : _front;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        _front.Measure(availableSize);
        _back.Measure(availableSize);
        return new Size(Math.Max(_front.DesiredSize.Width, _back.Opacity > 0 ? _back.DesiredSize.Width : 0), _front.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        _front.Arrange(rect);
        _back.Arrange(rect);
        return finalSize;
    }

    // A hit target over the whole cell, so the value's tooltip opens anywhere in it.
    protected override void OnRender(DrawingContext drawingContext) =>
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PerfFadeText)d).Update();

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PerfFadeText)d;
        foreach (var block in new[] { view._front, view._back })
        {
            block.TextAlignment = view.TextAlignment;
            block.TextWrapping = view.TextWrapping;
            if (double.IsFinite(view.LineHeight) && view.LineHeight > 0)
            {
                block.LineHeight = view.LineHeight;
                block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
            else
            {
                block.ClearValue(TextBlock.LineHeightProperty);
                block.ClearValue(TextBlock.LineStackingStrategyProperty);
            }
        }
    }

    private void Update()
    {
        var text = Text ?? string.Empty;
        var (weight, brushKey) = Look();
        if (_front.Text == text && _front.FontWeight == (weight ?? FontWeight) && Equals(_front.Tag, brushKey))
        {
            return;
        }

        if (IsLoaded && IsVisible && !string.IsNullOrEmpty(_front.Text))
        {
            if (!_captured)
            {
                // Several properties change in one update: the old look is taken once, at the first.
                _captured = true;
                _back.Text = _front.Text;
                _back.FontWeight = _front.FontWeight;
                _back.Foreground = _front.Foreground;
                var fade = ++_fades;
                var fadeOut = new DoubleAnimation(1, 0, PerfHistoryMotion.Fade) { EasingFunction = PerfHistoryMotion.FadeEasing };
                fadeOut.Completed += (_, _) =>
                {
                    if (fade == _fades)
                    {
                        _back.Text = string.Empty;
                        InvalidateMeasure();
                    }
                };
                _back.BeginAnimation(OpacityProperty, fadeOut);
                _front.BeginAnimation(OpacityProperty, PerfHistoryMotion.FadeTo(0, 1));
                Dispatcher.BeginInvoke(() => _captured = false, DispatcherPriority.Background);
            }
        }
        else
        {
            _back.BeginAnimation(OpacityProperty, null);
            _back.Opacity = 0;
            _back.Text = string.Empty;
            _front.BeginAnimation(OpacityProperty, null);
        }

        _front.Text = text;
        _front.Tag = brushKey;
        if (weight is { } strong)
        {
            _front.FontWeight = strong;
        }
        else
        {
            _front.ClearValue(TextBlock.FontWeightProperty);
        }

        if (brushKey is null)
        {
            _front.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _front.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        InvalidateMeasure();
    }

    private (FontWeight? Weight, string? BrushKey) Look() =>
        Absent ? (null, "TextFillColorTertiaryBrush") : Secondary ? (null, "TextFillColorSecondaryBrush") : (null, null);

    private sealed class Peer(PerfFadeText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(PerfFadeText);

        protected override string GetNameCore()
        {
            var name = AutomationProperties.GetName(owner);
            return string.IsNullOrEmpty(name) ? owner.Spoken : name;
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsControlElementCore() => true;
    }
}
