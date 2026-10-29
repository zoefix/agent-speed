using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The score column's meter (DESIGN 12, <i>Table</i>): 36 × 4 on the log scale in the score ink over the track, with the
/// 1000 tick; nothing while the row has no score. A new value springs to its length (at most 0.35 s); Reduce Motion: at once.
/// </summary>
internal sealed class PerfMeterBar : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(double), typeof(PerfMeterBar), new FrameworkPropertyMetadata(0.0, OnFillChanged));

    public static readonly DependencyProperty PresentProperty = DependencyProperty.Register(
        nameof(Present), typeof(bool), typeof(PerfMeterBar), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(PerfMeterBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _placed;

    public PerfMeterBar()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        Width = PerfHistoryLayout.MeterWidth;
        Height = 8;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public double Fill
    {
        get => (double)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool Present
    {
        get => (bool)GetValue(PresentProperty);
        set => SetValue(PresentProperty, value);
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0 || !Present)
        {
            return;
        }

        var h = PerfHistoryLayout.MeterHeight;
        var top = (ActualHeight - h) / 2;
        dc.DrawRoundedRectangle(PerfHistoryAppearance.Brush(PerfHistoryAppearance.Track(this)), null, new Rect(0, top, width, h), h / 2, h / 2);
        var fill = Math.Clamp((double)GetValue(ShownProperty), 0, 1) * width;
        if (fill > 0)
        {
            dc.DrawRoundedRectangle(PerfHistoryAppearance.Brush(PerfHistoryAppearance.Ink(this)), null, new Rect(0, top, Math.Max(fill, h), h), h / 2, h / 2);
        }

        var tick = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, PerfColors.ReferenceTickOpacity));
        dc.DrawRectangle(tick, null, new Rect(Math.Round(width / 2) - 0.5, 0, 1, ActualHeight));
    }

    private static void OnFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (PerfMeterBar)d;
        var target = Math.Clamp((double)e.NewValue, 0, 1);
        if (!bar._placed || !bar.IsLoaded || !bar.IsVisible || PerfHistoryAppearance.GetReduceMotion(bar))
        {
            bar._placed = true;
            bar.BeginAnimation(ShownProperty, null);
            bar.SetValue(ShownProperty, target);
            return;
        }

        bar.BeginAnimation(ShownProperty, new DoubleAnimation(target, PerfHistoryMotion.Spring)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 },
        });
    }
}
