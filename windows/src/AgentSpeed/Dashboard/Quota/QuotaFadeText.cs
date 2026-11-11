using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// A quota text that crossfades (0.2 s) when it changes (DESIGN 4, Quota lines): <see cref="Lead"/> in <see cref="LeadBrush"/>
/// and <see cref="LeadGap"/> after it (the <c>≈</c> of an extrapolated percent, kerned to the digits), <see cref="Text"/> in
/// the element's foreground, then <see cref="Tail"/> in <see cref="TailBrush"/>, the whole trimmed at its end. The lead and
/// the tail fade with the text they belong to.
/// </summary>
internal sealed class QuotaFadeText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(QuotaFadeText), new PropertyMetadata(string.Empty, (d, _) => ((QuotaFadeText)d).Changed()));

    public static readonly DependencyProperty TailProperty = DependencyProperty.Register(
        nameof(Tail), typeof(string), typeof(QuotaFadeText), new PropertyMetadata(string.Empty, (d, _) => ((QuotaFadeText)d).Changed()));

    public static readonly DependencyProperty TailBrushProperty = DependencyProperty.Register(
        nameof(TailBrush), typeof(Brush), typeof(QuotaFadeText), new PropertyMetadata(Brushes.Gray, (d, _) => ((QuotaFadeText)d).Refill()));

    public static readonly DependencyProperty LeadProperty = DependencyProperty.Register(
        nameof(Lead), typeof(string), typeof(QuotaFadeText), new PropertyMetadata(string.Empty, (d, _) => ((QuotaFadeText)d).Changed()));

    public static readonly DependencyProperty LeadBrushProperty = DependencyProperty.Register(
        nameof(LeadBrush), typeof(Brush), typeof(QuotaFadeText), new PropertyMetadata(Brushes.Gray, (d, _) => ((QuotaFadeText)d).Refill()));

    public static readonly DependencyProperty LeadGapProperty = DependencyProperty.Register(
        nameof(LeadGap), typeof(double), typeof(QuotaFadeText), new PropertyMetadata(1.0, (d, _) => ((QuotaFadeText)d).Refill()));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(QuotaFadeText), new PropertyMetadata(false));

    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(200));

    private readonly TextBlock _shown = NewText();
    private readonly TextBlock _leaving = NewText();
    private (string Lead, string Text, string Tail) _value = (string.Empty, string.Empty, string.Empty);

    public QuotaFadeText()
    {
        _leaving.Opacity = 0;
        _leaving.IsHitTestVisible = false;
        Children.Add(_leaving);
        Children.Add(_shown);
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public string Tail { get => (string)GetValue(TailProperty); set => SetValue(TailProperty, value); }

    public Brush TailBrush { get => (Brush)GetValue(TailBrushProperty); set => SetValue(TailBrushProperty, value); }

    public string Lead { get => (string)GetValue(LeadProperty); set => SetValue(LeadProperty, value); }

    public Brush LeadBrush { get => (Brush)GetValue(LeadBrushProperty); set => SetValue(LeadBrushProperty, value); }

    /// <summary>The space between the lead and the text, in DIPs.</summary>
    public double LeadGap { get => (double)GetValue(LeadGapProperty); set => SetValue(LeadGapProperty, value); }

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>A right-aligned text (the percent) crossfades on its right edge.</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == HorizontalAlignmentProperty)
        {
            var right = HorizontalAlignment == HorizontalAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _shown.HorizontalAlignment = right;
            _leaving.HorizontalAlignment = right;
        }
    }

    private static TextBlock NewText() => new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void Changed()
    {
        var value = (Lead ?? string.Empty, Text ?? string.Empty, Tail ?? string.Empty);
        if (value == _value)
        {
            return;
        }

        var old = _value;
        var animate = IsLoaded && IsVisible && !ReduceMotion && (old.Lead.Length > 0 || old.Text.Length > 0 || old.Tail.Length > 0);
        _value = value;
        if (animate)
        {
            Fill(_leaving, old);
            _leaving.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Fade));
            _shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fade));
        }

        Fill(_shown, _value);
    }

    /// <summary>A brush or the gap changed: the shown text takes it at once.</summary>
    private void Refill() => Fill(_shown, _value);

    private void Fill(TextBlock block, (string Lead, string Text, string Tail) value)
    {
        block.Inlines.Clear();
        if (value.Lead.Length > 0)
        {
            block.Inlines.Add(new Run(value.Lead) { Foreground = LeadBrush });
            if (LeadGap > 0)
            {
                block.Inlines.Add(new InlineUIContainer(new FrameworkElement { Width = LeadGap }) { BaselineAlignment = BaselineAlignment.Baseline });
            }
        }

        if (value.Text.Length > 0)
        {
            block.Inlines.Add(new Run(value.Text));
        }

        if (value.Tail.Length > 0)
        {
            block.Inlines.Add(new Run(value.Tail) { Foreground = TailBrush });
        }
    }
}
