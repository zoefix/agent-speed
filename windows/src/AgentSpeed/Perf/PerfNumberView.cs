using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// The score card's number (DESIGN 12, <i>Score card</i>, 2): 34 DIP semibold display face, tracking −0.02 em, tabular, in
/// full form — the label color live, secondary resting, a tertiary <c>–</c> of weight 500 while none — with the dotted
/// provisional mark under a provisional number. A change of at least 1 % and 3 counts through every integer, up or down,
/// with easeOutCubic timing; smaller changes replace the characters; none ↔ a value crossfades in 0.15 s; a change of form
/// crossfades the color in 0.2 s; Reduce Motion turns every change into a 0.15 s crossfade. Frames run only while
/// something moves; <see cref="Frame"/> tells the scale to follow.
/// </summary>
internal sealed class PerfNumberView : FrameworkElement
{
    public const double Size = 34;

    /// <summary>−0.02 em.</summary>
    public const double TrackingEm = -0.02;

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfNumberView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LiveBrushProperty = RegisterBrush(nameof(LiveBrush));
    public static readonly DependencyProperty RestingBrushProperty = RegisterBrush(nameof(RestingBrush));
    public static readonly DependencyProperty NoneBrushProperty = RegisterBrush(nameof(NoneBrush));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly PerfTicker _ticker = new(PerfSurface.Card);
    private PerfNumberTone _tone = PerfNumberTone.None;
    private PerfNumberTone _toneFrom = PerfNumberTone.None;
    private double _toneStart = double.NegativeInfinity;
    private double _markFrom;
    private double _markTo;
    private double _markStart = double.NegativeInfinity;
    private bool _rendering;

    public PerfNumberView()
    {
        SnapsToDevicePixels = true;
        SetResourceReference(LiveBrushProperty, PerfCardAppearance.PrimaryKey);
        SetResourceReference(RestingBrushProperty, PerfCardAppearance.SecondaryKey);
        SetResourceReference(NoneBrushProperty, PerfCardAppearance.TertiaryKey);
        Unloaded += (_, _) => StopFrames();
    }

    /// <summary>Every frame of a running change, so the scale can follow the shown value.</summary>
    public event EventHandler? Frame;

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public Brush? LiveBrush
    {
        get => (Brush?)GetValue(LiveBrushProperty);
        set => SetValue(LiveBrushProperty, value);
    }

    public Brush? RestingBrush
    {
        get => (Brush?)GetValue(RestingBrushProperty);
        set => SetValue(RestingBrushProperty, value);
    }

    public Brush? NoneBrush
    {
        get => (Brush?)GetValue(NoneBrushProperty);
        set => SetValue(NoneBrushProperty, value);
    }

    public bool ReduceMotion { get; set; }

    public bool HighContrast { get; set; }

    /// <summary>The integer on display now (a count's intermediate value); null while none.</summary>
    public int? ShownNow => _ticker.Shown(Now);

    /// <summary>The baseline's distance from the element's top.</summary>
    public double Baseline => Layout(Brushes.Black).Baseline;

    /// <summary>The digits' cap height, for centring the usual chip on them.</summary>
    public double CapHeight => Layout(Brushes.Black).CapHeight;

    private static double Now => Clock.Elapsed.TotalSeconds;

    private double Tracking => TrackingEm * Size;

    /// <summary>Shows a value: at once for a card that opens or switches, with DESIGN 12's motion on the open card's tick.</summary>
    public void Apply(int? shown, PerfNumberTone tone, bool provisional, bool animate)
    {
        var now = Now;
        animate &= IsLoaded && IsVisible;
        _ticker.Set(shown, now, ReduceMotion, animate);
        if (tone != _tone)
        {
            _toneFrom = animate ? _tone : tone;
            _toneStart = animate ? now : double.NegativeInfinity;
            _tone = tone;
        }

        var mark = provisional && shown is not null ? 1.0 : 0.0;
        if (mark != _markTo || !animate)
        {
            _markFrom = animate && !ReduceMotion ? MarkOpacity(now) : mark;
            _markStart = animate && !ReduceMotion ? now : double.NegativeInfinity;
            _markTo = mark;
        }

        InvalidateMeasure();
        InvalidateVisual();
        StartFramesIfMoving();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NumberPeer(this);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var now = Now;
        var layout = Layout(Brushes.Black);
        var width = layout.Width(PerfNumber.Full(_ticker.Shown(now)), Tracking);
        if (_ticker.Outgoing(now) is { } outgoing)
        {
            width = Math.Max(width, layout.Width(PerfNumber.Full(outgoing), Tracking));
        }

        // The dotted underline takes no room unless the line leaves none under the baseline.
        var below = layout.Baseline + PerfProvisionalMark.Below * Size + PerfProvisionalMark.Diameter(Size, 1, HighContrast) / 2;
        return new Size(Math.Ceiling(width), Math.Ceiling(Math.Max(Size * 1.18, below)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var now = Now;
        var brush = ToneBrush(now);
        var (outgoingOpacity, incomingOpacity) = _ticker.Fade(now);
        var shown = _ticker.Shown(now);
        var layout = Layout(brush);
        var baseline = layout.Baseline;

        if (_ticker.Outgoing(now) is { } outgoing && outgoingOpacity > 0)
        {
            dc.PushOpacity(outgoingOpacity);
            Layout(brush).Draw(dc, PerfNumber.Full(outgoing), 0, baseline, Tracking);
            dc.Pop();
        }

        dc.PushOpacity(incomingOpacity);
        var width = (shown is null ? Layout(brush, FontWeights.Medium) : layout).Draw(dc, PerfNumber.Full(shown), 0, baseline, Tracking);
        dc.Pop();

        var mark = MarkOpacity(now);
        if (mark > 0 && shown is not null)
        {
            var underline = PerfProvisionalMark.Underline(Size, width, VisualTreeHelper.GetDpi(this).PixelsPerDip, highContrast: HighContrast);
            var dots = HighContrast ? SystemColors.WindowTextBrush : brush;
            PerfMarkDrawing.Underline(dc, underline, 0, baseline, dots, mark * incomingOpacity);
        }
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(PerfNumberView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private PerfTabularText Layout(Brush brush, FontWeight? weight = null) =>
        new(FontFamily, weight ?? FontWeights.SemiBold, Size, VisualTreeHelper.GetDpi(this).PixelsPerDip, brush);

    private static double Progress(double now, double start, double seconds) =>
        double.IsNegativeInfinity(start) ? 1 : Easing.Clamp01((now - start) / seconds);

    private double MarkOpacity(double now) => Easing.Lerp(_markFrom, _markTo, Progress(now, _markStart, PerfProvisionalMark.FadeSeconds));

    private double ToneProgress(double now) => Progress(now, _toneStart, PerfMotion.FormSeconds);

    /// <summary>The number's color, crossfading between forms (kept with Reduce Motion: an opacity change, not motion).</summary>
    private Brush ToneBrush(double now)
    {
        var to = BrushOf(_tone);
        var t = ToneProgress(now);
        if (t >= 1 || _toneFrom == _tone || BrushOf(_toneFrom) is not SolidColorBrush from || to is not SolidColorBrush target)
        {
            return to ?? Brushes.Gray;
        }

        var a = from.Color;
        var b = target.Color;
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        var brush = new SolidColorBrush(Color.FromArgb(Mix(a.A, b.A), Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B)));
        brush.Freeze();
        return brush;
    }

    private Brush? BrushOf(PerfNumberTone tone) => HighContrast
        ? tone == PerfNumberTone.Live ? SystemColors.WindowTextBrush : SystemColors.GrayTextBrush
        : tone switch
        {
            PerfNumberTone.Live => LiveBrush,
            PerfNumberTone.Resting => RestingBrush,
            _ => NoneBrush,
        };

    private void StartFramesIfMoving()
    {
        if (_rendering || !IsLoaded || !Moving(Now))
        {
            return;
        }

        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (_rendering)
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private bool Moving(double now) =>
        _ticker.IsAnimating(now) || ToneProgress(now) < 1 || Progress(now, _markStart, PerfProvisionalMark.FadeSeconds) < 1;

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
        Frame?.Invoke(this, EventArgs.Empty);
        if (!Moving(Now))
        {
            StopFrames();
        }
    }

    private sealed class NumberPeer(PerfNumberView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(PerfNumberView);

        protected override string GetNameCore()
        {
            var number = (PerfNumberView)Owner;
            return PerfNumber.Full(number._ticker.Target);
        }
    }
}
