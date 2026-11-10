using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// A quota text that crossfades (0.2 s) when it changes (DESIGN 4, Quota lines): <see cref="Text"/> in the element's
/// foreground, then <see cref="Tail"/> in <see cref="TailBrush"/>, the whole trimmed at its end.
/// </summary>
internal sealed class QuotaFadeText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(QuotaFadeText), new PropertyMetadata(string.Empty, (d, _) => ((QuotaFadeText)d).Changed()));

    public static readonly DependencyProperty TailProperty = DependencyProperty.Register(
        nameof(Tail), typeof(string), typeof(QuotaFadeText), new PropertyMetadata(string.Empty, (d, _) => ((QuotaFadeText)d).Changed()));

    public static readonly DependencyProperty TailBrushProperty = DependencyProperty.Register(
        nameof(TailBrush), typeof(Brush), typeof(QuotaFadeText), new PropertyMetadata(Brushes.Gray, (d, _) => ((QuotaFadeText)d).Fill(((QuotaFadeText)d)._shown)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(QuotaFadeText), new PropertyMetadata(false));

    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(200));

    private readonly TextBlock _shown = NewText();
    private readonly TextBlock _leaving = NewText();
    private string _text = string.Empty;
    private string _tail = string.Empty;

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
        var text = Text ?? string.Empty;
        var tail = Tail ?? string.Empty;
        if (text == _text && tail == _tail)
        {
            return;
        }

        var animate = IsLoaded && IsVisible && !ReduceMotion && (_text.Length > 0 || _tail.Length > 0);
        _text = text;
        _tail = tail;
        if (animate)
        {
            _leaving.Inlines.Clear();
            foreach (var inline in _shown.Inlines.ToList())
            {
                _leaving.Inlines.Add(new Run(((Run)inline).Text) { Foreground = inline.Foreground });
            }

            _leaving.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Fade));
            _shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fade));
        }

        Fill(_shown);
    }

    private void Fill(TextBlock block)
    {
        block.Inlines.Clear();
        if (_text.Length > 0)
        {
            block.Inlines.Add(new Run(_text));
        }

        if (_tail.Length > 0)
        {
            block.Inlines.Add(new Run(_tail) { Foreground = TailBrush });
        }
    }
}
