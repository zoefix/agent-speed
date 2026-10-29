using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentSpeed.Core.Perf;
using AgentSpeed.Perf;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Dashboard;

/// <summary>
/// An agent row's 性能 (DESIGN 4, 性能 column; DESIGN 12): the agent's score in compact form right-aligned in the fixed
/// cell, 12 DIP semibold tabular on the 13 DIP live value's baseline. Live in the live value's number color, resting and
/// <c>–</c> in the tertiary color (high contrast: text and gray-text), the dotted provisional mark under a provisional
/// score. A change of at least 1 % and 3 counts through every integer, up or down, with easeOutCubic timing (each step in
/// compact form); smaller changes replace the characters; none ↔ a value crossfades in 0.15 s; a change of form
/// crossfades the color in 0.2 s; Reduce Motion turns every value change into a 0.15 s crossfade.
/// </summary>
internal sealed class IntelligenceCell : Grid
{
    /// <summary>The live value's size, whose baseline the number shares.</summary>
    public const double LiveValueSize = 13;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IntelligenceCell), new PropertyMetadata(string.Empty, (d, e) => ((IntelligenceCell)d).SetText((string?)e.NewValue)));

    public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
        nameof(Form), typeof(PerfForm), typeof(IntelligenceCell),
        new PropertyMetadata(PerfForm.None, (d, _) => ((IntelligenceCell)d).ShowForm(animate: true)));

    public static readonly DependencyProperty HighContrastProperty = DependencyProperty.Register(
        nameof(HighContrast), typeof(bool), typeof(IntelligenceCell), new PropertyMetadata(false, (d, _) => ((IntelligenceCell)d).Paint()));

    /// <summary>The shown score, which plans the motion of a change (null: none).</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int?), typeof(IntelligenceCell), new PropertyMetadata(null, (d, _) => ((IntelligenceCell)d).OnValueChanged()));

    /// <summary>A score from 3 to 9 timed requests: the dotted underline (DESIGN 12, <i>Provisional mark</i>).</summary>
    public static readonly DependencyProperty ProvisionalProperty = DependencyProperty.Register(
        nameof(Provisional), typeof(bool), typeof(IntelligenceCell), new PropertyMetadata(false, (d, _) => ((IntelligenceCell)d).ShowMark(animate: true)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(IntelligenceCell), new PropertyMetadata(false));

    private static readonly DependencyProperty MarkOpacityProperty = DependencyProperty.Register(
        "MarkOpacity", typeof(double), typeof(IntelligenceCell), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration Crossfade = new(TimeSpan.FromMilliseconds(200));
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Grid _digits = new() { IsHitTestVisible = false };
    private readonly TextBlock _live = Number();
    private readonly TextBlock _quiet = Number();
    private readonly TextBlock _outgoing = Number();
    private readonly PerfTicker _ticker = new(PerfSurface.DashboardRow);
    private string _target = string.Empty;
    private bool _counting;

    public IntelligenceCell()
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        _digits.Children.Add(_quiet);
        _digits.Children.Add(_live);
        _outgoing.Opacity = 0;
        Children.Add(_digits);
        Children.Add(_outgoing);
        Paint();
        ShowForm(animate: false);
        AlignBaseline();
        Unloaded += (_, _) => StopCount();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public PerfForm Form
    {
        get => (PerfForm)GetValue(FormProperty);
        set => SetValue(FormProperty, value);
    }

    public bool HighContrast
    {
        get => (bool)GetValue(HighContrastProperty);
        set => SetValue(HighContrastProperty, value);
    }

    public int? Value
    {
        get => (int?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Provisional
    {
        get => (bool)GetValue(ProvisionalProperty);
        set => SetValue(ProvisionalProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    private bool Animates => IsLoaded && IsVisible;

    protected override AutomationPeer OnCreateAutomationPeer() => new IntelligenceCellPeer(this);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.FontFamilyProperty)
        {
            AlignBaseline();
        }
    }

    /// <summary>The dotted underline under the digits, right-aligned like them (it takes no room in the row).</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var opacity = (double)GetValue(MarkOpacityProperty);
        if (opacity <= 0 || Value is null || string.IsNullOrEmpty(_live.Text) || ActualWidth <= 0)
        {
            return;
        }

        var number = Format(_live.Text, Brushes.Black);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mark = PerfProvisionalMark.Underline(AgentIntelligenceColumn.NumberSize, number.WidthIncludingTrailingWhitespace, dpi, highContrast: HighContrast);
        var baseline = Top(_live) + number.Baseline;
        var brush = HighContrast ? SystemColors.WindowTextBrush : Ink();
        PerfMarkDrawing.Underline(dc, mark, ActualWidth - number.WidthIncludingTrailingWhitespace, baseline, brush, opacity);
    }

    private static TextBlock Number()
    {
        var text = new TextBlock
        {
            FontSize = AgentIntelligenceColumn.NumberSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private void SetText(string? text)
    {
        _target = text ?? string.Empty;
        if (!_counting)
        {
            Show(_target);
        }
    }

    private void Show(string text)
    {
        _live.Text = text;
        _quiet.Text = text;
        InvalidateVisual();
    }

    private void Paint()
    {
        if (HighContrast)
        {
            _live.Foreground = SystemColors.WindowTextBrush;
            _quiet.Foreground = SystemColors.GrayTextBrush;
            _outgoing.Foreground = Form == PerfForm.Live ? SystemColors.WindowTextBrush : SystemColors.GrayTextBrush;
            InvalidateVisual();
            return;
        }

        _live.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _quiet.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _outgoing.SetResourceReference(TextBlock.ForegroundProperty, Form == PerfForm.Live ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        InvalidateVisual();
    }

    /// <summary>The number's color now: the live text color while live, else the quiet one.</summary>
    private Brush Ink() => (Form == PerfForm.Live ? _live.Foreground : _quiet.Foreground) ?? Brushes.Gray;

    /// <summary>
    /// Centered like the live value, then lowered so both sizes share one baseline: a centered line's baseline sits
    /// (baseline − line spacing / 2) ems below the center, and a top margin lowers a centered text by half of it.
    /// </summary>
    private void AlignBaseline()
    {
        var family = TextElement.GetFontFamily(this);
        var below = family.Baseline > 0 && family.LineSpacing > 0 ? family.Baseline - family.LineSpacing / 2 : 0.414;
        var margin = new Thickness(0, 2 * below * (LiveValueSize - AgentIntelligenceColumn.NumberSize), 0, 0);
        _live.Margin = margin;
        _quiet.Margin = margin;
        _outgoing.Margin = margin;
    }

    private void ShowForm(bool animate)
    {
        var live = Form == PerfForm.Live;
        animate &= Animates;
        Fade(_live, live ? 1 : 0, animate);
        Fade(_quiet, live ? 0 : 1, animate);
        Paint();
    }

    private void ShowMark(bool animate)
    {
        var target = Provisional && Value is not null ? 1.0 : 0.0;
        BeginAnimation(MarkOpacityProperty, null);
        if (!animate || !Animates || ReduceMotion)
        {
            SetValue(MarkOpacityProperty, target);
            return;
        }

        BeginAnimation(MarkOpacityProperty, new DoubleAnimation(target, new Duration(TimeSpan.FromSeconds(PerfProvisionalMark.FadeSeconds))));
    }

    private void OnValueChanged()
    {
        ShowMark(animate: true);
        var now = Clock.Elapsed.TotalSeconds;
        _ticker.Set(Value, now, ReduceMotion, Animates);
        var change = _ticker.Change;
        switch (change.Kind)
        {
            case PerfChangeKind.Count:
                StartCount();
                break;
            case PerfChangeKind.Crossfade:
                StopCount();
                _outgoing.Text = PerfNumber.Compact(change.From);
                _outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(change.Duration))));
                _digits.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(change.Duration)))
                {
                    FillBehavior = FillBehavior.Stop,
                });
                break;
            default:
                StopCount();
                break;
        }
    }

    private void StartCount()
    {
        if (!_counting)
        {
            _counting = true;
            CompositionTarget.Rendering += OnFrame;
        }

        OnFrame(this, EventArgs.Empty);
    }

    private void StopCount()
    {
        if (_counting)
        {
            _counting = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        Show(_target);
    }

    /// <summary>Each frame of a count draws the integer it has reached, in compact form (986 gains its comma as it passes 1,000).</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        if (!_ticker.IsAnimating(now))
        {
            StopCount();
            return;
        }

        Show(PerfNumber.Compact(_ticker.Shown(now)));
    }

    private static void Fade(UIElement element, double to, bool animate)
    {
        var from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = to;
        if (!animate || Math.Abs(from - to) < 0.001)
        {
            return;
        }

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, Crossfade)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private double Top(UIElement element) => element.TranslatePoint(default, this).Y;

    private FormattedText Format(string text, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            AgentIntelligenceColumn.NumberSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>One text element named by <c>AutomationProperties.Name</c>; nothing to read without a value.</summary>
    private sealed class IntelligenceCellPeer(IntelligenceCell owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(IntelligenceCell);

        protected override bool IsControlElementCore() => HasName;

        protected override bool IsContentElementCore() => HasName;

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        private bool HasName => !string.IsNullOrEmpty(AutomationProperties.GetName(Owner));
    }
}
