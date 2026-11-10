using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// A quota window's bar (DESIGN 4, Quota lines): a fully rounded track, the fill eased to its share (0.4 s ease-out; a
/// reset empties it over 0.6 s) and the pace mark, a 1 × <see cref="TickHeight"/> tick centered on the bar. Reduce Motion
/// changes the length at once.
/// </summary>
internal sealed class QuotaBar : FrameworkElement
{
    public const double BarHeight = 4;
    public const double TickHeight = 8;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(QuotaBar), new PropertyMetadata(0.0, (d, e) => ((QuotaBar)d).EaseTo((double)e.NewValue)));

    /// <summary>The reset passed: the next change empties the bar over the longer reset duration.</summary>
    public static readonly DependencyProperty PassedProperty = DependencyProperty.Register(
        nameof(Passed), typeof(bool), typeof(QuotaBar), new PropertyMetadata(false));

    /// <summary>The pace mark's position 0…1; NaN hides it.</summary>
    public static readonly DependencyProperty PaceProperty = DependencyProperty.Register(
        nameof(Pace), typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(QuotaBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(QuotaBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackOpacityProperty = DependencyProperty.Register(
        nameof(TrackOpacity), typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(0.08, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(Brush), typeof(QuotaBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickOpacityProperty = DependencyProperty.Register(
        nameof(TickOpacity), typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(0.35, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(QuotaBar), new PropertyMetadata(false));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _placed;

    public QuotaBar()
    {
        SnapsToDevicePixels = true;
        Height = TickHeight;
        Loaded += (_, _) => _placed = true;
        Unloaded += (_, _) => _placed = false;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool Passed { get => (bool)GetValue(PassedProperty); set => SetValue(PassedProperty, value); }

    public double Pace { get => (double)GetValue(PaceProperty); set => SetValue(PaceProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public double TrackOpacity { get => (double)GetValue(TrackOpacityProperty); set => SetValue(TrackOpacityProperty, value); }

    public Brush Tick { get => (Brush)GetValue(TickProperty); set => SetValue(TickProperty, value); }

    public double TickOpacity { get => (double)GetValue(TickOpacityProperty); set => SetValue(TickOpacityProperty, value); }

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    private void EaseTo(double target)
    {
        target = Math.Clamp(double.IsFinite(target) ? target : 0, 0, 1);
        if (!_placed || ReduceMotion || !IsVisible)
        {
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, target);
            return;
        }

        var emptying = Passed && target == 0;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(emptying ? 600 : 400))
        {
            EasingFunction = emptying ? new SineEase { EasingMode = EasingMode.EaseInOut } : new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(ShownProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, TickHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var top = Snap((ActualHeight - BarHeight) / 2, dpi);
        var radius = BarHeight / 2;
        dc.PushOpacity(TrackOpacity);
        dc.DrawRoundedRectangle(Track, null, new Rect(0, top, width, BarHeight), radius, radius);
        dc.Pop();

        var shown = (double)GetValue(ShownProperty);
        if (shown > 0.0005)
        {
            // A sliver never reads narrower than a dot: the fill keeps at least the bar's height.
            var fill = Math.Max(BarHeight, shown * width);
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, top, Math.Min(width, fill), BarHeight), radius, radius);
        }

        var pace = Pace;
        if (double.IsFinite(pace))
        {
            var x = Snap(Math.Clamp(pace, 0, 1) * (width - 1), VisualTreeHelper.GetDpi(this).DpiScaleX);
            dc.PushOpacity(TickOpacity);
            dc.DrawRectangle(Tick, null, new Rect(x, Snap((ActualHeight - TickHeight) / 2, dpi), 1, TickHeight));
            dc.Pop();
        }
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
