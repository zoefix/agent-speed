using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// A sub-score's mini meter (DESIGN 12, <i>Score card</i>, 3; the History table's score meter): a rounded track with the
/// sub-score's fill on the log scale in the score ink and a 1 × 8 tick at the middle (1000) in the label color at 35 %. An
/// absent component draws a dotted track without fill. It eases to a new length (Reduce Motion: at once).
/// </summary>
internal sealed class PerfMiniMeter : FrameworkElement
{
    public const double TickHeight = 8;
    public const double TickOpacity = 0.35;

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(PerfMiniMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private PerfCardAppearance _appearance;
    private bool _present;
    private bool _placed;

    public PerfMiniMeter()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        Width = 44;
        Height = TickHeight;
    }

    /// <summary>The track's height (4 on the card and in History).</summary>
    public double TrackHeight { get; set; } = 4;

    /// <param name="fill">The sub-score on the log scale (0–1); ignored while absent.</param>
    public void Apply(bool present, double fill, PerfCardAppearance appearance, bool animate)
    {
        _appearance = appearance;
        _present = present;
        var target = present ? Math.Clamp(fill, 0, 1) : 0;
        if (animate && _placed && IsLoaded && IsVisible && !appearance.ReduceMotion)
        {
            var from = (double)GetValue(ShownProperty);
            BeginAnimation(ShownProperty, new System.Windows.Media.Animation.DoubleAnimation(from, target, TimeSpan.FromSeconds(0.24))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        }
        else
        {
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, target);
        }

        _placed = true;
        InvalidateVisual();
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var h = TrackHeight;
        var track = new Rect(0, (ActualHeight - h) / 2, width, h);
        var radius = h / 2;
        if (!_present)
        {
            PerfMarkDrawing.DottedRow(dc, track, _appearance.AbsentDots);
            return;
        }

        if (_appearance.OutlinedTracks)
        {
            var pen = new Pen(_appearance.Track, 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(track.X + 0.5, track.Y + 0.5, track.Width - 1, track.Height - 1), radius - 0.5, radius - 0.5);
        }
        else
        {
            dc.DrawRoundedRectangle(_appearance.Track, null, track, radius, radius);
        }

        var fill = (double)GetValue(ShownProperty) * width;
        if (fill > 0)
        {
            dc.DrawRoundedRectangle(_appearance.Ink, null, new Rect(0, track.Y, Math.Max(fill, h), h), radius, radius);
        }

        var x = Math.Round(width / 2) - 0.5;
        dc.DrawRectangle(_appearance.Tick(TickOpacity), null, new Rect(x, (ActualHeight - TickHeight) / 2, 1, TickHeight));
    }
}
